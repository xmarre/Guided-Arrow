using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Prevents a delayed OnMissileRemoved callback from an earlier missed/returned shot from removing
    /// a later guided projectile that reused the same integer missile index.
    /// </summary>
    internal static class GlobalMissileRemovalIdentityGatePatch
    {
        private sealed class DeferredRemoval
        {
            internal MethodInfo Method;
            internal object[] Arguments;
            internal int Index;
            internal int Generation;
            internal Agent Shooter;
            internal object Missile;
        }

        private sealed class DeferredRemovalQueue
        {
            internal readonly List<DeferredRemoval> Items = new List<DeferredRemoval>();
            internal bool Flushing;
        }

        private static readonly ConditionalWeakTable<object, DeferredRemovalQueue> Pending =
            new ConditionalWeakTable<object, DeferredRemovalQueue>();

        private static PropertyInfo _missionProperty;
        private static FieldInfo _missionRegistryField;
        private static FieldInfo _activeGenerationField;
        private static FieldInfo _activeShooterField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedGenerationField;
        private static FieldInfo _trackedShooterField;
        private static FieldInfo _trackedMissileField;

        [ThreadStatic]
        private static bool _replaying;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _missionProperty = AccessTools.Property(behaviorType, "Mission");
            _missionRegistryField = AccessTools.Field(typeof(Mission), "_missilesDictionary");
            _activeGenerationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _activeShooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            _trackedGenerationField = AccessTools.Field(trackedType, "ShotGeneration");
            _trackedShooterField = AccessTools.Field(trackedType, "OriginalShooter");
            _trackedMissileField = AccessTools.Field(trackedType, "Missile");

            if (_missionRegistryField == null ||
                _activeGenerationField == null ||
                _activeShooterField == null ||
                _trackedMissilesField == null ||
                _trackedIndexField == null ||
                _trackedGenerationField == null ||
                _trackedShooterField == null ||
                _trackedMissileField == null)
                return;

            MethodInfo removalPrefix = AccessTools.Method(
                typeof(GlobalMissileRemovalIdentityGatePatch),
                nameof(RemovalPrefix));
            MethodInfo displayPrefix = AccessTools.Method(
                typeof(GlobalMissileRemovalIdentityGatePatch),
                nameof(DisplayPrefix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(GlobalMissileRemovalIdentityGatePatch),
                nameof(ClearPrefix));
            MethodInfo clearPostfix = AccessTools.Method(
                typeof(GlobalMissileRemovalIdentityGatePatch),
                nameof(ClearPostfix));
            if (removalPrefix == null || displayPrefix == null || clearPrefix == null || clearPostfix == null)
                return;

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnMissileRemoved" && !candidate.IsAbstract))
            {
                try
                {
                    HarmonyMethod prefix = new HarmonyMethod(removalPrefix)
                    {
                        priority = int.MaxValue,
                        before = new[] { "guidedarrow.progression.generation-scoped-impact-replay" }
                    };
                    harmony.Patch(method, prefix: prefix);
                }
                catch { }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnPreDisplayMissionTick" && !candidate.IsAbstract))
            {
                try
                {
                    // The generation-scoped impact replay runs at int.MaxValue. Process removals one
                    // priority step later so the core observes hit processing before removal.
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(displayPrefix) { priority = int.MaxValue - 1 });
                }
                catch { }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "StartGuidedShot" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(clearPrefix) { priority = int.MaxValue });
                }
                catch { }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "ResetAll" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(clearPostfix) { priority = Priority.Last });
                }
                catch { }
            }
        }

        private static bool RemovalPrefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (_replaying) return true;
            if (__instance == null || __originalMethod == null || __args == null) return false;

            try
            {
                int index = ReadFirstInt(__args);
                object tracked = FindTracked(__instance, index);
                if (tracked == null)
                    return false;

                int generation = (int)_trackedGenerationField.GetValue(tracked);
                Agent shooter = _trackedShooterField.GetValue(tracked) as Agent;
                object missile = _trackedMissileField.GetValue(tracked);

                int activeGeneration = ReadActiveGeneration(__instance);
                Agent activeShooter = ReadActiveShooter(__instance);
                if (generation != activeGeneration || !ReferenceEquals(shooter, activeShooter))
                    return false;

                // A callback for an older native object may arrive after this index has been reused.
                // If the currently tracked wrapper is still the exact live registry entry, this
                // callback cannot belong to it and must be discarded.
                if (IsExactLiveRegistryEntry(__instance, index, missile))
                    return false;

                DeferredRemovalQueue queue = Pending.GetOrCreateValue(__instance);
                queue.Items.Add(new DeferredRemoval
                {
                    Method = __originalMethod as MethodInfo,
                    Arguments = (object[])__args.Clone(),
                    Index = index,
                    Generation = generation,
                    Shooter = shooter,
                    Missile = missile
                });
            }
            catch
            {
                // Unverified removals are never allowed to act on a possibly reused native index.
            }

            return false;
        }

        private static void DisplayPrefix(object __instance)
        {
            if (__instance == null ||
                !Pending.TryGetValue(__instance, out DeferredRemovalQueue queue) ||
                queue == null ||
                queue.Flushing)
                return;

            DeferredRemoval[] removals = queue.Items.ToArray();
            queue.Items.Clear();
            queue.Flushing = true;

            try
            {
                int activeGeneration = ReadActiveGeneration(__instance);
                Agent activeShooter = ReadActiveShooter(__instance);

                for (int i = 0; i < removals.Length; i++)
                {
                    DeferredRemoval removal = removals[i];
                    if (removal?.Method == null || removal.Arguments == null) continue;
                    if (removal.Generation != activeGeneration ||
                        !ReferenceEquals(removal.Shooter, activeShooter))
                        continue;

                    object tracked = FindTracked(__instance, removal.Index);
                    if (tracked == null) continue;
                    if ((int)_trackedGenerationField.GetValue(tracked) != removal.Generation ||
                        !ReferenceEquals(_trackedShooterField.GetValue(tracked) as Agent, removal.Shooter) ||
                        !ReferenceEquals(_trackedMissileField.GetValue(tracked), removal.Missile))
                        continue;

                    if (IsExactLiveRegistryEntry(__instance, removal.Index, removal.Missile))
                        continue;

                    try
                    {
                        _replaying = true;
                        removal.Method.Invoke(__instance, removal.Arguments);
                    }
                    catch { }
                    finally
                    {
                        _replaying = false;
                    }
                }
            }
            finally
            {
                queue.Flushing = false;
                if (queue.Items.Count == 0)
                    Pending.Remove(__instance);
            }
        }

        private static object FindTracked(object instance, int index)
        {
            if (instance == null || index < 0) return null;

            try
            {
                IList tracked = _trackedMissilesField.GetValue(instance) as IList;
                if (tracked == null) return null;

                for (int i = 0; i < tracked.Count; i++)
                {
                    object candidate = tracked[i];
                    if (candidate == null) continue;
                    if ((int)_trackedIndexField.GetValue(candidate) == index)
                        return candidate;
                }
            }
            catch { }

            return null;
        }

        private static bool IsExactLiveRegistryEntry(object instance, int index, object missile)
        {
            if (instance == null || index < 0 || missile == null) return false;

            try
            {
                Mission mission = _missionProperty?.GetValue(instance, null) as Mission ?? Mission.Current;
                if (mission == null) return false;

                object registry = _missionRegistryField.GetValue(mission);
                if (registry == null) return false;

                if (registry is IDictionary dictionary)
                {
                    return dictionary.Contains(index) && ReferenceEquals(dictionary[index], missile);
                }

                MethodInfo tryGetValue = registry
                    .GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "TryGetValue" || method.ReturnType != typeof(bool)) return false;
                        ParameterInfo[] parameters = method.GetParameters();
                        return parameters.Length == 2 &&
                               parameters[0].ParameterType == typeof(int) &&
                               parameters[1].ParameterType.IsByRef;
                    });
                if (tryGetValue == null) return false;

                object[] args = { index, null };
                object found = tryGetValue.Invoke(registry, args);
                return found is bool value && value && ReferenceEquals(args[1], missile);
            }
            catch
            {
                return false;
            }
        }

        private static int ReadFirstInt(object[] arguments)
        {
            if (arguments == null) return -1;
            for (int i = 0; i < arguments.Length; i++)
                if (arguments[i] is int value) return value;
            return -1;
        }

        private static int ReadActiveGeneration(object instance)
        {
            try { return (int)_activeGenerationField.GetValue(instance); }
            catch { return 0; }
        }

        private static Agent ReadActiveShooter(object instance)
        {
            try { return _activeShooterField.GetValue(instance) as Agent; }
            catch { return null; }
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
            _replaying = false;
        }

        private static void ClearPostfix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
            _replaying = false;
        }
    }
}
