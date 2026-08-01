using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MCM.Abstractions;
using MCM.Abstractions.Base;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps the camera/targeting compatibility values in their existing sidecar settings object
    /// for migration and persistence, but presents those live properties inside the main Guided
    /// Arrow MCM page. The stable gameplay core remains byte-identical.
    /// </summary>
    internal static class McmMainSettingsIntegrationPatch
    {
        private const string MainSettingsId = "guided_arrow_v100";
        private const string IntegratedStorageSettingsId = "guided_arrow_camera_targeting_v1";

        [ThreadStatic]
        private static bool _forwardingProviderOperation;

        private static MethodInfo _providerSaveMethod;
        private static MethodInfo _providerResetMethod;

        internal static void Install(Harmony harmony)
        {
            if (harmony == null) return;

            MethodInfo groupsMethod = AccessTools.Method(
                typeof(BaseSettingsExtensions),
                nameof(BaseSettingsExtensions.GetUnsortedSettingPropertyGroups),
                new[] { typeof(BaseSettings) });
            if (groupsMethod != null)
            {
                try
                {
                    harmony.Patch(
                        groupsMethod,
                        postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(McmMainSettingsIntegrationPatch), nameof(GroupsPostfix)))
                        {
                            priority = Priority.Last
                        });
                }
                catch { }
            }

            Type providerType = typeof(BaseSettingsProvider).Assembly.GetType(
                "MCM.Implementation.DefaultSettingsProvider",
                false);
            if (providerType == null) return;

            MethodInfo definitionsGetter = AccessTools.PropertyGetter(providerType, "SettingsDefinitions");
            if (definitionsGetter != null)
            {
                try
                {
                    harmony.Patch(
                        definitionsGetter,
                        postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(McmMainSettingsIntegrationPatch), nameof(DefinitionsPostfix)))
                        {
                            priority = Priority.Last
                        });
                }
                catch { }
            }

            _providerSaveMethod = AccessTools.Method(
                providerType,
                "SaveSettings",
                new[] { typeof(BaseSettings) });
            if (_providerSaveMethod != null)
            {
                try
                {
                    harmony.Patch(
                        _providerSaveMethod,
                        postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(McmMainSettingsIntegrationPatch), nameof(SavePostfix)))
                        {
                            priority = Priority.Last
                        });
                }
                catch { _providerSaveMethod = null; }
            }

            _providerResetMethod = AccessTools.Method(
                providerType,
                "ResetSettings",
                new[] { typeof(BaseSettings) });
            if (_providerResetMethod != null)
            {
                try
                {
                    harmony.Patch(
                        _providerResetMethod,
                        postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(McmMainSettingsIntegrationPatch), nameof(ResetPostfix)))
                        {
                            priority = Priority.Last
                        });
                }
                catch { _providerResetMethod = null; }
            }
        }

        private static void GroupsPostfix(
            BaseSettings settings,
            ref IEnumerable<SettingsPropertyGroupDefinition> __result)
        {
            if (settings == null || settings.Id != MainSettingsId) return;

            try
            {
                List<SettingsPropertyGroupDefinition> groups = (__result ??
                    Enumerable.Empty<SettingsPropertyGroupDefinition>())
                    .Where(group => group != null)
                    .Select(group => group.Clone(true))
                    .ToList();

                ExperienceSettings integrated = ExperienceSettings.Instance;
                if (integrated == null)
                {
                    __result = groups;
                    return;
                }

                foreach (ISettingsPropertyDefinition property in
                    integrated.GetAllSettingPropertyDefinitions())
                {
                    if (property == null) continue;
                    SettingsPropertyDefinition clone = property.Clone(true);
                    SettingsUtils.GetGroupFor(
                        settings.SubGroupDelimiter,
                        clone,
                        groups).Add(clone);
                }

                __result = groups;
            }
            catch
            {
                // Unknown MCM shapes leave the original main settings definition untouched.
            }
        }

        private static void DefinitionsPostfix(ref IEnumerable<SettingsDefinition> __result)
        {
            try
            {
                __result = (__result ?? Enumerable.Empty<SettingsDefinition>())
                    .Where(definition =>
                        definition != null &&
                        definition.SettingsId != IntegratedStorageSettingsId)
                    .ToArray();
            }
            catch
            {
                // Failing to hide the storage page is preferable to breaking MCM discovery.
            }
        }

        private static void SavePostfix(object __instance, BaseSettings settings)
        {
            ForwardProviderOperation(
                __instance,
                settings,
                _providerSaveMethod);
        }

        private static void ResetPostfix(object __instance, BaseSettings settings)
        {
            ForwardProviderOperation(
                __instance,
                settings,
                _providerResetMethod);
        }

        private static void ForwardProviderOperation(
            object provider,
            BaseSettings settings,
            MethodInfo operation)
        {
            if (_forwardingProviderOperation ||
                provider == null ||
                operation == null ||
                settings == null ||
                settings.Id != MainSettingsId)
                return;

            ExperienceSettings integrated = ExperienceSettings.Instance;
            if (integrated == null) return;

            try
            {
                _forwardingProviderOperation = true;
                operation.Invoke(provider, new object[] { integrated });
            }
            catch
            {
                // Main settings save/reset must still complete if the sidecar storage is unavailable.
            }
            finally
            {
                _forwardingProviderOperation = false;
            }
        }
    }
}
