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
    /// Makes a promoted native PassThrough authoritative at the callback boundary that controls
    /// the native missile lifetime. Mission.MissileHitCallback computes and returns the original
    /// terminal result after HandleMissileCollisionReaction has notified mission behaviors; merely
    /// changing the downstream reaction therefore leaves the engine free to delete the missile.
    ///
    /// This patch is active only while one real MissileHitCallback is on the current mission thread.
    /// It preserves the core's normal OnMissileCollisionReaction/OnMissileHit ordering, camera and
    /// render teardown, and exact live-missile reacquisition. No synthetic missile is created.
    /// </summary>
    internal static class NativePenetrationLifetimePatch
    {
        private sealed class HitScope
        {
            internal Mission Mission;
            internal object Behavior;
            internal int MissileIndex = -1;
            internal Mission.MissileCollisionReaction OriginalReaction;
            internal bool Promoted;
        }

        private sealed class HitPatchState
        {
            internal HitScope Previous;
            internal HitScope Current;
        }

        private sealed class TaggedMissileIndex
        {
            internal int MissileIndex;
        }

        private sealed class EarlyQueuePatchState
        {
            internal IList Queue;
            internal readonly List<object> ExistingItems = new List<object>();
            internal int MissileIndex = -1;
            internal bool QueueIsolated;
            internal bool Restored;
        }

        [ThreadStatic]
        private static HitScope _activeHit;

        private static readonly ConditionalWeakTable<object, TaggedMissileIndex> EarlyReactionMissiles =
            new ConditionalWeakTable<object, TaggedMissileIndex>();

        private static MethodInfo _getMissionBehaviorMethod;
        private static MethodInfo _findTrackedMissileMethod;
        private static MethodInfo _hasRemainingPenetrationMethod;
        private static MethodInfo _logMethod;

        private static FieldInfo _activeShotShooterField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _trackedSyntheticField;
        private static FieldInfo _earlyCollisionReactionsField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo missileHitCallback = typeof(Mission)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "MissileHitCallback" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 12);

            MethodInfo collisionHandler = typeof(Mission)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "HandleMissileCollisionReaction") return false;
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 12 &&
                           parameters[0].ParameterType == typeof(int) &&
                           parameters[1].ParameterType == typeof(Mission.MissileCollisionReaction) &&
                           parameters[4].ParameterType == typeof(Agent) &&
                           parameters[5].ParameterType == typeof(Agent) &&
                           parameters[6].ParameterType == typeof(bool);
                });

            MethodInfo queueEarlyReaction = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "QueueEarlyCollisionReaction" &&
                    method.GetParameters().Length == 3);

            MethodInfo consumeEarlyReaction = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TryConsumeEarlyCollisionReaction" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));

            MethodInfo openGetMissionBehavior = typeof(Mission)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "GetMissionBehavior" &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    method.GetParameters().Length == 0);

            _findTrackedMissileMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "FindTrackedMissile" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));

            _hasRemainingPenetrationMethod = behaviorType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "HasRemainingAgentPenetration" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 1);

            _logMethod = behaviorType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "Log" &&
                    method.ReturnType == typeof(void) &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(string));

            _activeShotShooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _earlyCollisionReactionsField = AccessTools.Field(behaviorType, "_earlyCollisionReactions");

            Type trackedType = _findTrackedMissileMethod?.ReturnType;
            _trackedSyntheticField = trackedType == null
                ? null
                : AccessTools.Field(trackedType, "SyntheticProjectile");

            if (missileHitCallback == null ||
                collisionHandler == null ||
                queueEarlyReaction == null ||
                consumeEarlyReaction == null ||
                openGetMissionBehavior == null ||
                _findTrackedMissileMethod == null ||
                _hasRemainingPenetrationMethod == null ||
                _activeShotShooterField == null ||
                _trackedMissilesField == null ||
                _trackedSyntheticField == null ||
                _earlyCollisionReactionsField == null)
                return;

            MethodInfo hitPrefix = AccessTools.Method(
                typeof(NativePenetrationLifetimePatch),
                nameof(MissileHitPrefix));
            MethodInfo hitPostfix = AccessTools.Method(
                typeof(NativePenetrationLifetimePatch),
                nameof(MissileHitPostfix));
            MethodInfo hitFinalizer = AccessTools.Method(
                typeof(NativePenetrationLifetimePatch),
                nameof(MissileHitFinalizer));
            MethodInfo collisionPrefix = AccessTools.Method(
                typeof(NativePenetrationLifetimePatch),
                nameof(CollisionPrefix));
            MethodInfo queuePrefix = AccessTools.Method(
                typeof(NativePenetrationLifetimePatch),
                nameof(EarlyQueuePrefix));
            MethodInfo queuePostfix = AccessTools.Method(
                typeof(NativePenetrationLifetimePatch),
                nameof(EarlyQueuePostfix));
            MethodInfo queueFinalizer = AccessTools.Method(
                typeof(NativePenetrationLifetimePatch),
                nameof(EarlyQueueFinalizer));
            MethodInfo consumePrefix = AccessTools.Method(
                typeof(NativePenetrationLifetimePatch),
                nameof(EarlyConsumePrefix));

            if (hitPrefix == null ||
                hitPostfix == null ||
                hitFinalizer == null ||
                collisionPrefix == null ||
                queuePrefix == null ||
                queuePostfix == null ||
                queueFinalizer == null ||
                consumePrefix == null)
                return;

            try
            {
                _getMissionBehaviorMethod = openGetMissionBehavior.MakeGenericMethod(behaviorType);

                harmony.Patch(
                    missileHitCallback,
                    prefix: new HarmonyMethod(hitPrefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(hitPostfix) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(hitFinalizer) { priority = Priority.Last });

                // This prefix is deliberately inert outside one active MissileHitCallback. Network
                // replay, campaign state and unrelated mods calling the public handler are untouched.
                harmony.Patch(
                    collisionHandler,
                    prefix: new HarmonyMethod(collisionPrefix) { priority = Priority.First });

                // The locked core receives collision reactions before OnMissileHit creates its
                // pending context. Tag the actual early-reaction object with the native missile index
                // and let the core's unchanged consumer process that exact object later.
                harmony.Patch(
                    queueEarlyReaction,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(queuePostfix) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(queueFinalizer) { priority = Priority.Last });

                harmony.Patch(
                    consumeEarlyReaction,
                    prefix: new HarmonyMethod(consumePrefix) { priority = Priority.First });
            }
            catch
            {
                _getMissionBehaviorMethod = null;
                _activeHit = null;
            }
        }

        private static void MissileHitPrefix(Mission __instance, out HitPatchState __state)
        {
            HitScope current = new HitScope { Mission = __instance };
            __state = new HitPatchState
            {
                Previous = _activeHit,
                Current = current
            };
            _activeHit = current;
        }

        private static void MissileHitPostfix(ref bool __result, HitPatchState __state)
        {
            HitScope scope = __state?.Current;
            if (scope == null || !scope.Promoted) return;

            // MissileHitCallback returns true for every reaction except native PassThrough. The
            // native layer consumes that return after the managed behavior callbacks and otherwise
            // deletes the missile even though the core just reacquired it successfully.
            __result = false;
            TryLog(
                "Preserved native lifetime for guided missile #" + scope.MissileIndex +
                " after promoted " + scope.OriginalReaction +
                " by returning the PassThrough result from MissileHitCallback.");
        }

        private static Exception MissileHitFinalizer(Exception __exception, HitPatchState __state)
        {
            _activeHit = __state?.Previous;
            return __exception;
        }

        private static void CollisionPrefix(
            Mission __instance,
            int missileIndex,
            ref Mission.MissileCollisionReaction collisionReaction,
            Agent attackerAgent,
            Agent attachedAgent,
            bool attachedToShield)
        {
            HitScope scope = _activeHit;
            if (scope == null ||
                !ReferenceEquals(scope.Mission, __instance) ||
                attachedAgent == null ||
                attachedToShield ||
                !IsPromotableTerminalReaction(collisionReaction) ||
                _getMissionBehaviorMethod == null)
                return;

            try
            {
                object behavior = _getMissionBehaviorMethod.Invoke(__instance, null);
                if (behavior == null) return;

                Agent activeShooter = _activeShotShooterField.GetValue(behavior) as Agent;
                if (activeShooter == null ||
                    (attackerAgent != null && !ReferenceEquals(activeShooter, attackerAgent)))
                    return;

                object tracked = _findTrackedMissileMethod.Invoke(
                    behavior,
                    new object[] { missileIndex });
                if (tracked == null || IsOriginalNativeVolleyProjectile(behavior, tracked))
                    return;

                bool hasRemaining = (bool)_hasRemainingPenetrationMethod.Invoke(
                    null,
                    new[] { tracked });
                if (!hasRemaining) return;

                scope.Behavior = behavior;
                scope.MissileIndex = missileIndex;
                scope.OriginalReaction = collisionReaction;
                scope.Promoted = true;

                collisionReaction = Mission.MissileCollisionReaction.PassThrough;
            }
            catch
            {
                // Any unresolved private state leaves Bannerlord's original terminal result intact.
            }
        }

        private static void EarlyQueuePrefix(object __instance, out EarlyQueuePatchState __state)
        {
            __state = null;
            HitScope scope = _activeHit;
            if (__instance == null ||
                scope == null ||
                !scope.Promoted ||
                scope.MissileIndex < 0 ||
                !ReferenceEquals(scope.Behavior, __instance))
                return;

            try
            {
                IList queue = _earlyCollisionReactionsField.GetValue(__instance) as IList;
                if (queue == null) return;

                EarlyQueuePatchState state = new EarlyQueuePatchState
                {
                    Queue = queue,
                    MissileIndex = scope.MissileIndex,
                    QueueIsolated = true
                };

                for (int i = 0; i < queue.Count; i++)
                    state.ExistingItems.Add(queue[i]);

                // Isolate the one object created by the original method. This also prevents the
                // core's fixed 32-entry trimming loop from discarding an older exact reaction during
                // a 48-projectile stress callback burst.
                queue.Clear();
                __state = state;
            }
            catch
            {
                __state = null;
            }
        }

        private static void EarlyQueuePostfix(EarlyQueuePatchState __state)
        {
            RestoreAndTagEarlyQueue(__state);
        }

        private static Exception EarlyQueueFinalizer(
            Exception __exception,
            EarlyQueuePatchState __state)
        {
            RestoreAndTagEarlyQueue(__state);
            return __exception;
        }

        private static void RestoreAndTagEarlyQueue(EarlyQueuePatchState state)
        {
            if (state == null ||
                state.Restored ||
                !state.QueueIsolated ||
                state.Queue == null)
                return;

            state.Restored = true;
            try
            {
                List<object> createdItems = new List<object>();
                for (int i = 0; i < state.Queue.Count; i++)
                    createdItems.Add(state.Queue[i]);

                state.Queue.Clear();
                for (int i = 0; i < state.ExistingItems.Count; i++)
                    state.Queue.Add(state.ExistingItems[i]);

                for (int i = 0; i < createdItems.Count; i++)
                {
                    object item = createdItems[i];
                    state.Queue.Add(item);
                    if (item == null) continue;

                    EarlyReactionMissiles.Remove(item);
                    EarlyReactionMissiles.Add(
                        item,
                        new TaggedMissileIndex { MissileIndex = state.MissileIndex });
                }
            }
            catch
            {
                // Preserve the original mission exception and leave the core queue authoritative.
            }
        }

        private static void EarlyConsumePrefix(object __instance, int missileIndex)
        {
            if (__instance == null || missileIndex < 0) return;

            try
            {
                IList queue = _earlyCollisionReactionsField.GetValue(__instance) as IList;
                if (queue == null || queue.Count <= 1) return;

                for (int i = 0; i < queue.Count; i++)
                {
                    object item = queue[i];
                    if (item == null ||
                        !EarlyReactionMissiles.TryGetValue(item, out TaggedMissileIndex tag) ||
                        tag == null ||
                        tag.MissileIndex != missileIndex)
                        continue;

                    if (i > 0)
                    {
                        queue.RemoveAt(i);
                        queue.Insert(0, item);
                    }
                    return;
                }
            }
            catch
            {
                // The original shooter/victim matcher remains the fallback.
            }
        }

        private static bool IsOriginalNativeVolleyProjectile(object behavior, object tracked)
        {
            try
            {
                if ((bool)_trackedSyntheticField.GetValue(tracked)) return false;

                IList trackedMissiles = _trackedMissilesField.GetValue(behavior) as IList;
                if (trackedMissiles == null) return false;

                int originalNativeCount = 0;
                for (int i = 0; i < trackedMissiles.Count; i++)
                {
                    object candidate = trackedMissiles[i];
                    if (candidate == null || (bool)_trackedSyntheticField.GetValue(candidate))
                        continue;

                    originalNativeCount++;
                    if (originalNativeCount >= 2) return true;
                }
            }
            catch
            {
                return true;
            }

            // One original arrow plus generated Guided Arrow followers is an ordinary split shot.
            return false;
        }

        private static bool IsPromotableTerminalReaction(
            Mission.MissileCollisionReaction reaction)
        {
            return reaction == Mission.MissileCollisionReaction.Stick ||
                   reaction == Mission.MissileCollisionReaction.BecomeInvisible;
        }

        private static void TryLog(string message)
        {
            if (_logMethod == null || string.IsNullOrEmpty(message)) return;
            try { _logMethod.Invoke(null, new object[] { message }); }
            catch { }
        }
    }
}
