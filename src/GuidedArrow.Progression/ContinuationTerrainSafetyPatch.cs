using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps synthetic agent-penetration continuations above terrain.
    ///
    /// The stable core creates a replacement missile beyond the victim because Bannerlord has
    /// already terminated the native projectile. The sidecar extends that exit distance so the
    /// replacement clears the victim. On steep downward hits that exit point can cross the terrain
    /// surface, creating a valid moving missile below the map with no later world-impact callback.
    /// The projectile camera then follows that missile out of bounds.
    /// </summary>
    internal static class ContinuationTerrainSafetyPatch
    {
        private const float CoreContinuationOffset = 0.42f;
        private const float LaunchTerrainMargin = 0.08f;
        private const float LaunchLookAheadDistance = 0.60f;
        private const int LaunchTerrainSamples = 4;
        private const float RuntimeBelowTerrainTolerance = 0.75f;

        private static PropertyInfo _missionProperty;
        private static FieldInfo _impactPositionField;
        private static FieldInfo _impactVelocityField;
        private static FieldInfo _impactDirectionField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _cameraMissileIndexField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedMissileField;
        private static FieldInfo _trackedSyntheticField;
        private static FieldInfo _trackedAwaitingReactionField;
        private static MethodInfo _queueNativeMissileRemovalMethod;
        private static MethodInfo _removeTrackedMissileMethod;
        private static MethodInfo _logMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo spawnMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (spawnMethod == null || trackedType == null) return;

            Type contextType = spawnMethod.GetParameters()[1].ParameterType;
            _missionProperty = AccessTools.Property(behaviorType, "Mission");
            _impactPositionField = AccessTools.Field(contextType, "ImpactPosition");
            _impactVelocityField = AccessTools.Field(contextType, "ImpactVelocity");
            _impactDirectionField = AccessTools.Field(contextType, "ImpactDirection");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            _trackedMissileField = AccessTools.Field(trackedType, "Missile");
            _trackedSyntheticField = AccessTools.Field(trackedType, "SyntheticProjectile");
            _trackedAwaitingReactionField = AccessTools.Field(trackedType, "AwaitingCollisionReaction");
            _queueNativeMissileRemovalMethod = AccessTools.Method(
                behaviorType,
                "QueueNativeMissileRemoval",
                new[] { trackedType });
            _removeTrackedMissileMethod = AccessTools.Method(
                behaviorType,
                "RemoveTrackedMissile",
                new[] { trackedType, typeof(bool) });
            _logMethod = AccessTools.Method(
                behaviorType,
                "Log",
                new[] { typeof(string) });

            if (_impactPositionField == null ||
                _impactVelocityField == null ||
                _impactDirectionField == null ||
                _trackedMissilesField == null ||
                _cameraMissileIndexField == null ||
                _trackedIndexField == null ||
                _trackedMissileField == null ||
                _trackedSyntheticField == null ||
                _trackedAwaitingReactionField == null ||
                _queueNativeMissileRemovalMethod == null ||
                _removeTrackedMissileMethod == null)
                return;

            MethodInfo spawnPrefix = AccessTools.Method(
                typeof(ContinuationTerrainSafetyPatch),
                nameof(SpawnPrefix));
            MethodInfo displayPrefix = AccessTools.Method(
                typeof(ContinuationTerrainSafetyPatch),
                nameof(GuidanceDisplayPrefix));
            if (spawnPrefix == null || displayPrefix == null) return;

            try
            {
                // Run after PenetrationContinuationSafetyPatch has applied the final victim-exit
                // offset, then validate the exact position the core will pass to AddCustomMissile.
                harmony.Patch(
                    spawnMethod,
                    prefix: new HarmonyMethod(spawnPrefix) { priority = Priority.Last });

                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == "TickGuidanceDisplay" && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(displayPrefix) { priority = Priority.First });
                    }
                    catch { }
                }
            }
            catch
            {
                // Unknown private layouts retain the verified core behavior.
            }
        }

        private static bool SpawnPrefix(
            object __instance,
            object[] __args,
            ref bool __result)
        {
            if (__instance == null || __args == null || __args.Length < 2 || __args[1] == null)
                return true;

            object context = __args[1];
            try
            {
                Vec3 adjustedImpactPosition = (Vec3)_impactPositionField.GetValue(context);
                Vec3 impactVelocity = (Vec3)_impactVelocityField.GetValue(context);
                Vec3 impactDirection = (Vec3)_impactDirectionField.GetValue(context);
                if (!TryComputeNormalizedDirection(impactVelocity, impactDirection, out Vec3 direction))
                    return true;

                Vec3 launchPosition = adjustedImpactPosition + direction * CoreContinuationOffset;
                if (IsContinuationLaunchTerrainClear(__instance, launchPosition, direction))
                    return true;

                // The original initializes its out parameter to null before native work. Mirror that
                // result when skipping it so the worker follows the normal failed-continuation path.
                if (__args.Length > 2) __args[2] = null;
                __result = false;
                TryLog(
                    __instance,
                    "Blocked controlled penetration continuation because its launch corridor intersects terrain.");
                return false;
            }
            catch
            {
                // Terrain validation is additive. Reflection/API failure must not replace the core's
                // existing finite-value and native-entity validation.
                return true;
            }
        }

        private static bool GuidanceDisplayPrefix(object __instance)
        {
            if (__instance == null) return true;

            try
            {
                IList tracked = _trackedMissilesField.GetValue(__instance) as IList;
                if (tracked == null || tracked.Count == 0) return true;

                int cameraIndex = (int)_cameraMissileIndexField.GetValue(__instance);
                if (cameraIndex < 0) return true;

                object cameraTracked = null;
                for (int i = 0; i < tracked.Count; i++)
                {
                    object candidate = tracked[i];
                    if (candidate != null && (int)_trackedIndexField.GetValue(candidate) == cameraIndex)
                    {
                        cameraTracked = candidate;
                        break;
                    }
                }

                if (cameraTracked == null ||
                    !(bool)_trackedSyntheticField.GetValue(cameraTracked) ||
                    (bool)_trackedAwaitingReactionField.GetValue(cameraTracked))
                    return true;

                object missileObject = _trackedMissileField.GetValue(cameraTracked);
                if (!(missileObject is MBMissile missile)) return true;

                Vec3 position = missile.GetPosition();
                if (!IsClearlyBelowTerrain(__instance, position)) return true;

                // This runs at a complete display boundary, not in Bannerlord's collision callback.
                // Preserve the core's deferred native-removal ownership and drop only its invalid
                // managed continuation entry.
                try { _queueNativeMissileRemovalMethod.Invoke(__instance, new[] { cameraTracked }); }
                catch { }
                try { _removeTrackedMissileMethod.Invoke(__instance, new[] { cameraTracked, (object)true }); }
                catch { }
                TryLog(
                    __instance,
                    "Removed a synthetic continuation that escaped below terrain before its world-impact callback.");
            }
            catch
            {
                // Never replace a display tick with a sidecar exception.
            }

            return true;
        }

        private static bool IsContinuationLaunchTerrainClear(
            object instance,
            Vec3 launchPosition,
            Vec3 direction)
        {
            Mission mission = ResolveMission(instance);
            if (mission?.Scene == null) return true;

            int denominator = Math.Max(1, LaunchTerrainSamples - 1);
            for (int i = 0; i < LaunchTerrainSamples; i++)
            {
                float distance = LaunchLookAheadDistance * i / denominator;
                Vec3 point = launchPosition + direction * distance;
                if (TryGetGroundHeight(mission, point, out float groundHeight) &&
                    point.z <= groundHeight + LaunchTerrainMargin)
                    return false;
            }

            return true;
        }

        private static bool IsClearlyBelowTerrain(object instance, Vec3 position)
        {
            if (!IsFinite(position)) return true;

            Mission mission = ResolveMission(instance);
            return mission?.Scene != null &&
                   TryGetGroundHeight(mission, position, out float groundHeight) &&
                   position.z < groundHeight - RuntimeBelowTerrainTolerance;
        }

        private static Mission ResolveMission(object instance)
        {
            if (_missionProperty != null && instance != null)
            {
                try
                {
                    Mission mission = _missionProperty.GetValue(instance, null) as Mission;
                    if (mission != null) return mission;
                }
                catch { }
            }

            return Mission.Current;
        }

        private static bool TryGetGroundHeight(Mission mission, Vec3 position, out float groundHeight)
        {
            groundHeight = 0f;
            if (mission?.Scene == null || !IsFinite(position)) return false;

            try
            {
                groundHeight = mission.Scene.GetGroundHeightAtPosition(new Vec2(position.x, position.y));
                return IsFinite(groundHeight) &&
                       Math.Abs((double)groundHeight - position.z) < 10000.0;
            }
            catch
            {
                groundHeight = 0f;
                return false;
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

        private static void TryLog(object instance, string message)
        {
            if (_logMethod == null || instance == null || string.IsNullOrEmpty(message)) return;
            try { _logMethod.Invoke(instance, new object[] { message }); }
            catch { }
        }
    }
}
