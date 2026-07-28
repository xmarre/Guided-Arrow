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

                ImpactCameraTransitionSafetyPatch.Install(
                    new Harmony("guidedarrow.progression.impact-camera-transition"),
                    behaviorType);
            }
            catch
            {
                // The stable core remains available if this narrow patch cannot be installed.
            }
        }
    }
}
