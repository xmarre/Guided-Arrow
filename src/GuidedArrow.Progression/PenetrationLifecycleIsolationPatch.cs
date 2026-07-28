using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Restores the user's original penetration settings immediately after the shot-scoped
    /// progression snapshot is applied. This keeps all other mastery effects active while
    /// preventing progression from lowering the core penetration budget inside the native
    /// impact-lifetime path used by concentrated volleys.
    /// </summary>
    internal static class PenetrationLifecycleIsolationPatch
    {
        private static readonly string[] PenetrationSettingNames =
        {
            "EnablePenetrationOverride",
            "MaximumAgentPenetrations",
            "InfiniteAgentPenetrations"
        };

        private static FieldInfo _activeStateField;
        private static FieldInfo _settingsField;
        private static FieldInfo _valuesField;
        private static FieldInfo _savedFieldField;
        private static FieldInfo _savedValueField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type runtimeType = typeof(ProgressionRuntimeSettingsPatch);
            Type stateType = runtimeType.GetNestedType("RuntimeState", BindingFlags.NonPublic);
            Type valueType = runtimeType.GetNestedType("FieldValue", BindingFlags.NonPublic);
            if (stateType == null || valueType == null) return;

            _activeStateField = AccessTools.Field(runtimeType, "_activeState");
            _settingsField = AccessTools.Field(stateType, "Settings");
            _valuesField = AccessTools.Field(stateType, "Values");
            _savedFieldField = AccessTools.Field(valueType, "Field");
            _savedValueField = AccessTools.Field(valueType, "Value");
            if (_activeStateField == null ||
                _settingsField == null ||
                _valuesField == null ||
                _savedFieldField == null ||
                _savedValueField == null)
                return;

            MethodInfo prefix = AccessTools.Method(
                typeof(PenetrationLifecycleIsolationPatch),
                nameof(RestorePenetrationPrefix));
            if (prefix == null) return;

            foreach (string methodName in new[] { "OnAgentShootMissile", "StartGuidedShot" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == methodName && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(prefix) { priority = Priority.Last });
                    }
                    catch { }
                }
            }
        }

        private static void RestorePenetrationPrefix()
        {
            if (!ProgressionService.Enabled) return;

            try
            {
                object state = _activeStateField.GetValue(null);
                if (state == null) return;

                object settings = _settingsField.GetValue(state);
                IList values = _valuesField.GetValue(state) as IList;
                if (settings == null || values == null) return;

                for (int i = 0; i < values.Count; i++)
                {
                    object saved = values[i];
                    if (saved == null) continue;

                    FieldInfo field = _savedFieldField.GetValue(saved) as FieldInfo;
                    if (field == null || !IsPenetrationSetting(field.Name)) continue;

                    object original = _savedValueField.GetValue(saved);
                    field.SetValue(settings, original);
                }
            }
            catch
            {
                // Isolation must never replace the core shot callback with a sidecar exception.
            }
        }

        private static bool IsPenetrationSetting(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return false;

            string name = rawName;
            if (name.StartsWith("<", StringComparison.Ordinal) &&
                name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            {
                name = name.Substring(1, name.Length - 17);
            }

            for (int i = 0; i < PenetrationSettingNames.Length; i++)
            {
                if (string.Equals(name, PenetrationSettingNames[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
