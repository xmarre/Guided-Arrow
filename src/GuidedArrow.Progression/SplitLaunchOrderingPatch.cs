using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Materializes ordinary generated split followers during the launch callback, before the
    /// stable core initializes guidance or Autoguidance. Native/TOR sibling acquisition remains
    /// open for the core's original bounded window, so delayed native volley members can still
    /// join the already-complete generated group.
    /// </summary>
    internal static class SplitLaunchOrderingPatch
    {
        private sealed class EarlySpawnState
        {
            internal object Instance;
            internal int Generation;
            internal bool LegitimateCloseObserved;
            internal bool Restored;
        }

        [ThreadStatic]
        private static EarlySpawnState _activeEarlySpawn;

        private static FieldInfo _generationField;
        private static FieldInfo _acquisitionClosedField;
        private static FieldInfo _standaloneSplitSpawnedField;
        private static FieldInfo _nativeSplitBatchDetectedField;
        private static FieldInfo _trackedMissilesField;

        private static PropertyInfo _settingsInstanceProperty;
        private static PropertyInfo _enableStandaloneSplitProperty;
        private static PropertyInfo _standaloneSplitCountProperty;

        internal static void Install(Harmony harmony, Type behaviorType, Type settingsType)
        {
            if (harmony == null || behaviorType == null || settingsType == null) return;

            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _acquisitionClosedField = AccessTools.Field(behaviorType, "_splitSiblingAcquisitionClosed");
            _standaloneSplitSpawnedField = AccessTools.Field(behaviorType, "_standaloneSplitSpawned");
            _nativeSplitBatchDetectedField = AccessTools.Field(behaviorType, "_nativeSplitBatchDetected");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");

            Type closedSettings = typeof(MCM.Abstractions.Base.Global.GlobalSettings<>).MakeGenericType(settingsType);
            _settingsInstanceProperty = closedSettings.GetProperty(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            _enableStandaloneSplitProperty = settingsType.GetProperty(
                "EnableStandaloneSplitProjectiles",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _standaloneSplitCountProperty = settingsType.GetProperty(
                "StandaloneSplitProjectileCount",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            MethodInfo ensureMethod = AccessTools.Method(behaviorType, "EnsureStandaloneSplitProjectiles");
            MethodInfo closeMethod = AccessTools.Method(behaviorType, "CloseSplitSiblingAcquisition");
            if (_generationField == null ||
                _acquisitionClosedField == null ||
                _standaloneSplitSpawnedField == null ||
                _nativeSplitBatchDetectedField == null ||
                _trackedMissilesField == null ||
                _settingsInstanceProperty == null ||
                _enableStandaloneSplitProperty == null ||
                _standaloneSplitCountProperty == null ||
                ensureMethod == null ||
                closeMethod == null)
                return;

            try
            {
                harmony.Patch(
                    ensureMethod,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(SplitLaunchOrderingPatch), nameof(EnsurePrefix)))
                    {
                        // NativeVolleyAugmentationPatch must inspect the real open-acquisition
                        // state first. With one tracked missile it then correctly leaves the call alone.
                        priority = Priority.Last
                    },
                    postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(SplitLaunchOrderingPatch), nameof(EnsurePostfix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(SplitLaunchOrderingPatch), nameof(EnsureFinalizer)))
                    {
                        priority = Priority.First
                    });

                harmony.Patch(
                    closeMethod,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(SplitLaunchOrderingPatch), nameof(ClosePrefix)))
                    {
                        priority = Priority.First
                    });
            }
            catch
            {
                // The SHA-locked core remains untouched if its private shape changes.
            }
        }

        private static void EnsurePrefix(object __instance, out EarlySpawnState __state)
        {
            __state = null;
            if (__instance == null || _activeEarlySpawn != null) return;
            if (!IsGeneratedSplittingEnabled()) return;
            if (GetBool(_acquisitionClosedField, __instance) ||
                GetBool(_standaloneSplitSpawnedField, __instance) ||
                GetBool(_nativeSplitBatchDetectedField, __instance) ||
                GetCount(_trackedMissilesField, __instance) != 1)
                return;

            EarlySpawnState state = new EarlySpawnState
            {
                Instance = __instance,
                Generation = ReadInt(_generationField, __instance, 0)
            };

            // EnsureStandaloneSplitProjectiles normally waits for the 100 ms / 12 m sibling
            // window to close. Temporarily satisfying that one gate lets the exact core spawn
            // its configured followers at the leader's launch position and velocity.
            SetBool(_acquisitionClosedField, __instance, true);
            if (!GetBool(_acquisitionClosedField, __instance)) return;

            _activeEarlySpawn = state;
            __state = state;
        }

        private static bool ClosePrefix(object __instance, string __0)
        {
            EarlySpawnState state = _activeEarlySpawn;
            if (state == null ||
                __instance == null ||
                !ReferenceEquals(state.Instance, __instance) ||
                ReadInt(_generationField, __instance, 0) != state.Generation)
                return true;

            if (string.Equals(__0, "StandaloneSplitBatchCreated", StringComparison.Ordinal) &&
                !state.LegitimateCloseObserved)
            {
                // Generated followers now exist, while the original native/TOR sibling window
                // stays open. A delayed native volley can still be discovered and appended.
                SetBool(_acquisitionClosedField, __instance, false);
                return false;
            }

            // Preserve impact, travel-envelope, timeout and native-batch closures that occur
            // reentrantly while the generated followers are being created.
            state.LegitimateCloseObserved = true;
            return true;
        }

        private static void EnsurePostfix(EarlySpawnState __state)
        {
            Restore(__state);
        }

        private static Exception EnsureFinalizer(Exception __exception, EarlySpawnState __state)
        {
            Restore(__state);
            return __exception;
        }

        private static void Restore(EarlySpawnState state)
        {
            if (state == null || state.Restored) return;
            state.Restored = true;
            if (ReferenceEquals(_activeEarlySpawn, state)) _activeEarlySpawn = null;

            if (state.LegitimateCloseObserved || state.Instance == null) return;
            if (ReadInt(_generationField, state.Instance, 0) != state.Generation) return;

            // Restore the real acquisition state after an early return, a failed/partial spawn,
            // or the deliberately suppressed StandaloneSplitBatchCreated closure.
            SetBool(_acquisitionClosedField, state.Instance, false);
        }

        private static bool IsGeneratedSplittingEnabled()
        {
            try
            {
                object settings = _settingsInstanceProperty.GetValue(null, null);
                if (settings == null) return false;
                if (!(bool)_enableStandaloneSplitProperty.GetValue(settings, null)) return false;
                return (int)_standaloneSplitCountProperty.GetValue(settings, null) > 1;
            }
            catch
            {
                return false;
            }
        }

        private static IList ReadList(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return null;
            try { return field.GetValue(instance) as IList; }
            catch { return null; }
        }

        private static int GetCount(FieldInfo field, object instance)
        {
            IList list = ReadList(field, instance);
            return list?.Count ?? 0;
        }

        private static bool GetBool(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return false;
            try { return (bool)field.GetValue(instance); }
            catch { return false; }
        }

        private static int ReadInt(FieldInfo field, object instance, int fallback)
        {
            if (field == null || instance == null) return fallback;
            try { return (int)field.GetValue(instance); }
            catch { return fallback; }
        }

        private static void SetBool(FieldInfo field, object instance, bool value)
        {
            if (field == null || instance == null) return;
            try { field.SetValue(instance, value); }
            catch { }
        }
    }
}
