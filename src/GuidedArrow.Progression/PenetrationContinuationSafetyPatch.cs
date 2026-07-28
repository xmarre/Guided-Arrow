using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Applies narrow runtime corrections around the verified v1.1.17 core's
    /// synthetic penetration continuation without rebuilding the core assembly.
    /// </summary>
    internal static class PenetrationContinuationSafetyPatch
    {
        private const float CoreContinuationOffset = 0.42f;
        private const float SafeContinuationExitDistance = 1.25f;

        private static FieldInfo _impactPositionField;
        private static FieldInfo _impactVelocityField;
        private static FieldInfo _impactDirectionField;
        private static FieldInfo _trackedMissileNativeMissileField;
        private static FieldInfo _trackedMissileIndexField;
        private static FieldInfo _pendingContinuationSpawnsField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _leaderMissileField;
        private static FieldInfo _leaderIndexField;
        private static FieldInfo _cameraMissileIndexField;

        private sealed class ContinuationPatchState
        {
            internal object Context;
            internal Vec3 OriginalImpactPosition;
            internal bool ImpactPositionAdjusted;
        }

        private sealed class DeferredBatchState
        {
            internal object Instance;
            internal IList Queue;
            internal object Head;
            internal readonly List<object> DeferredTail = new List<object>();
            internal bool Restored;
        }

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo spawnMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                    m.Name == "TrySpawnPenetrationContinuation" &&
                    m.ReturnType == typeof(bool) &&
                    m.GetParameters().Length == 3);
            if (spawnMethod == null) return;

            ParameterInfo[] parameters = spawnMethod.GetParameters();
            Type trackedType = parameters[0].ParameterType;
            Type contextType = parameters[1].ParameterType;

            _impactPositionField = AccessTools.Field(contextType, "ImpactPosition");
            _impactVelocityField = AccessTools.Field(contextType, "ImpactVelocity");
            _impactDirectionField = AccessTools.Field(contextType, "ImpactDirection");
            _trackedMissileNativeMissileField = AccessTools.Field(trackedType, "Missile");
            _trackedMissileIndexField = AccessTools.Field(trackedType, "Index");
            _pendingContinuationSpawnsField = AccessTools.Field(behaviorType, "_pendingContinuationSpawns");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _leaderMissileField = AccessTools.Field(behaviorType, "_missile");
            _leaderIndexField = AccessTools.Field(behaviorType, "_missileIndex");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");

            if (_impactPositionField == null ||
                _impactVelocityField == null ||
                _impactDirectionField == null ||
                _trackedMissileNativeMissileField == null ||
                _trackedMissileIndexField == null)
                return;

            try
            {
                harmony.Patch(
                    spawnMethod,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PenetrationContinuationSafetyPatch), nameof(SpawnPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PenetrationContinuationSafetyPatch), nameof(SpawnPostfix))));
            }
            catch
            {
                // The stable core remains untouched if a future version changes its internals.
            }

            MethodInfo deferredMethod = AccessTools.Method(behaviorType, "ProcessDeferredNativeMissileWork");
            if (deferredMethod == null || _pendingContinuationSpawnsField == null || _trackedMissilesField == null)
                return;

            try
            {
                harmony.Patch(
                    deferredMethod,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PenetrationContinuationSafetyPatch), nameof(DeferredPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PenetrationContinuationSafetyPatch), nameof(DeferredPostfix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(PenetrationContinuationSafetyPatch), nameof(DeferredFinalizer))));
            }
            catch
            {
                // If the worker cannot be patched, the spawn validation still remains active.
            }
        }

        private static bool SpawnPrefix(object[] __args, ref bool __result, out ContinuationPatchState __state)
        {
            __state = null;
            if (__args == null || __args.Length < 2 || __args[1] == null)
            {
                __result = false;
                return false;
            }

            object context = __args[1];
            try
            {
                Vec3 impactPosition = (Vec3)_impactPositionField.GetValue(context);
                Vec3 impactVelocity = (Vec3)_impactVelocityField.GetValue(context);
                Vec3 impactDirection = (Vec3)_impactDirectionField.GetValue(context);

                if (!TryComputeNormalizedDirection(impactVelocity, impactDirection, out Vec3 direction))
                {
                    __result = false;
                    return false;
                }

                float desiredExitDistance = SafeContinuationExitDistance;

                // Agent.Position is backed by a native position pointer. A concentrated volley can
                // deliver later callbacks after an earlier impact has started victim teardown, so
                // continuation placement must use only collision value data and a fixed safe exit.
                // The stable core already advances 0.42 m; apply only the remaining distance.
                float additionalOffset = Math.Max(0f, desiredExitDistance - CoreContinuationOffset);
                _impactPositionField.SetValue(context, impactPosition + direction * additionalOffset);

                __state = new ContinuationPatchState
                {
                    Context = context,
                    OriginalImpactPosition = impactPosition,
                    ImpactPositionAdjusted = true
                };
                return true;
            }
            catch
            {
                // Do not let the uncorrected continuation spawn inside an agent.
                __state = null;
                __result = false;
                return false;
            }
        }

        private static void SpawnPostfix(object[] __args, ref bool __result, ContinuationPatchState __state)
        {
            if (__state != null && __state.ImpactPositionAdjusted && __state.Context != null)
            {
                try { _impactPositionField.SetValue(__state.Context, __state.OriginalImpactPosition); }
                catch { }
            }

            if (!__result) return;

            // Harmony exposes the original out parameter through __args[2]. The stable
            // worker dereferences it immediately when the method returns true, so never
            // allow a true/null or true/incomplete result to cross that boundary.
            object continuation =
                __args != null && __args.Length >= 3
                    ? __args[2]
                    : null;
            if (continuation == null)
            {
                __result = false;
                return;
            }

            object missile;
            try { missile = _trackedMissileNativeMissileField.GetValue(continuation); }
            catch
            {
                __result = false;
                return;
            }
            if (missile == null)
                __result = false;

            // Do not call AgentVisuals.GetEntity or PassThroughEntity here. The continuation is
            // already created beyond the fixed safe exit distance, while the previous victim's
            // native presentation may have been destroyed before this deferred worker runs.
        }

        private static void DeferredPrefix(object __instance, out DeferredBatchState __state)
        {
            __state = null;
            if (__instance == null || _pendingContinuationSpawnsField == null) return;

            try
            {
                IList queue = _pendingContinuationSpawnsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count <= 1) return;

                DeferredBatchState state = new DeferredBatchState
                {
                    Instance = __instance,
                    Queue = queue,
                    Head = queue[0]
                };

                // Bannerlord's custom-missile creation and the stable core's one-shot
                // damage bridge are not safe when dozens of continuations are spawned in
                // the same mission tick. Process exactly one and defer the rest in order.
                while (queue.Count > 1)
                {
                    state.DeferredTail.Add(queue[1]);
                    queue.RemoveAt(1);
                }

                __state = state;
            }
            catch
            {
                __state = null;
            }
        }

        private static void DeferredPostfix(object __instance, DeferredBatchState __state)
        {
            RestoreDeferredTail(__state, removeFailedHead: false);
            SanitizeTrackedMissiles(__instance);
        }

        private static Exception DeferredFinalizer(object __instance, Exception __exception, DeferredBatchState __state)
        {
            if (__exception == null) return null;

            // The current item has already been removed from the core queue before its
            // direct dereferences. Drop that one failed item, restore the untouched tail,
            // and repair leader/camera references so one malformed continuation cannot
            // terminate the entire native mission tick.
            RestoreDeferredTail(__state, removeFailedHead: true);
            SanitizeTrackedMissiles(__instance);

            return __exception is NullReferenceException ? null : __exception;
        }

        private static void RestoreDeferredTail(DeferredBatchState state, bool removeFailedHead)
        {
            if (state == null || state.Restored || state.Queue == null) return;
            state.Restored = true;

            try
            {
                if (removeFailedHead &&
                    state.Queue.Count > 0 &&
                    ReferenceEquals(state.Queue[0], state.Head))
                {
                    state.Queue.RemoveAt(0);
                }

                // Insert the deferred original entries ahead of any new work queued by
                // custom-missile callbacks during the current tick.
                for (int i = state.DeferredTail.Count - 1; i >= 0; i--)
                    state.Queue.Insert(0, state.DeferredTail[i]);
            }
            catch
            {
                // A failed restoration must not replace the original mission exception.
            }
        }

        private static void SanitizeTrackedMissiles(object instance)
        {
            if (instance == null || _trackedMissilesField == null) return;

            try
            {
                IList tracked = _trackedMissilesField.GetValue(instance) as IList;
                if (tracked == null) return;

                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    object item = tracked[i];
                    bool valid = item != null;
                    if (valid)
                    {
                        try { valid = _trackedMissileNativeMissileField.GetValue(item) != null; }
                        catch { valid = false; }
                    }
                    if (!valid) tracked.RemoveAt(i);
                }

                if (tracked.Count == 0)
                {
                    _leaderMissileField?.SetValue(instance, null);
                    _leaderIndexField?.SetValue(instance, -1);
                    _cameraMissileIndexField?.SetValue(instance, -1);
                    return;
                }

                object leader = tracked[0];
                object leaderMissile = _trackedMissileNativeMissileField.GetValue(leader);
                int leaderIndex = (int)_trackedMissileIndexField.GetValue(leader);

                object currentLeader = _leaderMissileField?.GetValue(instance);
                if (currentLeader == null)
                {
                    _leaderMissileField?.SetValue(instance, leaderMissile);
                    _leaderIndexField?.SetValue(instance, leaderIndex);
                }

                if (_cameraMissileIndexField != null)
                {
                    int cameraIndex = (int)_cameraMissileIndexField.GetValue(instance);
                    if (cameraIndex < 0) return;

                    bool cameraExists = false;
                    for (int i = 0; i < tracked.Count; i++)
                    {
                        object item = tracked[i];
                        if (item != null && (int)_trackedMissileIndexField.GetValue(item) == cameraIndex)
                        {
                            cameraExists = true;
                            break;
                        }
                    }
                    if (!cameraExists) _cameraMissileIndexField.SetValue(instance, leaderIndex);
                }
            }
            catch
            {
                // The stable core retains control if a future version changes these fields.
            }
        }

        private static bool TryComputeNormalizedDirection(
            Vec3 impactVelocity,
            Vec3 impactDirection,
            out Vec3 direction)
        {
            direction = impactVelocity;
            float speed = direction.Length;
            if (!IsFinite(speed) || speed <= 0.001f)
            {
                direction = impactDirection;
                speed = direction.Length;
            }
            if (!IsFinite(speed) || speed <= 0.001f)
                return false;

            direction /= speed;
            return IsFinite(direction);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vec3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }
    }
}
