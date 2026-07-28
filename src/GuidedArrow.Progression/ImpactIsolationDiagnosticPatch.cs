using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Diagnostic-only boundary that suppresses only GuidedArrowBehavior.OnMissileHit.
    /// All subsequent mission ticks, display ticks, collision-reaction and missile-removal callbacks
    /// remain enabled. This separates the original impact handler from the later callback set that
    /// was also suppressed by the preceding hard-bypass diagnostic.
    /// </summary>
    internal static class ImpactIsolationDiagnosticPatch
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

                Install(
                    new Harmony("guidedarrow.progression.onmissilehit-only-diagnostic"),
                    behaviorType);
            }
            catch
            {
                // A diagnostic patch must never block module loading.
            }
        }

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo impactPrefix = AccessTools.Method(
                typeof(ImpactIsolationDiagnosticPatch),
                nameof(ImpactPrefix));
            if (impactPrefix == null) return;

            foreach (MethodInfo method in FindMethods(behaviorType, "OnMissileHit"))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(impactPrefix) { priority = int.MaxValue });
                }
                catch { }
            }
        }

        private static bool ImpactPrefix()
        {
            // Diagnostic invariant: only the verified core's OnMissileHit processing is skipped.
            return false;
        }

        private static IEnumerable<MethodInfo> FindMethods(Type behaviorType, string methodName)
        {
            foreach (MethodInfo method in behaviorType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == methodName && !method.IsAbstract)
                    yield return method;
            }
        }
    }
}
