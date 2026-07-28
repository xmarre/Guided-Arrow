using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Prevents the v1.1.17 core from calling Mission.AddCustomMissile for a penetration
    /// continuation while a concentrated same-shooter missile burst is still resident in the
    /// mission. Bannerlord can raise AccessViolationException at that native boundary after a
    /// point-blank native volley has already produced PassThrough -> Stick for the tracked leader.
    /// The original volley remains authoritative; only the extra synthetic continuation is skipped.
    /// </summary>
    internal static class ConcentratedVolleyContinuationGuardPatch
    {
        private sealed class ShotLoadState
        {
            internal int Generation = -1;
            internal int PeakSameShooterMissiles;
            internal int PeakMissionMissiles;
        }

        private static readonly ConditionalWeakTable<object, ShotLoadState> States =
            new ConditionalWeakTable<object, ShotLoadState>();

        // Far below the reproduced 48-projectile burst, but above ordinary single/small split shots.
        private const int SameShooterMissileThreshold = 16;
        private const int MissionMissileThreshold = 40;

        private static FieldInfo _generationField;
        private static FieldInfo _activeShooterField;
        private static PropertyInfo _missilesListProperty;
        private static FieldInfo _missilesListField;
        private static Type _missileEntryType;
        private static PropertyInfo _missileShooterProperty;
        private static FieldInfo _missileShooterField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _activeShooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _missilesListProperty = AccessTools.Property(typeof(Mission), "MissilesList");
            _missilesListField = AccessTools.Field(typeof(Mission), "_missilesList");

            MethodInfo spawn = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            MethodInfo startPostfix = AccessTools.Method(
                typeof(ConcentratedVolleyContinuationGuardPatch),
                nameof(StartPostfix));
            MethodInfo samplePrefix = AccessTools.Method(
                typeof(ConcentratedVolleyContinuationGuardPatch),
                nameof(SamplePrefix));
            MethodInfo spawnPrefix = AccessTools.Method(
                typeof(ConcentratedVolleyContinuationGuardPatch),
                nameof(SpawnPrefix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(ConcentratedVolleyContinuationGuardPatch),
                nameof(ClearPrefix));

            if (spawn == null ||
                startPostfix == null ||
                samplePrefix == null ||
                spawnPrefix == null ||
                clearPrefix == null ||
                _generationField == null ||
                _activeShooterField == null)
                return;

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "StartGuidedShot" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(startPostfix) { priority = Priority.Last });
                }
                catch { }
            }

            foreach (string methodName in new[]
            {
                "OnMissileCollisionReaction",
                "OnMissileHit",
                "ProcessDeferredNativeMissileWork"
            })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == methodName && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(samplePrefix) { priority = Priority.First });
                    }
                    catch { }
                }
            }

            try
            {
                harmony.Patch(
                    spawn,
                    prefix: new HarmonyMethod(spawnPrefix) { priority = int.MaxValue });
            }
            catch
            {
                return;
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "ResetAll" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(clearPrefix) { priority = Priority.First });
                }
                catch { }
            }
        }

        private static void StartPostfix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                int generation = (int)_generationField.GetValue(__instance);
                ShotLoadState state = States.GetOrCreateValue(__instance);
                state.Generation = generation;
                state.PeakSameShooterMissiles = 0;
                state.PeakMissionMissiles = 0;
                Sample(__instance, state);
            }
            catch { }
        }

        private static void SamplePrefix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                int generation = (int)_generationField.GetValue(__instance);
                ShotLoadState state = States.GetOrCreateValue(__instance);
                if (state.Generation != generation)
                {
                    state.Generation = generation;
                    state.PeakSameShooterMissiles = 0;
                    state.PeakMissionMissiles = 0;
                }
                Sample(__instance, state);
            }
            catch { }
        }

        private static bool SpawnPrefix(object __instance, object[] __args, ref bool __result)
        {
            if (__instance == null) return true;

            try
            {
                int generation = (int)_generationField.GetValue(__instance);
                ShotLoadState state = States.GetOrCreateValue(__instance);
                if (state.Generation != generation)
                {
                    state.Generation = generation;
                    state.PeakSameShooterMissiles = 0;
                    state.PeakMissionMissiles = 0;
                }

                Sample(__instance, state);
                if (state.PeakSameShooterMissiles < SameShooterMissileThreshold &&
                    state.PeakMissionMissiles < MissionMissileThreshold)
                    return true;

                // Do not cross the exact native boundary that raised AccessViolationException.
                // The caller treats false/null as a normal failed continuation and completes the
                // existing tracked projectile's terminal path without creating another missile.
                if (__args != null && __args.Length >= 3)
                    __args[2] = null;
                __result = false;
                return false;
            }
            catch
            {
                // A failed managed load check must not replace the stable core's normal behavior.
                return true;
            }
        }

        private static void Sample(object instance, ShotLoadState state)
        {
            Mission mission = Mission.Current;
            if (mission == null || state == null) return;

            object listObject = null;
            try
            {
                if (_missilesListProperty != null)
                    listObject = _missilesListProperty.GetValue(mission, null);
                if (listObject == null && _missilesListField != null)
                    listObject = _missilesListField.GetValue(mission);
            }
            catch { }
            if (listObject == null) return;

            int total = ReadCount(listObject);
            if (total > state.PeakMissionMissiles)
                state.PeakMissionMissiles = total;

            Agent activeShooter;
            try { activeShooter = _activeShooterField.GetValue(instance) as Agent; }
            catch { activeShooter = null; }
            if (activeShooter == null || !(listObject is IEnumerable missiles)) return;

            int sameShooter = 0;
            try
            {
                foreach (object missile in missiles)
                {
                    if (missile == null) continue;
                    EnsureMissileEntryAccessors(missile.GetType());

                    Agent shooter = null;
                    try
                    {
                        if (_missileShooterProperty != null)
                            shooter = _missileShooterProperty.GetValue(missile, null) as Agent;
                        else if (_missileShooterField != null)
                            shooter = _missileShooterField.GetValue(missile) as Agent;
                    }
                    catch { }

                    if (ReferenceEquals(shooter, activeShooter))
                        sameShooter++;
                }
            }
            catch
            {
                return;
            }

            if (sameShooter > state.PeakSameShooterMissiles)
                state.PeakSameShooterMissiles = sameShooter;
        }

        private static void EnsureMissileEntryAccessors(Type entryType)
        {
            if (entryType == null || ReferenceEquals(_missileEntryType, entryType)) return;

            _missileEntryType = entryType;
            _missileShooterProperty = entryType.GetProperty(
                "ShooterAgent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _missileShooterField = AccessTools.Field(entryType, "<ShooterAgent>k__BackingField") ??
                                  AccessTools.Field(entryType, "ShooterAgent");
        }

        private static int ReadCount(object value)
        {
            if (value == null) return 0;
            if (value is ICollection collection) return collection.Count;

            try
            {
                PropertyInfo count = value.GetType().GetProperty(
                    "Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object result = count?.GetValue(value, null);
                return result is int integer ? integer : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                States.Remove(__instance);
        }
    }
}
