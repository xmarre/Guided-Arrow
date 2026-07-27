using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Prevents the binary-only stable core from retaining a managed Mission.Missile wrapper
    /// after Bannerlord has removed or replaced that wrapper in the mission missile registry.
    ///
    /// Native missile slots are reusable. A non-null managed wrapper is therefore not sufficient
    /// proof that the native missile is still alive or still belongs to the guided shot. This patch
    /// validates ownership using managed registry identity before the core's tick/camera paths can
    /// call GetPosition, GetVelocity or SetVelocity on a stale native handle.
    /// </summary>
    internal static class MissileLifetimeSafetyPatch
    {
        private static PropertyInfo _missionProperty;
        private static FieldInfo _missionMissilesDictionaryField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _trackedMissileField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _leaderMissileField;
        private static FieldInfo _leaderIndexField;
        private static FieldInfo _cameraMissileIndexField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _missionProperty = AccessTools.Property(behaviorType, "Mission");
            _missionMissilesDictionaryField = AccessTools.Field(typeof(Mission), "_missilesDictionary");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _trackedMissileField = AccessTools.Field(trackedType, "Missile");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            _leaderMissileField = AccessTools.Field(behaviorType, "_missile");
            _leaderIndexField = AccessTools.Field(behaviorType, "_missileIndex");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");

            if (_missionMissilesDictionaryField == null ||
                _trackedMissilesField == null ||
                _trackedMissileField == null ||
                _trackedIndexField == null)
                return;

            // These are the two outer per-frame entry points. Patching the private methods they call
            // as well would repeat the same scan several times per frame without closing another
            // native-lifetime gap.
            PatchPrefixes(
                harmony,
                behaviorType,
                "OnMissionTick",
                "OnPreDisplayMissionTick");

            PatchPostfixes(
                harmony,
                behaviorType,
                "OnMissileRemoved",
                "BeginReturn",
                "HandleGuidedSwarmTerminal",
                "ResetAll");
        }

        private static void PatchPrefixes(Harmony harmony, Type behaviorType, params string[] methodNames)
        {
            MethodInfo prefixMethod = AccessTools.Method(typeof(MissileLifetimeSafetyPatch), nameof(SanitizePrefix));
            if (prefixMethod == null) return;

            HarmonyMethod prefix = new HarmonyMethod(prefixMethod) { priority = Priority.First };
            foreach (string methodName in methodNames)
            {
                foreach (MethodInfo method in FindMethods(behaviorType, methodName))
                {
                    try { harmony.Patch(method, prefix: prefix); }
                    catch { }
                }
            }
        }

        private static void PatchPostfixes(Harmony harmony, Type behaviorType, params string[] methodNames)
        {
            MethodInfo postfixMethod = AccessTools.Method(typeof(MissileLifetimeSafetyPatch), nameof(SanitizePostfix));
            if (postfixMethod == null) return;

            HarmonyMethod postfix = new HarmonyMethod(postfixMethod) { priority = Priority.Last };
            foreach (string methodName in methodNames)
            {
                foreach (MethodInfo method in FindMethods(behaviorType, methodName))
                {
                    try { harmony.Patch(method, postfix: postfix); }
                    catch { }
                }
            }
        }

        private static IEnumerable<MethodInfo> FindMethods(Type behaviorType, string methodName)
        {
            return behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == methodName && !method.IsAbstract);
        }

        private static void SanitizePrefix(object __instance)
        {
            Sanitize(__instance);
        }

        private static void SanitizePostfix(object __instance)
        {
            Sanitize(__instance);
        }

        private static void Sanitize(object instance)
        {
            if (instance == null) return;

            try
            {
                Mission mission = ResolveMission(instance);
                if (mission == null) return;

                object registry = _missionMissilesDictionaryField.GetValue(mission);
                IList tracked = _trackedMissilesField.GetValue(instance) as IList;
                if (registry == null || tracked == null) return;

                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    object entry = tracked[i];
                    if (!IsExactLiveRegistryEntry(registry, entry, out _, out _))
                        tracked.RemoveAt(i);
                }

                RepairOwnership(instance, tracked);
            }
            catch
            {
                // The verified core remains authoritative when a future game/core version changes
                // a reflected field. Never replace a normal mission tick with a sidecar exception.
            }
        }

        private static Mission ResolveMission(object instance)
        {
            if (_missionProperty != null)
            {
                try
                {
                    Mission mission = _missionProperty.GetValue(instance, null) as Mission;
                    if (mission != null) return mission;
                }
                catch { }
            }

            return Mission.Current;
        }

        private static bool IsExactLiveRegistryEntry(
            object registry,
            object trackedEntry,
            out int index,
            out object missile)
        {
            index = -1;
            missile = null;
            if (trackedEntry == null) return false;

            try
            {
                index = (int)_trackedIndexField.GetValue(trackedEntry);
                missile = _trackedMissileField.GetValue(trackedEntry);
            }
            catch
            {
                return false;
            }

            if (index < 0 || missile == null) return false;
            return TryGetRegisteredMissile(registry, index, out object registered) &&
                   ReferenceEquals(registered, missile);
        }

        private static bool TryGetRegisteredMissile(object registry, int index, out object missile)
        {
            missile = null;
            if (registry == null || index < 0) return false;

            try
            {
                if (registry is IDictionary dictionary)
                {
                    if (!dictionary.Contains(index)) return false;
                    missile = dictionary[index];
                    return missile != null;
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
                object result = tryGetValue.Invoke(registry, args);
                if (!(result is bool found) || !found || args[1] == null) return false;
                missile = args[1];
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RepairOwnership(object instance, IList tracked)
        {
            if (tracked == null) return;

            if (tracked.Count == 0)
            {
                _leaderMissileField?.SetValue(instance, null);
                _leaderIndexField?.SetValue(instance, -1);
                _cameraMissileIndexField?.SetValue(instance, -1);
                return;
            }

            object firstMissile = null;
            int firstIndex = -1;
            object currentLeader = _leaderMissileField?.GetValue(instance);
            int currentLeaderIndex = ReadInt(_leaderIndexField, instance, -1);
            int cameraIndex = ReadInt(_cameraMissileIndexField, instance, -1);
            bool currentLeaderIsLive = false;
            bool cameraOwnerIsLive = cameraIndex < 0;

            for (int i = 0; i < tracked.Count; i++)
            {
                object entry = tracked[i];
                if (entry == null) continue;

                int index;
                object missile;
                try
                {
                    index = (int)_trackedIndexField.GetValue(entry);
                    missile = _trackedMissileField.GetValue(entry);
                }
                catch
                {
                    continue;
                }

                if (index < 0 || missile == null) continue;
                if (firstMissile == null)
                {
                    firstMissile = missile;
                    firstIndex = index;
                }

                if (index == currentLeaderIndex && ReferenceEquals(missile, currentLeader))
                    currentLeaderIsLive = true;
                if (index == cameraIndex)
                    cameraOwnerIsLive = true;
            }

            bool ownershipWasActive = currentLeader != null || currentLeaderIndex >= 0 || cameraIndex >= 0;
            int effectiveLeaderIndex = currentLeaderIsLive ? currentLeaderIndex : -1;

            if (!currentLeaderIsLive)
            {
                _leaderMissileField?.SetValue(instance, ownershipWasActive ? firstMissile : null);
                _leaderIndexField?.SetValue(instance, ownershipWasActive ? firstIndex : -1);
                effectiveLeaderIndex = ownershipWasActive ? firstIndex : -1;
            }

            // -1 is an intentional suspended/returned camera state. Preserve it. Repair only a
            // positive camera owner that no longer belongs to this exact live guided set.
            if (!cameraOwnerIsLive)
                _cameraMissileIndexField?.SetValue(instance, effectiveLeaderIndex);
        }

        private static int ReadInt(FieldInfo field, object instance, int fallback)
        {
            if (field == null || instance == null) return fallback;
            try { return (int)field.GetValue(instance); }
            catch { return fallback; }
        }
    }
}
