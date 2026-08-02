using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Prevents Mission.AddCustomMissile from racing Bannerlord's native disposal of the exact
    /// terminal source missile. A terminal collision can queue a continuation before the source
    /// wrapper has disappeared from Mission's missile registry. Spawning while that exact wrapper
    /// is still registered can corrupt the reusable native missile slot and raise
    /// AccessViolationException after several otherwise successful penetrations.
    /// </summary>
    internal static class NativeContinuationSourceReleasePatch
    {
        private sealed class BehaviorState
        {
            internal long CompletedWorkerPasses;
        }

        private sealed class ItemState
        {
            internal long FirstReleasedPass = -1L;
            internal bool WaitLogged;
        }

        private sealed class HiddenQueueState
        {
            internal IList Queue;
            internal readonly List<object> Items = new List<object>();
            internal BehaviorState Behavior;
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
        private static FieldInfo _pendingSourceField;
        private static FieldInfo _trackedMissileField;
        private static FieldInfo _trackedIndexField;
        private static MethodInfo _logMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type pendingType = behaviorType.GetNestedType(
                "PendingContinuationSpawn",
                BindingFlags.NonPublic);
            Type trackedType = behaviorType.GetNestedType(
                "TrackedMissile",
                BindingFlags.NonPublic);
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
            _pendingSourceField = pendingType == null
                ? null
                : AccessTools.Field(pendingType, "Source");
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

            if (worker == null ||
                _missionProperty == null ||
                _missionMissilesDictionaryField == null ||
                _pendingContinuationSpawnsField == null ||
                _pendingSourceField == null ||
                _trackedMissileField == null ||
                _trackedIndexField == null)
                return;

            MethodInfo prefix = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(WorkerPrefix));
            MethodInfo postfix = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(WorkerPostfix));
            MethodInfo finalizer = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(WorkerFinalizer));
            MethodInfo clear = AccessTools.Method(
                typeof(NativeContinuationSourceReleasePatch),
                nameof(ClearPrefix));

            if (prefix == null || postfix == null || finalizer == null || clear == null)
                return;

            try
            {
                // Run before the existing one-item serializer. When blocked, the original worker
                // still drains native-removal work but sees no continuation spawn request.
                harmony.Patch(
                    worker,
                    prefix: new HarmonyMethod(prefix) { priority = int.MaxValue },
                    postfix: new HarmonyMethod(postfix) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(finalizer) { priority = Priority.Last });

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

        private static void WorkerPrefix(
            object __instance,
            out HiddenQueueState __state)
        {
            __state = null;
            if (__instance == null) return;

            BehaviorState behavior = BehaviorStates.GetOrCreateValue(__instance);
            HiddenQueueState state = new HiddenQueueState { Behavior = behavior };
            __state = state;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                state.Queue = queue;
                if (queue == null || queue.Count == 0) return;

                object head = queue[0];
                if (head == null) return;

                ItemState item = ItemStates.GetOrCreateValue(head);
                bool exactSourceStillRegistered = IsExactSourceStillRegistered(
                    __instance,
                    head,
                    out bool registryResolved);

                if (!registryResolved)
                {
                    // Reflection failure must not become an endless penetration stall. Preserve a
                    // conservative two-pass quarantine before falling back to the locked core.
                    if (item.FirstReleasedPass < 0L)
                    {
                        item.FirstReleasedPass = behavior.CompletedWorkerPasses;
                        Hide(queue, state);
                        return;
                    }
                    if (behavior.CompletedWorkerPasses <= item.FirstReleasedPass + 1L)
                    {
                        Hide(queue, state);
                        return;
                    }
                    return;
                }

                if (exactSourceStillRegistered)
                {
                    item.FirstReleasedPass = -1L;
                    if (!item.WaitLogged)
                    {
                        item.WaitLogged = true;
                        TryLog(
                            __instance,
                            "Deferred penetration continuation is waiting for the exact native source missile to leave the mission registry.");
                    }
                    Hide(queue, state);
                    return;
                }

                // Observe one complete worker pass with the exact source absent before allowing
                // AddCustomMissile. This avoids spawning in the same native-container pass that
                // removed the terminal source wrapper.
                if (item.FirstReleasedPass < 0L)
                {
                    item.FirstReleasedPass = behavior.CompletedWorkerPasses;
                    Hide(queue, state);
                    return;
                }

                if (behavior.CompletedWorkerPasses <= item.FirstReleasedPass)
                {
                    Hide(queue, state);
                    return;
                }
            }
            catch
            {
                // Failure-open after the existing serializer; the stable core still validates all
                // managed launch data and the returned native missile entity.
            }
        }

        private static bool IsExactSourceStillRegistered(
            object instance,
            object pending,
            out bool registryResolved)
        {
            registryResolved = false;
            if (instance == null || pending == null) return false;

            try
            {
                object source = _pendingSourceField.GetValue(pending);
                if (source == null) return false;

                object sourceMissile = _trackedMissileField.GetValue(source);
                int sourceIndex = (int)_trackedIndexField.GetValue(source);
                Mission mission = _missionProperty.GetValue(instance, null) as Mission;
                if (mission == null || sourceIndex < 0 || sourceMissile == null)
                    return false;

                object registry = _missionMissilesDictionaryField.GetValue(mission);
                if (registry == null) return false;

                object registered;
                bool found = TryGetRegisteredMissile(
                    registry,
                    sourceIndex,
                    out registered);
                registryResolved = true;
                return found && ReferenceEquals(registered, sourceMissile);
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
            RestoreAndComplete(__state);
        }

        private static Exception WorkerFinalizer(
            Exception __exception,
            HiddenQueueState __state)
        {
            RestoreAndComplete(__state);
            return __exception;
        }

        private static void RestoreAndComplete(HiddenQueueState state)
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
            finally
            {
                if (state.Behavior != null &&
                    state.Behavior.CompletedWorkerPasses < long.MaxValue)
                {
                    state.Behavior.CompletedWorkerPasses++;
                }
            }
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
