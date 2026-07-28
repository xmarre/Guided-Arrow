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

            // Crash-containment mode:
            // progression remains available in campaign/UI data, but it no longer patches any
            // mission callback, runtime setting, penetration gate, target gate, split gate or
            // impact/XP accounting path. This makes an enabled progression save execute the
            // same Guided Arrow mission runtime as progression disabled.
            NativeVolleyPenetrationIsolationPatch.Install(harmony, behaviorType);
            MissileLifetimeSafetyPatch.Install(harmony, behaviorType);
            AutoguidanceRetargetSafetyPatch.Install(harmony, behaviorType);
            PenetrationContinuationSafetyPatch.Install(harmony, behaviorType);
            FinalMissileTerminalHandoffPatch.Install(harmony, behaviorType);

            if (settingsType != null)
                NativeVolleyAugmentationPatch.Install(harmony, behaviorType, settingsType);
        }
    }
}
