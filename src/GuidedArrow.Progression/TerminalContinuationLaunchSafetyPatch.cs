using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps terminal penetration continuations on Bannerlord's normal MissionWeapon launch path.
    /// The locked core's resolved-damage bridge patches Mission.AddMissileAux and overwrites native
    /// WeaponData/WeaponStatsData by reference. Re-entering that override after terminal missile
    /// teardown can raise AccessViolationException even after the native source and collision queues
    /// have been fully quarantined.
    ///
    /// Standalone split creation keeps the bridge. Only TrySpawnPenetrationContinuation bypasses the
    /// by-reference override. Synthetic tracked missiles also retain the original shot's canonical
    /// MissionWeapon and resolved launch packet rather than replacing them with values read back from
    /// a synthetic missile that will shortly be destroyed.
    /// </summary>
    internal static class TerminalContinuationLaunchSafetyPatch
    {
        [ThreadStatic]
        private static int _terminalContinuationDepth;

        private sealed class OverrideBypassScope : IDisposable
        {
            private readonly object _previousOverride;
            private bool _disposed;

            internal OverrideBypassScope(object previousOverride)
            {
                _previousOverride = previousOverride;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try { _activeOverrideField?.SetValue(null, _previousOverride); }
                catch { }
            }
        }

        private static FieldInfo _activeOverrideField;
        private static FieldInfo _spawnWeaponField;
        private static FieldInfo _spawnWeaponValidField;
        private static FieldInfo _resolvedLaunchDataField;
        private static FieldInfo _spawnOrientationField;
        private static FieldInfo _spawnOrientationValidField;
        private static FieldInfo _spawnBaseSpeedField;
        private static FieldInfo _spawnHasRigidBodyField;
        private static MethodInfo _resolvedLaunchCloneMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType(
                "TrackedMissile",
                BindingFlags.NonPublic);
            Type bridgeType = behaviorType.Assembly.GetType(
                "GuidedArrow.MissileDamageBridge",
                false);
            if (trackedType == null || bridgeType == null) return;

            MethodInfo continuationMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            MethodInfo createTrackedMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "CreateTrackedMissileFromSpawn" &&
                    method.GetParameters().Length == 5);
            MethodInfo overrideMethod = bridgeType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "OverrideNextSyntheticMissile" &&
                    method.GetParameters().Length == 3);

            _activeOverrideField = AccessTools.Field(bridgeType, "_activeOverride");
            _spawnWeaponField = AccessTools.Field(trackedType, "SpawnWeapon");
            _spawnWeaponValidField = AccessTools.Field(trackedType, "SpawnWeaponValid");
            _resolvedLaunchDataField = AccessTools.Field(trackedType, "ResolvedLaunchData");
            _spawnOrientationField = AccessTools.Field(trackedType, "SpawnOrientation");
            _spawnOrientationValidField = AccessTools.Field(trackedType, "SpawnOrientationValid");
            _spawnBaseSpeedField = AccessTools.Field(trackedType, "SpawnBaseSpeed");
            _spawnHasRigidBodyField = AccessTools.Field(trackedType, "SpawnHasRigidBody");
            _resolvedLaunchCloneMethod = _resolvedLaunchDataField?.FieldType.GetMethod(
                "Clone",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            if (continuationMethod == null ||
                createTrackedMethod == null ||
                overrideMethod == null ||
                _activeOverrideField == null ||
                _spawnWeaponField == null ||
                _spawnWeaponValidField == null ||
                _resolvedLaunchDataField == null ||
                _spawnOrientationField == null ||
                _spawnOrientationValidField == null ||
                _spawnBaseSpeedField == null ||
                _spawnHasRigidBodyField == null)
                return;

            MethodInfo continuationPrefix = AccessTools.Method(
                typeof(TerminalContinuationLaunchSafetyPatch),
                nameof(ContinuationPrefix));
            MethodInfo continuationFinalizer = AccessTools.Method(
                typeof(TerminalContinuationLaunchSafetyPatch),
                nameof(ContinuationFinalizer));
            MethodInfo overridePrefix = AccessTools.Method(
                typeof(TerminalContinuationLaunchSafetyPatch),
                nameof(OverridePrefix));
            MethodInfo createPostfix = AccessTools.Method(
                typeof(TerminalContinuationLaunchSafetyPatch),
                nameof(CreateTrackedPostfix));

            if (continuationPrefix == null ||
                continuationFinalizer == null ||
                overridePrefix == null ||
                createPostfix == null)
                return;

            try
            {
                harmony.Patch(
                    continuationMethod,
                    prefix: new HarmonyMethod(continuationPrefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(continuationFinalizer) { priority = Priority.Last });
                harmony.Patch(
                    overrideMethod,
                    prefix: new HarmonyMethod(overridePrefix) { priority = Priority.First });
                harmony.Patch(
                    createTrackedMethod,
                    postfix: new HarmonyMethod(createPostfix) { priority = Priority.Last });
            }
            catch
            {
                _terminalContinuationDepth = 0;
            }
        }

        private static void ContinuationPrefix(out bool __state)
        {
            __state = true;
            if (_terminalContinuationDepth < int.MaxValue)
                _terminalContinuationDepth++;
        }

        private static Exception ContinuationFinalizer(
            Exception __exception,
            bool __state)
        {
            if (__state && _terminalContinuationDepth > 0)
                _terminalContinuationDepth--;
            return __exception;
        }

        private static bool OverridePrefix(ref IDisposable __result)
        {
            if (_terminalContinuationDepth <= 0) return true;

            try
            {
                object previous = _activeOverrideField.GetValue(null);
                _activeOverrideField.SetValue(null, null);
                __result = new OverrideBypassScope(previous);
                return false;
            }
            catch
            {
                // Fall back to the locked core's bridge if its thread-static layout changes.
                return true;
            }
        }

        private static void CreateTrackedPostfix(
            object __1,
            object __result)
        {
            if (__1 == null || __result == null) return;

            try
            {
                _spawnWeaponField.SetValue(
                    __result,
                    _spawnWeaponField.GetValue(__1));
                _spawnWeaponValidField.SetValue(
                    __result,
                    _spawnWeaponValidField.GetValue(__1));
                _spawnOrientationField.SetValue(
                    __result,
                    _spawnOrientationField.GetValue(__1));
                _spawnOrientationValidField.SetValue(
                    __result,
                    _spawnOrientationValidField.GetValue(__1));
                _spawnBaseSpeedField.SetValue(
                    __result,
                    _spawnBaseSpeedField.GetValue(__1));
                _spawnHasRigidBodyField.SetValue(
                    __result,
                    _spawnHasRigidBodyField.GetValue(__1));

                object sourceData = _resolvedLaunchDataField.GetValue(__1);
                object canonicalData = sourceData;
                if (sourceData != null && _resolvedLaunchCloneMethod != null)
                {
                    try { canonicalData = _resolvedLaunchCloneMethod.Invoke(sourceData, null); }
                    catch { canonicalData = sourceData; }
                }
                _resolvedLaunchDataField.SetValue(__result, canonicalData);
            }
            catch
            {
                // The newly created tracked missile remains usable with the locked core's values.
            }
        }
    }
}
