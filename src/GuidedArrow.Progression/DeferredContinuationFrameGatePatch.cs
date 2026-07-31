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
    /// Establishes a real native-frame boundary before the stable core calls Mission.AddCustomMissile
    /// for a penetration continuation. Queueing alone is insufficient because a native collision can
    /// append work before GuidedArrowBehavior.OnMissionTick drains that queue in the same outer
    /// Mission.Tick. Calling AddCustomMissile in that window can corrupt Bannerlord's native missile
    /// container even when the resulting AccessViolationException is caught by managed code.
    /// </summary>
    internal static class DeferredContinuationFrameGatePatch
    {
        private sealed class PassState
        {
            internal long CompletedPasses;
        }

        private sealed class Eligibility
        {
            internal long FirstEligiblePass;
        }

        private sealed class WorkerState
        {
            internal IList Queue;
            internal PassState Pass;
            internal object Head;
            internal readonly List<object> OriginalItems = new List<object>();
            internal readonly List<object> DeferredItems = new List<object>();
            internal bool QueueMutated;
            internal bool PassCompleted;
        }

        private static readonly ConditionalWeakTable<object, PassState> PassStates =
            new ConditionalWeakTable<object, PassState>();
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
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(DeferredContinuationFrameGatePatch),
                nameof(ClearPrefix));

            if (queuePrefix == null ||
                queuePostfix == null ||
                workerPrefix == null ||
                workerFinalizer == null ||
                clearPrefix == null)
                return;

            try
            {
                harmony.Patch(
                    queueMethod,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(queuePostfix) { priority = Priority.Last });

                // This prefix must run before PenetrationContinuationSafetyPatch's existing
                // one-item serializer. It leaves that patch either zero or one eligible item.
                // The finalizer runs after normal postfix handling and restores quarantined work.
                harmony.Patch(
                    workerMethod,
                    prefix: new HarmonyMethod(workerPrefix) { priority = int.MaxValue },
                    finalizer: new HarmonyMethod(workerFinalizer) { priority = Priority.Last });

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

                PassState pass = PassStates.GetOrCreateValue(__instance);
                long firstEligiblePass = pass.CompletedPasses + 2L;

                for (int i = __state; i < queue.Count; i++)
                {
                    object item = queue[i];
                    if (item == null) continue;

                    Eligibilities.Remove(item);
                    Eligibilities.Add(
                        item,
                        new Eligibility { FirstEligiblePass = firstEligiblePass });
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

            PassState pass = PassStates.GetOrCreateValue(__instance);
            WorkerState state = new WorkerState { Pass = pass };
            __state = state;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                state.Queue = queue;
                if (queue == null || queue.Count == 0) return;

                bool collisionWorkPending = HasCollisionOwnedWork(__instance);
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
                            FirstEligiblePass = pass.CompletedPasses + 2L
                        };
                        Eligibilities.Remove(item);
                        Eligibilities.Add(item, eligibility);
                    }

                    if (!collisionWorkPending &&
                        eligibleHead == null &&
                        eligibility.FirstEligiblePass <= pass.CompletedPasses)
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

                // The original worker still runs. It can drain native-removal work normally, while
                // its continuation queue contains one age-eligible item or no continuation at all.
            }
            catch
            {
                RestoreOriginalQueueAfterPrefixFailure(state);
            }
        }

        private static Exception WorkerFinalizer(Exception __exception, WorkerState __state)
        {
            try
            {
                RestoreQuarantinedItems(__state);
            }
            finally
            {
                CompletePass(__state);
            }

            return __exception;
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

                // A head still present in the queue was never consumed, usually because unrelated
                // native-removal work threw before the continuation loop. Preserve it and its age.
                if (headStillQueued)
                    restore.Add(state.Head);
                else if (state.Head != null)
                    Eligibilities.Remove(state.Head);

                for (int i = 0; i < state.DeferredItems.Count; i++)
                    restore.Add(state.DeferredItems[i]);

                // Old quarantined work stays ahead of any new continuation queued by callbacks that
                // ran inside the original worker. Insert in reverse to preserve exact FIFO ordering.
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

        private static void CompletePass(WorkerState state)
        {
            if (state == null || state.PassCompleted || state.Pass == null) return;
            state.PassCompleted = true;

            if (state.Pass.CompletedPasses < long.MaxValue)
                state.Pass.CompletedPasses++;
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

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                PassStates.Remove(__instance);
        }
    }
}
