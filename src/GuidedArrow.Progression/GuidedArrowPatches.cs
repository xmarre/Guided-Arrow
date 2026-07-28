using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    internal static class GuidedArrowPatches
    {
        private static FieldInfo _generationField;
        private static FieldInfo _shooterField;
        private static FieldInfo _shotOriginField;
        private static FieldInfo _cameraMissileIndexField;
        private static MethodInfo _autoguidanceRuntimeMethod;
        private static PropertyInfo _trackedIndexProperty;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedFormationSlotField;
        private static FieldInfo _trackedSyntheticField;
        private static FieldInfo _trackedPenetrationsUsedField;

        internal static void Install(Harmony harmony)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => candidate.GetName().Name == "GuidedArrow");
            if (assembly == null) return;

            Type settingsType = assembly.GetType("GuidedArrow.Settings", false);
            Type behaviorType = assembly.GetType("GuidedArrow.GuidedArrowBehavior", false);
            if (behaviorType == null) return;

            PatchBehavior(harmony, behaviorType, settingsType);
        }

        private static void PatchBehavior(Harmony harmony, Type behaviorType, Type settingsType)
        {
            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _shooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _shotOriginField = AccessTools.Field(behaviorType, "_pendingShotPosition");
            _cameraMissileIndexField = AccessTools.Field(behaviorType, "_cameraMissileIndex");
            _autoguidanceRuntimeMethod = AccessTools.Method(behaviorType, "IsAutoguidanceRuntimeActive");

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType != null)
            {
                _trackedFormationSlotField = AccessTools.Field(trackedType, "FormationSlot");
                _trackedSyntheticField = AccessTools.Field(trackedType, "SyntheticProjectile");
                _trackedPenetrationsUsedField = AccessTools.Field(trackedType, "PenetrationsUsed");
            }

            PatchAgentHit(harmony, behaviorType);
            PatchBoolResult(harmony, behaviorType, "IsSplitSiblingAcquisitionOpen", nameof(SplitSiblingOpenPostfix));
            PatchBoolResult(harmony, behaviorType, "ShouldBreakFormationForAutoguidance", nameof(BreakFormationPostfix));
            PatchBoolResult(harmony, behaviorType, "IsAgentPenetrationOverrideEnabled", nameof(PenetrationEnabledPostfix));
            PatchBoolResult(harmony, behaviorType, "IsAutoguidanceEligibleMissile", nameof(AutoguidanceEligiblePostfix));
            PatchBoolResult(harmony, behaviorType, "HasRemainingAgentPenetration", nameof(RemainingPenetrationPostfix));

            if (settingsType != null)
                ProgressionRuntimeSettingsPatch.Install(harmony, behaviorType, settingsType);

            NativeSiblingMasteryPatch.Install(harmony, behaviorType);
            NativeVolleyPenetrationIsolationPatch.Install(harmony, behaviorType);
            MissileLifetimeSafetyPatch.Install(harmony, behaviorType);
            ConcentratedImpactSafetyPatch.Install(harmony, behaviorType);
            AutoguidanceRetargetSafetyPatch.Install(harmony, behaviorType);
            PenetrationContinuationSafetyPatch.Install(harmony, behaviorType);
            if (settingsType != null)
                NativeVolleyAugmentationPatch.Install(harmony, behaviorType, settingsType);
        }

        private static void PatchAgentHit(Harmony harmony, Type behaviorType)
        {
            MethodInfo hit = behaviorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "OnAgentHit");
            if (hit == null) return;

            Type declaringType = hit.DeclaringType;
            if (declaringType != null)
            {
                Type[] parameterTypes = hit.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
                MethodInfo declaredHit = declaringType.GetMethod(
                    hit.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null,
                    parameterTypes,
                    null);
                if (declaredHit != null) hit = declaredHit;
            }

            if (hit.IsAbstract) return;
            try
            {
                harmony.Patch(
                    hit,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(GuidedArrowPatches), nameof(OnAgentHitPostfix))));
            }
            catch { }
        }

        private static void PatchBoolResult(Harmony harmony, Type type, string methodName, string postfixName)
        {
            MethodInfo postfix = AccessTools.Method(typeof(GuidedArrowPatches), postfixName);
            if (postfix == null) return;

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == methodName && candidate.ReturnType == typeof(bool)))
            {
                try { harmony.Patch(method, postfix: new HarmonyMethod(postfix)); }
                catch { }
            }
        }

        private static void SplitSiblingOpenPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && ProgressionService.Level(SkillId.SplitAwareness) <= 0)
                __result = false;
        }

        private static void BreakFormationPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && ProgressionService.Level(SkillId.ManyHeadedFlight) < 3)
                __result = false;
        }

        private static void PenetrationEnabledPostfix(ref bool __result)
        {
            if (ProgressionService.Enabled && ProgressionService.Level(SkillId.DrivingShot) <= 0)
                __result = false;
        }

        private static void AutoguidanceEligiblePostfix(object __instance, object[] __args, ref bool __result)
        {
            if (!__result || !ProgressionService.Enabled || __instance == null || __args == null) return;

            object tracked = __args.FirstOrDefault(argument => argument != null && argument.GetType().Name == "TrackedMissile");
            if (tracked == null) return;

            int synchronizedLevel = ProgressionService.Level(SkillId.SynchronizedHunt);
            if (synchronizedLevel <= 0)
            {
                if (_cameraMissileIndexField == null) return;
                int cameraIndex;
                try { cameraIndex = (int)_cameraMissileIndexField.GetValue(__instance); }
                catch { return; }

                int trackedIndex = ReadTrackedIndex(tracked);
                if (trackedIndex != cameraIndex) __result = false;
                return;
            }

            if (_trackedFormationSlotField == null) return;
            try
            {
                int slot = (int)_trackedFormationSlotField.GetValue(tracked);
                if (slot >= ProgressionBalance.SynchronizedProjectileCap(synchronizedLevel)) __result = false;
            }
            catch { }
        }

        private static void RemainingPenetrationPostfix(object[] __args, ref bool __result)
        {
            if (!__result || !ProgressionService.Enabled || __args == null || __args.Length == 0 || __args[0] == null)
                return;

            object tracked = __args[0];
            if (_trackedSyntheticField == null || _trackedPenetrationsUsedField == null) return;

            try
            {
                if (!(bool)_trackedSyntheticField.GetValue(tracked)) return;
                int needleLevel = ProgressionService.Level(SkillId.NeedleStorm);
                int cap = ProgressionBalance.NeedleStormPenetrationCap(needleLevel);
                int used = (int)_trackedPenetrationsUsedField.GetValue(tracked);
                if (cap <= 0 || used >= cap) __result = false;
            }
            catch { }
        }

        private static int ReadTrackedIndex(object tracked)
        {
            if (tracked == null) return -1;
            if (_trackedIndexProperty == null && _trackedIndexField == null)
            {
                Type type = tracked.GetType();
                _trackedIndexProperty = type.GetProperty("Index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _trackedIndexField = type.GetField("Index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            try
            {
                if (_trackedIndexProperty != null) return (int)_trackedIndexProperty.GetValue(tracked, null);
                if (_trackedIndexField != null) return (int)_trackedIndexField.GetValue(tracked);
            }
            catch { }
            return -1;
        }

        private static void OnAgentHitPostfix(object __instance, object[] __args)
        {
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null || !progression.Enabled || __instance == null || __args == null) return;

            try
            {
                Agent shooter = _shooterField?.GetValue(__instance) as Agent;
                if (shooter == null || shooter != Agent.Main) return;

                Agent victim = __args.OfType<Agent>().FirstOrDefault(candidate => candidate != null && candidate != shooter);
                if (victim == null || !IsEnemy(victim, shooter)) return;

                int generation = _generationField != null ? (int)_generationField.GetValue(__instance) : 0;
                bool killed = victim.Health <= 0.01f;
                float distance = ReadManagedHitDistance(__instance, __args);
                float multiplier = 1f;

                if (_autoguidanceRuntimeMethod != null)
                {
                    object active = _autoguidanceRuntimeMethod.Invoke(__instance, null);
                    if (active is bool && (bool)active)
                        multiplier = ProgressionBalance.AutoguidedXpMultiplier(ProgressionService.Level(SkillId.BorrowedFlight));
                }

                progression.RecordGuidedHit(generation, victim.Index, killed, distance, multiplier);
            }
            catch { }
        }

        private static float ReadManagedHitDistance(object instance, object[] args)
        {
            if (instance == null || args == null || args.Length == 0 || _shotOriginField == null)
                return 0f;

            try
            {
                AttackCollisionData collision = default;
                bool foundCollision = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] is AttackCollisionData candidate)
                    {
                        collision = candidate;
                        foundCollision = true;
                        break;
                    }
                }
                if (!foundCollision) return 0f;

                Vec3 shotOrigin = (Vec3)_shotOriginField.GetValue(instance);
                float distance = (collision.CollisionGlobalPosition - shotOrigin).Length;
                return float.IsNaN(distance) || float.IsInfinity(distance) ? 0f : Math.Max(0f, distance);
            }
            catch
            {
                return 0f;
            }
        }

        private static bool IsEnemy(Agent victim, Agent shooter)
        {
            Team victimTeam = victim?.Team;
            Team shooterTeam = shooter?.Team;
            if (victimTeam == null || shooterTeam == null) return true;

            try { return victimTeam.IsEnemyOf(shooterTeam); }
            catch { return victimTeam != shooterTeam; }
        }
    }
}
