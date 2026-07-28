using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps projectile-camera ownership changes out of the native missile impact callback.
    ///
    /// The locked core scans sibling native velocities from OnMissileHit and, when no sibling is
    /// available, immediately restores Bannerlord's combat camera. Both paths touch mutable native
    /// objects while Bannerlord is still resolving the impact. During OnMissileHit this patch uses
    /// only managed tracked-missile state and defers the no-sibling camera suspension until a later
    /// display tick if the collision reaction is still pending.
    /// </summary>
    internal static class ImpactCameraTransitionSafetyPatch
    {
        private sealed class PendingCameraSuspension
        {
            internal int ImpactedMissileIndex;
        }

        private static readonly ConditionalWeakTable<object, PendingCameraSuspension> PendingSuspensions =
            new ConditionalWeakTable<object, PendingCameraSuspension>();

        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedShotGenerationField;
        private static FieldInfo _awaitingCollisionReactionField;
        private static FieldInfo _activeShotGenerationField;
        private static FieldInfo _cameraMissileIndexField;
        private static FieldInfo _cameraFrameValidField;
        private static FieldInfo _releaseCameraAfterOverrideField;
        private static MethodInfo _suspendCameraMethod;

        [ThreadStatic]
        private static bool _insideMissileHit;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            _trackedShotGenerationField = AccessTools.Field(trackedType, "ShotGeneration");
            _awaitingCollisionReactionField = AccessTools.Field(trackedType, "AwaitingCollisionReaction");
            _activeShotGenerationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _cameraFrameValidField = AccessTools.Field(behaviorType, "_cameraFrameValid");
            _releaseCameraAfterOverrideField = AccessTools.Field(behaviorType, "_releaseCameraAfterOverride");

            MethodInfo impactMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                    candidate.Name == "OnMissileHit" &&
                    !candidate.IsAbstract);
            MethodInfo promoteMethod = AccessTools.Method(
                behaviorType,
                "TryPromoteCameraOwnerWithinSwarm",
                new[] { typeof(int) });
            _suspendCameraMethod = AccessTools.Method(
                behaviorType,
                "SuspendProjectileCameraForCollisionReaction",
                new[] { typeof(int) });

            MethodInfo impactPrefix = AccessTools.Method(
                typeof(ImpactCameraTransitionSafetyPatch),
                nameof(ImpactPrefix));
            MethodInfo impactFinalizer = AccessTools.Method(
                typeof(ImpactCameraTransitionSafetyPatch),
                nameof(ImpactFinalizer));
            MethodInfo promotePrefix = AccessTools.Method(
                typeof(ImpactCameraTransitionSafetyPatch),
                nameof(PromotePrefix));
            MethodInfo suspendPrefix = AccessTools.Method(
                typeof(ImpactCameraTransitionSafetyPatch),
                nameof(SuspendPrefix));
            MethodInfo displayPrefix = AccessTools.Method(
                typeof(ImpactCameraTransitionSafetyPatch),
                nameof(DisplayPrefix));
            MethodInfo resetPostfix = AccessTools.Method(
                typeof(ImpactCameraTransitionSafetyPatch),
                nameof(ResetPostfix));

            if (impactMethod == null ||
                promoteMethod == null ||
                _suspendCameraMethod == null ||
                impactPrefix == null ||
                impactFinalizer == null ||
                promotePrefix == null ||
                suspendPrefix == null ||
                displayPrefix == null ||
                resetPostfix == null ||
                _trackedMissilesField == null ||
                _trackedIndexField == null ||
                _trackedShotGenerationField == null ||
                _awaitingCollisionReactionField == null ||
                _activeShotGenerationField == null ||
                _cameraMissileIndexField == null)
                return;

            try
            {
                harmony.Patch(
                    impactMethod,
                    prefix: new HarmonyMethod(impactPrefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(impactFinalizer) { priority = Priority.Last });
                harmony.Patch(
                    promoteMethod,
                    prefix: new HarmonyMethod(promotePrefix) { priority = Priority.First });
                harmony.Patch(
                    _suspendCameraMethod,
                    prefix: new HarmonyMethod(suspendPrefix) { priority = Priority.First });
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
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(displayPrefix) { priority = Priority.First });
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

        private static bool PromotePrefix(object __instance, int __0, ref bool __result)
        {
            if (!_insideMissileHit) return true;

            __result = TrySelectManagedCameraSibling(__instance, __0);
            return false;
        }

        private static bool SuspendPrefix(object __instance, int __0)
        {
            if (!_insideMissileHit) return true;
            if (__instance == null) return false;

            try
            {
                _cameraFrameValidField?.SetValue(__instance, false);
                _releaseCameraAfterOverrideField?.SetValue(__instance, false);

                PendingSuspensions.Remove(__instance);
                PendingSuspensions.Add(
                    __instance,
                    new PendingCameraSuspension { ImpactedMissileIndex = __0 });
            }
            catch { }

            // Native MissionScreen/Camera ownership is restored only after the collision callback.
            return false;
        }

        private static void DisplayPrefix(object __instance)
        {
            if (__instance == null ||
                !PendingSuspensions.TryGetValue(__instance, out PendingCameraSuspension pending) ||
                pending == null)
                return;

            PendingSuspensions.Remove(__instance);

            try
            {
                // A synchronous/early collision reaction may already have resumed, promoted or
                // removed this projectile. In that case the delayed suspension is obsolete.
                if (!IsStillAwaitingReaction(__instance, pending.ImpactedMissileIndex))
                    return;

                _suspendCameraMethod.Invoke(
                    __instance,
                    new object[] { pending.ImpactedMissileIndex });
            }
            catch
            {
                // The later lifetime sanitiser remains authoritative if camera restoration fails.
            }
        }

        private static bool TrySelectManagedCameraSibling(object instance, int excludedIndex)
        {
            if (instance == null) return false;

            try
            {
                IList tracked = _trackedMissilesField.GetValue(instance) as IList;
                if (tracked == null) return false;

                int generation = (int)_activeShotGenerationField.GetValue(instance);
                for (int i = 0; i < tracked.Count; i++)
                {
                    object candidate = tracked[i];
                    if (candidate == null) continue;

                    int index = (int)_trackedIndexField.GetValue(candidate);
                    if (index == excludedIndex) continue;
                    if ((int)_trackedShotGenerationField.GetValue(candidate) != generation) continue;
                    if ((bool)_awaitingCollisionReactionField.GetValue(candidate)) continue;

                    // Do not call MBMissile.GetVelocity from the collision callback. The normal
                    // display-tick live-registry validator will reject an unusable candidate before
                    // the camera or guidance code dereferences it.
                    _cameraMissileIndexField.SetValue(instance, index);
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool IsStillAwaitingReaction(object instance, int missileIndex)
        {
            try
            {
                IList tracked = _trackedMissilesField.GetValue(instance) as IList;
                if (tracked == null) return false;

                for (int i = 0; i < tracked.Count; i++)
                {
                    object entry = tracked[i];
                    if (entry == null) continue;
                    if ((int)_trackedIndexField.GetValue(entry) != missileIndex) continue;
                    return (bool)_awaitingCollisionReactionField.GetValue(entry);
                }
            }
            catch { }

            return false;
        }

        private static void ResetPostfix(object __instance)
        {
            if (__instance != null)
                PendingSuspensions.Remove(__instance);
        }
    }
}
