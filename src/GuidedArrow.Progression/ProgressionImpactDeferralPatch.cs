using System;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Disables the legacy synchronous progression postfix on Bannerlord's Agent-hit callback.
    /// Progression accounting is performed from FullImpactReplayDeferralPatch after the complete
    /// Guided Arrow missile-hit handler has been replayed outside native collision processing.
    /// </summary>
    internal static class ProgressionImpactDeferralPatch
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void InitializeModule()
        {
            try
            {
                MethodInfo legacyProgressionPostfix = AccessTools.Method(
                    typeof(GuidedArrowPatches),
                    "OnAgentHitPostfix");
                MethodInfo disablePrefix = AccessTools.Method(
                    typeof(ProgressionImpactDeferralPatch),
                    nameof(DisableLegacyProgressionPostfix));
                if (legacyProgressionPostfix == null || disablePrefix == null) return;

                new Harmony("guidedarrow.progression.disable-native-agent-hit-accounting").Patch(
                    legacyProgressionPostfix,
                    prefix: new HarmonyMethod(disablePrefix) { priority = int.MaxValue });
            }
            catch
            {
                // The stable core remains available if this compatibility patch cannot be installed.
            }
        }

        private static bool DisableLegacyProgressionPostfix()
        {
            return false;
        }
    }
}
