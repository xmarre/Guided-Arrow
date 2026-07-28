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
    /// Captures only managed impact data from GuidedArrowBehavior.OnMissileHit and performs
    /// progression accounting on the following display tick. This replaces the old inherited
    /// MissionBehavior.OnAgentHit postfix, which could read Agent health/position after the core
    /// impact callback had already completed native missile teardown.
    /// </summary>
    internal static class ProgressionHitAccountingPatch
    {
        private sealed class PendingHit
        {
            internal Agent Shooter;
            internal Agent Victim;
            internal int VictimKey;
            internal int Generation;
            internal Vec3 Origin;
            internal Vec3 CollisionPosition;
            internal bool Fatal;
        }

        private sealed class QueueState
        {
            internal readonly List<PendingHit> Items = new List<PendingHit>();
        }

        private static readonly ConditionalWeakTable<object, QueueState> Pending =
            new ConditionalWeakTable<object, QueueState>();

        private static FieldInfo _generationField;
        private static FieldInfo _shooterField;
        private static FieldInfo _shotOriginField;
        private static MethodInfo _autoguidanceRuntimeMethod;
        private static MethodInfo _fatalDamageGetter;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _shooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _shotOriginField = AccessTools.Field(behaviorType, "_pendingShotPosition");
            _autoguidanceRuntimeMethod = AccessTools.Method(behaviorType, "IsAutoguidanceRuntimeActive");
            _fatalDamageGetter = AccessTools.PropertyGetter(typeof(AttackCollisionData), "IsFatalDamage");

            MethodInfo impact = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "OnMissileHit" &&
                    !method.IsAbstract &&
                    method.GetParameters().Any(parameter => parameter.ParameterType == typeof(AttackCollisionData)));
            MethodInfo capturePostfix = AccessTools.Method(
                typeof(ProgressionHitAccountingPatch),
                nameof(CapturePostfix));
            MethodInfo flushPrefix = AccessTools.Method(
                typeof(ProgressionHitAccountingPatch),
                nameof(FlushPrefix));
            MethodInfo resetPostfix = AccessTools.Method(
                typeof(ProgressionHitAccountingPatch),
                nameof(ResetPostfix));

            if (impact == null ||
                capturePostfix == null ||
                flushPrefix == null ||
                resetPostfix == null ||
                _generationField == null ||
                _shooterField == null)
                return;

            try
            {
                harmony.Patch(
                    impact,
                    postfix: new HarmonyMethod(capturePostfix) { priority = Priority.Last });
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
                        postfix: new HarmonyMethod(resetPostfix) { priority = Priority.Last });
                }
                catch { }
            }
        }

        private static void CapturePostfix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null) return;

            try
            {
                Agent shooter = _shooterField.GetValue(__instance) as Agent;
                if (shooter == null) return;

                Agent victim = __args
                    .OfType<Agent>()
                    .FirstOrDefault(candidate => candidate != null && !ReferenceEquals(candidate, shooter));
                if (victim == null) return;

                AttackCollisionData collision = default;
                bool foundCollision = false;
                for (int i = 0; i < __args.Length; i++)
                {
                    if (!(__args[i] is AttackCollisionData candidate)) continue;
                    collision = candidate;
                    foundCollision = true;
                    break;
                }
                if (!foundCollision || !collision.IsMissile) return;

                Vec3 origin = Vec3.Zero;
                try
                {
                    if (_shotOriginField != null)
                        origin = (Vec3)_shotOriginField.GetValue(__instance);
                }
                catch { }

                QueueState queue = Pending.GetOrCreateValue(__instance);
                queue.Items.Add(new PendingHit
                {
                    Shooter = shooter,
                    Victim = victim,
                    VictimKey = RuntimeHelpers.GetHashCode(victim),
                    Generation = (int)_generationField.GetValue(__instance),
                    Origin = origin,
                    CollisionPosition = collision.CollisionGlobalPosition,
                    Fatal = ReadFatalDamage(collision)
                });
            }
            catch
            {
                // Capture must never alter the core impact result.
            }
        }

        private static void FlushPrefix(object __instance)
        {
            if (__instance == null ||
                !Pending.TryGetValue(__instance, out QueueState queue) ||
                queue == null)
                return;

            Pending.Remove(__instance);
            PendingHit[] hits = queue.Items.ToArray();

            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null || !progression.Enabled) return;

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

            for (int i = 0; i < hits.Length; i++)
            {
                PendingHit hit = hits[i];
                if (hit == null ||
                    hit.Generation <= 0 ||
                    hit.VictimKey < 0 ||
                    !ReferenceEquals(hit.Shooter, Agent.Main))
                    continue;

                float distance = (hit.CollisionPosition - hit.Origin).Length;
                if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
                    distance = 0f;

                try
                {
                    progression.RecordGuidedHit(
                        hit.Generation,
                        hit.VictimKey,
                        hit.Fatal,
                        distance,
                        multiplier);
                }
                catch
                {
                    // Progression accounting must not affect mission execution.
                }
            }
        }

        private static bool ReadFatalDamage(AttackCollisionData collision)
        {
            try
            {
                object boxed = collision;
                object value = _fatalDamageGetter?.Invoke(boxed, null);
                return value is bool fatal && fatal;
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
