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
    /// Repairs the ResolveCollisionReaction branch that removes a tracked projectile after native
    /// PassThrough exhausts the configured guided penetration budget but does not complete the
    /// terminal swarm transition when the last tracked projectile disappears.
    ///
    /// The terminal transition must not be invoked from RemoveTrackedMissile itself. That method can
    /// run inside Bannerlord's native collision callback, and starting the kill cinematic there can
    /// race the final Autoguidance impact, deferred collision contexts and native-removal queues.
    /// Instead, the handoff is requested per shot generation and executed only after two clean display
    /// ticks confirm that all collision-owned work has drained.
    /// </summary>
    internal static class FinalMissileTerminalHandoffPatch
    {
        private sealed class PendingState
        {
            internal readonly Dictionary<int, int> RemovalGenerations = new Dictionary<int, int>();
            internal int RequestedTerminalGeneration = -1;
            internal int CompletedTerminalGeneration = -1;
            internal bool SawCleanDisplayTick;
        }

        private static readonly ConditionalWeakTable<object, PendingState> Pending =
            new ConditionalWeakTable<object, PendingState>();

        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _stateField;
        private static FieldInfo _generationField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _pendingCollisionContextsField;
        private static FieldInfo _earlyCollisionReactionsField;
        private static FieldInfo _pendingContinuationSpawnsField;
        private static FieldInfo _pendingNativeMissileRemovalsField;
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
            _pendingCollisionContextsField = AccessTools.Field(behaviorType, "_pendingCollisionContexts");
            _earlyCollisionReactionsField = AccessTools.Field(behaviorType, "_earlyCollisionReactions");
            _pendingContinuationSpawnsField = AccessTools.Field(behaviorType, "_pendingContinuationSpawns");
            _pendingNativeMissileRemovalsField = AccessTools.Field(behaviorType, "_pendingNativeMissileRemovals");
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
            MethodInfo displayPostfix = AccessTools.Method(
                typeof(FinalMissileTerminalHandoffPatch),
                nameof(DisplayTickPostfix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(FinalMissileTerminalHandoffPatch),
                nameof(ClearPrefix));

            if (queuePrefix == null || removePostfix == null || displayPostfix == null || clearPrefix == null)
                return;

            try
            {
                harmony.Patch(
                    queueRemoval,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First });
                harmony.Patch(
                    removeTracked,
                    postfix: new HarmonyMethod(removePostfix) { priority = Priority.Last });

                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == "OnPreDisplayMissionTick" && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            postfix: new HarmonyMethod(displayPostfix) { priority = Priority.Last });
                    }
                    catch { }
                }

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
                if (queuedGeneration != currentGeneration ||
                    pending.CompletedTerminalGeneration == currentGeneration)
                    return;

                IList tracked = _trackedMissilesField.GetValue(__instance) as IList;
                int state = (int)_stateField.GetValue(__instance);
                if (tracked == null || tracked.Count != 0 || state != 2) return;

                // Record the missing terminal transition, but never invoke it while the native impact
                // callback is still unwinding. A later display tick owns the actual handoff.
                pending.RequestedTerminalGeneration = currentGeneration;
                pending.SawCleanDisplayTick = false;
            }
            catch
            {
                // The core remains authoritative if its private layout changes.
            }
        }

        private static void DisplayTickPostfix(object __instance)
        {
            if (__instance == null ||
                !Pending.TryGetValue(__instance, out PendingState pending) ||
                pending == null ||
                pending.RequestedTerminalGeneration < 0)
                return;

            try
            {
                int generation = (int)_generationField.GetValue(__instance);
                if (generation != pending.RequestedTerminalGeneration)
                {
                    pending.RequestedTerminalGeneration = -1;
                    pending.SawCleanDisplayTick = false;
                    return;
                }

                int state = (int)_stateField.GetValue(__instance);
                IList tracked = _trackedMissilesField.GetValue(__instance) as IList;
                if (state != 2 || tracked == null || tracked.Count != 0)
                {
                    // The core transitioned by itself or guidance resumed. Do not duplicate it.
                    if (state != 2)
                    {
                        pending.CompletedTerminalGeneration = generation;
                        pending.RequestedTerminalGeneration = -1;
                    }
                    pending.SawCleanDisplayTick = false;
                    return;
                }

                if (HasPendingCollisionWork(__instance))
                {
                    pending.SawCleanDisplayTick = false;
                    return;
                }

                // One completely clean display tick proves that the last impact callback and all
                // deferred missile work have finished. Execute on the following clean display tick.
                if (!pending.SawCleanDisplayTick)
                {
                    pending.SawCleanDisplayTick = true;
                    return;
                }

                pending.CompletedTerminalGeneration = generation;
                pending.RequestedTerminalGeneration = -1;
                pending.SawCleanDisplayTick = false;
                _terminalMethod.Invoke(
                    __instance,
                    new object[] { "PenetrationBudgetExhausted/FinalTrackedMissile/DeferredDisplayTick" });
            }
            catch
            {
                pending.SawCleanDisplayTick = false;
            }
        }

        private static bool HasPendingCollisionWork(object instance)
        {
            return ReadCount(_pendingCollisionContextsField, instance) > 0 ||
                   ReadCount(_earlyCollisionReactionsField, instance) > 0 ||
                   ReadCount(_pendingContinuationSpawnsField, instance) > 0 ||
                   ReadCount(_pendingNativeMissileRemovalsField, instance) > 0;
        }

        private static int ReadCount(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return 0;

            try
            {
                object value = field.GetValue(instance);
                if (value == null) return 0;
                if (value is ICollection collection) return collection.Count;

                PropertyInfo countProperty = value.GetType().GetProperty(
                    "Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object count = countProperty?.GetValue(value, null);
                return count is int integer ? integer : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
        }
    }
}
