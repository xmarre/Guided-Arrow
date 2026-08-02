using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    internal static class GuidedArrowPatches
    {
        internal static void Install(Harmony harmony)
        {
            McmMainSettingsIntegrationPatch.Install(harmony);

            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => candidate.GetName().Name == "GuidedArrow");
            if (assembly == null) return;

            Type settingsType = assembly.GetType("GuidedArrow.Settings", false);
            Type behaviorType = assembly.GetType("GuidedArrow.GuidedArrowBehavior", false);
            if (behaviorType == null) return;

            if (settingsType != null)
            {
                ProgressionRuntimeSettingsPatch.Install(harmony, behaviorType, settingsType);
                GuidanceTimeControlPatch.Install(harmony, behaviorType, settingsType);
                SplitLaunchOrderingPatch.Install(harmony, behaviorType, settingsType);
            }

            NativeVolleyPenetrationIsolationPatch.Install(harmony, behaviorType);
            ExactEarlyCollisionReactionPatch.Install(harmony, behaviorType);
            // All penetration corrections remain behavior-scoped. No Bannerlord Mission callback
            // is patched, preserving native command voices and mission presentation teardown.
            MissileLifetimeSafetyPatch.Install(harmony, behaviorType);
            AutoguidanceRetargetSafetyPatch.Install(harmony, behaviorType);
            SiegeAutoguidanceVisibilityPatch.Install(harmony, behaviorType);
            CameraControlPatch.Install(harmony, behaviorType);

            // A confirmed-kill callback sourced from OnMissileHitAlreadyDead can precede the
            // authoritative terminal collision reaction for the same still-live missile. Treating
            // that callback as a duplicate-continuation marker suppresses legitimate penetration.
            TerminalCollisionFailClosedPatch.Install(harmony, behaviorType);
            PenetrationContinuationSafetyPatch.Install(harmony, behaviorType);
            SafeContinuationMissionBoundaryPatch.Install(harmony, behaviorType);
            FinalMissileTerminalHandoffPatch.Install(harmony, behaviorType);
            ProgressionTerminalXpPatch.Install(harmony, behaviorType);

            if (settingsType != null)
                NativeVolleyAugmentationPatch.Install(harmony, behaviorType, settingsType);
        }
    }
}
