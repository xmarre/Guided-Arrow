using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Repairs the one ResolveCollisionReaction branch that removes the final tracked projectile
    /// after native PassThrough exhausts the configured guided penetration budget, but returns
    /// without calling HandleGuidedSwarmTerminal.
    /// </summary>
    internal static class FinalMissileTerminalHandoffPatch
    {
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _stateField;
        private static FieldInfo _trackedIndexField;
        private static MethodInfo _terminalMethod;

        [ThreadStatic]
        private static object _pendingInstance;

        [ThreadStatic]
        private static int _pendingMissileIndex;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _stateField = AccessTools.Field(behaviorType, "_state");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            _terminalMethod = AccessTools.Method(
                behaviorType,
                "HandleGuidedSwarmTerminal",
                new[] { typeof(string) });

            MethodInfo queueRemoval = AccessTools.Method(
                behaviorType,
                "QueueNativeMissileRemoval",
                new[] { trackedType });
            MethodInfo removeTracked = AccessTools.Method(
                behaviorType,
                "RemoveTrackedMissile",
                new[] { trackedType, typeof(bool) });
            MethodInfo resolveReaction = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "ResolveCollisionReaction" && !method.IsAbstract);

            if (_trackedMissilesField == null ||
                _stateField == null ||
                _trackedIndexField == null ||
                _terminalMethod == null ||
                queueRemoval == null ||
                removeTracked == null)
                return;

            MethodInfo queuePrefix = AccessTools.Method(
                typeof(FinalMissileTerminalHandoffPatch),
                nameof(QueueRemovalPrefix));
            MethodInfo removePostfix = AccessTools.Method(
                typeof(FinalMissileTerminalHandoffPatch),
                nameof(RemoveTrackedPostfix));
            MethodInfo resolveFinalizer = AccessTools.Method(
                typeof(FinalMissileTerminalHandoffPatch),
                nameof(ResolveFinalizer));

            if (queuePrefix == null || removePostfix == null || resolveFinalizer == null) return;

            try
            {
                harmony.Patch(
                    queueRemoval,
                    prefix: new HarmonyMethod(queuePrefix) { priority = Priority.First });
                harmony.Patch(
                    removeTracked,
                    postfix: new HarmonyMethod(removePostfix) { priority = Priority.Last });

                if (resolveReaction != null)
                {
                    harmony.Patch(
                        resolveReaction,
                        finalizer: new HarmonyMethod(resolveFinalizer) { priority = Priority.Last });
                }
            }
            catch
            {
                ClearPending();
            }
        }

        private static bool QueueRemovalPrefix(object __instance, object __0)
        {
            ClearPending();
            if (__instance == null || __0 == null) return false;

            try
            {
                _pendingInstance = __instance;
                _pendingMissileIndex = (int)_trackedIndexField.GetValue(__0);
            }
            catch
            {
                ClearPending();
            }

            // Bannerlord/TOR already returned PassThrough. Do not force-delete that transitioned
            // native projectile on the following mission tick. Guided Arrow only relinquishes it.
            return false;
        }

        private static void RemoveTrackedPostfix(object __instance, object __0)
        {
            if (__instance == null || __0 == null || !ReferenceEquals(__instance, _pendingInstance))
                return;

            try
            {
                int removedIndex = (int)_trackedIndexField.GetValue(__0);
                if (removedIndex != _pendingMissileIndex) return;

                IList tracked = _trackedMissilesField.GetValue(__instance) as IList;
                int state = (int)_stateField.GetValue(__instance);
                if (tracked == null || tracked.Count != 0 || state != 2) return;

                // The locked core calls this terminal routine after every other final-removal branch.
                // The penetration-budget-exhausted PassThrough branch is the sole missing handoff.
                _terminalMethod.Invoke(
                    __instance,
                    new object[] { "PenetrationBudgetExhausted/FinalTrackedMissile" });
            }
            catch
            {
                // The core remains authoritative if its private layout changes.
            }
            finally
            {
                ClearPending();
            }
        }

        private static Exception ResolveFinalizer(Exception __exception)
        {
            ClearPending();
            return __exception;
        }

        private static void ClearPending()
        {
            _pendingInstance = null;
            _pendingMissileIndex = -1;
        }
    }
}
