using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Diagnostic-only hard boundary used to determine whether the protected-memory failure occurs
    /// inside Guided Arrow's impact callbacks or outside the mod after Bannerlord reports the hit.
    ///
    /// Once the first GuidedArrowBehavior.OnMissileHit callback is entered, the original callback and
    /// all later core tick, display-tick, collision-reaction and missile-removal callbacks for that
    /// behavior instance are suppressed. No Guided Arrow continuation, retarget, camera-return or
    /// tracked-missile cleanup code is allowed to execute after that impact.
    /// </summary>
    internal static class ImpactIsolationDiagnosticPatch
    {
        private sealed class Marker { }

        private static readonly ConditionalWeakTable<object, Marker> BypassedInstances =
            new ConditionalWeakTable<object, Marker>();

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo impactPrefix = AccessTools.Method(
                typeof(ImpactIsolationDiagnosticPatch),
                nameof(ImpactPrefix));
            MethodInfo suppressAfterImpactPrefix = AccessTools.Method(
                typeof(ImpactIsolationDiagnosticPatch),
                nameof(SuppressAfterImpactPrefix));
            MethodInfo resetPostfix = AccessTools.Method(
                typeof(ImpactIsolationDiagnosticPatch),
                nameof(ResetPostfix));
            if (impactPrefix == null || suppressAfterImpactPrefix == null || resetPostfix == null)
                return;

            foreach (MethodInfo method in FindMethods(behaviorType, "OnMissileHit"))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(impactPrefix) { priority = Priority.First });
                }
                catch { }
            }

            foreach (string methodName in new[]
            {
                "OnMissionTick",
                "OnPreDisplayMissionTick",
                "OnMissileCollisionReaction",
                "OnMissileRemoved"
            })
            {
                foreach (MethodInfo method in FindMethods(behaviorType, methodName))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(suppressAfterImpactPrefix)
                            {
                                priority = Priority.First
                            });
                    }
                    catch { }
                }
            }

            foreach (MethodInfo method in FindMethods(behaviorType, "ResetAll"))
            {
                try
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(resetPostfix) { priority = Priority.Last });
                }
                catch { }
            }
        }

        internal static bool IsBypassed(object instance)
        {
            return instance != null && BypassedInstances.TryGetValue(instance, out _);
        }

        private static bool ImpactPrefix(object __instance)
        {
            if (__instance != null && !BypassedInstances.TryGetValue(__instance, out _))
            {
                try { BypassedInstances.Add(__instance, new Marker()); }
                catch (ArgumentException) { }
            }

            // Diagnostic invariant: none of the verified core's impact processing runs.
            return false;
        }

        private static bool SuppressAfterImpactPrefix(object __instance)
        {
            return !IsBypassed(__instance);
        }

        private static void ResetPostfix(object __instance)
        {
            if (__instance != null)
                BypassedInstances.Remove(__instance);
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
