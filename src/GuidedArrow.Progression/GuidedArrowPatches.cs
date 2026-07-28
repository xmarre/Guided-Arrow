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

            // Preserve the exact currently reproducing runtime. The diagnostic trace below records
            // concentrated-volley lifecycle transitions without suppressing or mutating behavior.
            if (settingsType != null)
                ProgressionRuntimeSettingsPatch.Install(harmony, behaviorType, settingsType);

            PenetrationLifecycleIsolationPatch.Install(harmony, behaviorType);
            NativeVolleyPenetrationIsolationPatch.Install(harmony, behaviorType);
            MissileLifetimeSafetyPatch.Install(harmony, behaviorType);
            AutoguidanceRetargetSafetyPatch.Install(harmony, behaviorType);
            PenetrationContinuationSafetyPatch.Install(harmony, behaviorType);
            FinalMissileTerminalHandoffPatch.Install(harmony, behaviorType);
            ProgressionTerminalXpPatch.Install(harmony, behaviorType);

            if (settingsType != null)
                NativeVolleyAugmentationPatch.Install(harmony, behaviorType, settingsType);

            ConcentratedVolleyTracePatch.Install(harmony, behaviorType, settingsType);
        }
    }
}
