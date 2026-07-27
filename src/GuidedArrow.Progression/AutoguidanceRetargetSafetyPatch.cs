using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps Autoguidance target changes outside Bannerlord's native missile-collision callback.
    /// Repeated penetration can otherwise run nearby-agent collection, skeleton/head lookup and
    /// route assignment while the impacted missile and victim are still changing native lifetime.
    /// </summary>
    internal static class AutoguidanceRetargetSafetyPatch
    {
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
        private static FieldInfo _autoguidanceCandidatesField;
        private static FieldInfo _autoguidanceRankCandidatesField;
        private static FieldInfo _autoguidanceAssignedTargetsField;

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
            _autoguidanceCandidatesField = AccessTools.Field(behaviorType, "_autoguidanceCandidates");
            _autoguidanceRankCandidatesField = AccessTools.Field(behaviorType, "_autoguidanceRankCandidates");
            _autoguidanceAssignedTargetsField = AccessTools.Field(behaviorType, "_autoguidanceAssignedTargets");

            if (_trackedMissilesField == null ||
                _guidanceTargetField == null ||
                _guidanceRouteTargetsField == null ||
                _guidanceConsumedTargetsField == null)
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
            if (removalPrefix == null) return;

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

        private static bool ImpactPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 2)
                return false;

            object tracked = __args[0];
            Agent impactedVictim = __args[1] as Agent;
            if (tracked == null || impactedVictim == null)
                return false;

            try
            {
                AddByReferenceIfMissing(_guidanceConsumedTargetsField.GetValue(tracked) as IList, impactedVictim);
                RemoveByReference(_guidanceRouteTargetsField.GetValue(tracked) as IList, impactedVictim);

                Agent currentTarget = _guidanceTargetField.GetValue(tracked) as Agent;
                if (ReferenceEquals(currentTarget, impactedVictim))
                {
                    // Leave the existing fallback flight direction intact. The continuing native
                    // missile or next-tick synthetic continuation coasts safely until the normal
                    // display-tick reacquisition path chooses and initializes the next target.
                    _guidanceTargetField.SetValue(tracked, null);
                    _guidanceHeadBoneIndexField?.SetValue(tracked, -1);
                    _guidanceSmoothedHeadValidField?.SetValue(tracked, false);
                    _guidanceLastRawHeadValidField?.SetValue(tracked, false);
                    _guidanceTargetVelocityValidField?.SetValue(tracked, false);

                    IList route = _guidanceRouteTargetsField.GetValue(tracked) as IList;
                    _guidanceRouteReplanRequestedField?.SetValue(tracked, route == null || route.Count == 0);
                    _guidanceNoProgressElapsedField?.SetValue(tracked, 0f);
                    _autoguidanceReacquireCountdownField?.SetValue(__instance, 0f);
                }
            }
            catch
            {
                // Skipping the core's collision-time retarget remains safer than re-entering
                // native agent/skeleton queries after partial reflected state changes.
            }

            return false;
        }

        private static void AgentRemovalPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null) return;
            Agent removed = __args.OfType<Agent>().FirstOrDefault();
            if (removed == null) return;

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

                        _guidanceTargetField.SetValue(tracked, null);
                        _guidanceHeadBoneIndexField?.SetValue(tracked, -1);
                        _guidanceSmoothedHeadValidField?.SetValue(tracked, false);
                        _guidanceLastRawHeadValidField?.SetValue(tracked, false);
                        _guidanceTargetVelocityValidField?.SetValue(tracked, false);
                        _guidanceRouteReplanRequestedField?.SetValue(tracked, true);
                        _guidanceNoProgressElapsedField?.SetValue(tracked, 0f);
                    }
                }

                RemoveByReference(_autoguidanceCandidatesField?.GetValue(__instance) as IList, removed);
                RemoveByReference(_autoguidanceRankCandidatesField?.GetValue(__instance) as IList, removed);
                RemoveByReference(_autoguidanceAssignedTargetsField?.GetValue(__instance) as IList, removed);
                _autoguidanceReacquireCountdownField?.SetValue(__instance, 0f);
            }
            catch
            {
                // Agent removal must never be replaced by a sidecar failure.
            }
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
    }
}
