using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps true native/TOR ability volleys on their own penetration path while allowing
    /// ordinary Guided Arrow shots and generated standalone followers to use the controlled
    /// continuation system.
    /// </summary>
    internal static class NativeVolleyPenetrationIsolationPatch
    {
        private sealed class ShotState
        {
            internal int Generation = -1;
            internal bool NativeVolleyObserved;
        }

        private static readonly ConditionalWeakTable<object, ShotState> States =
            new ConditionalWeakTable<object, ShotState>();

        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _trackedSyntheticField;
        private static FieldInfo _generationField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo spawnMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            MethodInfo closeMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "CloseSplitSiblingAcquisition" &&
                    method.GetParameters().Length == 1);
            if (spawnMethod == null || closeMethod == null) return;

            Type trackedType = spawnMethod.GetParameters()[0].ParameterType;
            _trackedMissilesField = AccessTools.Field(
                behaviorType,
                "_trackedMissiles");
            _trackedSyntheticField = AccessTools.Field(
                trackedType,
                "SyntheticProjectile");
            _generationField = AccessTools.Field(
                behaviorType,
                "_activeShotGeneration");
            if (_trackedMissilesField == null ||
                _trackedSyntheticField == null ||
                _generationField == null)
                return;

            MethodInfo spawnPrefix = AccessTools.Method(
                typeof(NativeVolleyPenetrationIsolationPatch),
                nameof(SpawnPrefix));
            MethodInfo observePostfix = AccessTools.Method(
                typeof(NativeVolleyPenetrationIsolationPatch),
                nameof(ObservePostfix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(NativeVolleyPenetrationIsolationPatch),
                nameof(ClearPrefix));
            if (spawnPrefix == null ||
                observePostfix == null ||
                clearPrefix == null)
                return;

            try
            {
                harmony.Patch(
                    spawnMethod,
                    prefix: new HarmonyMethod(spawnPrefix)
                    {
                        priority = Priority.First
                    });

                // At acquisition close the complete launch group is known. Remember whether it
                // contained multiple original native/TOR missiles for the rest of this generation.
                harmony.Patch(
                    closeMethod,
                    postfix: new HarmonyMethod(observePostfix)
                    {
                        priority = Priority.Last
                    });

                foreach (string methodName in new[] { "StartGuidedShot", "ResetAll" })
                {
                    foreach (MethodInfo method in behaviorType
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(candidate =>
                            candidate.Name == methodName &&
                            !candidate.IsAbstract))
                    {
                        try
                        {
                            harmony.Patch(
                                method,
                                prefix: new HarmonyMethod(clearPrefix)
                                {
                                    priority = Priority.First
                                });
                        }
                        catch { }
                    }
                }
            }
            catch
            {
                // A changed core layout leaves the verified runtime untouched.
            }
        }

        internal static bool ShouldBlockSyntheticContinuation(
            object behavior,
            object tracked)
        {
            if (behavior == null || tracked == null) return true;
            if (IsSynthetic(tracked)) return false;

            try
            {
                ObserveNativeVolley(behavior);
                int generation = (int)_generationField.GetValue(behavior);
                ShotState state = GetState(behavior, generation);
                return state.NativeVolleyObserved;
            }
            catch
            {
                // Unknown ownership must remain with the native/TOR source.
                return true;
            }
        }

        private static bool SpawnPrefix(
            object __instance,
            object[] __args,
            ref bool __result)
        {
            if (__instance == null ||
                __args == null ||
                __args.Length == 0 ||
                __args[0] == null)
                return true;

            if (!ShouldBlockSyntheticContinuation(__instance, __args[0]))
                return true;

            // QueuePenetrationContinuation may still be reached by a future core shape. Do not
            // create a custom missile from an original native/TOR ability projectile.
            if (__args.Length >= 3)
                __args[2] = null;
            __result = false;
            return false;
        }

        private static void ObservePostfix(object __instance)
        {
            ObserveNativeVolley(__instance);
        }

        private static void ObserveNativeVolley(object instance)
        {
            if (instance == null) return;

            try
            {
                int generation = (int)_generationField.GetValue(instance);
                ShotState state = GetState(instance, generation);
                if (state.NativeVolleyObserved) return;

                IList tracked =
                    _trackedMissilesField.GetValue(instance) as IList;
                if (tracked == null) return;

                int originalNativeCount = 0;
                for (int i = 0; i < tracked.Count; i++)
                {
                    object item = tracked[i];
                    if (item == null || IsSynthetic(item)) continue;

                    originalNativeCount++;
                    if (originalNativeCount < 2) continue;

                    state.NativeVolleyObserved = true;
                    return;
                }
            }
            catch
            {
                // Observation failure is handled conservatively by the spawn guard.
            }
        }

        private static ShotState GetState(
            object instance,
            int generation)
        {
            ShotState state = States.GetOrCreateValue(instance);
            if (state.Generation == generation) return state;

            state.Generation = generation;
            state.NativeVolleyObserved = false;
            return state;
        }

        private static bool IsSynthetic(object tracked)
        {
            try
            {
                return tracked != null &&
                       (bool)_trackedSyntheticField.GetValue(tracked);
            }
            catch
            {
                return false;
            }
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                States.Remove(__instance);
        }
    }
}
