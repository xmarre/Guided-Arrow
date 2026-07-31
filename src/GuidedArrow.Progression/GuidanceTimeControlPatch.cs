using System;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Makes disabling Proximity Time Dilation mean normal mission speed. The stable core falls
    /// back to the legacy InitialGuidanceTimeSpeed request when proximity control is disabled.
    /// Manual Q/E speed selection remains available after that automatic request is removed.
    /// </summary>
    internal static class GuidanceTimeControlPatch
    {
        private static PropertyInfo _settingsInstanceProperty;
        private static PropertyInfo _enableProximityProperty;
        private static FieldInfo _proximityTargetSpeedField;
        private static FieldInfo _proximityCurrentSpeedField;
        private static MethodInfo _removeGuidanceTimeRequestMethod;

        internal static void Install(Harmony harmony, Type behaviorType, Type settingsType)
        {
            if (harmony == null || behaviorType == null || settingsType == null) return;

            Type closedSettings = typeof(MCM.Abstractions.Base.Global.GlobalSettings<>).MakeGenericType(settingsType);
            _settingsInstanceProperty = closedSettings.GetProperty(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            _enableProximityProperty = settingsType.GetProperty(
                "EnableProximityTimeDilation",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _proximityTargetSpeedField = AccessTools.Field(behaviorType, "_proximityTargetSpeed");
            _proximityCurrentSpeedField = AccessTools.Field(behaviorType, "_proximityCurrentSpeed");
            _removeGuidanceTimeRequestMethod = AccessTools.Method(behaviorType, "RemoveGuidanceTimeRequest");

            MethodInfo initialize = AccessTools.Method(behaviorType, "InitializeGuidanceTimeControl");
            MethodInfo postfix = AccessTools.Method(typeof(GuidanceTimeControlPatch), nameof(InitializePostfix));
            if (initialize == null || postfix == null) return;

            try
            {
                harmony.Patch(initialize, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
            }
            catch { }
        }

        private static void InitializePostfix(object __instance)
        {
            if (__instance == null || IsProximityEnabled()) return;

            try
            {
                // Remove only Guided Arrow's guidance request. Requests owned by Bannerlord or
                // other systems retain their normal priority and lifetime.
                _removeGuidanceTimeRequestMethod?.Invoke(__instance, null);
                _proximityTargetSpeedField?.SetValue(__instance, 1f);
                _proximityCurrentSpeedField?.SetValue(__instance, 1f);
            }
            catch
            {
                // Failure-open preserves the stable core on unknown reflected API shapes.
            }
        }

        private static bool IsProximityEnabled()
        {
            if (_settingsInstanceProperty == null || _enableProximityProperty == null) return true;

            try
            {
                object settings = _settingsInstanceProperty.GetValue(null, null);
                return settings == null || Convert.ToBoolean(_enableProximityProperty.GetValue(settings, null));
            }
            catch
            {
                return true;
            }
        }
    }
}
