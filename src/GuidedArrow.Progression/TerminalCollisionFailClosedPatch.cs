using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps non-agent, shield and native/TOR terminal collisions fail-closed. Eligible
    /// Guided Arrow agent Stick/BecomeInvisible reactions may use the controlled continuation
    /// path, which is quarantined until a complete behavior pre-display boundary.
    /// </summary>
    internal static class TerminalCollisionFailClosedPatch
    {
        private const int NativePassThroughReaction = 1;

        [ThreadStatic]
        private static int _terminalResolutionDepth;

        private static MethodInfo _findTrackedMissileMethod;
        private static MethodInfo _logMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo resolveMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "ResolveCollisionReaction" &&
                    method.GetParameters().Length == 3);

            MethodInfo hasRemainingMethod = behaviorType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "HasRemainingAgentPenetration" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 1);

            _findTrackedMissileMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "FindTrackedMissile" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));

            if (resolveMethod == null ||
                hasRemainingMethod == null ||
                _findTrackedMissileMethod == null)
                return;

            _logMethod = AccessTools.Method(
                behaviorType,
                "Log",
                new[] { typeof(string) });

            MethodInfo resolvePrefix = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(ResolvePrefix));
            MethodInfo resolveFinalizer = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(ResolveFinalizer));
            MethodInfo hasRemainingPostfix = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(HasRemainingPostfix));

            if (resolvePrefix == null ||
                resolveFinalizer == null ||
                hasRemainingPostfix == null)
                return;

            try
            {
                harmony.Patch(
                    hasRemainingMethod,
                    postfix: new HarmonyMethod(hasRemainingPostfix)
                    {
                        priority = Priority.Last
                    });

                harmony.Patch(
                    resolveMethod,
                    prefix: new HarmonyMethod(resolvePrefix)
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(resolveFinalizer)
                    {
                        priority = Priority.Last
                    });
            }
            catch
            {
                // An isolated predicate postfix remains inert outside a successfully entered
                // fail-closed ResolveCollisionReaction context.
            }
        }

        private static void ResolvePrefix(
            object __instance,
            object[] __args,
            out bool __state)
        {
            __state = false;
            if (__instance == null ||
                __args == null ||
                __args.Length < 2 ||
                IsNativePassThrough(__args[1]))
                return;

            if (CanUseControlledContinuation(__instance, __args))
            {
                TryLog(
                    __instance,
                    "Eligible terminal native collision " +
                    ReactionName(__args[1]) +
                    " may use the controlled pre-display continuation path.");
                return;
            }

            _terminalResolutionDepth++;
            __state = true;

            TryLog(
                __instance,
                "Terminal native collision " +
                ReactionName(__args[1]) +
                " will terminate without a synthetic penetration continuation.");
        }

        private static Exception ResolveFinalizer(
            Exception __exception,
            bool __state)
        {
            if (__state && _terminalResolutionDepth > 0)
                _terminalResolutionDepth--;

            return __exception;
        }

        private static void HasRemainingPostfix(ref bool __result)
        {
            if (_terminalResolutionDepth > 0)
                __result = false;
        }

        private static bool CanUseControlledContinuation(
            object instance,
            object[] args)
        {
            if (args == null ||
                args.Length < 2 ||
                !IsSupportedTerminalReaction(args[1]))
                return false;

            int missileIndex;
            try { missileIndex = (int)args[0]; }
            catch { return false; }

            if (!ExactEarlyCollisionReactionPatch.TryGetActiveAgentHit(
                    missileIndex,
                    out bool hitShield) ||
                hitShield)
                return false;

            object tracked;
            try
            {
                tracked = _findTrackedMissileMethod.Invoke(
                    instance,
                    new object[] { missileIndex });
            }
            catch
            {
                return false;
            }

            if (tracked == null)
                return false;

            return !NativeVolleyPenetrationIsolationPatch
                .ShouldBlockSyntheticContinuation(instance, tracked);
        }

        private static bool IsSupportedTerminalReaction(object reaction)
        {
            string name = ReactionName(reaction);
            return string.Equals(name, "Stick", StringComparison.Ordinal) ||
                   string.Equals(name, "BecomeInvisible", StringComparison.Ordinal);
        }

        private static bool IsNativePassThrough(object reaction)
        {
            try
            {
                return Convert.ToInt32(reaction) == NativePassThroughReaction;
            }
            catch
            {
                return false;
            }
        }

        private static string ReactionName(object reaction)
        {
            return reaction == null ? "Unknown" : reaction.ToString();
        }

        private static void TryLog(object instance, string message)
        {
            if (_logMethod == null ||
                instance == null ||
                string.IsNullOrEmpty(message))
                return;

            try
            {
                _logMethod.Invoke(instance, new object[] { message });
            }
            catch { }
        }
    }
}
