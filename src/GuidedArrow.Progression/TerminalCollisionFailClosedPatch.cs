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

            MethodInfo queueMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "QueuePenetrationContinuation" &&
                    method.GetParameters().Length == 2);

            if (resolveMethod == null || hasRemainingMethod == null || queueMethod == null) return;

            MethodInfo resolvePrefix = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(ResolvePrefix));
            MethodInfo resolveFinalizer = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(ResolveFinalizer));
            MethodInfo hasRemainingPostfix = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(HasRemainingPostfix));
            MethodInfo queuePrefix = AccessTools.Method(
                typeof(TerminalCollisionFailClosedPatch),
                nameof(QueuePrefix));

            if (resolvePrefix == null ||
                resolveFinalizer == null ||
                hasRemainingPostfix == null ||
                queuePrefix == null)
                return;

            try
            {
                harmony.Patch(
                    resolveMethod,
                    prefix: new HarmonyMethod(resolvePrefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(resolveFinalizer) { priority = Priority.Last });

                harmony.Patch(
                    hasRemainingMethod,
                    postfix: new HarmonyMethod(hasRemainingPostfix) { priority = Priority.Last });

                // Defense in depth: if another patch or future core revision bypasses the normal
                // HasRemainingAgentPenetration decision, terminal resolution still cannot enqueue a
                // synthetic replacement while this collision context is active.
                harmony.Patch(
                    queueMethod,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First });
            }
            catch
            {
                // Unknown private core layouts retain their original behavior rather than receiving
                // a partial collision policy patch.
            }
        }

        private static void ResolvePrefix(object[] __args, out bool __state)
        {
            __state = false;
            if (__args == null || __args.Length < 2 || IsNativePassThrough(__args[1])) return;

            _terminalResolutionDepth++;
            __state = true;
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

        private static bool QueuePrefix()
        {
            return _terminalResolutionDepth <= 0;
        }

        private static bool IsNativePassThrough(object reaction)
        {
            try { return Convert.ToInt32(reaction) == NativePassThroughReaction; }
            catch { return false; }
        }
    }
}
