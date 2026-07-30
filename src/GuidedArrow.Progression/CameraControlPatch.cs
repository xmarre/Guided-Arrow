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
        }

        private static readonly ConditionalWeakTable<object, ShotState> States =
            new ConditionalWeakTable<object, ShotState>();

        private static FieldInfo _stateField;
        private static FieldInfo _cameraFrameValidField;
        private static MethodInfo _beginReturnMethod;
        private static MethodInfo _releaseCameraMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _stateField = AccessTools.Field(behaviorType, "_state");
            _cameraFrameValidField = AccessTools.Field(behaviorType, "_cameraFrameValid");
            _beginReturnMethod = AccessTools.Method(
                behaviorType,
                "BeginReturn",
                new[] { typeof(string), typeof(bool) });
            _releaseCameraMethod = AccessTools.Method(
                behaviorType,
                "ReleaseCustomCameraOwnership",
                new[] { typeof(string) });

            PatchPrefix(harmony, behaviorType, "StartGuidedShot", nameof(StartShotPrefix));
            PatchPrefix(harmony, behaviorType, "AcquireCustomCameraOwnership", nameof(AcquireCameraPrefix));
            PatchPrefix(harmony, behaviorType, "SetMissionCamera", nameof(SetMissionCameraPrefix));
            PatchPrefix(harmony, behaviorType, "BeginKillCinematic", nameof(BeginKillCinematicPrefix));
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

        private static void StartShotPrefix(object __instance)
        {
            if (__instance == null) return;

            States.Remove(__instance);
            ExperienceSettings settings = ExperienceSettings.Instance;
            States.Add(__instance, new ShotState
            {
                FollowProjectile = settings == null || settings.FollowProjectileCamera,
                EnableKillCinematic = settings == null || settings.EnableKillCinematics,
                CameraWasShown = false
            });
        }

        private static bool AcquireCameraPrefix(object __instance)
        {
            ShotState state;
            if (!TryGetState(__instance, out state) || state.FollowProjectile) return true;

            int coreState = ReadCoreState(__instance);
            return coreState == 4 || (coreState == 5 && state.CameraWasShown);
        }

        private static bool SetMissionCameraPrefix(object __instance)
        {
            ShotState state;
            if (!TryGetState(__instance, out state)) return true;

            int coreState = ReadCoreState(__instance);
            if (coreState == 4)
            {
                if (!state.EnableKillCinematic) return false;
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

                ReleaseCamera(__instance, "ProjectileFollowCameraDisabled");
                return false;
            }

            return true;
        }

        private static bool BeginKillCinematicPrefix(object __instance)
        {
            ShotState state;
            if (!TryGetState(__instance, out state) || state.EnableKillCinematic) return true;
            if (_beginReturnMethod == null) return true;

            try
            {
                if (!state.CameraWasShown && _cameraFrameValidField != null)
                    _cameraFrameValidField.SetValue(__instance, false);
                _beginReturnMethod.Invoke(__instance, new object[] { "KillCinematicDisabled", true });
                return false;
            }
            catch
            {
                // Preserve the core transition if the reflected contract is unavailable.
                return true;
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

        private static void ReleaseCamera(object instance, string reason)
        {
            if (_releaseCameraMethod == null || instance == null) return;
            try { _releaseCameraMethod.Invoke(instance, new object[] { reason }); }
            catch { }
        }
    }
}
