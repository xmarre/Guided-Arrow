using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps a controlled terminal penetration continuation inside one coherent guided-shot state.
    /// The core normally releases the custom camera as soon as the last live missile reports an
    /// impact, even when that impact is immediately converted into a replacement missile. It also
    /// waits for the entire global continuation queue to drain before repairing Autoguidance, and
    /// cannot expire orphaned collision records once no tracked missile remains. Together those
    /// transitions cause camera strobing, targetless continuation chains and a permanently active
    /// guided generation after the final impact.
    /// </summary>
    internal static class ContinuationRuntimeStabilityPatch
    {
        private sealed class BehaviorState
        {
            internal bool RetargetRequested;
        }

        private static readonly ConditionalWeakTable<object, BehaviorState> States =
            new ConditionalWeakTable<object, BehaviorState>();

        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _pendingContinuationSpawnsField;
        private static FieldInfo _pendingCollisionContextsField;
        private static FieldInfo _earlyCollisionReactionsField;
        private static MethodInfo _findTrackedMissileMethod;
        private static MethodInfo _hasRemainingAgentPenetrationMethod;
        private static MethodInfo _assignAutoguidanceTargetsMethod;
        private static MethodInfo _isAutoguidanceRuntimeActiveMethod;
        private static MethodInfo _logMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType(
                "TrackedMissile",
                BindingFlags.NonPublic);
            if (trackedType == null) return;

            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _pendingContinuationSpawnsField = AccessTools.Field(
                behaviorType,
                "_pendingContinuationSpawns");
            _pendingCollisionContextsField = AccessTools.Field(
                behaviorType,
                "_pendingCollisionContexts");
            _earlyCollisionReactionsField = AccessTools.Field(
                behaviorType,
                "_earlyCollisionReactions");

            _findTrackedMissileMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "FindTrackedMissile" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));
            _hasRemainingAgentPenetrationMethod = behaviorType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "HasRemainingAgentPenetration" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == trackedType);
            _assignAutoguidanceTargetsMethod = AccessTools.Method(
                behaviorType,
                "AssignAutoguidanceTargets",
                new[] { typeof(bool) });
            _isAutoguidanceRuntimeActiveMethod = AccessTools.Method(
                behaviorType,
                "IsAutoguidanceRuntimeActive",
                Type.EmptyTypes);
            _logMethod = AccessTools.Method(
                behaviorType,
                "Log",
                new[] { typeof(string) });

            if (_trackedMissilesField == null ||
                _pendingContinuationSpawnsField == null ||
                _pendingCollisionContextsField == null ||
                _earlyCollisionReactionsField == null ||
                _findTrackedMissileMethod == null ||
                _hasRemainingAgentPenetrationMethod == null ||
                _assignAutoguidanceTargetsMethod == null ||
                _isAutoguidanceRuntimeActiveMethod == null)
                return;

            MethodInfo suspendMethod = AccessTools.Method(
                behaviorType,
                "SuspendProjectileCameraForCollisionReaction",
                new[] { typeof(int) });
            MethodInfo createTrackedMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "CreateTrackedMissileFromSpawn" &&
                    method.GetParameters().Length == 5);

            MethodInfo suspendPrefix = AccessTools.Method(
                typeof(ContinuationRuntimeStabilityPatch),
                nameof(SuspendCameraPrefix));
            MethodInfo createPostfix = AccessTools.Method(
                typeof(ContinuationRuntimeStabilityPatch),
                nameof(CreateTrackedPostfix));
            MethodInfo missionTickPostfix = AccessTools.Method(
                typeof(ContinuationRuntimeStabilityPatch),
                nameof(MissionTickPostfix));
            MethodInfo displayPostfix = AccessTools.Method(
                typeof(ContinuationRuntimeStabilityPatch),
                nameof(DisplayPostfix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(ContinuationRuntimeStabilityPatch),
                nameof(ClearPrefix));

            try
            {
                if (suspendMethod != null && suspendPrefix != null)
                {
                    harmony.Patch(
                        suspendMethod,
                        prefix: new HarmonyMethod(suspendPrefix)
                        {
                            priority = Priority.First
                        });
                }

                if (createTrackedMethod != null && createPostfix != null)
                {
                    harmony.Patch(
                        createTrackedMethod,
                        postfix: new HarmonyMethod(createPostfix)
                        {
                            priority = Priority.Last
                        });
                }

                if (missionTickPostfix != null)
                {
                    foreach (MethodInfo method in behaviorType
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(candidate =>
                            candidate.Name == "OnMissionTick" &&
                            !candidate.IsAbstract))
                    {
                        try
                        {
                            harmony.Patch(
                                method,
                                postfix: new HarmonyMethod(missionTickPostfix)
                                {
                                    priority = Priority.Last
                                });
                        }
                        catch { }
                    }
                }

                if (displayPostfix != null)
                {
                    foreach (MethodInfo method in behaviorType
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(candidate =>
                            candidate.Name == "OnPreDisplayMissionTick" &&
                            !candidate.IsAbstract))
                    {
                        try
                        {
                            harmony.Patch(
                                method,
                                postfix: new HarmonyMethod(displayPostfix)
                                {
                                    priority = int.MinValue
                                });
                        }
                        catch { }
                    }
                }

                if (clearPrefix != null)
                {
                    foreach (string methodName in new[] { "StartGuidedShot", "ResetAll" })
                    {
                        foreach (MethodInfo method in behaviorType
                            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .Where(candidate =>
                                candidate.Name == methodName &&
                                !candidate.IsAbstract))
                        {
                            try
                            {
                                harmony.Patch(
                                    method,
                                    prefix: new HarmonyMethod(clearPrefix)
                                    {
                                        priority = Priority.First
                                    });
                            }
                            catch { }
                        }
                    }
                }
            }
            catch
            {
                // Unknown private layouts retain the locked core behavior.
            }
        }

        private static bool SuspendCameraPrefix(
            object __instance,
            int missileIndex)
        {
            if (__instance == null || missileIndex < 0)
                return true;

            try
            {
                if (!ExactEarlyCollisionReactionPatch.TryGetAgentHit(
                        __instance,
                        missileIndex,
                        out bool hitShield) ||
                    hitShield)
                    return true;

                object tracked = _findTrackedMissileMethod.Invoke(
                    __instance,
                    new object[] { missileIndex });
                if (tracked == null)
                    return true;

                object remaining = _hasRemainingAgentPenetrationMethod.Invoke(
                    null,
                    new[] { tracked });
                if (!(remaining is bool canContinue) || !canContinue)
                    return true;

                if (NativeVolleyPenetrationIsolationPatch
                    .ShouldBlockSyntheticContinuation(__instance, tracked))
                    return true;

                // Keep the last submitted projectile frame and custom-camera ownership while the
                // native reaction is resolved and the replacement is quarantined. PassThrough then
                // continues normally; a terminal continuation resumes on the new exact missile;
                // a real terminal outcome still releases the camera through the core terminal path.
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void CreateTrackedPostfix(
            object __instance,
            bool __4,
            object __result)
        {
            if (__instance == null || !__4 || __result == null)
                return;

            States.GetOrCreateValue(__instance).RetargetRequested = true;
        }

        private static void MissionTickPostfix(object __instance)
        {
            if (__instance == null ||
                Count(_trackedMissilesField, __instance) != 0 ||
                Count(_pendingContinuationSpawnsField, __instance) <= 0)
                return;

            int cleared = ClearList(_pendingCollisionContextsField, __instance) +
                          ClearList(_earlyCollisionReactionsField, __instance);
            if (cleared > 0)
            {
                TryLog(
                    __instance,
                    "Cleared orphaned collision work so the final deferred penetration continuation can proceed.");
            }
        }

        private static void DisplayPostfix(object __instance)
        {
            if (__instance == null ||
                !States.TryGetValue(__instance, out BehaviorState state) ||
                state == null ||
                !state.RetargetRequested ||
                Count(_trackedMissilesField, __instance) <= 0)
                return;

            try
            {
                object active = _isAutoguidanceRuntimeActiveMethod.Invoke(
                    __instance,
                    null);
                if (!(active is bool enabled) || !enabled)
                {
                    state.RetargetRequested = false;
                    return;
                }

                // Assign every continuation that currently exists. Do not wait for unrelated
                // continuation requests to drain: a later materialized continuation requests its
                // own assignment through CreateTrackedPostfix.
                _assignAutoguidanceTargetsMethod.Invoke(
                    __instance,
                    new object[] { false });
                state.RetargetRequested = false;
                TryLog(
                    __instance,
                    "Autoguidance reassigned after the current penetration continuation materialized.");
            }
            catch
            {
                state.RetargetRequested = true;
            }
        }

        private static int Count(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return -1;
            try
            {
                object value = field.GetValue(instance);
                if (value is ICollection collection) return collection.Count;

                PropertyInfo countProperty = value?.GetType().GetProperty(
                    "Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object count = countProperty?.GetValue(value, null);
                return count is int integer ? integer : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static int ClearList(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return 0;
            try
            {
                IList list = field.GetValue(instance) as IList;
                if (list == null || list.Count == 0) return 0;
                int count = list.Count;
                list.Clear();
                return count;
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

        private static void TryLog(object instance, string message)
        {
            if (_logMethod == null ||
                instance == null ||
                string.IsNullOrEmpty(message))
                return;

            try
            {
                _logMethod.Invoke(instance, new object[] { message });
            }
            catch { }
        }
    }
}
