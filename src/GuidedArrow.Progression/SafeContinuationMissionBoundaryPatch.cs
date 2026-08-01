using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps custom penetration continuation creation outside Mission.OnTick.
    /// The core may queue terminal agent continuations during collision handling, but normal
    /// behavior ticks only drain native-removal work. One aged continuation is processed after
    /// the complete outer mission tick returns and all collision-owned queues are empty.
    /// </summary>
    internal static class SafeContinuationMissionBoundaryPatch
    {
        private sealed class BoundaryState
        {
            internal long CompletedOuterTicks;
        }

        private sealed class Eligibility
        {
            internal long FirstEligibleOuterTick;
        }

        private sealed class QueuePatchState
        {
            internal IList Queue;
            internal readonly List<object> HiddenItems = new List<object>();
            internal bool Isolated;
            internal bool Restored;
        }

        [ThreadStatic]
        private static int _safeDrainDepth;

        private static readonly ConditionalWeakTable<object, BoundaryState> BoundaryStates =
            new ConditionalWeakTable<object, BoundaryState>();
        private static readonly ConditionalWeakTable<object, Eligibility> Eligibilities =
            new ConditionalWeakTable<object, Eligibility>();

        private static MethodInfo _getMissionBehaviorMethod;
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
            MethodInfo missionTick = typeof(Mission)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "OnTick" ||
                        method.ReturnType != typeof(void))
                        return false;

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 4 &&
                           parameters[0].ParameterType == typeof(float) &&
                           parameters[1].ParameterType == typeof(float) &&
                           parameters[2].ParameterType == typeof(bool) &&
                           parameters[3].ParameterType == typeof(bool);
                });
            MethodInfo openGetMissionBehavior = typeof(Mission)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "GetMissionBehavior" &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    method.GetParameters().Length == 0);

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
            _logMethod = behaviorType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "Log" &&
                    method.ReturnType == typeof(void) &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(string));

            if (_processDeferredMethod == null ||
                queueMethod == null ||
                missionTick == null ||
                openGetMissionBehavior == null ||
                _pendingContinuationSpawnsField == null ||
                _pendingCollisionContextsField == null ||
                _earlyCollisionReactionsField == null ||
                _pendingNativeMissileRemovalsField == null)
                return;

            MethodInfo queuePrefix = AccessTools.Method(
                typeof(SafeContinuationMissionBoundaryPatch),
                nameof(QueuePrefix));
            MethodInfo queuePostfix = AccessTools.Method(
                typeof(SafeContinuationMissionBoundaryPatch),
                nameof(QueuePostfix));
            MethodInfo workerPrefix = AccessTools.Method(
                typeof(SafeContinuationMissionBoundaryPatch),
                nameof(WorkerPrefix));
            MethodInfo workerPostfix = AccessTools.Method(
                typeof(SafeContinuationMissionBoundaryPatch),
                nameof(WorkerPostfix));
            MethodInfo workerFinalizer = AccessTools.Method(
                typeof(SafeContinuationMissionBoundaryPatch),
                nameof(WorkerFinalizer));
            MethodInfo missionTickPostfix = AccessTools.Method(
                typeof(SafeContinuationMissionBoundaryPatch),
                nameof(MissionTickPostfix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(SafeContinuationMissionBoundaryPatch),
                nameof(ClearPrefix));

            if (queuePrefix == null ||
                queuePostfix == null ||
                workerPrefix == null ||
                workerPostfix == null ||
                workerFinalizer == null ||
                missionTickPostfix == null ||
                clearPrefix == null)
                return;

            try
            {
                _getMissionBehaviorMethod =
                    openGetMissionBehavior.MakeGenericMethod(behaviorType);

                harmony.Patch(
                    queueMethod,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(queuePostfix) { priority = Priority.Last });

                // Run before the existing continuation serializer. During an ordinary behavior
                // tick that serializer sees no custom continuation work; native removals still run.
                harmony.Patch(
                    _processDeferredMethod,
                    prefix: new HarmonyMethod(workerPrefix)
                    {
                        priority = int.MaxValue
                    },
                    postfix: new HarmonyMethod(workerPostfix)
                    {
                        priority = Priority.Last
                    },
                    finalizer: new HarmonyMethod(workerFinalizer)
                    {
                        priority = Priority.Last
                    });

                // Priority.Last places the drain after every normal Mission.OnTick postfix that
                // participates in the engine-owned mission lifecycle.
                harmony.Patch(
                    missionTick,
                    postfix: new HarmonyMethod(missionTickPostfix)
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
                _getMissionBehaviorMethod = null;
                _safeDrainDepth = 0;
            }
        }

        private static void QueuePrefix(
            object __instance,
            out int __state)
        {
            __state = -1;
            if (__instance == null) return;

            try
            {
                IList queue =
                    _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue != null) __state = queue.Count;
            }
            catch { }
        }

        private static void QueuePostfix(
            object __instance,
            int __state)
        {
            if (__instance == null || __state < 0) return;

            try
            {
                IList queue =
                    _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count <= __state) return;

                BoundaryState boundary = BoundaryStates.GetOrCreateValue(__instance);
                long firstEligible =
                    boundary.CompletedOuterTicks == long.MaxValue
                        ? long.MaxValue
                        : boundary.CompletedOuterTicks + 1L;

                for (int i = __state; i < queue.Count; i++)
                {
                    object item = queue[i];
                    if (item == null) continue;

                    Eligibilities.Remove(item);
                    Eligibilities.Add(
                        item,
                        new Eligibility
                        {
                            FirstEligibleOuterTick = firstEligible
                        });
                }
            }
            catch
            {
                // MissionTickPostfix gives untagged entries a conservative extra-tick delay.
            }
        }

        private static void WorkerPrefix(
            object __instance,
            out QueuePatchState __state)
        {
            __state = null;
            if (__instance == null || _safeDrainDepth > 0) return;

            try
            {
                IList queue =
                    _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count == 0) return;

                QueuePatchState state = new QueuePatchState
                {
                    Queue = queue,
                    Isolated = true
                };

                for (int i = 0; i < queue.Count; i++)
                    state.HiddenItems.Add(queue[i]);

                queue.Clear();
                __state = state;
            }
            catch
            {
                __state = null;
            }
        }

        private static void WorkerPostfix(QueuePatchState __state)
        {
            RestoreHiddenContinuations(__state);
        }

        private static Exception WorkerFinalizer(
            Exception __exception,
            QueuePatchState __state)
        {
            RestoreHiddenContinuations(__state);
            return __exception;
        }

        private static void RestoreHiddenContinuations(QueuePatchState state)
        {
            if (state == null ||
                state.Restored ||
                !state.Isolated ||
                state.Queue == null)
                return;

            state.Restored = true;
            try
            {
                // Existing queued work remains ahead of callbacks that appended newer work while
                // native-removal processing ran.
                for (int i = state.HiddenItems.Count - 1; i >= 0; i--)
                    state.Queue.Insert(0, state.HiddenItems[i]);
            }
            catch
            {
                // The original worker exception, if any, remains authoritative.
            }
        }

        private static void MissionTickPostfix(Mission __instance)
        {
            if (__instance == null ||
                _safeDrainDepth > 0 ||
                _getMissionBehaviorMethod == null ||
                _processDeferredMethod == null)
                return;

            object behavior = null;
            BoundaryState boundary = null;

            try
            {
                behavior = _getMissionBehaviorMethod.Invoke(__instance, null);
                if (behavior == null) return;

                boundary = BoundaryStates.GetOrCreateValue(behavior);
                IList queue =
                    _pendingContinuationSpawnsField.GetValue(behavior) as IList;
                if (queue == null || queue.Count == 0) return;

                object head = queue[0];
                if (head == null)
                {
                    queue.RemoveAt(0);
                    return;
                }

                if (!Eligibilities.TryGetValue(
                        head,
                        out Eligibility eligibility) ||
                    eligibility == null)
                {
                    long firstEligible =
                        boundary.CompletedOuterTicks == long.MaxValue
                            ? long.MaxValue
                            : boundary.CompletedOuterTicks + 1L;
                    eligibility = new Eligibility
                    {
                        FirstEligibleOuterTick = firstEligible
                    };
                    Eligibilities.Remove(head);
                    Eligibilities.Add(head, eligibility);
                    return;
                }

                if (eligibility.FirstEligibleOuterTick >
                    boundary.CompletedOuterTicks)
                    return;

                if (HasCollisionOwnedWork(behavior))
                    return;

                TryLog(
                    "Processing one controlled penetration continuation after " +
                    "the complete Mission.OnTick boundary.");

                _safeDrainDepth++;
                try
                {
                    _processDeferredMethod.Invoke(behavior, null);
                }
                finally
                {
                    _safeDrainDepth--;
                }

                if (!ContainsByReference(queue, head))
                    Eligibilities.Remove(head);
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                TryLog(
                    "Deferred penetration continuation failed after Mission.OnTick: " +
                    inner.GetType().Name + ".");
            }
            catch (Exception exception)
            {
                TryLog(
                    "Deferred penetration continuation boundary failed: " +
                    exception.GetType().Name + ".");
            }
            finally
            {
                if (boundary != null &&
                    boundary.CompletedOuterTicks < long.MaxValue)
                {
                    boundary.CompletedOuterTicks++;
                }
            }
        }

        private static bool HasCollisionOwnedWork(object instance)
        {
            return ReadCount(_pendingCollisionContextsField, instance) > 0 ||
                   ReadCount(_earlyCollisionReactionsField, instance) > 0 ||
                   ReadCount(_pendingNativeMissileRemovalsField, instance) > 0;
        }

        private static int ReadCount(
            FieldInfo field,
            object instance)
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

        private static bool ContainsByReference(
            IList list,
            object item)
        {
            if (list == null || item == null) return false;

            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], item))
                    return true;
            }
            return false;
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                BoundaryStates.Remove(__instance);
        }

        private static void TryLog(string message)
        {
            if (_logMethod == null || string.IsNullOrEmpty(message)) return;
            try { _logMethod.Invoke(null, new object[] { message }); }
            catch { }
        }
    }
}
