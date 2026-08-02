using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Prevents Mission.AddCustomMissile from racing Bannerlord's native disposal of the exact
    /// terminal source missile. The core clears the tracked missile's presentation handles while
    /// QueuePenetrationContinuation is still returning, so the exact source wrapper must be captured
    /// before that method runs. A worker pass is not an outer native-frame boundary; release is
    /// therefore followed by completed display boundaries and a short real-time quarantine while
    /// the actual spawn remains in GuidedArrowBehavior.OnMissionTick.
    /// </summary>
    internal static class NativeContinuationSourceReleasePatch
    {
        private const int RequiredDisplayBoundaries = 6;
        private const double MinimumReleaseQuarantineSeconds = 0.15d;

        private sealed class BehaviorState
        {
            internal long CompletedDisplayTicks;
        }

        private sealed class QueuePatchState
        {
            internal int PreviousCount = -1;
            internal object SourceMissile;
            internal int SourceIndex = -1;
        }

        private sealed class ItemState
        {
            internal object ExactSourceMissile;
            internal int SourceIndex = -1;
            internal long FirstReleasedDisplayTick = -1L;
            internal long NotBeforeTimestamp;
            internal bool SourceWaitLogged;
            internal bool CollisionWaitLogged;
            internal bool QuarantineLogged;
        }

        private sealed class HiddenQueueState
        {
            internal IList Queue;
            internal readonly List<object> Items = new List<object>();
            internal bool Hidden;
            internal bool Restored;
        }

        private static readonly ConditionalWeakTable<object, BehaviorState> BehaviorStates =
            new ConditionalWeakTable<object, BehaviorState>();
        private static readonly ConditionalWeakTable<object, ItemState> ItemStates =
            new ConditionalWeakTable<object, ItemState>();

        private static PropertyInfo _missionProperty;
        private static FieldInfo _missionMissilesDictionaryField;
        private static FieldInfo _pendingContinuationSpawnsField;
        private static FieldInfo _pendingCollisionContextsField;
        private static FieldInfo _earlyCollisionReactionsField;
        private static FieldInfo _pendingNativeMissileRemovalsField;
        private static FieldInfo _trackedMissileField;
        private static FieldInfo _trackedIndexField;
        private static MethodInfo _logMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType(
                "TrackedMissile",
                BindingFlags.NonPublic);
            MethodInfo queueMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "QueuePenetrationContinuation" &&
                    method.GetParameters().Length == 2);
            MethodInfo worker = AccessTools.Method(
                behaviorType,
                "ProcessDeferredNativeMissileWork");

            _missionProperty = AccessTools.Property(behaviorType, "Mission");
            _missionMissilesDictionaryField = AccessTools.Field(
                typeof(Mission),
                "_missilesDictionary");
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
            _trackedMissileField = trackedType == null
                ? null
                : AccessTools.Field(trackedType, "Missile");
            _trackedIndexField = trackedType == null
                ? null
                : AccessTools.Field(trackedType, "Index");
            _logMethod = AccessTools.Method(
                behaviorType,
                "Log",
                new[] { typeof(string) });

            if (queueMethod == null ||
                worker == null ||
                _missionProperty == null ||
                _missionMissilesDictionaryField == null ||
                _pendingContinuationSpawnsField == null ||
                _pendingCollisionContextsField == null ||
                _earlyCollisionReactionsField == null ||
                _pendingNativeMissileRemovalsField == null ||
                _trackedMissileField == null ||
                _trackedIndexField == null)
                return;

            MethodInfo queuePrefix = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(QueuePrefix));
            MethodInfo queuePostfix = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(QueuePostfix));
            MethodInfo workerPrefix = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(WorkerPrefix));
            MethodInfo workerPostfix = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(WorkerPostfix));
            MethodInfo workerFinalizer = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(WorkerFinalizer));
            MethodInfo displayPostfix = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(DisplayPostfix));
            MethodInfo clear = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(ClearPrefix));

            if (queuePrefix == null ||
                queuePostfix == null ||
                workerPrefix == null ||
                workerPostfix == null ||
                workerFinalizer == null ||
                displayPostfix == null ||
                clear == null)
                return;

            try
            {
                harmony.Patch(
                    queueMethod,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(queuePostfix) { priority = Priority.Last });

                // Run before the existing one-item serializer. When blocked, the original worker
                // still drains native-removal work but sees no continuation spawn request.
                harmony.Patch(
                    worker,
                    prefix: new HarmonyMethod(workerPrefix) { priority = int.MaxValue },
                    postfix: new HarmonyMethod(workerPostfix) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(workerFinalizer) { priority = Priority.Last });

                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate =>
                        candidate.Name == "OnPreDisplayMissionTick" &&
                        !candidate.IsAbstract))
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
                                prefix: new HarmonyMethod(clear) { priority = Priority.First });
                        }
                        catch { }
                    }
                }
            }
            catch
            {
                // Unknown private layouts retain the locked core's normal worker.
            }
        }

        private static void QueuePrefix(
            object __instance,
            object __0,
            out QueuePatchState __state)
        {
            __state = new QueuePatchState();
            if (__instance == null || __0 == null) return;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue != null) __state.PreviousCount = queue.Count;
                __state.SourceMissile = _trackedMissileField.GetValue(__0);
                __state.SourceIndex = (int)_trackedIndexField.GetValue(__0);
            }
            catch
            {
                __state.SourceMissile = null;
                __state.SourceIndex = -1;
            }
        }

        private static void QueuePostfix(
            object __instance,
            QueuePatchState __state)
        {
            if (__instance == null || __state == null || __state.PreviousCount < 0)
                return;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count <= __state.PreviousCount) return;

                for (int i = __state.PreviousCount; i < queue.Count; i++)
                {
                    object pending = queue[i];
                    if (pending == null) continue;

                    ItemState item = ItemStates.GetOrCreateValue(pending);
                    item.ExactSourceMissile = __state.SourceMissile;
                    item.SourceIndex = __state.SourceIndex;
                    item.FirstReleasedDisplayTick = -1L;
                    item.NotBeforeTimestamp = 0L;
                    item.SourceWaitLogged = false;
                    item.CollisionWaitLogged = false;
                    item.QuarantineLogged = false;
                }
            }
            catch
            {
                // WorkerPrefix falls back to the same display/time quarantine without identity data.
            }
        }

        private static void WorkerPrefix(
            object __instance,
            out HiddenQueueState __state)
        {
            __state = null;
            if (__instance == null) return;

            HiddenQueueState state = new HiddenQueueState();
            __state = state;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                state.Queue = queue;
                if (queue == null || queue.Count == 0) return;

                object head = queue[0];
                if (head == null) return;

                ItemState item = ItemStates.GetOrCreateValue(head);
                BehaviorState behavior = BehaviorStates.GetOrCreateValue(__instance);

                if (HasCollisionOwnedWork(__instance))
                {
                    if (!item.CollisionWaitLogged)
                    {
                        item.CollisionWaitLogged = true;
                        TryLog(
                            __instance,
                            "Deferred penetration continuation is waiting for native collision/removal work to drain.");
                    }
                    ResetReleaseObservation(item);
                    Hide(queue, state);
                    return;
                }

                bool exactSourceStillRegistered = IsExactSourceStillRegistered(
                    __instance,
                    item,
                    out bool registryResolved);

                if (registryResolved && exactSourceStillRegistered)
                {
                    ResetReleaseObservation(item);
                    if (!item.SourceWaitLogged)
                    {
                        item.SourceWaitLogged = true;
                        TryLog(
                            __instance,
                            "Deferred penetration continuation is waiting for the exact native source missile to leave the mission registry.");
                    }
                    Hide(queue, state);
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                if (item.FirstReleasedDisplayTick < 0L)
                {
                    item.FirstReleasedDisplayTick = behavior.CompletedDisplayTicks;
                    item.NotBeforeTimestamp = AddSeconds(
                        now,
                        MinimumReleaseQuarantineSeconds);
                    if (!item.QuarantineLogged)
                    {
                        item.QuarantineLogged = true;
                        TryLog(
                            __instance,
                            "Deferred penetration continuation entered the native-frame release quarantine.");
                    }
                    Hide(queue, state);
                    return;
                }

                long requiredDisplayTick = item.FirstReleasedDisplayTick >=
                                           long.MaxValue - RequiredDisplayBoundaries
                    ? long.MaxValue
                    : item.FirstReleasedDisplayTick + RequiredDisplayBoundaries;

                if (behavior.CompletedDisplayTicks < requiredDisplayTick ||
                    now < item.NotBeforeTimestamp)
                {
                    Hide(queue, state);
                    return;
                }

                // Identity data can be unavailable only if a future core clears the source before
                // QueuePrefix. The display/time boundary remains fail-safe in that case. When exact
                // identity is available, re-check immediately before the spawn is exposed.
                if (registryResolved && IsExactSourceStillRegistered(
                        __instance,
                        item,
                        out bool finalRegistryResolved) &&
                    finalRegistryResolved)
                {
                    ResetReleaseObservation(item);
                    Hide(queue, state);
                }
            }
            catch
            {
                // Fail closed for this pass. A later behavior tick may resolve the reflected state.
                if (state.Queue != null) Hide(state.Queue, state);
            }
        }

        private static void ResetReleaseObservation(ItemState item)
        {
            if (item == null) return;
            item.FirstReleasedDisplayTick = -1L;
            item.NotBeforeTimestamp = 0L;
            item.QuarantineLogged = false;
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

        private static bool IsExactSourceStillRegistered(
            object instance,
            ItemState item,
            out bool registryResolved)
        {
            registryResolved = false;
            if (instance == null || item == null ||
                item.SourceIndex < 0 || item.ExactSourceMissile == null)
                return false;

            try
            {
                Mission mission = _missionProperty.GetValue(instance, null) as Mission;
                if (mission == null) return false;

                object registry = _missionMissilesDictionaryField.GetValue(mission);
                if (registry == null) return false;

                bool found = TryGetRegisteredMissile(
                    registry,
                    item.SourceIndex,
                    out object registered);
                registryResolved = true;
                return found && ReferenceEquals(registered, item.ExactSourceMissile);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetRegisteredMissile(
            object registry,
            int index,
            out object missile)
        {
            missile = null;
            if (registry == null || index < 0) return false;

            if (registry is IDictionary dictionary)
            {
                if (!dictionary.Contains(index)) return false;
                missile = dictionary[index];
                return missile != null;
            }

            MethodInfo tryGetValue = null;
            foreach (MethodInfo method in registry.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "TryGetValue" || method.ReturnType != typeof(bool))
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(int) &&
                    parameters[1].ParameterType.IsByRef)
                {
                    tryGetValue = method;
                    break;
                }
            }

            if (tryGetValue == null) return false;
            object[] args = { index, null };
            object result = tryGetValue.Invoke(registry, args);
            if (!(result is bool found) || !found || args[1] == null)
                return false;

            missile = args[1];
            return true;
        }

        private static void Hide(IList queue, HiddenQueueState state)
        {
            if (queue == null || state == null || state.Hidden) return;
            for (int i = 0; i < queue.Count; i++)
                state.Items.Add(queue[i]);
            queue.Clear();
            state.Hidden = true;
        }

        private static void WorkerPostfix(HiddenQueueState __state)
        {
            Restore(__state);
        }

        private static Exception WorkerFinalizer(
            Exception __exception,
            HiddenQueueState __state)
        {
            Restore(__state);
            return __exception;
        }

        private static void Restore(HiddenQueueState state)
        {
            if (state == null || state.Restored) return;
            state.Restored = true;

            try
            {
                if (state.Hidden && state.Queue != null)
                {
                    // Preserve FIFO ownership ahead of any continuation work queued by callbacks
                    // while the original worker drained native-removal work.
                    for (int i = state.Items.Count - 1; i >= 0; i--)
                        state.Queue.Insert(0, state.Items[i]);
                }
            }
            catch { }
        }

        private static void DisplayPostfix(object __instance)
        {
            if (__instance == null) return;
            BehaviorState state = BehaviorStates.GetOrCreateValue(__instance);
            if (state.CompletedDisplayTicks < long.MaxValue)
                state.CompletedDisplayTicks++;
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
                BehaviorStates.Remove(__instance);
        }

        private static void TryLog(object instance, string message)
        {
            if (_logMethod == null || instance == null || string.IsNullOrEmpty(message))
                return;
            try { _logMethod.Invoke(instance, new object[] { message }); }
            catch { }
        }
    }
}
