using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Limits how many original native/TOR missiles join Guided Arrow's controlled group.
    /// Excess native missiles are not removed from the mission; they simply continue under
    /// their original mod/native behaviour and retain every visual and impact callback.
    /// </summary>
    internal static class NativeSiblingMasteryPatch
    {
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _nativeSplitBatchDetectedField;
        private static FieldInfo _trackedSyntheticField;
        private static FieldInfo _trackedFormationSlotField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _leaderIndexField;
        private static FieldInfo _cameraIndexField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo acquire = AccessTools.Method(behaviorType, "AcquireSplitShotSiblings");
            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (acquire == null || trackedType == null) return;

            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _nativeSplitBatchDetectedField = AccessTools.Field(behaviorType, "_nativeSplitBatchDetected");
            _leaderIndexField = AccessTools.Field(behaviorType, "_missileIndex");
            _cameraIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _trackedSyntheticField = AccessTools.Field(trackedType, "SyntheticProjectile");
            _trackedFormationSlotField = AccessTools.Field(trackedType, "FormationSlot");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");

            if (_trackedMissilesField == null || _trackedSyntheticField == null || _trackedIndexField == null) return;

            try
            {
                harmony.Patch(
                    acquire,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(NativeSiblingMasteryPatch), nameof(Postfix))));
            }
            catch { }
        }

        private static void Postfix(object __instance)
        {
            if (!ProgressionService.Enabled || __instance == null) return;

            int cap = ProgressionBalance.NativeGuidedProjectileCap(ProgressionService.Level(SkillId.SplitAwareness));
            IList tracked;
            try { tracked = _trackedMissilesField.GetValue(__instance) as IList; }
            catch { return; }
            if (tracked == null || tracked.Count <= cap) return;

            int leaderIndex = ReadInt(_leaderIndexField, __instance, -1);
            int cameraIndex = ReadInt(_cameraIndexField, __instance, -1);
            int nativeKept = 0;

            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                object item = tracked[i];
                if (item == null || IsSynthetic(item)) continue;

                int index = ReadInt(_trackedIndexField, item, -1);
                bool protectedItem = index == leaderIndex || index == cameraIndex;
                if (protectedItem) continue;

                int earlierNative = 0;
                for (int j = 0; j < i; j++)
                {
                    object earlier = tracked[j];
                    if (earlier != null && !IsSynthetic(earlier)) earlierNative++;
                }
                if (earlierNative >= cap) tracked.RemoveAt(i);
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item == null) continue;
                if (!IsSynthetic(item)) nativeKept++;
                if (_trackedFormationSlotField != null)
                {
                    try { _trackedFormationSlotField.SetValue(item, i); }
                    catch { }
                }
            }

            if (_nativeSplitBatchDetectedField != null)
            {
                try { _nativeSplitBatchDetectedField.SetValue(__instance, nativeKept > 1); }
                catch { }
            }
        }

        private static bool IsSynthetic(object tracked)
        {
            try { return tracked != null && (bool)_trackedSyntheticField.GetValue(tracked); }
            catch { return false; }
        }

        private static int ReadInt(FieldInfo field, object instance, int fallback)
        {
            if (field == null || instance == null) return fallback;
            try { return (int)field.GetValue(instance); }
            catch { return fallback; }
        }
    }
}
