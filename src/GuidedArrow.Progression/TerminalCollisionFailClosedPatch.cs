using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Restores the previously stable collision invariant: only a native PassThrough reaction may
    /// continue the exact live projectile. Stick, BecomeInvisible and every other terminal native
    /// reaction must terminate that projectile instead of creating a synthetic replacement missile.
    /// </summary>
    internal static class TerminalCollisionFailClosedPatch
    {
        private const int NativePassThroughReaction = 1;

        [ThreadStatic]
        private static int _terminalResolutionDepth;

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

            if (resolveMethod == null || hasRemainingMethod == null) return;

            _logMethod = AccessTools.Method(behaviorType, "Log", new[] { typeof(string) });

            MethodInfo resolvePrefix = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(ResolvePrefix));
            MethodInfo resolveFinalizer = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(ResolveFinalizer));
            MethodInfo hasRemainingPostfix = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(HasRemainingPostfix));

            if (resolvePrefix == null || resolveFinalizer == null || hasRemainingPostfix == null)
                return;

            try
            {
                // Patch the guarded predicate first. By itself it is inert because the terminal
                // resolution depth remains zero. This avoids a partially installed prefix changing
                // control flow without the predicate override that makes termination fail closed.
                harmony.Patch(
                    hasRemainingMethod,
                    postfix: new HarmonyMethod(hasRemainingPostfix) { priority = Priority.Last });

                harmony.Patch(
                    resolveMethod,
                    prefix: new HarmonyMethod(resolvePrefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(resolveFinalizer) { priority = Priority.Last });
            }
            catch
            {
                // An isolated HasRemainingAgentPenetration postfix is inert outside a successfully
                // entered terminal ResolveCollisionReaction context.
            }
        }

        private static void ResolvePrefix(object __instance, object[] __args, out bool __state)
        {
            __state = false;
            if (__args == null || __args.Length < 2 || IsNativePassThrough(__args[1])) return;

            _terminalResolutionDepth++;
            __state = true;

            try
            {
                string reactionName = __args[1] == null ? "Unknown" : __args[1].ToString();
                _logMethod?.Invoke(
                    __instance,
                    new object[]
                    {
                        "Terminal native collision " + reactionName +
                        " will terminate without a synthetic penetration continuation."
                    });
            }
            catch
            {
                // Logging must never affect collision handling.
            }
        }

        private static Exception ResolveFinalizer(Exception __exception, bool __state)
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

        private static bool IsNativePassThrough(object reaction)
        {
            try { return Convert.ToInt32(reaction) == NativePassThroughReaction; }
            catch { return false; }
        }
    }
}
