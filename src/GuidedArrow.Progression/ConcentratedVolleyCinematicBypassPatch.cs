using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Handles the exact lifecycle recorded by the concentrated-volley trace: a tracked native
    /// volley leader receives PassThrough and then Stick for the same missile index, confirms a
    /// kill, loses its final tracked entry and would enter the core kill-cinematic state. For that
    /// exact native-volley sequence only, return the camera normally instead of entering state 4.
    /// Ordinary single-shot and synthetic split cinematics remain untouched.
    /// </summary>
    internal static class ConcentratedVolleyCinematicBypassPatch
    {
        private sealed class ShotState
        {
            internal int Generation = -1;
            internal bool SawNativeVolley;
            internal readonly HashSet<int> PassedThrough = new HashSet<int>();
            internal readonly HashSet<int> PassedThenStuck = new HashSet<int>();
            internal int BypassedGeneration = -1;
        }

        private static readonly ConditionalWeakTable<object, ShotState> States =
            new ConditionalWeakTable<object, ShotState>();

        private static FieldInfo _stateField;
        private static FieldInfo _generationField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _confirmedKillsField;
        private static FieldInfo _nativeSplitBatchField;
        private static MethodInfo _beginReturnMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _stateField = AccessTools.Field(behaviorType, "_state");
            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _confirmedKillsField = AccessTools.Field(behaviorType, "_confirmedCinematicKillCount");
            _nativeSplitBatchField = AccessTools.Field(behaviorType, "_nativeSplitBatchDetected");
            _beginReturnMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "BeginReturn" && method.GetParameters().Length == 2);

            if (_stateField == null ||
                _generationField == null ||
                _trackedMissilesField == null ||
                _confirmedKillsField == null ||
                _nativeSplitBatchField == null ||
                _beginReturnMethod == null)
                return;

            MethodInfo reactionPrefix = AccessTools.Method(
                typeof(ConcentratedVolleyCinematicBypassPatch),
                nameof(ReactionPrefix));
            MethodInfo nativeObserverPostfix = AccessTools.Method(
                typeof(ConcentratedVolleyCinematicBypassPatch),
                nameof(NativeObserverPostfix));
            MethodInfo terminalPrefix = AccessTools.Method(
                typeof(ConcentratedVolleyCinematicBypassPatch),
                nameof(TerminalPrefix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(ConcentratedVolleyCinematicBypassPatch),
                nameof(ClearPrefix));

            if (reactionPrefix == null || nativeObserverPostfix == null || terminalPrefix == null || clearPrefix == null)
                return;

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "ResolveCollisionReaction" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(reactionPrefix) { priority = Priority.First });
                }
                catch { }
            }

            foreach (string methodName in new[] { "EnsureStandaloneSplitProjectiles", "CloseSplitSiblingAcquisition" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == methodName && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            postfix: new HarmonyMethod(nativeObserverPostfix) { priority = Priority.Last });
                    }
                    catch { }
                }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "HandleGuidedSwarmTerminal" && !candidate.IsAbstract))
            {
                try
                {
                    // Run after the normal terminal-XP prefix has sampled the completed shot, then
                    // suppress only the core cinematic body for the recorded native-volley sequence.
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(terminalPrefix) { priority = Priority.Last });
                }
                catch { }
            }

            foreach (string methodName in new[] { "StartGuidedShot", "ResetAll" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == methodName && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(clearPrefix) { priority = Priority.First });
                    }
                    catch { }
                }
            }
        }

        private static void ReactionPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 2) return;

            try
            {
                if (!TryReadInt(__args[0], out int missileIndex)) return;
                if (!TryReadInt(__args[1], out int reaction)) return;

                int generation = (int)_generationField.GetValue(__instance);
                ShotState state = GetState(__instance, generation);
                ObserveNativeVolley(__instance, state);

                // MissileCollisionReaction: Stick=0, PassThrough=1.
                if (reaction == 1)
                {
                    state.PassedThrough.Add(missileIndex);
                }
                else if (reaction == 0 && state.PassedThrough.Contains(missileIndex))
                {
                    state.PassedThenStuck.Add(missileIndex);
                }
            }
            catch { }
        }

        private static void NativeObserverPostfix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                int generation = (int)_generationField.GetValue(__instance);
                ObserveNativeVolley(__instance, GetState(__instance, generation));
            }
            catch { }
        }

        private static bool TerminalPrefix(object __instance)
        {
            if (__instance == null) return true;

            try
            {
                int generation = (int)_generationField.GetValue(__instance);
                ShotState shot = GetState(__instance, generation);
                ObserveNativeVolley(__instance, shot);

                if (!shot.SawNativeVolley ||
                    shot.PassedThenStuck.Count == 0 ||
                    shot.BypassedGeneration == generation)
                    return true;

                int coreState = (int)_stateField.GetValue(__instance);
                if (coreState != 2 && coreState != 3) return true;

                IList tracked = _trackedMissilesField.GetValue(__instance) as IList;
                if (tracked == null || tracked.Count != 0) return true;

                int kills = (int)_confirmedKillsField.GetValue(__instance);
                if (kills <= 0) return true;

                shot.BypassedGeneration = generation;
                _beginReturnMethod.Invoke(
                    __instance,
                    new object[] { "NativeVolleyPassThroughThenStick/SkipCinematic", true });
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static ShotState GetState(object instance, int generation)
        {
            ShotState state = States.GetOrCreateValue(instance);
            if (state.Generation != generation)
            {
                state.Generation = generation;
                state.SawNativeVolley = false;
                state.PassedThrough.Clear();
                state.PassedThenStuck.Clear();
                state.BypassedGeneration = -1;
            }
            return state;
        }

        private static void ObserveNativeVolley(object instance, ShotState state)
        {
            if (state == null || state.SawNativeVolley || instance == null) return;
            try
            {
                if ((bool)_nativeSplitBatchField.GetValue(instance))
                    state.SawNativeVolley = true;
            }
            catch { }
        }

        private static bool TryReadInt(object value, out int result)
        {
            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch
            {
                result = 0;
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
