using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Stops Guided Arrow from re-entering Bannerlord's native missile-removal API after a native
    /// PassThrough reaction has already completed the guided penetration budget.
    ///
    /// The core used QueueNativeMissileRemoval only from that budget-exhausted PassThrough branch.
    /// It then removed the final tracked entry but left the projectile-camera index pointing at the
    /// discarded missile. On the following mission tick the queued native removal or stale camera
    /// ownership could touch the already-transitioned native missile.
    ///
    /// Once Guided Arrow's budget is exhausted, ownership is now relinquished to Bannerlord/TOR:
    /// the projectile stops being guided, but its native PassThrough behaviour is not forcibly
    /// deleted. This also preserves native ability effects instead of overriding them.
    /// </summary>
    internal static class PassThroughBudgetSafetyPatch
    {
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _missileField;
        private static FieldInfo _missileIndexField;
        private static FieldInfo _cameraMissileIndexField;
        private static FieldInfo _cameraFrameValidField;
        private static FieldInfo _pendingNativeMissileRemovalsField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _missileField = AccessTools.Field(behaviorType, "_missile");
            _missileIndexField = AccessTools.Field(behaviorType, "_missileIndex");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _cameraFrameValidField = AccessTools.Field(behaviorType, "_cameraFrameValid");
            _pendingNativeMissileRemovalsField = AccessTools.Field(
                behaviorType,
                "_pendingNativeMissileRemovals");

            MethodInfo queueRemoval = AccessTools.Method(
                behaviorType,
                "QueueNativeMissileRemoval",
                new[] { trackedType });
            MethodInfo removeTracked = AccessTools.Method(
                behaviorType,
                "RemoveTrackedMissile",
                new[] { trackedType, typeof(bool) });
            MethodInfo skipQueuePrefix = AccessTools.Method(
                typeof(PassThroughBudgetSafetyPatch),
                nameof(SkipQueuePrefix));
            MethodInfo repairPostfix = AccessTools.Method(
                typeof(PassThroughBudgetSafetyPatch),
                nameof(RepairEmptyOwnershipPostfix));

            if (queueRemoval != null && skipQueuePrefix != null)
            {
                try
                {
                    harmony.Patch(
                        queueRemoval,
                        prefix: new HarmonyMethod(skipQueuePrefix) { priority = Priority.First });
                }
                catch { }
            }

            if (removeTracked != null && repairPostfix != null)
            {
                try
                {
                    harmony.Patch(
                        removeTracked,
                        postfix: new HarmonyMethod(repairPostfix) { priority = Priority.Last });
                }
                catch { }
            }
        }

        private static bool SkipQueuePrefix(object __instance)
        {
            // QueueNativeMissileRemoval has exactly one caller in the locked core: the native
            // PassThrough branch where the configured guided penetration budget is exhausted.
            // Bannerlord already owns the continuing projectile. Do not call RemoveMissileAsClient
            // against that transitioned native object on a later mission tick.
            try
            {
                if (__instance != null &&
                    _pendingNativeMissileRemovalsField?.GetValue(__instance) is IList pending)
                    pending.Clear();
            }
            catch { }

            return false;
        }

        private static void RepairEmptyOwnershipPostfix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                IList tracked = _trackedMissilesField?.GetValue(__instance) as IList;
                if (tracked == null || tracked.Count != 0) return;

                // RemoveTrackedMissile already clears the leader wrapper/index, but the locked core
                // leaves _cameraMissileIndex unchanged. Keep all managed ownership fields coherent
                // before the next mission/display tick decides whether to return or start a killcam.
                _missileField?.SetValue(__instance, null);
                _missileIndexField?.SetValue(__instance, -1);
                _cameraMissileIndexField?.SetValue(__instance, -1);
                _cameraFrameValidField?.SetValue(__instance, false);

                if (_pendingNativeMissileRemovalsField?.GetValue(__instance) is IList pending)
                    pending.Clear();
            }
            catch
            {
                // The locked core remains authoritative if reflected fields change in a later build.
            }
        }
    }
}
