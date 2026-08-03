using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Ends guided camera ownership when a confirmed-kill projectile has become a targetless,
    /// unsteered dead flight toward terrain. The handoff runs only from a clean display boundary;
    /// Bannerlord keeps simulating the released native missile after Guided Arrow returns to combat.
    /// </summary>
    internal static class PostKillDeadFlightCameraPatch
    {
        private const float IdleGraceSeconds = 0.45f;
        private const float PredictionSeconds = 3.5f;
        private const int PredictionSamples = 10;
        private const float TerrainMargin = 0.15f;
        private const float MinimumDownwardSpeed = 0.25f;

        private sealed class BehaviorState
        {
            internal bool SawDeferredKill;
            internal bool CameraDetached;
            internal float IdleElapsed;
        }

        private static readonly ConditionalWeakTable<object, BehaviorState> States =
            new ConditionalWeakTable<object, BehaviorState>();

        private static PropertyInfo _missionProperty;
        private static FieldInfo _stateField;
        private static FieldInfo _deferredCinematicVictimField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _cameraMissileIndexField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedMissileField;
        private static FieldInfo _guidanceTargetField;
        private static FieldInfo _pendingCollisionContextsField;
        private static FieldInfo _earlyCollisionReactionsField;
        private static FieldInfo _pendingContinuationSpawnsField;
        private static FieldInfo _pendingNativeMissileRemovalsField;
        private static MethodInfo _beginReturnMethod;
        private static MethodInfo _logMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _missionProperty = AccessTools.Property(behaviorType, "Mission");
            _stateField = AccessTools.Field(behaviorType, "_state");
            _deferredCinematicVictimField = AccessTools.Field(behaviorType, "_deferredCinematicVictim");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            _trackedMissileField = AccessTools.Field(trackedType, "Missile");
            _guidanceTargetField = AccessTools.Field(trackedType, "GuidanceTarget");
            _pendingCollisionContextsField = AccessTools.Field(behaviorType, "_pendingCollisionContexts");
            _earlyCollisionReactionsField = AccessTools.Field(behaviorType, "_earlyCollisionReactions");
            _pendingContinuationSpawnsField = AccessTools.Field(behaviorType, "_pendingContinuationSpawns");
            _pendingNativeMissileRemovalsField = AccessTools.Field(behaviorType, "_pendingNativeMissileRemovals");
            _beginReturnMethod = AccessTools.Method(
                behaviorType,
                "BeginReturn",
                new[] { typeof(string), typeof(bool) });
            _logMethod = AccessTools.Method(behaviorType, "Log", new[] { typeof(string) });

            if (!ReflectionAvailable()) return;

            MethodInfo steeringInput = AccessTools.Method(
                behaviorType,
                "QueueDirectSteeringInput",
                new[] { typeof(float), typeof(float) });
            MethodInfo steeringPostfix = AccessTools.Method(
                typeof(PostKillDeadFlightCameraPatch),
                nameof(SteeringInputPostfix));
            if (steeringInput != null && steeringPostfix != null)
            {
                try
                {
                    harmony.Patch(
                        steeringInput,
                        postfix: new HarmonyMethod(steeringPostfix) { priority = Priority.Last });
                }
                catch { }
            }

            MethodInfo displayPostfix = AccessTools.Method(
                typeof(PostKillDeadFlightCameraPatch),
                nameof(DisplayPostfix));
            if (displayPostfix != null)
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate =>
                        candidate.Name == "OnPreDisplayMissionTick" &&
                        !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            postfix: new HarmonyMethod(displayPostfix) { priority = Priority.Last });
                    }
                    catch { }
                }
            }

            MethodInfo clearPrefix = AccessTools.Method(
                typeof(PostKillDeadFlightCameraPatch),
                nameof(ClearPrefix));
            if (clearPrefix == null) return;

            foreach (string name in new[] { "StartGuidedShot", "ResetAll" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == name && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(clearPrefix) { priority = Priority.First });
                    }
                    catch { }
                }
            }
        }

        private static void SteeringInputPostfix(
            object __instance,
            float __0,
            float __1)
        {
            if (__instance == null ||
                !States.TryGetValue(__instance, out BehaviorState state) ||
                state == null ||
                !state.SawDeferredKill)
                return;

            if (Math.Abs(__0) > 0.00001f || Math.Abs(__1) > 0.00001f)
                state.IdleElapsed = 0f;
        }

        private static void DisplayPostfix(object __instance, float __0)
        {
            if (__instance == null || ReadCoreState(__instance) != 2) return;

            ExperienceSettings settings = ExperienceSettings.Instance;
            if (settings == null ||
                settings.EnableKillCinematics ||
                !settings.FollowProjectileCamera)
                return;

            BehaviorState state = States.GetOrCreateValue(__instance);
            if (state.CameraDetached) return;

            object deferredKill;
            try { deferredKill = _deferredCinematicVictimField.GetValue(__instance); }
            catch { return; }

            if (deferredKill == null)
            {
                state.SawDeferredKill = false;
                state.IdleElapsed = 0f;
                return;
            }

            if (!state.SawDeferredKill)
            {
                state.SawDeferredKill = true;
                state.IdleElapsed = 0f;
                return;
            }

            if (HasCollisionOwnedWork(__instance))
            {
                state.IdleElapsed = 0f;
                return;
            }

            object tracked = FindCameraTrackedMissile(__instance);
            if (tracked == null) return;

            try
            {
                if (_guidanceTargetField.GetValue(tracked) is Agent)
                {
                    state.IdleElapsed = 0f;
                    return;
                }
            }
            catch
            {
                return;
            }

            float dt = IsFinite(__0) ? Math.Max(0f, Math.Min(0.1f, __0)) : 0f;
            state.IdleElapsed += dt;
            if (state.IdleElapsed < IdleGraceSeconds) return;

            object missileObject;
            try { missileObject = _trackedMissileField.GetValue(tracked); }
            catch { return; }
            if (!(missileObject is MBMissile missile) ||
                !IsProjectedTowardTerrain(__instance, missile))
                return;

            try
            {
                _beginReturnMethod.Invoke(
                    __instance,
                    new object[] { "TargetlessPostKillContinuationGroundPath", true });
                state.CameraDetached = true;
                TryLog(
                    __instance,
                    "Returned from a targetless post-kill continuation projected to terminate against terrain; Bannerlord continues the released native missile without guided camera ownership.");
            }
            catch
            {
                state.IdleElapsed = 0f;
            }
        }

        private static object FindCameraTrackedMissile(object instance)
        {
            try
            {
                int cameraIndex = (int)_cameraMissileIndexField.GetValue(instance);
                IList tracked = _trackedMissilesField.GetValue(instance) as IList;
                if (cameraIndex < 0 || tracked == null) return null;

                for (int i = 0; i < tracked.Count; i++)
                {
                    object item = tracked[i];
                    if (item != null && (int)_trackedIndexField.GetValue(item) == cameraIndex)
                        return item;
                }
            }
            catch { }

            return null;
        }

        private static bool IsProjectedTowardTerrain(object instance, MBMissile missile)
        {
            Mission mission = ResolveMission(instance);
            if (missile == null || mission?.Scene == null) return false;

            Vec3 position;
            Vec3 velocity;
            try
            {
                position = missile.GetPosition();
                velocity = missile.GetVelocity();
            }
            catch
            {
                return false;
            }

            if (!IsFinite(position) ||
                !IsFinite(velocity) ||
                velocity.z >= -MinimumDownwardSpeed)
                return false;

            for (int i = 1; i <= PredictionSamples; i++)
            {
                float time = PredictionSeconds * i / PredictionSamples;
                Vec3 point = position + velocity * time;
                try
                {
                    float groundHeight = mission.Scene.GetGroundHeightAtPosition(point);
                    if (IsFinite(groundHeight) && point.z <= groundHeight + TerrainMargin)
                        return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool HasCollisionOwnedWork(object instance)
        {
            return ReadCount(_pendingCollisionContextsField, instance) > 0 ||
                   ReadCount(_earlyCollisionReactionsField, instance) > 0 ||
                   ReadCount(_pendingContinuationSpawnsField, instance) > 0 ||
                   ReadCount(_pendingNativeMissileRemovalsField, instance) > 0;
        }

        private static int ReadCount(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return int.MaxValue;
            try
            {
                object value = field.GetValue(instance);
                if (value is ICollection collection) return collection.Count;
                PropertyInfo countProperty = value?.GetType().GetProperty(
                    "Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object count = countProperty?.GetValue(value, null);
                return count is int integer ? integer : int.MaxValue;
            }
            catch
            {
                return int.MaxValue;
            }
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

        private static int ReadCoreState(object instance)
        {
            if (_stateField == null || instance == null) return -1;
            try { return Convert.ToInt32(_stateField.GetValue(instance)); }
            catch { return -1; }
        }

        private static bool ReflectionAvailable()
        {
            return _missionProperty != null &&
                   _stateField != null &&
                   _deferredCinematicVictimField != null &&
                   _trackedMissilesField != null &&
                   _cameraMissileIndexField != null &&
                   _trackedIndexField != null &&
                   _trackedMissileField != null &&
                   _guidanceTargetField != null &&
                   _pendingCollisionContextsField != null &&
                   _earlyCollisionReactionsField != null &&
                   _pendingContinuationSpawnsField != null &&
                   _pendingNativeMissileRemovalsField != null &&
                   _beginReturnMethod != null;
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null) States.Remove(__instance);
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
