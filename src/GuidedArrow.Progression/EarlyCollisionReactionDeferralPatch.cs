using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Prevents an early native collision-reaction callback from being resolved recursively inside
    /// GuidedArrowBehavior.OnMissileHit.
    ///
    /// Bannerlord may deliver OnMissileCollisionReaction before the matching OnMissileHit context has
    /// been registered. The locked core stores that reaction and then calls
    /// TryConsumeEarlyCollisionReaction at the end of OnMissileHit. That immediately enters the full
    /// ResolveCollisionReaction path while the native impact callback is still active. Penetration
    /// and progression settings can make that path resume the projectile camera, validate the native
    /// missile identity, remove the tracked missile or queue a continuation synchronously.
    ///
    /// This patch leaves the early reaction queued and resolves it on the next display tick, after
    /// Bannerlord has returned from the impact callback.
    /// </summary>
    internal static class EarlyCollisionReactionDeferralPatch
    {
        private sealed class PendingIndices
        {
            internal readonly List<int> Items = new List<int>();
        }

        private static readonly ConditionalWeakTable<object, PendingIndices> Pending =
            new ConditionalWeakTable<object, PendingIndices>();

        private static MethodInfo _consumeMethod;

        [ThreadStatic]
        private static bool _insideMissileHit;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo impactMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == "OnMissileHit" && !candidate.IsAbstract);
            _consumeMethod = AccessTools.Method(
                behaviorType,
                "TryConsumeEarlyCollisionReaction",
                new[] { typeof(int) });

            MethodInfo impactPrefix = AccessTools.Method(
                typeof(EarlyCollisionReactionDeferralPatch),
                nameof(ImpactPrefix));
            MethodInfo impactFinalizer = AccessTools.Method(
                typeof(EarlyCollisionReactionDeferralPatch),
                nameof(ImpactFinalizer));
            MethodInfo consumePrefix = AccessTools.Method(
                typeof(EarlyCollisionReactionDeferralPatch),
                nameof(ConsumePrefix));
            MethodInfo displayPrefix = AccessTools.Method(
                typeof(EarlyCollisionReactionDeferralPatch),
                nameof(DisplayPrefix));
            MethodInfo resetPostfix = AccessTools.Method(
                typeof(EarlyCollisionReactionDeferralPatch),
                nameof(ResetPostfix));

            if (impactMethod == null ||
                _consumeMethod == null ||
                impactPrefix == null ||
                impactFinalizer == null ||
                consumePrefix == null ||
                displayPrefix == null ||
                resetPostfix == null)
                return;

            try
            {
                harmony.Patch(
                    impactMethod,
                    prefix: new HarmonyMethod(impactPrefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(impactFinalizer) { priority = Priority.Last });
                harmony.Patch(
                    _consumeMethod,
                    prefix: new HarmonyMethod(consumePrefix) { priority = Priority.First });
            }
            catch
            {
                return;
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnPreDisplayMissionTick" && !candidate.IsAbstract))
            {
                try
                {
                    // Run after the deferred projectile-camera suspension (Priority.First) but before
                    // deferred victim/cinematic replay (Priority.High).
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(displayPrefix) { priority = Priority.VeryHigh });
                }
                catch { }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "ResetAll" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(resetPostfix) { priority = Priority.Last });
                }
                catch { }
            }
        }

        private static void ImpactPrefix(out bool __state)
        {
            __state = _insideMissileHit;
            _insideMissileHit = true;
        }

        private static Exception ImpactFinalizer(Exception __exception, bool __state)
        {
            _insideMissileHit = __state;
            return __exception;
        }

        private static bool ConsumePrefix(object __instance, int __0)
        {
            if (!_insideMissileHit) return true;
            if (__instance == null || __0 < 0) return false;

            try
            {
                PendingIndices pending = Pending.GetOrCreateValue(__instance);
                if (!pending.Items.Contains(__0))
                    pending.Items.Add(__0);
            }
            catch { }

            return false;
        }

        private static void DisplayPrefix(object __instance)
        {
            if (__instance == null ||
                !Pending.TryGetValue(__instance, out PendingIndices pending) ||
                pending == null)
                return;

            Pending.Remove(__instance);
            int[] indices = pending.Items.ToArray();
            for (int i = 0; i < indices.Length; i++)
            {
                try
                {
                    _consumeMethod.Invoke(__instance, new object[] { indices[i] });
                }
                catch
                {
                    // The core's pending-lifetime cleanup remains authoritative if the projectile or
                    // its reaction disappeared before the safe replay point.
                }
            }
        }

        private static void ResetPostfix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
        }
    }
}
