using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Makes the standalone split count authoritative for player shots that also
    /// produce a native/TOR multi-projectile volley. One native projectile is kept
    /// as the damage/weapon source; extra native siblings are removed before the
    /// core mission tick and the existing standalone generator creates the configured
    /// total. The stable GuidedArrow.dll remains unchanged.
    /// </summary>
    internal static class NativeVolleyOverridePatch
    {
        private sealed class VolleyState
        {
            internal readonly HashSet<int> PendingNativeIndices = new HashSet<int>();
        }

        private static readonly ConditionalWeakTable<object, VolleyState> States =
            new ConditionalWeakTable<object, VolleyState>();

        private static FieldInfo _activeShotShooterField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _nativeSplitBatchDetectedField;
        private static FieldInfo _standaloneSplitSpawnedField;
        private static FieldInfo _splitSiblingAcquisitionClosedField;
        private static FieldInfo _splitSiblingAcquireStartTimestampField;
        private static FieldInfo _pendingShotSeedsField;
        private static FieldInfo _leaderMissileField;
        private static FieldInfo _leaderIndexField;
        private static FieldInfo _cameraMissileIndexField;

        private static FieldInfo _trackedNativeMissileField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedSyntheticField;

        private static MethodInfo _pruneInvalidTrackedMissilesMethod;
        private static MethodInfo _forgetMissileMethod;
        private static FieldInfo _syntheticOverrideField;

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
            _splitSiblingAcquireStartTimestampField = AccessTools.Field(behaviorType, "_splitSiblingAcquireStartTimestamp");
            _pendingShotSeedsField = AccessTools.Field(behaviorType, "_pendingShotSeeds");
            _leaderMissileField = AccessTools.Field(behaviorType, "_missile");
            _leaderIndexField = AccessTools.Field(behaviorType, "_missileIndex");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _pruneInvalidTrackedMissilesMethod = AccessTools.Method(behaviorType, "PruneInvalidTrackedMissiles");

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType != null)
            {
                _trackedNativeMissileField = AccessTools.Field(trackedType, "Missile");
                _trackedIndexField = AccessTools.Field(trackedType, "Index");
                _trackedSyntheticField = AccessTools.Field(trackedType, "SyntheticProjectile");
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

            Type bridgeType = behaviorType.Assembly.GetType("GuidedArrow.MissileDamageBridge", false);
            if (bridgeType != null)
            {
                _forgetMissileMethod = bridgeType.GetMethod(
                    "Forget",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                _syntheticOverrideField = AccessTools.Field(bridgeType, "_activeOverride");
            }

            if (_activeShotShooterField == null ||
                _trackedMissilesField == null ||
                _trackedNativeMissileField == null ||
                _trackedIndexField == null ||
                _settingsInstanceProperty == null ||
                _enableStandaloneSplitProperty == null ||
                _standaloneSplitCountProperty == null)
                return;

            MethodInfo shootMethod = AccessTools.Method(behaviorType, "OnAgentShootMissile");
            if (shootMethod != null)
            {
                try
                {
                    harmony.Patch(
                        shootMethod,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(NativeVolleyOverridePatch), nameof(ShootPrefix))));
                }
                catch { }
            }

            MethodInfo tickMethod = AccessTools.Method(behaviorType, "OnMissionTick");
            if (tickMethod != null)
            {
                try
                {
                    HarmonyMethod prefix = new HarmonyMethod(
                        AccessTools.Method(typeof(NativeVolleyOverridePatch), nameof(MissionTickPrefix)))
                    {
                        priority = Priority.First
                    };
                    harmony.Patch(tickMethod, prefix: prefix);
                }
                catch { }
            }

            MethodInfo ensureStandaloneMethod = AccessTools.Method(behaviorType, "EnsureStandaloneSplitProjectiles");
            if (ensureStandaloneMethod != null)
            {
                try
                {
                    HarmonyMethod prefix = new HarmonyMethod(
                        AccessTools.Method(typeof(NativeVolleyOverridePatch), nameof(EnsureStandalonePrefix)))
                    {
                        priority = Priority.First
                    };
                    harmony.Patch(ensureStandaloneMethod, prefix: prefix);
                }
                catch { }
            }
        }

        private static bool ShootPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 7 || !IsOverrideActive()) return true;
            if (IsSyntheticMissileCreationActive()) return true;

            Agent shooter = __args[0] as Agent;
            if (shooter == null) return true;

            Agent activeShooter;
            try { activeShooter = _activeShotShooterField.GetValue(__instance) as Agent; }
            catch { return true; }
            if (activeShooter == null || activeShooter != shooter) return true;

            IList tracked = GetTracked(__instance);
            if (tracked == null || tracked.Count == 0) return true;

            int forcedIndex;
            try { forcedIndex = (int)__args[6]; }
            catch { return true; }

            // This is an additional native projectile for the already-active player shot.
            // Suppress only Guided Arrow's callback handling; TOR/native mission logic still
            // creates the projectile, which is removed safely at the next mission tick.
            if (forcedIndex >= 0)
                States.GetOrCreateValue(__instance).PendingNativeIndices.Add(forcedIndex);

            return false;
        }

        private static void MissionTickPrefix(object __instance)
        {
            TryReplaceNativeVolley(__instance);
        }

        private static void EnsureStandalonePrefix(object __instance)
        {
            // This prefix runs at the exact point where the stable core would otherwise
            // skip standalone generation because it has just discovered a TOR/native batch.
            // It avoids waiting for a later mission tick and prevents the five native
            // projectiles from reaching collision handling.
            TryReplaceNativeVolley(__instance);
        }

        private static bool TryReplaceNativeVolley(object __instance)
        {
            if (__instance == null || !IsOverrideActive()) return false;

            IList tracked = GetTracked(__instance);
            if (tracked == null || tracked.Count == 0) return false;

            Agent activeShooter;
            try { activeShooter = _activeShotShooterField.GetValue(__instance) as Agent; }
            catch { return false; }
            if (activeShooter == null || (Agent.Main != null && activeShooter != Agent.Main)) return false;

            object leader = SelectLeader(__instance, tracked);
            if (leader == null) return false;

            List<object> nativeExtras = new List<object>();
            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item == null || ReferenceEquals(item, leader)) continue;
                if (IsSynthetic(item)) continue;
                nativeExtras.Add(item);
            }

            RemoveQueuedNativeMissiles(__instance, nativeExtras);
            if (nativeExtras.Count == 0) return false;

            // TOR siblings may be discovered during the first original OnMissionTick, after
            // the outer tick prefix has already run. Running this same conversion directly
            // before EnsureStandaloneSplitProjectiles makes the override authoritative in
            // that very tick instead of allowing the native batch to become permanent.
            try { _pruneInvalidTrackedMissilesMethod?.Invoke(__instance, null); }
            catch { }

            tracked = GetTracked(__instance);
            if (tracked == null) return false;
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                object item = tracked[i];
                if (item != null && !ReferenceEquals(item, leader) && nativeExtras.Contains(item))
                    tracked.RemoveAt(i);
            }

            // The previous core path may already have marked standalone splitting as handled
            // when it saw the native batch. Reset both decisions after removing the siblings.
            SetBool(_nativeSplitBatchDetectedField, __instance, false);
            SetBool(_standaloneSplitSpawnedField, __instance, false);
            SetBool(_splitSiblingAcquisitionClosedField, __instance, true);
            try { _splitSiblingAcquireStartTimestampField?.SetValue(__instance, 0L); }
            catch { }
            try { (_pendingShotSeedsField?.GetValue(__instance) as IList)?.Clear(); }
            catch { }

            try
            {
                object leaderMissile = _trackedNativeMissileField.GetValue(leader);
                int leaderIndex = (int)_trackedIndexField.GetValue(leader);
                _leaderMissileField?.SetValue(__instance, leaderMissile);
                _leaderIndexField?.SetValue(__instance, leaderIndex);
                _cameraMissileIndexField?.SetValue(__instance, leaderIndex);
            }
            catch { }

            return true;
        }

        private static object SelectLeader(object instance, IList tracked)
        {
            if (tracked == null || tracked.Count == 0) return null;

            int leaderIndex = -1;
            try
            {
                if (_leaderIndexField != null)
                    leaderIndex = (int)_leaderIndexField.GetValue(instance);
            }
            catch { }

            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item == null || IsSynthetic(item)) continue;
                try
                {
                    if ((int)_trackedIndexField.GetValue(item) == leaderIndex)
                        return item;
                }
                catch { }
            }

            for (int i = 0; i < tracked.Count; i++)
            {
                object item = tracked[i];
                if (item != null && !IsSynthetic(item)) return item;
            }
            return tracked[0];
        }

        private static void RemoveQueuedNativeMissiles(object instance, IList<object> trackedExtras)
        {
            Mission mission = (instance as MissionBehavior)?.Mission ?? Mission.Current;
            if (mission == null) return;

            HashSet<int> indices = new HashSet<int>();
            VolleyState state;
            if (States.TryGetValue(instance, out state))
            {
                foreach (int index in state.PendingNativeIndices) indices.Add(index);
                state.PendingNativeIndices.Clear();
            }

            if (trackedExtras != null)
            {
                for (int i = 0; i < trackedExtras.Count; i++)
                {
                    object item = trackedExtras[i];
                    if (item == null) continue;
                    try
                    {
                        int index = (int)_trackedIndexField.GetValue(item);
                        if (index >= 0) indices.Add(index);
                    }
                    catch { }
                }
            }

            foreach (int index in indices)
            {
                try { mission.RemoveMissileAsClient(index); }
                catch { }
                try { _forgetMissileMethod?.Invoke(null, new object[] { mission, index }); }
                catch { }
            }
        }

        private static IList GetTracked(object instance)
        {
            try { return _trackedMissilesField.GetValue(instance) as IList; }
            catch { return null; }
        }

        private static bool IsSynthetic(object tracked)
        {
            if (tracked == null || _trackedSyntheticField == null) return false;
            try { return (bool)_trackedSyntheticField.GetValue(tracked); }
            catch { return false; }
        }

        private static bool IsSyntheticMissileCreationActive()
        {
            if (_syntheticOverrideField == null) return false;
            try { return _syntheticOverrideField.GetValue(null) != null; }
            catch { return false; }
        }

        private static bool IsOverrideActive()
        {
            try
            {
                object settings = _settingsInstanceProperty.GetValue(null, null);
                if (settings == null) return false;
                bool enabled = (bool)_enableStandaloneSplitProperty.GetValue(settings, null);
                int count = (int)_standaloneSplitCountProperty.GetValue(settings, null);
                return enabled && count > 1;
            }
            catch
            {
                return false;
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
