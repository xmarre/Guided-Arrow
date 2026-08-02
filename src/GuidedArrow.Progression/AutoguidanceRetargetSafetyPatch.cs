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
    /// Keeps Autoguidance target collection and route assignment outside Bannerlord's native
    /// missile-collision callback. Impact callbacks mutate only shot-local managed state; the
    /// normal core assignment path then runs from a safe pre-display boundary.
    /// </summary>
    internal static class AutoguidanceRetargetSafetyPatch
    {
        private sealed class RetargetState
        {
            internal bool Requested;
        }

        private static readonly ConditionalWeakTable<object, RetargetState> RetargetStates =
            new ConditionalWeakTable<object, RetargetState>();

        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _guidanceTargetField;
        private static FieldInfo _guidanceHeadBoneIndexField;
        private static FieldInfo _guidanceSmoothedHeadValidField;
        private static FieldInfo _guidanceLastRawHeadValidField;
        private static FieldInfo _guidanceTargetVelocityValidField;
        private static FieldInfo _guidanceRouteTargetsField;
        private static FieldInfo _guidanceConsumedTargetsField;
        private static FieldInfo _guidanceRouteReplanRequestedField;
        private static FieldInfo _guidanceNoProgressElapsedField;
        private static FieldInfo _autoguidanceReacquireCountdownField;
        private static FieldInfo _pendingCollisionContextsField;
        private static FieldInfo _earlyCollisionReactionsField;
        private static FieldInfo _pendingNativeMissileRemovalsField;
        private static FieldInfo _pendingContinuationSpawnsField;
        private static MethodInfo _assignAutoguidanceTargetsMethod;
        private static MethodInfo _isAutoguidanceRuntimeActiveMethod;
        private static MethodInfo _logMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _guidanceTargetField = AccessTools.Field(trackedType, "GuidanceTarget");
            _guidanceHeadBoneIndexField = AccessTools.Field(trackedType, "GuidanceHeadBoneIndex");
            _guidanceSmoothedHeadValidField = AccessTools.Field(trackedType, "GuidanceSmoothedHeadValid");
            _guidanceLastRawHeadValidField = AccessTools.Field(trackedType, "GuidanceLastRawHeadValid");
            _guidanceTargetVelocityValidField = AccessTools.Field(trackedType, "GuidanceTargetVelocityValid");
            _guidanceRouteTargetsField = AccessTools.Field(trackedType, "GuidanceRouteTargets");
            _guidanceConsumedTargetsField = AccessTools.Field(trackedType, "GuidanceConsumedTargets");
            _guidanceRouteReplanRequestedField = AccessTools.Field(trackedType, "GuidanceRouteReplanRequested");
            _guidanceNoProgressElapsedField = AccessTools.Field(trackedType, "GuidanceNoProgressElapsed");
            _autoguidanceReacquireCountdownField = AccessTools.Field(behaviorType, "_autoguidanceReacquireCountdown");
            _pendingCollisionContextsField = AccessTools.Field(behaviorType, "_pendingCollisionContexts");
            _earlyCollisionReactionsField = AccessTools.Field(behaviorType, "_earlyCollisionReactions");
            _pendingNativeMissileRemovalsField = AccessTools.Field(behaviorType, "_pendingNativeMissileRemovals");
            _pendingContinuationSpawnsField = AccessTools.Field(behaviorType, "_pendingContinuationSpawns");
            _assignAutoguidanceTargetsMethod = AccessTools.Method(
                behaviorType,
                "AssignAutoguidanceTargets",
                new[] { typeof(bool) });
            _isAutoguidanceRuntimeActiveMethod = AccessTools.Method(
                behaviorType,
                "IsAutoguidanceRuntimeActive",
                Type.EmptyTypes);
            _logMethod = AccessTools.Method(behaviorType, "Log", new[] { typeof(string) });

            if (_trackedMissilesField == null ||
                _guidanceTargetField == null ||
                _guidanceRouteTargetsField == null ||
                _guidanceConsumedTargetsField == null ||
                _pendingCollisionContextsField == null ||
                _earlyCollisionReactionsField == null ||
                _pendingNativeMissileRemovalsField == null ||
                _pendingContinuationSpawnsField == null ||
                _assignAutoguidanceTargetsMethod == null ||
                _isAutoguidanceRuntimeActiveMethod == null)
                return;

            MethodInfo impactMethod = AccessTools.Method(
                behaviorType,
                "HandleAutoguidanceAfterAgentImpact",
                new[] { trackedType, typeof(Agent) });
            MethodInfo impactPrefix = AccessTools.Method(
                typeof(AutoguidanceRetargetSafetyPatch),
                nameof(ImpactPrefix));
            if (impactMethod != null && impactPrefix != null)
            {
                try
                {
                    harmony.Patch(
                        impactMethod,
                        prefix: new HarmonyMethod(impactPrefix) { priority = Priority.First });
                }
                catch { }
            }

            MethodInfo removalPrefix = AccessTools.Method(
                typeof(AutoguidanceRetargetSafetyPatch),
                nameof(AgentRemovalPrefix));
            if (removalPrefix != null)
            {
                foreach (string callback in new[] { "OnEarlyAgentRemoved", "OnAgentRemoved", "OnAgentDeleted" })
                {
                    foreach (MethodInfo method in behaviorType
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(candidate => candidate.Name == callback && !candidate.IsAbstract))
                    {
                        try
                        {
                            harmony.Patch(
                                method,
                                prefix: new HarmonyMethod(removalPrefix) { priority = Priority.First });
                        }
                        catch { }
                    }
                }
            }

            MethodInfo displayPostfix = AccessTools.Method(
                typeof(AutoguidanceRetargetSafetyPatch),
                nameof(DisplayPostfix));
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
                            postfix: new HarmonyMethod(displayPostfix) { priority = int.MinValue });
                    }
                    catch { }
                }
            }

            MethodInfo clearPrefix = AccessTools.Method(
                typeof(AutoguidanceRetargetSafetyPatch),
                nameof(ClearPrefix));
            if (clearPrefix == null) return;

            foreach (string name in new[] { "StartGuidedShot", "ResetAll" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == name && !candidate.IsAbstract))
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
        }

        private static bool ImpactPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 2)
                return true;

            object tracked = __args[0];
            Agent impactedVictim = __args[1] as Agent;
            if (tracked == null || impactedVictim == null)
                return true;

            try
            {
                AddByReferenceIfMissing(
                    _guidanceConsumedTargetsField.GetValue(tracked) as IList,
                    impactedVictim);
                RemoveByReference(
                    _guidanceRouteTargetsField.GetValue(tracked) as IList,
                    impactedVictim);

                Agent currentTarget = _guidanceTargetField.GetValue(tracked) as Agent;
                if (!ReferenceEquals(currentTarget, impactedVictim))
                    return false;

                // Preserve the current fallback direction during the collision callback. The same
                // core route planner is invoked from OnPreDisplayMissionTick after collision-owned
                // queues have drained, independent of the optional target-loss reacquisition toggle.
                IList remainingRoute = _guidanceRouteTargetsField.GetValue(tracked) as IList;
                ClearTrackedTarget(
                    tracked,
                    requestReplan: remainingRoute == null || remainingRoute.Count == 0);
                _autoguidanceReacquireCountdownField?.SetValue(__instance, 0f);
                RequestRetarget(__instance);
            }
            catch
            {
                // Never re-enter native agent/skeleton collection after a partially processed hit.
                RequestRetarget(__instance);
            }

            return false;
        }

        private static void AgentRemovalPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null) return;
            Agent removed = __args.OfType<Agent>().FirstOrDefault();
            if (removed == null) return;

            bool targetCleared = false;
            try
            {
                IList trackedMissiles = _trackedMissilesField.GetValue(__instance) as IList;
                if (trackedMissiles != null)
                {
                    for (int i = 0; i < trackedMissiles.Count; i++)
                    {
                        object tracked = trackedMissiles[i];
                        if (tracked == null) continue;

                        RemoveByReference(_guidanceRouteTargetsField.GetValue(tracked) as IList, removed);
                        RemoveByReference(_guidanceConsumedTargetsField.GetValue(tracked) as IList, removed);

                        Agent target = _guidanceTargetField.GetValue(tracked) as Agent;
                        if (!ReferenceEquals(target, removed)) continue;

                        IList remainingRoute = _guidanceRouteTargetsField.GetValue(tracked) as IList;
                        ClearTrackedTarget(
                            tracked,
                            requestReplan: remainingRoute == null || remainingRoute.Count == 0);
                        targetCleared = true;
                    }
                }

                // Do not mutate _autoguidanceCandidates, _autoguidanceRankCandidates or their
                // parallel head-position list here. Route planning stores integer indices into those
                // arrays; deleting from only some of them corrupts all subsequent target selection.
                if (targetCleared)
                {
                    _autoguidanceReacquireCountdownField?.SetValue(__instance, 0f);
                    RequestRetarget(__instance);
                }
            }
            catch
            {
                if (targetCleared) RequestRetarget(__instance);
            }
        }

        private static void DisplayPostfix(object __instance)
        {
            if (__instance == null ||
                !RetargetStates.TryGetValue(__instance, out RetargetState state) ||
                state == null ||
                !state.Requested ||
                HasCollisionOwnedWork(__instance))
                return;

            try
            {
                object active = _isAutoguidanceRuntimeActiveMethod.Invoke(
                    __instance,
                    null);
                if (!(active is bool enabled) || !enabled)
                {
                    state.Requested = false;
                    return;
                }

                // clearExisting=false preserves every still-valid target and repairs only missiles
                // whose target was consumed, removed, or newly materialized targetless. Pending
                // replacements do not block the current assignment; each later materialization
                // requests one more pass through this single coordinator.
                _assignAutoguidanceTargetsMethod.Invoke(__instance, new object[] { false });
                TryLog(__instance, "Autoguidance targets safely reassigned after impact or continuation materialization.");
                state.Requested = false;
            }
            catch
            {
                state.Requested = true;
            }
        }

        private static bool HasCollisionOwnedWork(object instance)
        {
            return Count(_pendingCollisionContextsField, instance) > 0 ||
                   Count(_earlyCollisionReactionsField, instance) > 0 ||
                   Count(_pendingNativeMissileRemovalsField, instance) > 0;
        }

        private static int Count(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return int.MaxValue;
            try
            {
                object value = field.GetValue(instance);
                if (value is ICollection collection) return collection.Count;
                PropertyInfo property = value?.GetType().GetProperty(
                    "Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object count = property?.GetValue(value, null);
                return count is int integer ? integer : int.MaxValue;
            }
            catch
            {
                return int.MaxValue;
            }
        }

        private static void ClearTrackedTarget(object tracked, bool requestReplan)
        {
            if (tracked == null) return;

            _guidanceTargetField.SetValue(tracked, null);
            _guidanceHeadBoneIndexField?.SetValue(tracked, -1);
            _guidanceSmoothedHeadValidField?.SetValue(tracked, false);
            _guidanceLastRawHeadValidField?.SetValue(tracked, false);
            _guidanceTargetVelocityValidField?.SetValue(tracked, false);
            _guidanceRouteReplanRequestedField?.SetValue(tracked, requestReplan);
            _guidanceNoProgressElapsedField?.SetValue(tracked, 0f);
        }

        internal static void RequestRetarget(object instance)
        {
            if (instance == null) return;
            RetargetStates.GetOrCreateValue(instance).Requested = true;
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null) RetargetStates.Remove(__instance);
        }

        private static void AddByReferenceIfMissing(IList list, object item)
        {
            if (list == null || item == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], item)) return;
            }
            list.Add(item);
        }

        private static void RemoveByReference(IList list, object item)
        {
            if (list == null || item == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i], item))
                    list.RemoveAt(i);
            }
        }

        private static void TryLog(object instance, string message)
        {
            if (_logMethod == null || instance == null || string.IsNullOrEmpty(message)) return;
            try { _logMethod.Invoke(instance, new object[] { message }); }
            catch { }
        }
    }
}
