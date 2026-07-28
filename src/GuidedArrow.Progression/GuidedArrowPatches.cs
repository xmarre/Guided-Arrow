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
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => candidate.GetName().Name == "GuidedArrow");
            if (assembly == null) return;

            Type settingsType = assembly.GetType("GuidedArrow.Settings", false);
            Type behaviorType = assembly.GetType("GuidedArrow.GuidedArrowBehavior", false);
            if (behaviorType == null) return;

            // Progression settings are applied once for the complete guided-shot lifetime.
            // ConcentratedImpactSafetyPatch then expands the core's 32-entry impact correlation
            // queues and prevents repeated native victim sampling during same-target bursts.
            if (settingsType != null)
                ProgressionRuntimeSettingsPatch.Install(harmony, behaviorType, settingsType);

            ConcentratedImpactSafetyPatch.Install(harmony, behaviorType);
            NativeVolleyPenetrationIsolationPatch.Install(harmony, behaviorType);
            MissileLifetimeSafetyPatch.Install(harmony, behaviorType);
            AutoguidanceRetargetSafetyPatch.Install(harmony, behaviorType);
            PenetrationContinuationSafetyPatch.Install(harmony, behaviorType);
            FinalMissileTerminalHandoffPatch.Install(harmony, behaviorType);
            ProgressionTerminalXpPatch.Install(harmony, behaviorType);

            if (settingsType != null)
                NativeVolleyAugmentationPatch.Install(harmony, behaviorType, settingsType);
        }
    }
}
