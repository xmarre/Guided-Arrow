using System;
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
    /// Moves the complete GuidedArrowBehavior.OnMissileHit body beyond Bannerlord's native
    /// missile-impact callback instead of trying to guard individual operations inside it.
    ///
    /// AttackCollisionData is a value snapshot, so the callback arguments can be retained safely.
    /// The original method is replayed on the next display tick. Missile-removal callbacks that arrive
    /// before that replay are retained as well, preserving the tracked missile and the core's normal
    /// hit -> collision reaction -> removal ordering.
    /// </summary>
    internal static class FullImpactReplayDeferralPatch
    {
        private sealed class PendingImpact
        {
            internal object[] Arguments;
            internal Agent Shooter;
            internal Agent Victim;
            internal int VictimKey;
            internal int Generation;
            internal Vec3 ShotOrigin;
            internal AttackCollisionData Collision;
            internal bool HasCollision;
            internal bool Fatal;
        }

        private sealed class PendingRemoval
        {
            internal MethodInfo Method;
            internal object[] Arguments;
        }

        private sealed class PendingReplayQueue
        {
            internal readonly List<PendingImpact> Impacts = new List<PendingImpact>();
            internal readonly List<PendingRemoval> Removals = new List<PendingRemoval>();
            internal bool Flushing;
        }

        private static readonly ConditionalWeakTable<object, PendingReplayQueue> Pending =
            new ConditionalWeakTable<object, PendingReplayQueue>();

        private static MethodInfo _impactMethod;
        private static FieldInfo _generationField;
        private static FieldInfo _shotOriginField;
        private static MethodInfo _autoguidanceRuntimeMethod;

        [ThreadStatic]
        private static bool _replayingImpact;

        [ThreadStatic]
        private static bool _replayingRemoval;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _impactMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                    candidate.Name == "OnMissileHit" &&
                    !candidate.IsAbstract &&
                    candidate.GetParameters().Any(parameter =>
                        parameter.ParameterType == typeof(AttackCollisionData)));
            if (_impactMethod == null) return;

            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _shotOriginField = AccessTools.Field(behaviorType, "_pendingShotPosition");
            _autoguidanceRuntimeMethod = AccessTools.Method(behaviorType, "IsAutoguidanceRuntimeActive");

            MethodInfo impactPrefix = AccessTools.Method(
                typeof(FullImpactReplayDeferralPatch),
                nameof(ImpactPrefix));
            MethodInfo removalPrefix = AccessTools.Method(
                typeof(FullImpactReplayDeferralPatch),
                nameof(RemovalPrefix));
            MethodInfo displayPrefix = AccessTools.Method(
                typeof(FullImpactReplayDeferralPatch),
                nameof(DisplayPrefix));
            MethodInfo resetPostfix = AccessTools.Method(
                typeof(FullImpactReplayDeferralPatch),
                nameof(ResetPostfix));
            if (impactPrefix == null || removalPrefix == null || displayPrefix == null || resetPostfix == null)
                return;

            try
            {
                harmony.Patch(
                    _impactMethod,
                    prefix: new HarmonyMethod(impactPrefix)
                    {
                        priority = int.MaxValue
                    });
            }
            catch
            {
                return;
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnMissileRemoved" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(removalPrefix) { priority = int.MaxValue });
                }
                catch { }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnPreDisplayMissionTick" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(displayPrefix) { priority = int.MaxValue });
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
                        postfix: new HarmonyMethod(resetPostfix) { priority = Priority.Last });
                }
                catch { }
            }
        }

        private static bool ImpactPrefix(object __instance, object[] __args)
        {
            if (_replayingImpact) return true;
            if (__instance == null || __args == null) return false;

            try
            {
                object[] arguments = (object[])__args.Clone();
                PendingImpact impact = CaptureImpact(__instance, arguments);
                PendingReplayQueue queue = Pending.GetOrCreateValue(__instance);
                queue.Impacts.Add(impact);
            }
            catch
            {
                // Skipping the synchronous body is safer than falling back into native impact work.
            }

            return false;
        }

        private static bool RemovalPrefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (_replayingRemoval) return true;
            if (__instance == null || __originalMethod == null || __args == null) return true;

            try
            {
                if (!Pending.TryGetValue(__instance, out PendingReplayQueue queue) ||
                    queue == null ||
                    queue.Impacts.Count == 0)
                    return true;

                queue.Removals.Add(new PendingRemoval
                {
                    Method = __originalMethod as MethodInfo,
                    Arguments = (object[])__args.Clone()
                });
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void DisplayPrefix(object __instance)
        {
            if (__instance == null ||
                !Pending.TryGetValue(__instance, out PendingReplayQueue queue) ||
                queue == null ||
                queue.Flushing)
                return;

            PendingImpact[] impacts = queue.Impacts.ToArray();
            PendingRemoval[] removals = queue.Removals.ToArray();
            queue.Impacts.Clear();
            queue.Removals.Clear();
            queue.Flushing = true;

            try
            {
                for (int i = 0; i < impacts.Length; i++)
                {
                    PendingImpact impact = impacts[i];
                    if (impact?.Arguments == null) continue;

                    try
                    {
                        _replayingImpact = true;
                        _impactMethod.Invoke(__instance, impact.Arguments);
                    }
                    catch
                    {
                        // A vanished target or stale managed reference must not break the mission.
                    }
                    finally
                    {
                        _replayingImpact = false;
                    }

                    AwardDeferredProgression(__instance, impact);
                }

                for (int i = 0; i < removals.Length; i++)
                {
                    PendingRemoval removal = removals[i];
                    if (removal?.Method == null || removal.Arguments == null) continue;

                    try
                    {
                        _replayingRemoval = true;
                        removal.Method.Invoke(__instance, removal.Arguments);
                    }
                    catch { }
                    finally
                    {
                        _replayingRemoval = false;
                    }
                }
            }
            finally
            {
                queue.Flushing = false;
                if (queue.Impacts.Count == 0 && queue.Removals.Count == 0)
                    Pending.Remove(__instance);
            }
        }

        private static PendingImpact CaptureImpact(object instance, object[] arguments)
        {
            PendingImpact impact = new PendingImpact
            {
                Arguments = arguments,
                VictimKey = -1
            };

            if (arguments != null)
            {
                Agent[] agents = arguments.OfType<Agent>().Where(agent => agent != null).ToArray();
                if (agents.Length > 0) impact.Shooter = agents[0];
                if (agents.Length > 1) impact.Victim = agents[1];

                for (int i = 0; i < arguments.Length; i++)
                {
                    if (!(arguments[i] is AttackCollisionData collision)) continue;
                    impact.Collision = collision;
                    impact.HasCollision = true;
                    impact.Fatal = ConcentratedImpactSafetyPatch.ReadCollisionFatalDamage(collision);
                    break;
                }
            }

            if (impact.Victim != null)
                impact.VictimKey = RuntimeHelpers.GetHashCode(impact.Victim);

            try
            {
                if (_generationField != null)
                    impact.Generation = (int)_generationField.GetValue(instance);
                if (_shotOriginField != null)
                    impact.ShotOrigin = (Vec3)_shotOriginField.GetValue(instance);
            }
            catch { }

            return impact;
        }

        private static void AwardDeferredProgression(object instance, PendingImpact impact)
        {
            if (instance == null || impact == null || !impact.HasCollision || !impact.Collision.IsMissile)
                return;

            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null || !progression.Enabled || impact.Generation <= 0 || impact.VictimKey < 0)
                return;

            try
            {
                if (!ReferenceEquals(impact.Shooter, Agent.Main)) return;
                if (!IsEnemy(impact.Victim, impact.Shooter)) return;

                float distance = (impact.Collision.CollisionGlobalPosition - impact.ShotOrigin).Length;
                if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
                    distance = 0f;

                bool autoguidance = false;
                if (_autoguidanceRuntimeMethod != null)
                {
                    object active = _autoguidanceRuntimeMethod.Invoke(instance, null);
                    autoguidance = active is bool enabled && enabled;
                }

                float multiplier = autoguidance
                    ? ProgressionBalance.AutoguidedXpMultiplier(
                        ProgressionService.Level(SkillId.BorrowedFlight))
                    : 1f;

                progression.RecordGuidedHit(
                    impact.Generation,
                    impact.VictimKey,
                    impact.Fatal,
                    distance,
                    multiplier);
            }
            catch { }
        }

        private static bool IsEnemy(Agent victim, Agent shooter)
        {
            if (victim == null || shooter == null) return false;

            try
            {
                Team victimTeam = victim.Team;
                Team shooterTeam = shooter.Team;
                if (victimTeam == null || shooterTeam == null) return false;
                return victimTeam.IsEnemyOf(shooterTeam);
            }
            catch
            {
                return false;
            }
        }

        private static void ResetPostfix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
        }
    }
}
