using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Preserves native/TOR multi-projectile volleys and adds the configured Guided
    /// Arrow standalone split count on top. Native ability missiles are never removed
    /// or replaced, so their perk-specific visuals, explosions and other callbacks remain intact.
    /// </summary>
    internal static class NativeVolleyAugmentationPatch
    {
        private sealed class NativeEntry
        {
            internal object Item;
            internal int Index;
        }

        private sealed class AugmentationState
        {
            internal object Instance;
            internal IList Tracked;
            internal object Leader;
            internal readonly List<NativeEntry> Extras = new List<NativeEntry>();
            internal bool OriginalNativeBatch;
            internal bool OriginalStandaloneSpawned;
            internal bool OriginalAcquisitionClosed;
            internal int PreviousForcedCount;
            internal bool Active;
            internal bool Restored;
        }

        [ThreadStatic]
        private static int _forcedStandaloneCount;

        private static FieldInfo _activeShotShooterField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _nativeSplitBatchDetectedField;
        private static FieldInfo _standaloneSplitSpawnedField;
        private static FieldInfo _splitSiblingAcquisitionClosedField;
        private static FieldInfo _leaderIndexField;

        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedSyntheticField;
        private static FieldInfo _trackedFormationSlotField;

        private static PropertyInfo _settingsInstanceProperty;
        private static PropertyInfo _enableStandaloneSplitProperty;
        private static PropertyInfo _standaloneSplitCountProperty;

        internal static void Install(Harmony harmony, Type behaviorType, Type settingsType)
        {
            if (harmony == null || behaviorType == null || settingsType == null) return;

            _activeShotShooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _nativeSplitBatchDetectedField = AccessTools.Field(behaviorType, "_nativeSplitBatchDetected");
            _standaloneSplitSpawnedField = AccessTools.Field(behaviorType, "_standaloneSplitSpawned");
            _splitSiblingAcquisitionClosedField = AccessTools.Field(behaviorType, "_splitSiblingAcquisitionClosed");
            _leaderIndexField = AccessTools.Field(behaviorType, "_missileIndex");

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType != null)
            {
                _trackedIndexField = AccessTools.Field(trackedType, "Index");
                _trackedSyntheticField = AccessTools.Field(trackedType, "SyntheticProjectile");
                _trackedFormationSlotField = AccessTools.Field(trackedType, "FormationSlot");
            }

            Type globalSettingsOpen = typeof(MCM.Abstractions.Base.Global.GlobalSettings<>);
            Type closedSettings = globalSettingsOpen.MakeGenericType(settingsType);
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
            MethodInfo countGetter = _standaloneSplitCountProperty?.GetGetMethod(true);
            if (ensureMethod == null ||
                countGetter == null ||
                _activeShotShooterField == null ||
                _trackedMissilesField == null ||
                _nativeSplitBatchDetectedField == null ||
                _standaloneSplitSpawnedField == null ||
                _splitSiblingAcquisitionClosedField == null ||
                _trackedIndexField == null ||
                _trackedSyntheticField == null ||
                _settingsInstanceProperty == null ||
                _enableStandaloneSplitProperty == null)
                return;

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(NativeVolleyAugmentationPatch), nameof(EnsurePrefix)))
                {
                    priority = Priority.First
                };
                HarmonyMethod postfix = new HarmonyMethod(
                    AccessTools.Method(typeof(NativeVolleyAugmentationPatch), nameof(EnsurePostfix)))
                {
                    priority = Priority.Last
                };
                HarmonyMethod finalizer = new HarmonyMethod(
                    AccessTools.Method(typeof(NativeVolleyAugmentationPatch), nameof(EnsureFinalizer)))
                {
                    priority = Priority.Last
                };
                harmony.Patch(ensureMethod, prefix: prefix, postfix: postfix, finalizer: finalizer);
            }
            catch
            {
                return;
            }

            try
            {
                HarmonyMethod countPostfix = new HarmonyMethod(
                    AccessTools.Method(typeof(NativeVolleyAugmentationPatch), nameof(CountGetterPostfix)))
                {
                    priority = Priority.Last
                };
                harmony.Patch(countGetter, postfix: countPostfix);
            }
            catch
            {
                // Without the temporary count override, leave native volleys untouched.
            }
        }

        private static void EnsurePrefix(object __instance, out AugmentationState __state)
        {
            __state = null;
            if (__instance == null || !TryGetConfiguredAdditionalCount(out int additionalCount)) return;
            if (GetBool(_standaloneSplitSpawnedField, __instance)) return;

            Agent shooter;
            try { shooter = _activeShotShooterField.GetValue(__instance) as Agent; }
            catch { return; }
            if (shooter == null || (Agent.Main != null && shooter != Agent.Main)) return;

            IList tracked = GetTracked(__instance);
            if (tracked == null || tracked.Count < 2) return;

            object leader = SelectNativeLeader(__instance, tracked);
            if (leader == null) return;

            List<NativeEntry> nativeEntries = new List<NativeEntry>();
            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item != null && !IsSynthetic(item))
                {
                    nativeEntries.Add(new NativeEntry { Item = item, Index = i });
                }
            }
            if (nativeEntries.Count < 2) return;

            AugmentationState state = new AugmentationState
            {
                Instance = __instance,
                Tracked = tracked,
                Leader = leader,
                OriginalNativeBatch = GetBool(_nativeSplitBatchDetectedField, __instance),
                OriginalStandaloneSpawned = GetBool(_standaloneSplitSpawnedField, __instance),
                OriginalAcquisitionClosed = GetBool(_splitSiblingAcquisitionClosedField, __instance),
                PreviousForcedCount = _forcedStandaloneCount,
                Active = true
            };

            for (int i = 0; i < nativeEntries.Count; i++)
            {
                NativeEntry entry = nativeEntries[i];
                if (!ReferenceEquals(entry.Item, leader)) state.Extras.Add(entry);
            }
            if (state.Extras.Count == 0) return;

            try
            {
                for (int i = state.Extras.Count - 1; i >= 0; i--)
                    tracked.RemoveAt(state.Extras[i].Index);

                SetBool(_nativeSplitBatchDetectedField, __instance, false);
                SetBool(_standaloneSplitSpawnedField, __instance, false);
                SetBool(_splitSiblingAcquisitionClosedField, __instance, true);

                // The core calculates additions as targetCount - currentTrackedCount. With only
                // the authoritative leader temporarily visible, configured + 1 yields exactly
                // the configured number of new Guided Arrow followers.
                _forcedStandaloneCount = Math.Min(48, additionalCount + 1);
                __state = state;
            }
            catch
            {
                Restore(state);
                __state = null;
            }
        }

        private static void EnsurePostfix(object __instance, AugmentationState __state)
        {
            Restore(__state);
        }

        private static Exception EnsureFinalizer(Exception __exception, AugmentationState __state)
        {
            Restore(__state);
            return __exception;
        }

        private static void Restore(AugmentationState state)
        {
            if (state == null || !state.Active || state.Restored) return;
            state.Restored = true;
            _forcedStandaloneCount = state.PreviousForcedCount;

            try
            {
                IList tracked = state.Tracked ?? GetTracked(state.Instance);
                if (tracked != null)
                {
                    state.Extras.Sort((left, right) => left.Index.CompareTo(right.Index));
                    for (int i = 0; i < state.Extras.Count; i++)
                    {
                        NativeEntry entry = state.Extras[i];
                        if (entry.Item == null || ContainsReference(tracked, entry.Item)) continue;
                        int insertionIndex = Math.Max(0, Math.Min(entry.Index, tracked.Count));
                        tracked.Insert(insertionIndex, entry.Item);
                    }
                    ReassignFormationSlots(tracked);
                }

                bool standaloneCreated = GetBool(_standaloneSplitSpawnedField, state.Instance);
                SetBool(_nativeSplitBatchDetectedField, state.Instance, state.OriginalNativeBatch || state.Extras.Count > 0);
                SetBool(
                    _standaloneSplitSpawnedField,
                    state.Instance,
                    standaloneCreated || state.OriginalStandaloneSpawned);
                SetBool(
                    _splitSiblingAcquisitionClosedField,
                    state.Instance,
                    state.OriginalAcquisitionClosed || state.Extras.Count > 0);
            }
            catch
            {
                try
                {
                    SetBool(_nativeSplitBatchDetectedField, state.Instance, state.OriginalNativeBatch);
                    SetBool(_standaloneSplitSpawnedField, state.Instance, state.OriginalStandaloneSpawned);
                    SetBool(_splitSiblingAcquisitionClosedField, state.Instance, state.OriginalAcquisitionClosed);
                }
                catch { }
            }
        }

        private static void CountGetterPostfix(ref int __result)
        {
            if (_forcedStandaloneCount > 0) __result = _forcedStandaloneCount;
        }

        private static bool TryGetConfiguredAdditionalCount(out int count)
        {
            count = 0;
            try
            {
                object settings = _settingsInstanceProperty.GetValue(null, null);
                if (settings == null) return false;
                bool enabled = (bool)_enableStandaloneSplitProperty.GetValue(settings, null);
                if (!enabled) return false;
                count = (int)_standaloneSplitCountProperty.GetValue(settings, null);
                return count > 1;
            }
            catch
            {
                count = 0;
                return false;
            }
        }

        private static object SelectNativeLeader(object instance, IList tracked)
        {
            int leaderIndex = -1;
            try { leaderIndex = (int)_leaderIndexField.GetValue(instance); }
            catch { }

            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item == null || IsSynthetic(item)) continue;
                try
                {
                    if ((int)_trackedIndexField.GetValue(item) == leaderIndex) return item;
                }
                catch { }
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item != null && !IsSynthetic(item)) return item;
            }
            return null;
        }

        private static IList GetTracked(object instance)
        {
            try { return _trackedMissilesField.GetValue(instance) as IList; }
            catch { return null; }
        }

        private static bool IsSynthetic(object tracked)
        {
            try { return tracked != null && (bool)_trackedSyntheticField.GetValue(tracked); }
            catch { return false; }
        }

        private static bool ContainsReference(IList list, object item)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return true;
            return false;
        }

        private static void ReassignFormationSlots(IList tracked)
        {
            if (_trackedFormationSlotField == null || tracked == null) return;
            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item == null) continue;
                try { _trackedFormationSlotField.SetValue(item, i); }
                catch { }
            }
        }

        private static bool GetBool(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return false;
            try { return (bool)field.GetValue(instance); }
            catch { return false; }
        }

        private static void SetBool(FieldInfo field, object instance, bool value)
        {
            if (field == null || instance == null) return;
            try { field.SetValue(instance, value); }
            catch { }
        }
    }
}
