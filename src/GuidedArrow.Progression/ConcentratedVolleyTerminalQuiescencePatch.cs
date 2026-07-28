using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Prevents the core from entering its kill-cinematic terminal state while additional missile
    /// callbacks from the same concentrated volley are still being processed. The terminal call is
    /// released after a short quiet period, with a hard upper bound so a malformed callback stream
    /// cannot hold guidance open indefinitely.
    /// </summary>
    internal static class ConcentratedVolleyTerminalQuiescencePatch
    {
        private sealed class BurstState
        {
            internal int Generation;
            internal long LastImpactTimestamp;
            internal long WaitStartedTimestamp;
            internal bool DeferringTerminal;
        }

        private static readonly ConditionalWeakTable<object, BurstState> States =
            new ConditionalWeakTable<object, BurstState>();

        private const double QuietSeconds = 0.12;
        private const double MaximumWaitSeconds = 0.40;

        private static FieldInfo _stateField;
        private static FieldInfo _generationField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _deferredVictimField;
        private static FieldInfo _confirmedKillsField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _stateField = AccessTools.Field(behaviorType, "_state");
            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _deferredVictimField = AccessTools.Field(behaviorType, "_deferredCinematicVictim");
            _confirmedKillsField = AccessTools.Field(behaviorType, "_confirmedCinematicKillCount");

            MethodInfo impactPrefix = AccessTools.Method(
                typeof(ConcentratedVolleyTerminalQuiescencePatch),
                nameof(ImpactPrefix));
            MethodInfo terminalPrefix = AccessTools.Method(
                typeof(ConcentratedVolleyTerminalQuiescencePatch),
                nameof(TerminalPrefix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(ConcentratedVolleyTerminalQuiescencePatch),
                nameof(ClearPrefix));

            if (impactPrefix == null || terminalPrefix == null || clearPrefix == null ||
                _stateField == null || _generationField == null || _trackedMissilesField == null)
                return;

            foreach (string methodName in new[] { "OnMissileCollisionReaction", "OnMissileHit" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == methodName && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(impactPrefix) { priority = Priority.First });
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
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(terminalPrefix) { priority = int.MaxValue });
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

            PatchTerminalObserver(harmony, typeof(ProgressionRuntimeSettingsPatch), "MarkRestorePostfix");
            PatchTerminalObserver(harmony, typeof(ProgressionTerminalXpPatch), "CapturePrefix");
        }

        internal static bool IsTerminalDeferred(object instance)
        {
            if (instance == null) return false;
            return States.TryGetValue(instance, out BurstState state) &&
                   state != null &&
                   state.DeferringTerminal;
        }

        private static void PatchTerminalObserver(Harmony harmony, Type type, string methodName)
        {
            MethodInfo original = AccessTools.Method(type, methodName);
            MethodInfo prefix = AccessTools.Method(
                typeof(ConcentratedVolleyTerminalQuiescencePatch),
                nameof(TerminalObserverPrefix));
            if (original == null || prefix == null) return;

            try
            {
                harmony.Patch(
                    original,
                    prefix: new HarmonyMethod(prefix) { priority = int.MaxValue });
            }
            catch { }
        }

        private static bool TerminalObserverPrefix(object __instance)
        {
            return !IsTerminalDeferred(__instance);
        }

        private static void ImpactPrefix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                int coreState = (int)_stateField.GetValue(__instance);
                if (coreState != 2 && coreState != 3) return;

                int generation = (int)_generationField.GetValue(__instance);
                BurstState state = States.GetOrCreateValue(__instance);
                if (state.Generation != generation)
                {
                    state.Generation = generation;
                    state.WaitStartedTimestamp = 0;
                }

                state.LastImpactTimestamp = Stopwatch.GetTimestamp();
                state.DeferringTerminal = false;
            }
            catch { }
        }

        private static bool TerminalPrefix(object __instance)
        {
            if (__instance == null) return true;

            try
            {
                BurstState state = States.GetOrCreateValue(__instance);
                state.DeferringTerminal = false;

                int coreState = (int)_stateField.GetValue(__instance);
                if (coreState != 2 && coreState != 3) return true;

                IList tracked = _trackedMissilesField.GetValue(__instance) as IList;
                if (tracked == null || tracked.Count != 0) return true;

                bool hasConfirmedKill = ReadInt(_confirmedKillsField, __instance) > 0;
                bool hasDeferredVictim = false;
                try { hasDeferredVictim = _deferredVictimField?.GetValue(__instance) != null; }
                catch { }
                if (!hasConfirmedKill && !hasDeferredVictim) return true;

                int generation = (int)_generationField.GetValue(__instance);
                if (state.Generation != generation || state.LastImpactTimestamp <= 0)
                    return true;

                long now = Stopwatch.GetTimestamp();
                if (state.WaitStartedTimestamp <= 0)
                    state.WaitStartedTimestamp = now;

                double quiet = (now - state.LastImpactTimestamp) / (double)Stopwatch.Frequency;
                double total = (now - state.WaitStartedTimestamp) / (double)Stopwatch.Frequency;

                if (quiet < QuietSeconds && total < MaximumWaitSeconds)
                {
                    state.DeferringTerminal = true;
                    return false;
                }

                state.WaitStartedTimestamp = 0;
                state.DeferringTerminal = false;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static int ReadInt(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return 0;
            try { return (int)field.GetValue(instance); }
            catch { return 0; }
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                States.Remove(__instance);
        }
    }
}
