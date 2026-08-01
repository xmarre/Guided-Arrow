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
    /// Correlates the core's early collision-reaction queue with the exact missile index using only
    /// GuidedArrowBehavior callbacks. No Bannerlord Mission method is patched: the OnMissileHit
    /// prefix observes the collision packet after the matching early reaction has been queued, tags
    /// the newest unclaimed queue entry, and leaves Bannerlord's presentation/audio path untouched.
    /// </summary>
    internal static class ExactEarlyCollisionReactionPatch
    {
        private sealed class HitScope
        {
            internal int MissileIndex = -1;
            internal bool HasVictim;
            internal bool HitShield;
        }

        private sealed class HitPatchState
        {
            internal HitScope Previous;
        }

        private sealed class TaggedMissileIndex
        {
            internal int MissileIndex;
        }

        private sealed class EarlyQueuePatchState
        {
            internal IList Queue;
            internal readonly List<object> ExistingItems = new List<object>();
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

            MethodInfo onMissileHit = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "OnMissileHit" &&
                    !method.IsAbstract &&
                    method.GetParameters().Any(parameter =>
                        parameter.ParameterType == typeof(AttackCollisionData) ||
                        parameter.ParameterType == typeof(AttackCollisionData).MakeByRefType()));

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

            if (onMissileHit == null ||
                queueEarlyReaction == null ||
                consumeEarlyReaction == null ||
                _findTrackedMissileMethod == null ||
                _missileIndexProperty == null ||
                _shieldHitProperty == null ||
                _earlyCollisionReactionsField == null)
                return;

            MethodInfo hitPrefix = AccessTools.Method(
                typeof(ExactEarlyCollisionReactionPatch),
                nameof(OnMissileHitPrefix));
            MethodInfo hitFinalizer = AccessTools.Method(
                typeof(ExactEarlyCollisionReactionPatch),
                nameof(OnMissileHitFinalizer));
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
                    onMissileHit,
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

        private static void OnMissileHitPrefix(
            object __instance,
            object[] __args,
            MethodBase __originalMethod,
            out HitPatchState __state)
        {
            __state = new HitPatchState { Previous = _activeHit };
            HitScope current = new HitScope();

            try
            {
                object collisionData = FindCollisionData(__args);
                if (collisionData != null)
                {
                    current.MissileIndex = Convert.ToInt32(
                        _missileIndexProperty.GetValue(collisionData, null));
                    current.HitShield = Convert.ToBoolean(
                        _shieldHitProperty.GetValue(collisionData, null));
                }

                current.HasVictim = FindVictim(__args, __originalMethod) != null;

                if (__instance != null && current.MissileIndex >= 0)
                {
                    object tracked = _findTrackedMissileMethod.Invoke(
                        __instance,
                        new object[] { current.MissileIndex });
                    if (tracked != null)
                        TagNewestUnclaimedReaction(__instance, current.MissileIndex);
                }
            }
            catch
            {
                current.MissileIndex = -1;
                current.HasVictim = false;
                current.HitShield = false;
            }

            _activeHit = current;
        }

        private static Exception OnMissileHitFinalizer(
            Exception __exception,
            HitPatchState __state)
        {
            _activeHit = __state?.Previous;
            return __exception;
        }

        private static object FindCollisionData(object[] args)
        {
            if (args == null) return null;

            for (int i = 0; i < args.Length; i++)
            {
                object value = args[i];
                if (value != null && value.GetType() == typeof(AttackCollisionData))
                    return value;
            }
            return null;
        }

        private static Agent FindVictim(
            object[] args,
            MethodBase originalMethod)
        {
            if (args == null) return null;

            try
            {
                ParameterInfo[] parameters = originalMethod?.GetParameters();
                if (parameters != null)
                {
                    for (int i = 0; i < parameters.Length && i < args.Length; i++)
                    {
                        string name = parameters[i].Name ?? string.Empty;
                        if (args[i] is Agent agent &&
                            name.IndexOf("victim", StringComparison.OrdinalIgnoreCase) >= 0)
                            return agent;
                    }
                }
            }
            catch { }

            int seenAgents = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (!(args[i] is Agent agent)) continue;
                seenAgents++;
                if (seenAgents == 2) return agent;
            }
            return null;
        }

        private static void TagNewestUnclaimedReaction(
            object instance,
            int missileIndex)
        {
            try
            {
                IList queue = _earlyCollisionReactionsField.GetValue(instance) as IList;
                if (queue == null || queue.Count == 0) return;

                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    object item = queue[i];
                    if (item == null || EarlyReactionMissiles.TryGetValue(item, out _))
                        continue;

                    EarlyReactionMissiles.Add(
                        item,
                        new TaggedMissileIndex { MissileIndex = missileIndex });
                    return;
                }
            }
            catch
            {
                // The core's shooter/victim matcher remains the fallback.
            }
        }

        private static void EarlyQueuePrefix(
            object __instance,
            out EarlyQueuePatchState __state)
        {
            __state = null;
            if (__instance == null) return;

            try
            {
                IList queue = _earlyCollisionReactionsField.GetValue(__instance) as IList;
                if (queue == null) return;

                EarlyQueuePatchState state = new EarlyQueuePatchState
                {
                    Queue = queue,
                    QueueIsolated = true
                };

                for (int i = 0; i < queue.Count; i++)
                    state.ExistingItems.Add(queue[i]);

                // Let the core append its new reaction to an isolated list so its fixed 32-entry
                // trim cannot discard an older live projectile's pending reaction.
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
            RestoreEarlyQueue(__state);
        }

        private static Exception EarlyQueueFinalizer(
            Exception __exception,
            EarlyQueuePatchState __state)
        {
            RestoreEarlyQueue(__state);
            return __exception;
        }

        private static void RestoreEarlyQueue(EarlyQueuePatchState state)
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
                    state.Queue.Add(createdItems[i]);
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
