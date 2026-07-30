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
    /// Prevents siege autoguidance from committing to active agents hidden behind castle geometry.
    /// Candidate collection remains the core's bounded nearby-agent query; visibility is evaluated
    /// only when candidates are validated or selected and is cached to avoid per-frame ray casts.
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

        [ThreadStatic]
        private static int _candidateCollectionDepth;

        [ThreadStatic]
        private static bool _reselecting;

        private static readonly Dictionary<Agent, VisibilitySample> VisibilityCache =
            new Dictionary<Agent, VisibilitySample>(AgentReferenceComparer.Instance);

        private static readonly long VisibilityCacheTicks = Math.Max(1L, Stopwatch.Frequency * 3L / 4L);

        private static FieldInfo _activeShotShooterField;
        private static FieldInfo _rankCandidatesField;
        private static FieldInfo _candidateHeadsField;
        private static MethodInfo _findBestMethod;
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
            _rankCandidatesField = AccessTools.Field(behaviorType, "_autoguidanceRankCandidates");
            _candidateHeadsField = AccessTools.Field(behaviorType, "_autoguidanceCandidateHeads");
            _findBestMethod = trackedType == null
                ? null
                : AccessTools.Method(behaviorType, "FindBestAutoguidanceCandidate", new[] { trackedType, typeof(bool) });
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

            MethodInfo collect = AccessTools.Method(behaviorType, "CollectAutoguidanceCandidates");
            MethodInfo collectPrefix = AccessTools.Method(typeof(SiegeAutoguidanceVisibilityPatch), nameof(CollectionPrefix));
            MethodInfo collectFinalizer = AccessTools.Method(typeof(SiegeAutoguidanceVisibilityPatch), nameof(CollectionFinalizer));
            if (collect != null && collectPrefix != null && collectFinalizer != null)
            {
                try
                {
                    harmony.Patch(
                        collect,
                        prefix: new HarmonyMethod(collectPrefix) { priority = Priority.First },
                        finalizer: new HarmonyMethod(collectFinalizer) { priority = Priority.Last });
                }
                catch { }
            }

            MethodInfo valid = AccessTools.Method(behaviorType, "IsAutoguidanceTargetValid", new[] { typeof(Agent) });
            MethodInfo validPostfix = AccessTools.Method(typeof(SiegeAutoguidanceVisibilityPatch), nameof(TargetValidPostfix));
            if (valid != null && validPostfix != null)
            {
                try { harmony.Patch(valid, postfix: new HarmonyMethod(validPostfix) { priority = Priority.Last }); }
                catch { }
            }

            MethodInfo findPostfix = AccessTools.Method(typeof(SiegeAutoguidanceVisibilityPatch), nameof(FindBestPostfix));
            if (_findBestMethod != null && findPostfix != null)
            {
                try { harmony.Patch(_findBestMethod, postfix: new HarmonyMethod(findPostfix) { priority = Priority.Last }); }
                catch { }
            }
        }

        private static void CollectionPrefix()
        {
            _candidateCollectionDepth++;
        }

        private static Exception CollectionFinalizer(Exception __exception)
        {
            if (_candidateCollectionDepth > 0) _candidateCollectionDepth--;
            return __exception;
        }

        private static void TargetValidPostfix(object __instance, Agent target, ref bool __result)
        {
            if (!__result || _candidateCollectionDepth > 0 || _reselecting) return;
            if (!ShouldFilterCurrentMission()) return;
            if (!IsVisibleFromShooter(__instance, target)) __result = false;
        }

        private static void FindBestPostfix(object __instance, object[] __args, ref int __result)
        {
            if (_reselecting || __instance == null || __result < 0 || __args == null || __args.Length < 2) return;
            if (!ShouldFilterCurrentMission() || _findBestMethod == null) return;

            IList candidates = _rankCandidatesField?.GetValue(__instance) as IList;
            IList heads = _candidateHeadsField?.GetValue(__instance) as IList;
            if (candidates == null || heads == null) return;

            int attemptsRemaining = candidates.Count;
            while (__result >= 0 && __result < candidates.Count && attemptsRemaining-- > 0)
            {
                Agent selected = candidates[__result] as Agent;
                if (selected == null || IsVisibleFromShooter(__instance, selected)) return;

                // Hidden candidates are removed from both parallel lists for this bounded
                // reacquisition pass, then the unchanged core selector chooses again.
                candidates.RemoveAt(__result);
                if (__result < heads.Count) heads.RemoveAt(__result);
                if (candidates.Count == 0)
                {
                    __result = -1;
                    return;
                }

                try
                {
                    _reselecting = true;
                    __result = Convert.ToInt32(_findBestMethod.Invoke(__instance, __args));
                }
                catch
                {
                    __result = -1;
                    return;
                }
                finally
                {
                    _reselecting = false;
                }
            }

            // The bounded retry is a corruption guard. Never return a candidate that still
            // violates the siege-visibility invariant if the core selector repeatedly chooses it.
            if (__result >= 0 && __result < candidates.Count)
            {
                Agent selected = candidates[__result] as Agent;
                if (selected != null && !IsVisibleFromShooter(__instance, selected)) __result = -1;
            }
        }

        private static bool ShouldFilterCurrentMission()
        {
            ExperienceSettings settings = ExperienceSettings.Instance;
            if (settings != null && !settings.VisibleSiegeTargetsOnly) return false;

            Mission mission = Mission.Current;
            if (mission == null) return false;
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
                foreach (FieldInfo field in mission.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.Name.IndexOf("behavior", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (ContainsSiegeBehavior(field.GetValue(mission) as IEnumerable)) return true;
                }
            }
            catch { }

            try
            {
                object scene = mission.Scene;
                MethodInfo getName = scene == null ? null : AccessTools.Method(scene.GetType(), "GetName", Type.EmptyTypes);
                string name = getName?.Invoke(scene, null) as string;
                return !string.IsNullOrEmpty(name) && name.IndexOf("siege", StringComparison.OrdinalIgnoreCase) >= 0;
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
                if (!string.IsNullOrEmpty(name) && name.IndexOf("Siege", StringComparison.OrdinalIgnoreCase) >= 0)
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
            VisibilitySample sample;
            if (VisibilityCache.TryGetValue(target, out sample) &&
                ReferenceEquals(sample.Shooter, shooter) &&
                now - sample.Timestamp <= VisibilityCacheTicks)
                return sample.Visible;

            bool visible = true;
            try
            {
                Vec3 shooterHead;
                Vec3 targetHead;
                if (TryGetHead(shooter, out shooterHead) && TryGetHead(target, out targetHead))
                {
                    object[] args = { shooterHead, targetHead, null, Vec3.Zero, true };
                    bool obstructed = Convert.ToBoolean(_segmentObstructedMethod.Invoke(behavior, args));
                    visible = !obstructed;
                }
            }
            catch
            {
                // Failure-open preserves the core's native target selection on unknown API shapes.
                visible = true;
            }

            if (VisibilityCache.Count > 256) VisibilityCache.Clear();
            VisibilityCache[target] = new VisibilitySample { Shooter = shooter, Timestamp = now, Visible = visible };
            return visible;
        }

        private static bool TryGetHead(Agent agent, out Vec3 position)
        {
            position = Vec3.Zero;
            if (agent == null) return false;
            try
            {
                int boneIndex = Convert.ToInt32(_resolveHeadBoneMethod.Invoke(null, new object[] { agent }));
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
