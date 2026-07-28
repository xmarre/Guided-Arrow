using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Applies mastery-derived settings once before Guided Arrow evaluates a shot and keeps the
    /// resulting snapshot active for the complete guided-shot lifetime. Terminal handling only
    /// marks the snapshot for restoration; the actual restore happens after the next display tick
    /// so a concentrated impact burst cannot observe settings changing while callbacks unwind.
    /// </summary>
    internal static class ProgressionRuntimeSettingsPatch
    {
        private sealed class FieldValue
        {
            internal FieldInfo Field;
            internal object Value;
        }

        private sealed class RuntimeState
        {
            internal object Instance;
            internal object Settings;
            internal readonly List<FieldValue> Values = new List<FieldValue>();
            internal object FlightProfile;
            internal PropertyInfo FlightProfileIndexProperty;
            internal int FlightProfileIndex;
            internal bool FlightProfileChanged;
            internal bool RestorePending;
            internal bool Restored;
        }

        private static readonly Dictionary<string, FieldInfo> Fields =
            new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        private static PropertyInfo _settingsInstanceProperty;
        private static FieldInfo _guidanceRealElapsedField;
        private static FieldInfo _stateField;
        private static FieldInfo _generationField;
        private static MethodInfo _beginReturnMethod;
        private static RuntimeState _activeState;

        internal static void Install(Harmony harmony, Type behaviorType, Type settingsType)
        {
            if (harmony == null || behaviorType == null || settingsType == null) return;

            Type closedSettings = typeof(MCM.Abstractions.Base.Global.GlobalSettings<>).MakeGenericType(settingsType);
            _settingsInstanceProperty = closedSettings.GetProperty(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

            Fields.Clear();
            foreach (FieldInfo field in settingsType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                string name = field.Name;
                if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    name = name.Substring(1, name.Length - 17);
                if (!Fields.ContainsKey(name)) Fields.Add(name, field);
            }

            _guidanceRealElapsedField = AccessTools.Field(behaviorType, "_guidanceRealElapsed");
            _stateField = AccessTools.Field(behaviorType, "_state");
            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _beginReturnMethod = behaviorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "BeginReturn" && method.GetParameters().Length == 2);

            MethodInfo shootPrefix = AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(ShootPrefix));
            MethodInfo shootPostfix = AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(ShootPostfix));
            MethodInfo shootFinalizer = AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(ShootFinalizer));
            MethodInfo startPrefix = AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(StartPrefix));
            MethodInfo startFinalizer = AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(StartFinalizer));
            MethodInfo restorePrefix = AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(RestorePrefix));
            MethodInfo markRestorePostfix = AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(MarkRestorePostfix));
            MethodInfo displayTickPostfix = AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(DisplayTickPostfix));

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnAgentShootMissile" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(shootPrefix) { priority = Priority.First },
                        postfix: new HarmonyMethod(shootPostfix) { priority = Priority.Last },
                        finalizer: new HarmonyMethod(shootFinalizer) { priority = Priority.Last });
                }
                catch { }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "StartGuidedShot" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(startPrefix) { priority = Priority.First },
                        finalizer: new HarmonyMethod(startFinalizer) { priority = Priority.Last });
                }
                catch { }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "ResetAll" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(restorePrefix) { priority = Priority.First });
                }
                catch { }
            }

            foreach (string methodName in new[] { "HandleGuidedSwarmTerminal", "BeginReturn" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == methodName && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            postfix: new HarmonyMethod(markRestorePostfix) { priority = Priority.Last });
                    }
                    catch { }
                }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnPreDisplayMissionTick" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(displayTickPostfix) { priority = Priority.Last });
                }
                catch { }
            }

            MethodInfo guidanceTick = AccessTools.Method(behaviorType, "TickGuidanceDisplay");
            if (guidanceTick != null && _guidanceRealElapsedField != null && _beginReturnMethod != null)
            {
                try
                {
                    harmony.Patch(
                        guidanceTick,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(ProgressionRuntimeSettingsPatch), nameof(GuidanceTickPrefix))));
                }
                catch { }
            }
        }

        internal static void RestoreActive()
        {
            Restore(_activeState);
        }

        private static void ShootPrefix(object __instance, out int __state)
        {
            __state = ReadInt(_generationField, __instance, 0);
            EnsureApplied(__instance);
        }

        private static void ShootPostfix(object __instance, int __state)
        {
            RuntimeState active = _activeState;
            if (active == null || !ReferenceEquals(active.Instance, __instance)) return;

            int currentGeneration = ReadInt(_generationField, __instance, __state);
            int state = ReadInt(_stateField, __instance, -1);
            if (state != 2 && currentGeneration == __state)
                Restore(active);
        }

        private static Exception ShootFinalizer(Exception __exception, object __instance)
        {
            if (__exception != null && _activeState != null && ReferenceEquals(_activeState.Instance, __instance))
                Restore(_activeState);
            return __exception;
        }

        private static void StartPrefix(object __instance)
        {
            EnsureApplied(__instance);
        }

        private static Exception StartFinalizer(Exception __exception, object __instance)
        {
            if (__exception != null && _activeState != null && ReferenceEquals(_activeState.Instance, __instance))
                Restore(_activeState);
            return __exception;
        }

        private static void RestorePrefix(object __instance)
        {
            if (_activeState != null && ReferenceEquals(_activeState.Instance, __instance))
                Restore(_activeState);
        }

        private static void MarkRestorePostfix(object __instance)
        {
            RuntimeState active = _activeState;
            if (active != null && ReferenceEquals(active.Instance, __instance) && !active.Restored)
                active.RestorePending = true;
        }

        private static void DisplayTickPostfix(object __instance)
        {
            RuntimeState active = _activeState;
            if (active != null &&
                ReferenceEquals(active.Instance, __instance) &&
                active.RestorePending &&
                !active.Restored)
            {
                Restore(active);
            }
        }

        private static void EnsureApplied(object instance)
        {
            if (instance == null || !ProgressionService.Enabled || Mission.Current == null || _settingsInstanceProperty == null)
                return;

            if (_activeState != null)
            {
                if (ReferenceEquals(_activeState.Instance, instance) && !_activeState.Restored)
                {
                    if (!_activeState.RestorePending) return;
                    Restore(_activeState);
                }
                else
                {
                    Restore(_activeState);
                }
            }

            object settings;
            try { settings = _settingsInstanceProperty.GetValue(null, null); }
            catch { return; }
            if (settings == null) return;

            RuntimeState state = new RuntimeState { Instance = instance, Settings = settings };
            try
            {
                ApplyProgression(state);
                _activeState = state;
            }
            catch
            {
                Restore(state);
            }
        }

        private static void ApplyProgression(RuntimeState state)
        {
            int guidedRelease = ProgressionService.Level(SkillId.GuidedRelease);
            int steadyHand = ProgressionService.Level(SkillId.SteadyHand);
            int fineCorrection = ProgressionService.Level(SkillId.FineCorrection);
            int temporalFocus = ProgressionService.Level(SkillId.TemporalFocus);
            int masterCurve = ProgressionService.Level(SkillId.MasterOfTheCurve);
            int predatorsEye = ProgressionService.Level(SkillId.PredatorsEye);
            int relentlessLock = ProgressionService.Level(SkillId.RelentlessLock);
            int pathfinder = ProgressionService.Level(SkillId.Pathfinder);
            int borrowedFlight = ProgressionService.Level(SkillId.BorrowedFlight);
            int unblinkingEye = ProgressionService.Level(SkillId.UnblinkingEye);
            int forkedShaft = ProgressionService.Level(SkillId.ForkedShaft);
            int formationDiscipline = ProgressionService.Level(SkillId.FormationDiscipline);
            int manyHeadedFlight = ProgressionService.Level(SkillId.ManyHeadedFlight);
            int drivingShot = ProgressionService.Level(SkillId.DrivingShot);
            int throughTheRanks = ProgressionService.Level(SkillId.ThroughTheRanks);
            int unbrokenFlight = ProgressionService.Level(SkillId.UnbrokenFlight);
            int synchronizedHunt = ProgressionService.Level(SkillId.SynchronizedHunt);

            OverrideBool(state, "Enabled", value => value && guidedRelease > 0);

            float guidanceCap = ProgressionBalance.GuidanceTimeCap(guidedRelease, masterCurve);
            OverrideFloat(state, "MaximumGuidanceTime", value => Math.Max(5f, Math.Min(value, guidanceCap <= 0f ? 5f : guidanceCap)));
            OverrideFloat(state, "MinimumTurnRadius", value => Math.Max(value, ProgressionBalance.TurnRadiusFloor(steadyHand, fineCorrection, masterCurve)));
            OverrideFloat(state, "SpeedAdaptiveSteeringStrength", value => Math.Min(value, ProgressionBalance.SpeedAdaptiveSteeringCap(fineCorrection, masterCurve)));

            OverrideBool(state, "EnableProximityTimeDilation", value => value && temporalFocus > 0);
            float timeFloor = ProgressionBalance.TimeSpeedFloor(temporalFocus, masterCurve);
            OverrideFloat(state, "InitialGuidanceTimeSpeed", value => Math.Max(value, timeFloor));
            OverrideFloat(state, "ProximityNearTimeSpeed", value => Math.Max(value, timeFloor));

            OverrideBool(state, "EnableAutonomousGuidance", value => value && predatorsEye > 0);
            OverrideBool(state, "AutoguidanceAutomaticReacquisition", value => value && relentlessLock > 0);
            OverrideBool(state, "AutoguidanceObstacleAvoidance", value => value && pathfinder > 0);
            OverrideBool(state, "EnableAlliedArrowTakeover", value => value && borrowedFlight > 0);
            OverrideBool(state, "AutoguidanceAlwaysOn", value => value && unblinkingEye >= 1);
            OverrideBool(state, "AutoguidancePersistToggleForBattle", value => value && unblinkingEye >= 3);
            OverrideBool(state, "AutoguidanceMultiTargetTrajectoryPlanning", value => value && unblinkingEye >= 5);
            OverrideInt(state, "AutoguidancePlannedTargetCount", value => Math.Min(value,
                ProgressionBalance.PlannedTargetCap(predatorsEye, relentlessLock, unblinkingEye, synchronizedHunt)));
            OverrideInt(state, "AutoguidanceTargetSelection", value => Math.Min(value, relentlessLock >= 10 ? 2 : (relentlessLock >= 5 ? 1 : 0)));
            OverrideInt(state, "AutoguidanceScope", value => synchronizedHunt > 0 ? value : 0);
            OverrideInt(state, "AutoguidanceSplitBehaviour", value => Math.Min(value, manyHeadedFlight >= 7 ? 2 : (manyHeadedFlight >= 3 ? 1 : 0)));
            OverrideInt(state, "AutoguidanceSplitTargetDistribution", value => manyHeadedFlight >= 5 ? value : 0);
            OverrideFloat(state, "AutoguidanceObstacleScanInterval", value => Math.Max(value, ProgressionBalance.ObstacleScanIntervalFloor(pathfinder)));
            OverrideFlightProfile(state, ProgressionBalance.FlightProfileMaximumIndex(pathfinder));

            OverrideBool(state, "EnableStandaloneSplitProjectiles", value => value && forkedShaft > 0);
            OverrideInt(state, "StandaloneSplitProjectileCount", value => Math.Min(value,
                ProgressionBalance.StandaloneSplitCountCap(forkedShaft, manyHeadedFlight)));
            OverrideInt(state, "SplitArrowFormationMode", value => Math.Min(value,
                ProgressionBalance.FormationModeMaximum(formationDiscipline)));
            OverrideFloat(state, "SplitArrowFormationResponse", value => Math.Min(value,
                ProgressionBalance.FormationResponseCap(formationDiscipline)));
            OverrideFloat(state, "SplitArrowFormationCatchUpSpeedLimit", value => Math.Min(value,
                ProgressionBalance.FormationCatchUpCap(formationDiscipline)));

            OverrideBool(state, "EnablePenetrationOverride", value => value && drivingShot > 0);
            OverrideInt(state, "MaximumAgentPenetrations", value => Math.Min(value,
                ProgressionBalance.PenetrationCap(drivingShot, throughTheRanks, unbrokenFlight)));
            OverrideBool(state, "InfiniteAgentPenetrations", value => value && unbrokenFlight >= 10);
        }

        private static bool GuidanceTickPrefix(object __instance)
        {
            if (!ProgressionService.Enabled || __instance == null || _guidanceRealElapsedField == null || _beginReturnMethod == null)
                return true;

            int guidedRelease = ProgressionService.Level(SkillId.GuidedRelease);
            if (guidedRelease <= 0) return true;

            float configured = ReadFloat("MaximumGuidanceTime", 120f);
            float cap = Math.Min(configured, ProgressionBalance.GuidanceTimeCap(
                guidedRelease,
                ProgressionService.Level(SkillId.MasterOfTheCurve)));
            if (cap <= 0f) return true;

            try
            {
                float elapsed = (float)_guidanceRealElapsedField.GetValue(__instance);
                if (elapsed < cap) return true;
                _beginReturnMethod.Invoke(__instance, new object[] { "MasteryGuidanceTimeout", true });
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void OverrideBool(RuntimeState state, string name, Func<bool, bool> transform)
        {
            if (!Fields.TryGetValue(name, out FieldInfo field) || field.FieldType != typeof(bool)) return;
            bool original = (bool)field.GetValue(state.Settings);
            SaveAndSet(state, field, original, transform(original));
        }

        private static void OverrideInt(RuntimeState state, string name, Func<int, int> transform)
        {
            if (!Fields.TryGetValue(name, out FieldInfo field) || field.FieldType != typeof(int)) return;
            int original = (int)field.GetValue(state.Settings);
            SaveAndSet(state, field, original, transform(original));
        }

        private static void OverrideFloat(RuntimeState state, string name, Func<float, float> transform)
        {
            if (!Fields.TryGetValue(name, out FieldInfo field) || field.FieldType != typeof(float)) return;
            float original = (float)field.GetValue(state.Settings);
            SaveAndSet(state, field, original, transform(original));
        }

        private static void SaveAndSet(RuntimeState state, FieldInfo field, object original, object replacement)
        {
            state.Values.Add(new FieldValue { Field = field, Value = original });
            field.SetValue(state.Settings, replacement);
        }

        private static void OverrideFlightProfile(RuntimeState state, int maximumIndex)
        {
            if (!Fields.TryGetValue("AutoguidanceFlightProfile", out FieldInfo field)) return;
            object dropdown = field.GetValue(state.Settings);
            if (dropdown == null) return;

            PropertyInfo selected = dropdown.GetType().GetProperty(
                "SelectedIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (selected == null || !selected.CanRead || !selected.CanWrite || selected.PropertyType != typeof(int)) return;

            int original = (int)selected.GetValue(dropdown, null);
            int replacement = Math.Max(0, Math.Min(original, maximumIndex));
            if (replacement == original) return;

            state.FlightProfile = dropdown;
            state.FlightProfileIndexProperty = selected;
            state.FlightProfileIndex = original;
            state.FlightProfileChanged = true;
            selected.SetValue(dropdown, replacement, null);
        }

        private static float ReadFloat(string name, float fallback)
        {
            if (_settingsInstanceProperty == null) return fallback;
            try
            {
                object settings = _settingsInstanceProperty.GetValue(null, null);
                return settings != null && Fields.TryGetValue(name, out FieldInfo field) && field.FieldType == typeof(float)
                    ? (float)field.GetValue(settings)
                    : fallback;
            }
            catch { return fallback; }
        }

        private static int ReadInt(FieldInfo field, object instance, int fallback)
        {
            if (field == null || instance == null) return fallback;
            try { return (int)field.GetValue(instance); }
            catch { return fallback; }
        }

        private static void Restore(RuntimeState state)
        {
            if (state == null || state.Restored) return;
            state.Restored = true;

            if (state.FlightProfileChanged && state.FlightProfile != null && state.FlightProfileIndexProperty != null)
            {
                try { state.FlightProfileIndexProperty.SetValue(state.FlightProfile, state.FlightProfileIndex, null); }
                catch { }
            }

            for (int i = state.Values.Count - 1; i >= 0; i--)
            {
                FieldValue value = state.Values[i];
                try { value.Field.SetValue(state.Settings, value.Value); }
                catch { }
            }

            if (ReferenceEquals(_activeState, state))
                _activeState = null;
        }
    }
}
