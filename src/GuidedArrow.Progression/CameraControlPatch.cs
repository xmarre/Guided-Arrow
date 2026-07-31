using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Separates projectile-follow camera ownership from guidance and from kill cinematics.
    /// The stable core previously treated all three as one lifecycle.
    /// </summary>
    internal static class CameraControlPatch
    {
        private sealed class ShotState
        {
            internal bool FollowProjectile;
            internal bool EnableKillCinematic;
            internal bool CameraWasShown;
            internal bool SkipKillCinematicRequested;
            internal bool SawSafeDisplayBoundary;
        }

        private static readonly ConditionalWeakTable<object, ShotState> States =
            new ConditionalWeakTable<object, ShotState>();

        private static FieldInfo _stateField;
        private static FieldInfo _cameraFrameValidField;
        private static MethodInfo _beginReturnMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _stateField = AccessTools.Field(behaviorType, "_state");
            _cameraFrameValidField = AccessTools.Field(behaviorType, "_cameraFrameValid");
            _beginReturnMethod = AccessTools.Method(
                behaviorType,
                "BeginReturn",
                new[] { typeof(string), typeof(bool) });
            PatchPrefix(harmony, behaviorType, "StartGuidedShot", nameof(StartShotPrefix));
            PatchPrefix(harmony, behaviorType, "AcquireCustomCameraOwnership", nameof(AcquireCameraPrefix));
            PatchPrefix(harmony, behaviorType, "SetMissionCamera", nameof(SetMissionCameraPrefix));
            PatchPrefix(harmony, behaviorType, "UpdateOverridenCamera", nameof(UpdateOverridenCameraPrefix));
            PatchPrefix(harmony, behaviorType, "SuspendProjectileCameraForCollisionReaction", nameof(SuspendProjectileCameraPrefix));
            PatchPrefix(harmony, behaviorType, "InitializeCinematicCamera", nameof(InitializeCinematicCameraPrefix));
            PatchPrefix(harmony, behaviorType, "SetCinematicTimeSpeed", nameof(CinematicTimeSpeedPrefix));
            PatchPrefix(harmony, behaviorType, "EnsureCinematicTimeSpeed", nameof(CinematicTimeSpeedPrefix));
            PatchPostfix(harmony, behaviorType, "BeginKillCinematic", nameof(BeginKillCinematicPostfix));
            PatchPrefix(harmony, behaviorType, "OnPreDisplayMissionTick", nameof(PreDisplayTickPrefix));
            PatchPrefix(harmony, behaviorType, "ResetAll", nameof(ResetPrefix));
        }

        private static void PatchPrefix(Harmony harmony, Type behaviorType, string methodName, string prefixName)
        {
            MethodInfo prefix = AccessTools.Method(typeof(CameraControlPatch), prefixName);
            if (prefix == null) return;

            foreach (MethodInfo method in behaviorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != methodName || method.IsAbstract) continue;
                try
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
                }
                catch { }
            }
        }

        private static void PatchPostfix(Harmony harmony, Type behaviorType, string methodName, string postfixName)
        {
            MethodInfo postfix = AccessTools.Method(typeof(CameraControlPatch), postfixName);
            if (postfix == null) return;

            foreach (MethodInfo method in behaviorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != methodName || method.IsAbstract) continue;
                try
                {
                    harmony.Patch(method, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
                }
                catch { }
            }
        }

        private static void StartShotPrefix(object __instance)
        {
            if (__instance == null) return;

            States.Remove(__instance);
            ExperienceSettings settings = ExperienceSettings.Instance;
            States.Add(__instance, new ShotState
            {
                FollowProjectile = settings == null || settings.FollowProjectileCamera,
                EnableKillCinematic = settings == null || settings.EnableKillCinematics,
                CameraWasShown = false,
                SkipKillCinematicRequested = false,
                SawSafeDisplayBoundary = false
            });
        }

        private static bool AcquireCameraPrefix(object __instance)
        {
            ShotState state;
            if (!TryGetState(__instance, out state) || state.FollowProjectile) return true;

            int coreState = ReadCoreState(__instance);
            return (coreState == 4 && state.EnableKillCinematic) ||
                   (coreState == 5 && state.CameraWasShown);
        }

        private static bool SetMissionCameraPrefix(object __instance)
        {
            ShotState state;
            if (!TryGetState(__instance, out state)) return true;

            int coreState = ReadCoreState(__instance);
            if (coreState == 4)
            {
                if (!state.EnableKillCinematic)
                {
                    if (!state.CameraWasShown) ClearCameraFrame(__instance);
                    return false;
                }

                state.CameraWasShown = true;
                return true;
            }

            if (coreState == 5)
                return state.CameraWasShown;

            if (coreState == 1 || coreState == 2 || coreState == 3)
            {
                if (state.FollowProjectile)
                {
                    state.CameraWasShown = true;
                    return true;
                }

                ClearCameraFrame(__instance);
                return false;
            }

            return true;
        }

        private static bool UpdateOverridenCameraPrefix(object __instance, ref bool __result)
        {
            ShotState state;
            if (!TryGetState(__instance, out state)) return true;

            int coreState = ReadCoreState(__instance);
            bool suppressProjectileCamera = !state.FollowProjectile &&
                                            (coreState == 1 || coreState == 2 || coreState == 3);
            bool suppressKillCamera = !state.EnableKillCinematic && coreState == 4;
            if (!suppressProjectileCamera && !suppressKillCamera) return true;

            // Do not call ReleaseCustomCameraOwnership or RestoreNativeCameraAfterGuidance here.
            // Those methods write MissionScreen/Camera native state and can be reached from the
            // missile-impact call stack. A disabled camera needs only to withhold the custom frame.
            ClearCameraFrame(__instance);
            __result = false;
            return false;
        }

        private static bool SuspendProjectileCameraPrefix(object __instance)
        {
            ShotState state;
            if (!TryGetState(__instance, out state) || state.FollowProjectile) return true;

            // No projectile camera was requested for this shot. The core suspension method would
            // redundantly restore MissionScreen.CombatCamera from inside OnMissileHit, immediately
            // before collision-reaction and penetration-continuation bookkeeping. Skip that native
            // camera mutation and keep the already-active combat camera untouched.
            ClearCameraFrame(__instance);
            return false;
        }

        private static bool InitializeCinematicCameraPrefix(object __instance)
        {
            ShotState state;
            return !TryGetState(__instance, out state) || state.EnableKillCinematic;
        }

        private static bool CinematicTimeSpeedPrefix(object __instance)
        {
            ShotState state;
            return !TryGetState(__instance, out state) || state.EnableKillCinematic;
        }

        private static void BeginKillCinematicPostfix(object __instance)
        {
            ShotState state;
            if (!TryGetState(__instance, out state) || state.EnableKillCinematic) return;
            if (ReadCoreState(__instance) != 4) return;

            // Do not call BeginReturn here. BeginKillCinematic can run inside Bannerlord's native
            // missile-hit callback. Re-entering the core's tracked-missile cleanup from that callback
            // races the projectile currently being finalized and can surface as protected-memory
            // corruption. Let the core finish its normal confirmed-kill bookkeeping, then perform the
            // camera-free terminal transition only after a complete display-tick boundary.
            state.SkipKillCinematicRequested = true;
            state.SawSafeDisplayBoundary = false;
            if (!state.CameraWasShown) ClearCameraFrame(__instance);
        }

        private static bool PreDisplayTickPrefix(object __instance)
        {
            ShotState state;
            if (!TryGetState(__instance, out state) || !state.SkipKillCinematicRequested)
                return true;

            if (ReadCoreState(__instance) != 4)
            {
                state.SkipKillCinematicRequested = false;
                state.SawSafeDisplayBoundary = false;
                return true;
            }

            if (!state.SawSafeDisplayBoundary)
            {
                state.SawSafeDisplayBoundary = true;
                if (!state.CameraWasShown)
                    ClearCameraFrame(__instance);

                // Suppress one complete cinematic display tick. This provides a real frame boundary
                // after the native collision callback without advancing cinematic camera, ragdoll or
                // time-control state that the user explicitly disabled.
                return false;
            }

            if (_beginReturnMethod == null)
            {
                state.SkipKillCinematicRequested = false;
                state.SawSafeDisplayBoundary = false;
                return true;
            }

            try
            {
                if (!state.CameraWasShown) ClearCameraFrame(__instance);
                _beginReturnMethod.Invoke(__instance, new object[] { "KillCinematicDisabled/SafeDisplayHandoff", true });
                state.SkipKillCinematicRequested = false;
                state.SawSafeDisplayBoundary = false;
                return true;
            }
            catch
            {
                // Keep the request pending and wait for another complete display boundary instead of
                // falling back to the unsafe collision-time transition.
                state.SawSafeDisplayBoundary = false;
                return false;
            }
        }

        private static void ResetPrefix(object __instance)
        {
            if (__instance != null) States.Remove(__instance);
        }

        private static bool TryGetState(object instance, out ShotState state)
        {
            state = null;
            return instance != null && States.TryGetValue(instance, out state);
        }

        private static int ReadCoreState(object instance)
        {
            if (_stateField == null || instance == null) return -1;
            try { return Convert.ToInt32(_stateField.GetValue(instance)); }
            catch { return -1; }
        }

        private static void ClearCameraFrame(object instance)
        {
            if (_cameraFrameValidField == null || instance == null) return;
            try { _cameraFrameValidField.SetValue(instance, false); }
            catch { }
        }
    }
}
