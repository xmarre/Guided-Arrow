using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Collapses the legacy emergency continuation quarantine to one completed display boundary.
    /// The protected-memory crash was caused by the terminal launch bridge's native data override;
    /// retaining the old six-boundary/150 ms delay only serializes split volleys and produces visible
    /// projectile and camera stalls.
    /// </summary>
    internal static class ContinuationReleaseGateRelaxationPatch
    {
        private static FieldInfo _pendingContinuationSpawnsField;
        private static object _behaviorStates;
        private static object _itemStates;
        private static MethodInfo _behaviorGetOrCreateMethod;
        private static MethodInfo _itemGetOrCreateMethod;
        private static FieldInfo _completedDisplayTicksField;
        private static FieldInfo _firstReleasedDisplayTickField;
        private static FieldInfo _notBeforeTimestampField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type releaseType = typeof(NativeContinuationSourceReleasePatch);
            Type behaviorStateType = releaseType.GetNestedType(
                "BehaviorState",
                BindingFlags.NonPublic);
            Type itemStateType = releaseType.GetNestedType(
                "ItemState",
                BindingFlags.NonPublic);

            _pendingContinuationSpawnsField = AccessTools.Field(
                behaviorType,
                "_pendingContinuationSpawns");
            FieldInfo behaviorStatesField = AccessTools.Field(
                releaseType,
                "BehaviorStates");
            FieldInfo itemStatesField = AccessTools.Field(
                releaseType,
                "ItemStates");

            _behaviorStates = behaviorStatesField?.GetValue(null);
            _itemStates = itemStatesField?.GetValue(null);
            _behaviorGetOrCreateMethod = _behaviorStates?.GetType().GetMethod(
                "GetOrCreateValue",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(object) },
                null);
            _itemGetOrCreateMethod = _itemStates?.GetType().GetMethod(
                "GetOrCreateValue",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(object) },
                null);
            _completedDisplayTicksField = behaviorStateType == null
                ? null
                : AccessTools.Field(behaviorStateType, "CompletedDisplayTicks");
            _firstReleasedDisplayTickField = itemStateType == null
                ? null
                : AccessTools.Field(itemStateType, "FirstReleasedDisplayTick");
            _notBeforeTimestampField = itemStateType == null
                ? null
                : AccessTools.Field(itemStateType, "NotBeforeTimestamp");

            MethodInfo workerPrefix = AccessTools.Method(
                releaseType,
                "WorkerPrefix");
            MethodInfo relaxationPrefix = AccessTools.Method(
                typeof(ContinuationReleaseGateRelaxationPatch),
                nameof(ReleaseGatePrefix));

            if (_pendingContinuationSpawnsField == null ||
                _behaviorStates == null ||
                _itemStates == null ||
                _behaviorGetOrCreateMethod == null ||
                _itemGetOrCreateMethod == null ||
                _completedDisplayTicksField == null ||
                _firstReleasedDisplayTickField == null ||
                _notBeforeTimestampField == null ||
                workerPrefix == null ||
                relaxationPrefix == null)
                return;

            try
            {
                harmony.Patch(
                    workerPrefix,
                    prefix: new HarmonyMethod(relaxationPrefix)
                    {
                        priority = int.MaxValue
                    });
            }
            catch
            {
                // The legacy release gate remains unchanged if its private layout changes.
            }
        }

        private static void ReleaseGatePrefix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count == 0 || queue[0] == null)
                    return;

                object behaviorState = _behaviorGetOrCreateMethod.Invoke(
                    _behaviorStates,
                    new[] { __instance });
                object itemState = _itemGetOrCreateMethod.Invoke(
                    _itemStates,
                    new[] { queue[0] });
                if (behaviorState == null || itemState == null)
                    return;

                long firstReleasedDisplayTick = Convert.ToInt64(
                    _firstReleasedDisplayTickField.GetValue(itemState));
                long completedDisplayTicks = Convert.ToInt64(
                    _completedDisplayTicksField.GetValue(behaviorState));

                // Keep one real display boundary after exact source release. Once that boundary has
                // completed, satisfy the obsolete six-boundary and wall-clock portions of the gate.
                if (firstReleasedDisplayTick < 0L ||
                    completedDisplayTicks <= firstReleasedDisplayTick)
                    return;

                long acceleratedTick = firstReleasedDisplayTick > long.MaxValue - 16L
                    ? long.MaxValue
                    : firstReleasedDisplayTick + 16L;
                if (completedDisplayTicks < acceleratedTick)
                {
                    _completedDisplayTicksField.SetValue(
                        behaviorState,
                        acceleratedTick);
                }

                _notBeforeTimestampField.SetValue(itemState, 0L);
            }
            catch
            {
                // The legacy release gate remains fail-safe for this pass.
            }
        }
    }
}
