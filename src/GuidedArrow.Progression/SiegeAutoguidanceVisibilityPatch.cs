using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Excludes siege targets hidden behind fortification geometry before the stable core adds them
    /// to its parallel candidate/head lists. Those lists must never be structurally modified after
    /// route planning has captured integer indices into them.
    /// </summary>
    internal static class SiegeAutoguidanceVisibilityPatch
    {
        private sealed class VisibilitySample
        {
            internal Agent Shooter;
            internal long Timestamp;
            internal bool Visible;
        }

        private sealed class AgentReferenceComparer : IEqualityComparer<Agent>
        {
            internal static readonly AgentReferenceComparer Instance = new AgentReferenceComparer();
            public bool Equals(Agent x, Agent y) => ReferenceEquals(x, y);
            public int GetHashCode(Agent obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly Dictionary<Agent, VisibilitySample> VisibilityCache =
            new Dictionary<Agent, VisibilitySample>(AgentReferenceComparer.Instance);

        private static readonly long VisibilityCacheTicks = Math.Max(1L, Stopwatch.Frequency * 3L / 4L);

        private static FieldInfo _activeShotShooterField;
        private static MethodInfo _resolveHeadBoneMethod;
        private static MethodInfo _tryGetHeadMethod;
        private static MethodInfo _segmentObstructedMethod;
        private static Mission _cachedMission;
        private static bool _cachedMissionIsSiege;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            _activeShotShooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _resolveHeadBoneMethod = AccessTools.Method(
                behaviorType,
                "ResolveGuidanceHeadBoneIndex",
                new[] { typeof(Agent) });
            _tryGetHeadMethod = AccessTools.Method(
                behaviorType,
                "TryGetGuidanceHeadPosition",
                new[] { typeof(Agent), typeof(int), typeof(Vec3).MakeByRefType() });
            _segmentObstructedMethod = trackedType == null
                ? null
                : AccessTools.Method(
                    behaviorType,
                    "IsAutoguidanceSegmentObstructed",
                    new[]
                    {
                        typeof(Vec3),
                        typeof(Vec3),
                        trackedType,
                        typeof(Vec3).MakeByRefType(),
                        typeof(bool)
                    });

            MethodInfo valid = AccessTools.Method(
                behaviorType,
                "IsAutoguidanceTargetValid",
                new[] { typeof(Agent) });
            MethodInfo validPostfix = AccessTools.Method(
                typeof(SiegeAutoguidanceVisibilityPatch),
                nameof(TargetValidPostfix));
            if (valid == null || validPostfix == null) return;

            try
            {
                harmony.Patch(
                    valid,
                    postfix: new HarmonyMethod(validPostfix) { priority = Priority.Last });
            }
            catch
            {
                // Unknown private API shapes retain the locked core's native target validation.
            }
        }

        private static void TargetValidPostfix(object __instance, Agent target, ref bool __result)
        {
            if (!__result || __instance == null || target == null) return;
            if (!ShouldFilterCurrentMission()) return;

            // This validation is invoked while CollectAutoguidanceCandidates is building both
            // parallel lists. Rejecting the target here means neither list receives an entry. Never
            // remove or reorder entries later: route planning stores integer indices into them.
            if (!IsVisibleFromShooter(__instance, target))
                __result = false;
        }

        private static bool ShouldFilterCurrentMission()
        {
            ExperienceSettings settings = ExperienceSettings.Instance;
            if (settings != null && !settings.VisibleSiegeTargetsOnly) return false;

            Mission mission = Mission.Current;
            if (mission == null)
            {
                _cachedMission = null;
                _cachedMissionIsSiege = false;
                VisibilityCache.Clear();
                return false;
            }

            if (!ReferenceEquals(mission, _cachedMission))
            {
                _cachedMission = mission;
                _cachedMissionIsSiege = DetectSiegeMission(mission);
                VisibilityCache.Clear();
            }

            return _cachedMissionIsSiege;
        }

        private static bool DetectSiegeMission(Mission mission)
        {
            if (mission == null) return false;

            try
            {
                PropertyInfo property = AccessTools.Property(mission.GetType(), "MissionBehaviors");
                IEnumerable behaviors = property?.GetValue(mission, null) as IEnumerable;
                if (ContainsSiegeBehavior(behaviors)) return true;
            }
            catch { }

            try
            {
                foreach (FieldInfo field in mission.GetType().GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.Name.IndexOf("behavior", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (ContainsSiegeBehavior(field.GetValue(mission) as IEnumerable)) return true;
                }
            }
            catch { }

            try
            {
                object scene = mission.Scene;
                MethodInfo getName = scene == null
                    ? null
                    : AccessTools.Method(scene.GetType(), "GetName", Type.EmptyTypes);
                string name = getName?.Invoke(scene, null) as string;
                return !string.IsNullOrEmpty(name) &&
                       name.IndexOf("siege", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsSiegeBehavior(IEnumerable behaviors)
        {
            if (behaviors == null) return false;

            foreach (object behavior in behaviors)
            {
                string name = behavior?.GetType().FullName;
                if (!string.IsNullOrEmpty(name) &&
                    name.IndexOf("Siege", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool IsVisibleFromShooter(object behavior, Agent target)
        {
            if (behavior == null || target == null ||
                _activeShotShooterField == null ||
                _resolveHeadBoneMethod == null ||
                _tryGetHeadMethod == null ||
                _segmentObstructedMethod == null)
                return true;

            Agent shooter;
            try { shooter = _activeShotShooterField.GetValue(behavior) as Agent; }
            catch { return true; }
            if (shooter == null) return true;

            long now = Stopwatch.GetTimestamp();
            if (VisibilityCache.TryGetValue(target, out VisibilitySample sample) &&
                ReferenceEquals(sample.Shooter, shooter) &&
                now - sample.Timestamp <= VisibilityCacheTicks)
                return sample.Visible;

            bool visible = true;
            try
            {
                if (TryGetHead(shooter, out Vec3 shooterHead) &&
                    TryGetHead(target, out Vec3 targetHead))
                {
                    object[] args = { shooterHead, targetHead, null, Vec3.Zero, true };
                    bool obstructed = Convert.ToBoolean(_segmentObstructedMethod.Invoke(behavior, args));
                    visible = !obstructed;
                }
            }
            catch
            {
                // Failure-open preserves the stable core on an unknown engine API shape.
                visible = true;
            }

            if (VisibilityCache.Count > 256) VisibilityCache.Clear();
            VisibilityCache[target] = new VisibilitySample
            {
                Shooter = shooter,
                Timestamp = now,
                Visible = visible
            };
            return visible;
        }

        private static bool TryGetHead(Agent agent, out Vec3 position)
        {
            position = Vec3.Zero;
            if (agent == null) return false;

            try
            {
                int boneIndex = Convert.ToInt32(
                    _resolveHeadBoneMethod.Invoke(null, new object[] { agent }));
                object[] args = { agent, boneIndex, Vec3.Zero };
                bool success = Convert.ToBoolean(_tryGetHeadMethod.Invoke(null, args));
                if (success) position = (Vec3)args[2];
                return success;
            }
            catch
            {
                return false;
            }
        }
    }
}
