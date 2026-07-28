using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Repairs the ResolveCollisionReaction branch that removes a tracked projectile after native
    /// PassThrough exhausts the configured guided penetration budget but does not complete the
    /// terminal swarm transition when the last tracked projectile disappears.
    ///
    /// Concentrated volleys can enter this branch for many projectiles in one callback window, so
    /// pending removals are tracked per behavior instance and shot generation rather than in one
    /// thread-static slot.
    /// </summary>
    internal static class FinalMissileTerminalHandoffPatch
    {
        private sealed class PendingState
        {
            internal readonly Dictionary<int, int> RemovalGenerations = new Dictionary<int, int>();
            internal int TerminalGeneration = -1;
        }

        private static readonly ConditionalWeakTable<object, PendingState> Pending =
            new ConditionalWeakTable<object, PendingState>();

        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _stateField;
        private static FieldInfo _generationField;
        private static FieldInfo _trackedIndexField;
        private static MethodInfo _terminalMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _stateField = AccessTools.Field(behaviorType, "_state");
            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            _terminalMethod = AccessTools.Method(
                behaviorType,
                "HandleGuidedSwarmTerminal",
                new[] { typeof(string) });

            MethodInfo queueRemoval = AccessTools.Method(
                behaviorType,
                "QueueNativeMissileRemoval",
                new[] { trackedType });
            MethodInfo removeTracked = AccessTools.Method(
                behaviorType,
                "RemoveTrackedMissile",
                new[] { trackedType, typeof(bool) });

            if (_trackedMissilesField == null ||
                _stateField == null ||
                _generationField == null ||
                _trackedIndexField == null ||
                _terminalMethod == null ||
                queueRemoval == null ||
                removeTracked == null)
                return;

            MethodInfo queuePrefix = AccessTools.Method(
                typeof(FinalMissileTerminalHandoffPatch),
                nameof(QueueRemovalPrefix));
            MethodInfo removePostfix = AccessTools.Method(
                typeof(FinalMissileTerminalHandoffPatch),
                nameof(RemoveTrackedPostfix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(FinalMissileTerminalHandoffPatch),
                nameof(ClearPrefix));

            if (queuePrefix == null || removePostfix == null || clearPrefix == null) return;

            try
            {
                harmony.Patch(
                    queueRemoval,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First });
                harmony.Patch(
                    removeTracked,
                    postfix: new HarmonyMethod(removePostfix) { priority = Priority.Last });

                foreach (string methodName in new[] { "StartGuidedShot", "ResetAll" })
                {
                    foreach (MethodInfo method in behaviorType.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (method.Name != methodName || method.IsAbstract) continue;
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
            catch
            {
                // Leave the locked core authoritative if private layout changes.
            }
        }

        private static bool QueueRemovalPrefix(object __instance, object __0)
        {
            if (__instance == null || __0 == null) return true;

            try
            {
                PendingState state = Pending.GetOrCreateValue(__instance);
                if (state.RemovalGenerations.Count >= 256)
                    state.RemovalGenerations.Clear();

                int index = (int)_trackedIndexField.GetValue(__0);
                int generation = (int)_generationField.GetValue(__instance);
                state.RemovalGenerations[index] = generation;

                // Bannerlord/TOR already returned PassThrough. Do not force-delete that transitioned
                // native projectile on the following mission tick. Guided Arrow only relinquishes it.
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void RemoveTrackedPostfix(object __instance, object __0)
        {
            if (__instance == null || __0 == null) return;

            try
            {
                if (!Pending.TryGetValue(__instance, out PendingState pending) || pending == null)
                    return;

                int removedIndex = (int)_trackedIndexField.GetValue(__0);
                if (!pending.RemovalGenerations.TryGetValue(removedIndex, out int queuedGeneration))
                    return;

                pending.RemovalGenerations.Remove(removedIndex);

                int currentGeneration = (int)_generationField.GetValue(__instance);
                if (queuedGeneration != currentGeneration || pending.TerminalGeneration == currentGeneration)
                    return;

                IList tracked = _trackedMissilesField.GetValue(__instance) as IList;
                int state = (int)_stateField.GetValue(__instance);
                if (tracked == null || tracked.Count != 0 || state != 2) return;

                // Mark first to prevent a nested terminal callback from invoking the handoff twice.
                pending.TerminalGeneration = currentGeneration;
                _terminalMethod.Invoke(
                    __instance,
                    new object[] { "PenetrationBudgetExhausted/FinalTrackedMissile" });
            }
            catch
            {
                // The core remains authoritative if its private layout changes.
            }
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
        }
    }
}
