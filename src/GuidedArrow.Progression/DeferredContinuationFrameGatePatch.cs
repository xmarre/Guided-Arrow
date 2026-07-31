using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps terminal Stick continuations out of Bannerlord's collision-finalization window.
    /// ProcessDeferredNativeMissileWork can run more than once before a rendered frame completes,
    /// so mission-worker pass counts do not establish a native frame boundary. Eligibility is tied
    /// to completed OnPreDisplayMissionTick calls and a minimum real-time quarantine instead.
    /// </summary>
    internal static class DeferredContinuationFrameGatePatch
    {
        private const int RequiredDisplayBoundaries = 3;
        private const double MinimumQuarantineSeconds = 0.075;

        private sealed class DisplayState
        {
            internal long CompletedDisplayTicks;
        }

        private sealed class Eligibility
        {
            internal long FirstEligibleDisplayTick;
            internal long NotBeforeTimestamp;
        }

        private sealed class WorkerState
        {
            internal IList Queue;
            internal object Head;
            internal readonly List<object> OriginalItems = new List<object>();
            internal readonly List<object> DeferredItems = new List<object>();
            internal bool QueueMutated;
        }

        private static readonly ConditionalWeakTable<object, DisplayState> DisplayStates =
            new ConditionalWeakTable<object, DisplayState>();
        private static readonly ConditionalWeakTable<object, Eligibility> Eligibilities =
            new ConditionalWeakTable<object, Eligibility>();

        private static FieldInfo _pendingContinuationSpawnsField;
        private static FieldInfo _pendingCollisionContextsField;
        private static FieldInfo _earlyCollisionReactionsField;
        private static FieldInfo _pendingNativeMissileRemovalsField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo spawnMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            if (spawnMethod == null) return;

            ParameterInfo[] spawnParameters = spawnMethod.GetParameters();
            Type trackedType = spawnParameters[0].ParameterType;
            Type contextType = spawnParameters[1].ParameterType;

            MethodInfo queueMethod = AccessTools.Method(
                behaviorType,
                "QueuePenetrationContinuation",
                new[] { trackedType, contextType });
            MethodInfo workerMethod = AccessTools.Method(
                behaviorType,
                "ProcessDeferredNativeMissileWork");

            _pendingContinuationSpawnsField = AccessTools.Field(
                behaviorType,
                "_pendingContinuationSpawns");
            _pendingCollisionContextsField = AccessTools.Field(
                behaviorType,
                "_pendingCollisionContexts");
            _earlyCollisionReactionsField = AccessTools.Field(
                behaviorType,
                "_earlyCollisionReactions");
            _pendingNativeMissileRemovalsField = AccessTools.Field(
                behaviorType,
                "_pendingNativeMissileRemovals");

            if (queueMethod == null ||
                workerMethod == null ||
                _pendingContinuationSpawnsField == null ||
                _pendingCollisionContextsField == null ||
                _earlyCollisionReactionsField == null ||
                _pendingNativeMissileRemovalsField == null)
                return;

            MethodInfo queuePrefix = AccessTools.Method(
                typeof(DeferredContinuationFrameGatePatch),
                nameof(QueuePrefix));
            MethodInfo queuePostfix = AccessTools.Method(
                typeof(DeferredContinuationFrameGatePatch),
                nameof(QueuePostfix));
            MethodInfo workerPrefix = AccessTools.Method(
                typeof(DeferredContinuationFrameGatePatch),
                nameof(WorkerPrefix));
            MethodInfo workerFinalizer = AccessTools.Method(
                typeof(DeferredContinuationFrameGatePatch),
                nameof(WorkerFinalizer));
            MethodInfo displayPostfix = AccessTools.Method(
                typeof(DeferredContinuationFrameGatePatch),
                nameof(DisplayTickPostfix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(DeferredContinuationFrameGatePatch),
                nameof(ClearPrefix));

            if (queuePrefix == null ||
                queuePostfix == null ||
                workerPrefix == null ||
                workerFinalizer == null ||
                displayPostfix == null ||
                clearPrefix == null)
                return;

            try
            {
                harmony.Patch(
                    queueMethod,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(queuePostfix) { priority = Priority.Last });

                // Run before the existing one-item serializer. The original worker sees at most one
                // continuation whose native-frame and collision-owned-work gates have both cleared.
                harmony.Patch(
                    workerMethod,
                    prefix: new HarmonyMethod(workerPrefix) { priority = int.MaxValue },
                    finalizer: new HarmonyMethod(workerFinalizer) { priority = Priority.Last });

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
            catch
            {
                // The SHA-locked core remains authoritative if its private method layout changes.
            }
        }

        private static void QueuePrefix(object __instance, out int __state)
        {
            __state = -1;
            if (__instance == null) return;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue != null) __state = queue.Count;
            }
            catch { }
        }

        private static void QueuePostfix(object __instance, int __state)
        {
            if (__instance == null || __state < 0) return;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count <= __state) return;

                DisplayState display = DisplayStates.GetOrCreateValue(__instance);
                long firstEligibleDisplayTick = display.CompletedDisplayTicks + RequiredDisplayBoundaries;
                long notBeforeTimestamp = AddSeconds(Stopwatch.GetTimestamp(), MinimumQuarantineSeconds);

                for (int i = __state; i < queue.Count; i++)
                {
                    object item = queue[i];
                    if (item == null) continue;

                    Eligibilities.Remove(item);
                    Eligibilities.Add(
                        item,
                        new Eligibility
                        {
                            FirstEligibleDisplayTick = firstEligibleDisplayTick,
                            NotBeforeTimestamp = notBeforeTimestamp
                        });
                }
            }
            catch
            {
                // WorkerPrefix treats an untagged item as newly queued and quarantines it.
            }
        }

        private static void WorkerPrefix(object __instance, out WorkerState __state)
        {
            __state = null;
            if (__instance == null) return;

            WorkerState state = new WorkerState();
            __state = state;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                state.Queue = queue;
                if (queue == null || queue.Count == 0) return;

                DisplayState display = DisplayStates.GetOrCreateValue(__instance);
                bool collisionWorkPending = HasCollisionOwnedWork(__instance);
                long now = Stopwatch.GetTimestamp();
                object eligibleHead = null;

                for (int i = 0; i < queue.Count; i++)
                {
                    object item = queue[i];
                    state.OriginalItems.Add(item);
                    if (item == null) continue;

                    if (!Eligibilities.TryGetValue(item, out Eligibility eligibility) ||
                        eligibility == null)
                    {
                        eligibility = new Eligibility
                        {
                            FirstEligibleDisplayTick = display.CompletedDisplayTicks + RequiredDisplayBoundaries,
                            NotBeforeTimestamp = AddSeconds(now, MinimumQuarantineSeconds)
                        };
                        Eligibilities.Remove(item);
                        Eligibilities.Add(item, eligibility);
                    }

                    if (!collisionWorkPending &&
                        eligibleHead == null &&
                        eligibility.FirstEligibleDisplayTick <= display.CompletedDisplayTicks &&
                        eligibility.NotBeforeTimestamp <= now)
                    {
                        eligibleHead = item;
                    }
                    else
                    {
                        state.DeferredItems.Add(item);
                    }
                }

                queue.Clear();
                state.QueueMutated = true;
                if (eligibleHead != null)
                {
                    state.Head = eligibleHead;
                    queue.Add(eligibleHead);
                }
            }
            catch
            {
                RestoreOriginalQueueAfterPrefixFailure(state);
            }
        }

        private static Exception WorkerFinalizer(Exception __exception, WorkerState __state)
        {
            RestoreQuarantinedItems(__state);
            return __exception;
        }

        private static void DisplayTickPostfix(object __instance)
        {
            if (__instance == null) return;

            DisplayState display = DisplayStates.GetOrCreateValue(__instance);
            if (display.CompletedDisplayTicks < long.MaxValue)
                display.CompletedDisplayTicks++;
        }

        private static void RestoreOriginalQueueAfterPrefixFailure(WorkerState state)
        {
            if (state == null || state.Queue == null || !state.QueueMutated) return;

            try
            {
                state.Queue.Clear();
                for (int i = 0; i < state.OriginalItems.Count; i++)
                    state.Queue.Add(state.OriginalItems[i]);
            }
            catch { }

            state.Queue = null;
            state.Head = null;
            state.DeferredItems.Clear();
        }

        private static void RestoreQuarantinedItems(WorkerState state)
        {
            if (state == null || state.Queue == null || !state.QueueMutated) return;

            try
            {
                bool headStillQueued = state.Head != null && RemoveByReference(state.Queue, state.Head);
                List<object> restore = new List<object>(state.DeferredItems.Count + 1);

                if (headStillQueued)
                    restore.Add(state.Head);
                else if (state.Head != null)
                    Eligibilities.Remove(state.Head);

                for (int i = 0; i < state.DeferredItems.Count; i++)
                    restore.Add(state.DeferredItems[i]);

                // Old quarantined work stays ahead of callbacks that queued new work while the
                // original worker ran. Reverse insertion preserves exact FIFO ordering.
                for (int i = restore.Count - 1; i >= 0; i--)
                    state.Queue.Insert(0, restore[i]);
            }
            catch
            {
                // Do not replace the original mission exception with restoration bookkeeping.
            }
        }

        private static bool RemoveByReference(IList list, object item)
        {
            if (list == null || item == null) return false;

            for (int i = 0; i < list.Count; i++)
            {
                if (!ReferenceEquals(list[i], item)) continue;
                list.RemoveAt(i);
                return true;
            }
            return false;
        }

        private static bool HasCollisionOwnedWork(object instance)
        {
            return ReadCount(_pendingCollisionContextsField, instance) > 0 ||
                   ReadCount(_earlyCollisionReactionsField, instance) > 0 ||
                   ReadCount(_pendingNativeMissileRemovalsField, instance) > 0;
        }

        private static int ReadCount(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return int.MaxValue;

            try
            {
                object value = field.GetValue(instance);
                if (value == null) return int.MaxValue;
                if (value is ICollection collection) return collection.Count;

                PropertyInfo countProperty = value.GetType().GetProperty(
                    "Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (countProperty == null) return int.MaxValue;

                object count = countProperty.GetValue(value, null);
                return count is int integer ? integer : int.MaxValue;
            }
            catch
            {
                return int.MaxValue;
            }
        }

        private static long AddSeconds(long timestamp, double seconds)
        {
            if (seconds <= 0d) return timestamp;

            double ticks = seconds * Stopwatch.Frequency;
            if (double.IsNaN(ticks) || double.IsInfinity(ticks) || ticks <= 0d)
                return timestamp;
            if (ticks >= long.MaxValue - timestamp)
                return long.MaxValue;

            return timestamp + (long)Math.Ceiling(ticks);
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                DisplayStates.Remove(__instance);
        }
    }
}
