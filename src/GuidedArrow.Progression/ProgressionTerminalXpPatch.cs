using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Awards mastery XP from a completed guided-shot summary instead of attaching progression
    /// logic to OnMissileHit or MissionBehavior.OnAgentHit. Only managed fields are sampled when
    /// the core enters its own terminal routine; campaign XP work is deferred to display tick.
    /// </summary>
    internal static class ProgressionTerminalXpPatch
    {
        private sealed class PendingSummary
        {
            internal int Generation;
            internal bool HadHit;
            internal int KillCount;
            internal float Distance;
            internal float Multiplier;
        }

        private sealed class QueueState
        {
            internal readonly List<PendingSummary> Items = new List<PendingSummary>();
        }

        private static readonly ConditionalWeakTable<object, QueueState> Pending =
            new ConditionalWeakTable<object, QueueState>();

        private static FieldInfo _generationField;
        private static FieldInfo _shooterField;
        private static FieldInfo _shotOriginField;
        private static FieldInfo _impactPositionField;
        private static FieldInfo _hitVictimField;
        private static FieldInfo _pendingHitVictimsField;
        private static FieldInfo _confirmedKillCountField;
        private static MethodInfo _autoguidanceRuntimeMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _shooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _shotOriginField = AccessTools.Field(behaviorType, "_pendingShotPosition");
            _impactPositionField = AccessTools.Field(behaviorType, "_impactPosition");
            _hitVictimField = AccessTools.Field(behaviorType, "_hitVictim");
            _pendingHitVictimsField = AccessTools.Field(behaviorType, "_pendingHitVictims");
            _confirmedKillCountField = AccessTools.Field(behaviorType, "_confirmedCinematicKillCount");
            _autoguidanceRuntimeMethod = AccessTools.Method(behaviorType, "IsAutoguidanceRuntimeActive");

            MethodInfo terminal = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "HandleGuidedSwarmTerminal" && !method.IsAbstract);
            MethodInfo capturePrefix = AccessTools.Method(typeof(ProgressionTerminalXpPatch), nameof(CapturePrefix));
            MethodInfo flushPrefix = AccessTools.Method(typeof(ProgressionTerminalXpPatch), nameof(FlushPrefix));
            MethodInfo resetPrefix = AccessTools.Method(typeof(ProgressionTerminalXpPatch), nameof(ResetPrefix));

            if (terminal == null || capturePrefix == null || flushPrefix == null || resetPrefix == null ||
                _generationField == null || _shooterField == null)
                return;

            try
            {
                harmony.Patch(
                    terminal,
                    prefix: new HarmonyMethod(capturePrefix) { priority = Priority.First });
            }
            catch
            {
                return;
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnPreDisplayMissionTick" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(flushPrefix) { priority = Priority.Last });
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
                        prefix: new HarmonyMethod(resetPrefix) { priority = Priority.First });
                }
                catch { }
            }
        }

        private static void CapturePrefix(object __instance)
        {
            if (__instance == null || !ProgressionService.Enabled) return;

            try
            {
                Agent shooter = _shooterField.GetValue(__instance) as Agent;
                if (shooter == null || !ReferenceEquals(shooter, Agent.Main)) return;

                int generation = (int)_generationField.GetValue(__instance);
                if (generation <= 0) return;

                int killCount = ReadInt(_confirmedKillCountField, __instance, 0);
                bool hadHit = killCount > 0 || _hitVictimField?.GetValue(__instance) != null;

                if (!hadHit && _pendingHitVictimsField != null)
                {
                    object pending = _pendingHitVictimsField.GetValue(__instance);
                    if (pending is ICollection collection && collection.Count > 0)
                        hadHit = true;
                }

                if (!hadHit) return;

                Vec3 origin = ReadVec3(_shotOriginField, __instance);
                Vec3 impact = ReadVec3(_impactPositionField, __instance);
                float distance = (impact - origin).Length;
                if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
                    distance = 0f;

                bool autoguidance = false;
                if (_autoguidanceRuntimeMethod != null)
                {
                    try
                    {
                        object active = _autoguidanceRuntimeMethod.Invoke(__instance, null);
                        autoguidance = active is bool value && value;
                    }
                    catch { }
                }

                float multiplier = autoguidance
                    ? ProgressionBalance.AutoguidedXpMultiplier(
                        ProgressionService.Level(SkillId.BorrowedFlight))
                    : 1f;

                QueueState queue = Pending.GetOrCreateValue(__instance);
                queue.Items.Add(new PendingSummary
                {
                    Generation = generation,
                    HadHit = true,
                    KillCount = Math.Max(0, killCount),
                    Distance = distance,
                    Multiplier = multiplier
                });
            }
            catch
            {
                // XP capture must never alter the core terminal transition.
            }
        }

        private static void FlushPrefix(object __instance)
        {
            if (__instance == null ||
                !Pending.TryGetValue(__instance, out QueueState queue) ||
                queue == null)
                return;

            Pending.Remove(__instance);
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null || !progression.Enabled) return;

            PendingSummary[] items = queue.Items.ToArray();
            for (int i = 0; i < items.Length; i++)
            {
                PendingSummary summary = items[i];
                if (summary == null || !summary.HadHit || summary.Generation <= 0) continue;

                int kills = Math.Min(16, Math.Max(0, summary.KillCount));
                if (kills <= 0)
                {
                    TryRecord(progression, summary.Generation, 0, false, summary.Distance, summary.Multiplier);
                    continue;
                }

                for (int kill = 0; kill < kills; kill++)
                {
                    // Synthetic non-negative keys are scoped by shot generation and therefore remain
                    // deterministic without dereferencing post-impact Agent wrappers.
                    TryRecord(
                        progression,
                        summary.Generation,
                        kill,
                        true,
                        summary.Distance,
                        summary.Multiplier);
                }
            }
        }

        private static void TryRecord(
            ProgressionCampaignBehavior progression,
            int generation,
            int victimKey,
            bool killed,
            float distance,
            float multiplier)
        {
            try
            {
                progression.RecordGuidedHit(generation, victimKey, killed, distance, multiplier);
            }
            catch
            {
                // Campaign accounting is isolated from mission execution.
            }
        }

        private static void ResetPrefix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
        }

        private static int ReadInt(FieldInfo field, object instance, int fallback)
        {
            if (field == null || instance == null) return fallback;
            try { return (int)field.GetValue(instance); }
            catch { return fallback; }
        }

        private static Vec3 ReadVec3(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return Vec3.Zero;
            try { return (Vec3)field.GetValue(instance); }
            catch { return Vec3.Zero; }
        }
    }
}
