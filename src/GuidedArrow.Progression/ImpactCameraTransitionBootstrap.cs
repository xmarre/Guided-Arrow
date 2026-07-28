using System;
using System.Reflection;
using HarmonyLib;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace GuidedArrow.Progression
{
    internal static class ImpactCameraTransitionBootstrap
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void InitializeModule()
        {
            try
            {
                Assembly guidedArrowAssembly = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "GuidedArrow")
                    {
                        guidedArrowAssembly = assembly;
                        break;
                    }
                }

                Type behaviorType = guidedArrowAssembly?.GetType(
                    "GuidedArrow.GuidedArrowBehavior",
                    false);
                if (behaviorType == null) return;

                Harmony harmony = new Harmony("guidedarrow.progression.post-impact-native-boundary");
                ImpactCameraTransitionSafetyPatch.Install(harmony, behaviorType);
                EarlyCollisionReactionDeferralPatch.Install(harmony, behaviorType);
                ImpactCinematicDeferralPatch.Install(harmony, behaviorType);
            }
            catch
            {
                // The stable core remains available if these narrow patches cannot be installed.
            }
        }
    }
}
