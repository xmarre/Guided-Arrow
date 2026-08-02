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
    /// Keeps custom continuation creation outside collision-owned behavior ticks without patching
    /// Bannerlord Mission methods. Normal deferred workers see only native-removal work. One aged
    /// continuation is drained from the GuidedArrowBehavior OnPreDisplayMissionTick postfix.
    /// </summary>
    internal static class SafeContinuationMissionBoundaryPatch
    {
        private sealed class BoundaryState
        {
            internal long CompletedDisplayTicks;
        }

        private sealed class Eligibility
        {
            internal long FirstEligibleDisplayTick;
        }

        private sealed class HiddenQueueState
        {
            internal IList Queue;
            internal readonly List<object> Items = new List<object>();
            internal bool Restored;
        }

        [ThreadStatic]
        private static int _safeDrainDepth;

        private static readonly ConditionalWeakTable<object, BoundaryState> BoundaryStates =
            new ConditionalWeakTable<object, BoundaryState>();
        private static readonly ConditionalWeakTable<object, Eligibility> Eligibilities =
            new ConditionalWeakTable<object, Eligibility>();

        private static MethodInfo _processDeferredMethod;
        private static MethodInfo _logMethod;
        private static FieldInfo _pendingContinuationSpawnsField;
        private static FieldInfo _pendingCollisionContextsField;
        private static FieldInfo _earlyCollisionReactionsField;
        private static FieldInfo _pendingNativeMissileRemovalsField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _processDeferredMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "ProcessDeferredNativeMissileWork" &&
                    method.GetParameters().Length == 0);
            MethodInfo queueMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "QueuePenetrationContinuation" &&
                    method.GetParameters().Length == 2);
            MethodInfo[] displayMethods = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "OnPreDisplayMissionTick" && !method.IsAbstract)
                .ToArray();

            _pendingContinuationSpawnsField = AccessTools.Field(behaviorType, "_pendingContinuationSpawns");
            _pendingCollisionContextsField = AccessTools.Field(behaviorType, "_pendingCollisionContexts");
            _earlyCollisionReactionsField = AccessTools.Field(behaviorType, "_earlyCollisionReactions");
            _pendingNativeMissileRemovalsField = AccessTools.Field(behaviorType, "_pendingNativeMissileRemovals");
            _logMethod = AccessTools.Method(behaviorType, "Log", new[] { typeof(string) });

            if (_processDeferredMethod == null ||
                queueMethod == null ||
                displayMethods.Length == 0 ||
                _pendingContinuationSpawnsField == null ||
                _pendingCollisionContextsField == null ||
                _earlyCollisionReactionsField == null ||
                _pendingNativeMissileRemovalsField == null)
                return;

            MethodInfo queuePrefix = AccessTools.Method(typeof(SafeContinuationMissionBoundaryPatch), nameof(QueuePrefix));
            MethodInfo queuePostfix = AccessTools.Method(typeof(SafeContinuationMissionBoundaryPatch), nameof(QueuePostfix));
            MethodInfo workerPrefix = AccessTools.Method(typeof(SafeContinuationMissionBoundaryPatch), nameof(WorkerPrefix));
            MethodInfo workerPostfix = AccessTools.Method(typeof(SafeContinuationMissionBoundaryPatch), nameof(WorkerPostfix));
            MethodInfo workerFinalizer = AccessTools.Method(typeof(SafeContinuationMissionBoundaryPatch), nameof(WorkerFinalizer));
            MethodInfo displayPostfix = AccessTools.Method(typeof(SafeContinuationMissionBoundaryPatch), nameof(DisplayPostfix));
            MethodInfo clearPrefix = AccessTools.Method(typeof(SafeContinuationMissionBoundaryPatch), nameof(ClearPrefix));

            if (queuePrefix == null || queuePostfix == null || workerPrefix == null ||
                workerPostfix == null || workerFinalizer == null || displayPostfix == null ||
                clearPrefix == null)
                return;

            try
            {
                harmony.Patch(
                    queueMethod,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(queuePostfix) { priority = Priority.Last });

                harmony.Patch(
                    _processDeferredMethod,
                    prefix: new HarmonyMethod(workerPrefix) { priority = int.MaxValue },
                    postfix: new HarmonyMethod(workerPostfix) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(workerFinalizer) { priority = Priority.Last });

                for (int i = 0; i < displayMethods.Length; i++)
                {
                    harmony.Patch(
                        displayMethods[i],
                        postfix: new HarmonyMethod(displayPostfix) { priority = Priority.Last });
                }

                foreach (string name in new[] { "StartGuidedShot", "ResetAll" })
                {
                    foreach (MethodInfo method in behaviorType
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(candidate => candidate.Name == name && !candidate.IsAbstract))
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
                _safeDrainDepth = 0;
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

                BoundaryState boundary = BoundaryStates.GetOrCreateValue(__instance);
                long ready = boundary.CompletedDisplayTicks == long.MaxValue
                    ? long.MaxValue
                    : boundary.CompletedDisplayTicks + 1L;

                for (int i = __state; i < queue.Count; i++)
                {
                    object item = queue[i];
                    if (item == null) continue;
                    Eligibilities.Remove(item);
                    Eligibilities.Add(item, new Eligibility { FirstEligibleDisplayTick = ready });
                }
            }
            catch { }
        }

        private static void WorkerPrefix(object __instance, out HiddenQueueState __state)
        {
            __state = null;
            if (__instance == null || _safeDrainDepth > 0) return;
            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count == 0) return;

                HiddenQueueState state = new HiddenQueueState { Queue = queue };
                for (int i = 0; i < queue.Count; i++) state.Items.Add(queue[i]);
                queue.Clear();
                __state = state;
            }
            catch { }
        }

        private static void WorkerPostfix(HiddenQueueState __state)
        {
            Restore(__state);
        }

        private static Exception WorkerFinalizer(Exception __exception, HiddenQueueState __state)
        {
            Restore(__state);
            return __exception;
        }

        private static void Restore(HiddenQueueState state)
        {
            if (state == null || state.Restored || state.Queue == null) return;
            state.Restored = true;
            try
            {
                for (int i = state.Items.Count - 1; i >= 0; i--)
                    state.Queue.Insert(0, state.Items[i]);
            }
            catch { }
        }

        private static void DisplayPostfix(object __instance)
        {
            if (__instance == null || _safeDrainDepth > 0 || _processDeferredMethod == null) return;

            BoundaryState boundary = BoundaryStates.GetOrCreateValue(__instance);
            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count == 0) return;

                object head = queue[0];
                if (head == null)
                {
                    queue.RemoveAt(0);
                    return;
                }

                if (!Eligibilities.TryGetValue(head, out Eligibility eligibility) || eligibility == null)
                {
                    long ready = boundary.CompletedDisplayTicks == long.MaxValue
                        ? long.MaxValue
                        : boundary.CompletedDisplayTicks + 1L;
                    Eligibilities.Remove(head);
                    Eligibilities.Add(head, new Eligibility { FirstEligibleDisplayTick = ready });
                    return;
                }

                if (eligibility.FirstEligibleDisplayTick > boundary.CompletedDisplayTicks ||
                    HasCollisionOwnedWork(__instance))
                    return;

                TryLog("Processing one controlled penetration continuation after a complete GuidedArrowBehavior.OnPreDisplayMissionTick boundary.");

                _safeDrainDepth++;
                try
                {
                    _processDeferredMethod.Invoke(__instance, null);
                }
                finally
                {
                    _safeDrainDepth--;
                }

                if (!ContainsReference(queue, head)) Eligibilities.Remove(head);
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                TryLog("Deferred penetration continuation failed after pre-display boundary: " + inner.GetType().Name + ".");
            }
            catch (Exception exception)
            {
                TryLog("Deferred penetration pre-display boundary failed: " + exception.GetType().Name + ".");
            }
            finally
            {
                if (boundary.CompletedDisplayTicks < long.MaxValue)
                    boundary.CompletedDisplayTicks++;
            }
        }

        private static bool HasCollisionOwnedWork(object instance)
        {
            return Count(_pendingCollisionContextsField, instance) > 0 ||
                   Count(_earlyCollisionReactionsField, instance) > 0 ||
                   Count(_pendingNativeMissileRemovalsField, instance) > 0;
        }

        private static int Count(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return int.MaxValue;
            try
            {
                object value = field.GetValue(instance);
                if (value is ICollection collection) return collection.Count;
                PropertyInfo property = value?.GetType().GetProperty(
                    "Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object count = property?.GetValue(value, null);
                return count is int integer ? integer : int.MaxValue;
            }
            catch { return int.MaxValue; }
        }

        private static bool ContainsReference(IList list, object item)
        {
            if (list == null || item == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return true;
            return false;
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null) BoundaryStates.Remove(__instance);
        }

        private static void TryLog(string message)
        {
            if (_logMethod == null || string.IsNullOrEmpty(message)) return;
            try { _logMethod.Invoke(null, new object[] { message }); }
            catch { }
        }
    }
}
