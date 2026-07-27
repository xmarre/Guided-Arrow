using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    internal static class GuidedArrowPatches
    {
        private static readonly Dictionary<string, SkillId> BoolGates = new Dictionary<string, SkillId>(StringComparer.Ordinal)
        {
            { "Enabled", SkillId.GuidedRelease },
            { "EnableProximityTimeDilation", SkillId.TemporalFocus },
            { "EnableAutonomousGuidance", SkillId.PredatorsEye },
            { "AutoguidanceAutomaticReacquisition", SkillId.RelentlessLock },
            { "AutoguidanceObstacleAvoidance", SkillId.Pathfinder },
            { "EnableAlliedArrowTakeover", SkillId.BorrowedFlight },
            { "AutoguidanceAlwaysOn", SkillId.UnblinkingEye },
            { "AutoguidancePersistToggleForBattle", SkillId.UnblinkingEye },
            { "AutoguidanceMultiTargetTrajectoryPlanning", SkillId.UnblinkingEye },
            { "EnableStandaloneSplitProjectiles", SkillId.ForkedShaft },
            { "EnablePenetrationOverride", SkillId.DrivingShot },
            { "InfiniteAgentPenetrations", SkillId.UnbrokenFlight }
        };

        private static readonly HashSet<string> EnumProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "AutoguidanceScope", "AutoguidanceTargetSelection", "AutoguidanceFlightProfile",
            "SplitArrowFormationMode", "AutoguidanceSplitBehaviour", "AutoguidanceSplitTargetDistribution"
        };

        private static FieldInfo _generationField;
        private static FieldInfo _shooterField;
        private static FieldInfo _cameraMissileIndexField;
        private static MethodInfo _autoguidanceRuntimeMethod;
        private static PropertyInfo _trackedIndexProperty;
        private static FieldInfo _trackedIndexField;

        internal static void Install(Harmony harmony)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "GuidedArrow");
            if (assembly == null) return;
            Type settingsType = assembly.GetType("GuidedArrow.Settings", false);
            Type behaviorType = assembly.GetType("GuidedArrow.GuidedArrowBehavior", false);
            if (settingsType != null) PatchSettings(harmony, settingsType);
            if (behaviorType != null) PatchBehavior(harmony, behaviorType, settingsType);
        }

        private static void PatchSettings(Harmony harmony, Type settingsType)
        {
            MethodInfo boolPostfix = AccessTools.Method(typeof(GuidedArrowPatches), nameof(BoolGetterPostfix));
            MethodInfo intPostfix = AccessTools.Method(typeof(GuidedArrowPatches), nameof(IntGetterPostfix));
            MethodInfo floatPostfix = AccessTools.Method(typeof(GuidedArrowPatches), nameof(FloatGetterPostfix));
            MethodInfo enumGeneric = AccessTools.Method(typeof(GuidedArrowPatches), nameof(EnumGetterPostfix));

            foreach (PropertyInfo property in settingsType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                MethodInfo getter = property.GetGetMethod(true);
                if (getter == null) continue;
                try
                {
                    if (property.PropertyType == typeof(bool) && BoolGates.ContainsKey(property.Name))
                        harmony.Patch(getter, postfix: new HarmonyMethod(boolPostfix));
                    else if (property.PropertyType == typeof(int) && (property.Name == "StandaloneSplitProjectileCount" || property.Name == "MaximumAgentPenetrations" || property.Name == "AutoguidancePlannedTargetCount"))
                        harmony.Patch(getter, postfix: new HarmonyMethod(intPostfix));
                    else if (property.PropertyType == typeof(float) && (property.Name == "MaximumGuidanceTime" || property.Name == "MinimumTurnRadius" || property.Name == "InitialGuidanceTimeSpeed" || property.Name == "ProximityNearTimeSpeed" || property.Name == "SpeedAdaptiveSteeringStrength"))
                        harmony.Patch(getter, postfix: new HarmonyMethod(floatPostfix));
                    else if (property.PropertyType.IsEnum && EnumProperties.Contains(property.Name))
                        harmony.Patch(getter, postfix: new HarmonyMethod(enumGeneric.MakeGenericMethod(property.PropertyType)));
                }
                catch { }
            }
        }

        private static void PatchBehavior(Harmony harmony, Type behaviorType, Type settingsType)
        {
            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _shooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _autoguidanceRuntimeMethod = AccessTools.Method(behaviorType, "IsAutoguidanceRuntimeActive");

            MethodInfo hit = behaviorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "OnAgentHit");
            if (hit != null)
            {
                // Harmony 2.4 rejects an inherited MethodInfo whose ReflectedType is the
                // derived GuidedArrowBehavior. Resolve the same signature directly on the
                // declaring type so the patch always targets the implemented method body.
                Type declaringType = hit.DeclaringType;
                if (declaringType != null)
                {
                    Type[] parameterTypes = hit.GetParameters().Select(p => p.ParameterType).ToArray();
                    MethodInfo declaredHit = declaringType.GetMethod(
                        hit.Name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null,
                        parameterTypes,
                        null);
                    if (declaredHit != null) hit = declaredHit;
                }

                if (!hit.IsAbstract)
                    harmony.Patch(hit, postfix: new HarmonyMethod(AccessTools.Method(typeof(GuidedArrowPatches), nameof(OnAgentHitPostfix))));
            }

            PatchBoolResult(harmony, behaviorType, "IsSplitSiblingAcquisitionOpen", nameof(SplitSiblingOpenPostfix));
            PatchBoolResult(harmony, behaviorType, "ShouldBreakFormationForAutoguidance", nameof(BreakFormationPostfix));
            PatchBoolResult(harmony, behaviorType, "IsAgentPenetrationOverrideEnabled", nameof(PenetrationEnabledPostfix));
            PatchBoolResult(harmony, behaviorType, "IsAutoguidanceEligibleMissile", nameof(AutoguidanceEligiblePostfix));

            // Native ability arrows retain TOR/native collision ownership. The safety patch
            // continues to protect ordinary and Guided Arrow-generated continuations.
            NativeVolleyPenetrationIsolationPatch.Install(harmony, behaviorType);
            PenetrationContinuationSafetyPatch.Install(harmony, behaviorType);
            if (settingsType != null)
                NativeVolleyAugmentationPatch.Install(harmony, behaviorType, settingsType);
        }

        private static void PatchBoolResult(Harmony harmony, Type type, string methodName, string postfixName)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == methodName && m.ReturnType == typeof(bool)))
            {
                try { harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(GuidedArrowPatches), postfixName))); }
                catch { }
            }
        }

        private static void BoolGetterPostfix(MethodBase __originalMethod, ref bool __result)
        {
            if (!ProgressionService.Enabled) return;
            string name = PropertyName(__originalMethod);
            SkillId gate;
            if (BoolGates.TryGetValue(name, out gate) && !ProgressionService.Has(gate)) __result = false;
        }

        private static void IntGetterPostfix(MethodBase __originalMethod, ref int __result)
        {
            if (!ProgressionService.Enabled) return;
            string name = PropertyName(__originalMethod);
            if (name == "StandaloneSplitProjectileCount" && !ProgressionService.Has(SkillId.ManyHeadedFlight))
                __result = Math.Min(__result, ProgressionService.Has(SkillId.ForkedShaft) ? 2 : 0);
            else if (name == "MaximumAgentPenetrations" && !ProgressionService.Has(SkillId.UnbrokenFlight))
                __result = Math.Min(__result, ProgressionService.Has(SkillId.ThroughTheRanks) ? 3 : (ProgressionService.Has(SkillId.DrivingShot) ? 1 : 0));
            else if (name == "AutoguidancePlannedTargetCount" && !ProgressionService.Has(SkillId.UnblinkingEye))
                __result = Math.Min(__result, 1);
        }

        private static void FloatGetterPostfix(MethodBase __originalMethod, ref float __result)
        {
            if (!ProgressionService.Enabled) return;
            string name = PropertyName(__originalMethod);
            if (name == "MaximumGuidanceTime" && !ProgressionService.Has(SkillId.MasterOfTheCurve))
                __result = Math.Min(__result, ProgressionService.Has(SkillId.SteadyHand) ? 8f : 4f);
            else if (name == "MinimumTurnRadius" && !ProgressionService.Has(SkillId.MasterOfTheCurve))
                __result = Math.Max(__result, ProgressionService.Has(SkillId.FineCorrection) ? 14f : (ProgressionService.Has(SkillId.SteadyHand) ? 22f : 35f));
            else if ((name == "InitialGuidanceTimeSpeed" || name == "ProximityNearTimeSpeed") && !ProgressionService.Has(SkillId.MasterOfTheCurve))
                __result = Math.Max(__result, ProgressionService.Has(SkillId.TemporalFocus) ? 0.5f : 0.8f);
            else if (name == "SpeedAdaptiveSteeringStrength" && !ProgressionService.Has(SkillId.FineCorrection))
                __result = 0f;
        }

        private static void EnumGetterPostfix<T>(MethodBase __originalMethod, ref T __result) where T : struct
        {
            if (!ProgressionService.Enabled) return;
            string name = PropertyName(__originalMethod);
            bool allowed = true;
            if (name == "AutoguidanceScope") allowed = ProgressionService.Has(SkillId.SynchronizedHunt);
            else if (name == "AutoguidanceTargetSelection") allowed = ProgressionService.Has(SkillId.RelentlessLock);
            else if (name == "AutoguidanceFlightProfile") allowed = ProgressionService.Has(SkillId.Pathfinder);
            else if (name == "SplitArrowFormationMode") allowed = ProgressionService.Has(SkillId.FormationDiscipline);
            else if (name == "AutoguidanceSplitBehaviour" || name == "AutoguidanceSplitTargetDistribution") allowed = ProgressionService.Has(SkillId.ManyHeadedFlight);
            if (!allowed) __result = (T)Enum.ToObject(typeof(T), 0);
        }

        private static string PropertyName(MethodBase method)
        {
            string name = method != null ? method.Name : string.Empty;
            return name.StartsWith("get_", StringComparison.Ordinal) ? name.Substring(4) : name;
        }

        private static void SplitSiblingOpenPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && !ProgressionService.Has(SkillId.SplitAwareness)) __result = false;
        }

        private static void BreakFormationPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && !ProgressionService.Has(SkillId.ManyHeadedFlight)) __result = false;
        }

        private static void PenetrationEnabledPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && !ProgressionService.Has(SkillId.DrivingShot)) __result = false;
        }

        private static void AutoguidanceEligiblePostfix(object __instance, object[] __args, ref bool __result)
        {
            if (!__result || !ProgressionService.Enabled || ProgressionService.Has(SkillId.SynchronizedHunt)) return;
            if (__instance == null || __args == null || _cameraMissileIndexField == null) return;
            int cameraIndex;
            try { cameraIndex = (int)_cameraMissileIndexField.GetValue(__instance); }
            catch { return; }
            object tracked = __args.FirstOrDefault(a => a != null && a.GetType().Name == "TrackedMissile");
            if (tracked == null) return;
            if (_trackedIndexProperty == null && _trackedIndexField == null)
            {
                _trackedIndexProperty = tracked.GetType().GetProperty("Index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _trackedIndexField = tracked.GetType().GetField("Index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            try
            {
                int index = _trackedIndexProperty != null ? (int)_trackedIndexProperty.GetValue(tracked, null) : (int)_trackedIndexField.GetValue(tracked);
                if (index != cameraIndex) __result = false;
            }
            catch { }
        }

        private static void OnAgentHitPostfix(object __instance, object[] __args)
        {
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null || !progression.Enabled || __instance == null || __args == null) return;
            try
            {
                Agent shooter = _shooterField?.GetValue(__instance) as Agent;
                if (shooter == null || shooter != Agent.Main) return;
                Agent victim = __args.OfType<Agent>().FirstOrDefault(a => a != null && a != shooter);
                if (victim == null) return;
                if (!IsEnemy(victim, shooter)) return;
                int generation = _generationField != null ? (int)_generationField.GetValue(__instance) : 0;
                bool killed = victim.Health <= 0.01f;
                float distance = (victim.Position - shooter.Position).Length;
                float multiplier = 1f;
                if (_autoguidanceRuntimeMethod != null)
                {
                    object active = _autoguidanceRuntimeMethod.Invoke(__instance, null);
                    if (active is bool && (bool)active) multiplier = 0.4f;
                }
                progression.RecordGuidedHit(generation, victim.Index, killed, distance, multiplier);
            }
            catch { }
        }

        private static bool IsEnemy(Agent victim, Agent shooter)
        {
            try
            {
                MethodInfo method = victim.GetType().GetMethod("IsEnemyOf", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Agent) }, null);
                if (method != null) return (bool)method.Invoke(victim, new object[] { shooter });
            }
            catch { }
            return victim.Team == null || shooter.Team == null || victim.Team != shooter.Team;
        }
    }
}
