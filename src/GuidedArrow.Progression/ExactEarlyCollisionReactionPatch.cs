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
    /// Correlates the core's early collision-reaction queue with the exact native missile index.
    /// The patch observes Mission.MissileHitCallback without mutating its reaction or return value,
    /// so Bannerlord retains complete ownership of collision presentation, sound and teardown.
    /// </summary>
    internal static class ExactEarlyCollisionReactionPatch
    {
        private sealed class HitScope
        {
            internal Mission Mission;
            internal int MissileIndex = -1;
            internal bool HasVictim;
            internal bool HitShield;
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

        private static PropertyInfo _missileIndexProperty;
        private static PropertyInfo _shieldHitProperty;
        private static FieldInfo _earlyCollisionReactionsField;
        private static MethodInfo _findTrackedMissileMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo missileHitCallback = typeof(Mission)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "MissileHitCallback" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 12);

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

            _findTrackedMissileMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "FindTrackedMissile" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));

            _missileIndexProperty = typeof(AttackCollisionData).GetProperty(
                "AffectorWeaponSlotOrMissileIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _shieldHitProperty = typeof(AttackCollisionData).GetProperty(
                "AttackBlockedWithShield",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _earlyCollisionReactionsField = AccessTools.Field(
                behaviorType,
                "_earlyCollisionReactions");

            if (missileHitCallback == null ||
                queueEarlyReaction == null ||
                consumeEarlyReaction == null ||
                _findTrackedMissileMethod == null ||
                _missileIndexProperty == null ||
                _shieldHitProperty == null ||
                _earlyCollisionReactionsField == null)
                return;

            MethodInfo hitPrefix = AccessTools.Method(
                typeof(ExactEarlyCollisionReactionPatch),
                nameof(MissileHitPrefix));
            MethodInfo hitFinalizer = AccessTools.Method(
                typeof(ExactEarlyCollisionReactionPatch),
                nameof(MissileHitFinalizer));
            MethodInfo queuePrefix = AccessTools.Method(
                typeof(ExactEarlyCollisionReactionPatch),
                nameof(EarlyQueuePrefix));
            MethodInfo queuePostfix = AccessTools.Method(
                typeof(ExactEarlyCollisionReactionPatch),
                nameof(EarlyQueuePostfix));
            MethodInfo queueFinalizer = AccessTools.Method(
                typeof(ExactEarlyCollisionReactionPatch),
                nameof(EarlyQueueFinalizer));
            MethodInfo consumePrefix = AccessTools.Method(
                typeof(ExactEarlyCollisionReactionPatch),
                nameof(EarlyConsumePrefix));

            if (hitPrefix == null ||
                hitFinalizer == null ||
                queuePrefix == null ||
                queuePostfix == null ||
                queueFinalizer == null ||
                consumePrefix == null)
                return;

            try
            {
                harmony.Patch(
                    missileHitCallback,
                    prefix: new HarmonyMethod(hitPrefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(hitFinalizer) { priority = Priority.Last });

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
                _activeHit = null;
            }
        }

        internal static bool TryGetActiveAgentHit(
            int missileIndex,
            out bool hitShield)
        {
            hitShield = false;
            HitScope scope = _activeHit;
            if (scope == null ||
                scope.MissileIndex != missileIndex ||
                !scope.HasVictim)
                return false;

            hitShield = scope.HitShield;
            return true;
        }

        private static void MissileHitPrefix(
            Mission __instance,
            object[] __args,
            out HitPatchState __state)
        {
            HitScope current = new HitScope { Mission = __instance };

            try
            {
                object collisionData =
                    __args != null && __args.Length > 1
                        ? __args[1]
                        : null;
                if (collisionData != null)
                {
                    current.MissileIndex = (int)_missileIndexProperty.GetValue(
                        collisionData,
                        null);
                    current.HitShield = (bool)_shieldHitProperty.GetValue(
                        collisionData,
                        null);
                }

                current.HasVictim =
                    __args != null &&
                    __args.Length > 10 &&
                    __args[10] is Agent;
            }
            catch
            {
                current.MissileIndex = -1;
                current.HasVictim = false;
                current.HitShield = false;
            }

            __state = new HitPatchState
            {
                Previous = _activeHit,
                Current = current
            };
            _activeHit = current;
        }

        private static Exception MissileHitFinalizer(
            Exception __exception,
            HitPatchState __state)
        {
            _activeHit = __state?.Previous;
            return __exception;
        }

        private static void EarlyQueuePrefix(
            object __instance,
            out EarlyQueuePatchState __state)
        {
            __state = null;
            HitScope scope = _activeHit;
            if (__instance == null ||
                scope == null ||
                scope.MissileIndex < 0)
                return;

            try
            {
                object tracked = _findTrackedMissileMethod.Invoke(
                    __instance,
                    new object[] { scope.MissileIndex });
                if (tracked == null) return;

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

                // Let the core create its one new reaction in an isolated list. This prevents
                // the fixed 32-entry trim from deleting another live missile's exact reaction.
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
                        new TaggedMissileIndex
                        {
                            MissileIndex = state.MissileIndex
                        });
                }
            }
            catch
            {
                // Preserve the core queue as the fallback if an unknown private shape changes.
            }
        }

        private static void EarlyConsumePrefix(
            object __instance,
            int missileIndex)
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
                        !EarlyReactionMissiles.TryGetValue(
                            item,
                            out TaggedMissileIndex tag) ||
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
                // The core's shooter/victim matcher remains the fallback.
            }
        }
    }
}
