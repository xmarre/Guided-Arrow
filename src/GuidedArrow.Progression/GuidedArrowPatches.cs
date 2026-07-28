using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    internal static class GuidedArrowPatches
    {
        private static FieldInfo _cameraMissileIndexField;
        private static PropertyInfo _trackedIndexProperty;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedFormationSlotField;
        private static FieldInfo _trackedSyntheticField;
        private static FieldInfo _trackedPenetrationsUsedField;

        internal static void Install(Harmony harmony)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => candidate.GetName().Name == "GuidedArrow");
            if (assembly == null) return;

            Type settingsType = assembly.GetType("GuidedArrow.Settings", false);
            Type behaviorType = assembly.GetType("GuidedArrow.GuidedArrowBehavior", false);
            if (behaviorType == null) return;

            PatchBehavior(harmony, behaviorType, settingsType);
        }

        private static void PatchBehavior(Harmony harmony, Type behaviorType, Type settingsType)
        {
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType != null)
            {
                _trackedFormationSlotField = AccessTools.Field(trackedType, "FormationSlot");
                _trackedSyntheticField = AccessTools.Field(trackedType, "SyntheticProjectile");
                _trackedPenetrationsUsedField = AccessTools.Field(trackedType, "PenetrationsUsed");
            }

            PatchBoolResult(harmony, behaviorType, "IsSplitSiblingAcquisitionOpen", nameof(SplitSiblingOpenPostfix));
            PatchBoolResult(harmony, behaviorType, "ShouldBreakFormationForAutoguidance", nameof(BreakFormationPostfix));
            PatchBoolResult(harmony, behaviorType, "IsAgentPenetrationOverrideEnabled", nameof(PenetrationEnabledPostfix));
            PatchBoolResult(harmony, behaviorType, "IsAutoguidanceEligibleMissile", nameof(AutoguidanceEligiblePostfix));
            PatchBoolResult(harmony, behaviorType, "HasRemainingAgentPenetration", nameof(RemainingPenetrationPostfix));

            if (settingsType != null)
                ProgressionRuntimeSettingsPatch.Install(harmony, behaviorType, settingsType);

            NativeSiblingMasteryPatch.Install(harmony, behaviorType);
            NativeVolleyPenetrationIsolationPatch.Install(harmony, behaviorType);
            MissileLifetimeSafetyPatch.Install(harmony, behaviorType);
            AutoguidanceRetargetSafetyPatch.Install(harmony, behaviorType);
            PenetrationContinuationSafetyPatch.Install(harmony, behaviorType);
            FinalMissileTerminalHandoffPatch.Install(harmony, behaviorType);

            // Deliberately do not install ProgressionHitAccountingPatch in this isolation build.
            // All purchased-skill effects remain active, but no progression code runs from the
            // OnMissileHit callback or the following display tick. This separates the mastery
            // gameplay layer from battle-XP accounting without reverting the terminal-handoff fix.

            if (settingsType != null)
                NativeVolleyAugmentationPatch.Install(harmony, behaviorType, settingsType);
        }

        private static void PatchBoolResult(Harmony harmony, Type type, string methodName, string postfixName)
        {
            MethodInfo postfix = AccessTools.Method(typeof(GuidedArrowPatches), postfixName);
            if (postfix == null) return;

            foreach (MethodInfo method in type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == methodName && candidate.ReturnType == typeof(bool)))
            {
                try { harmony.Patch(method, postfix: new HarmonyMethod(postfix)); }
                catch { }
            }
        }

        private static void SplitSiblingOpenPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && ProgressionService.Level(SkillId.SplitAwareness) <= 0)
                __result = false;
        }

        private static void BreakFormationPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && ProgressionService.Level(SkillId.ManyHeadedFlight) < 3)
                __result = false;
        }

        private static void PenetrationEnabledPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && ProgressionService.Level(SkillId.DrivingShot) <= 0)
                __result = false;
        }

        private static void AutoguidanceEligiblePostfix(object __instance, object[] __args, ref bool __result)
        {
            if (!__result || !ProgressionService.Enabled || __instance == null || __args == null) return;

            object tracked = __args.FirstOrDefault(
                argument => argument != null && argument.GetType().Name == "TrackedMissile");
            if (tracked == null) return;

            int synchronizedLevel = ProgressionService.Level(SkillId.SynchronizedHunt);
            if (synchronizedLevel <= 0)
            {
                if (_cameraMissileIndexField == null) return;
                int cameraIndex;
                try { cameraIndex = (int)_cameraMissileIndexField.GetValue(__instance); }
                catch { return; }

                int trackedIndex = ReadTrackedIndex(tracked);
                if (trackedIndex != cameraIndex) __result = false;
                return;
            }

            if (_trackedFormationSlotField == null) return;
            try
            {
                int slot = (int)_trackedFormationSlotField.GetValue(tracked);
                if (slot >= ProgressionBalance.SynchronizedProjectileCap(synchronizedLevel))
                    __result = false;
            }
            catch { }
        }

        private static void RemainingPenetrationPostfix(object[] __args, ref bool __result)
        {
            if (!__result ||
                !ProgressionService.Enabled ||
                __args == null ||
                __args.Length == 0 ||
                __args[0] == null)
                return;

            object tracked = __args[0];
            if (_trackedSyntheticField == null || _trackedPenetrationsUsedField == null) return;

            try
            {
                if (!(bool)_trackedSyntheticField.GetValue(tracked)) return;
                int needleLevel = ProgressionService.Level(SkillId.NeedleStorm);
                int cap = ProgressionBalance.NeedleStormPenetrationCap(needleLevel);
                int used = (int)_trackedPenetrationsUsedField.GetValue(tracked);
                if (cap <= 0 || used >= cap) __result = false;
            }
            catch { }
        }

        private static int ReadTrackedIndex(object tracked)
        {
            if (tracked == null) return -1;
            if (_trackedIndexProperty == null && _trackedIndexField == null)
            {
                Type type = tracked.GetType();
                _trackedIndexProperty = type.GetProperty(
                    "Index",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _trackedIndexField = type.GetField(
                    "Index",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            try
            {
                if (_trackedIndexProperty != null)
                    return (int)_trackedIndexProperty.GetValue(tracked, null);
                if (_trackedIndexField != null)
                    return (int)_trackedIndexField.GetValue(tracked);
            }
            catch { }
            return -1;
        }
    }
}
