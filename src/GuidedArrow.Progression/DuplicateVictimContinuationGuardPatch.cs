using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Suppresses only the synthetic continuation produced by the exact duplicate-hit sequence
    /// observed in the point-blank concentrated volley: the same tracked missile first receives
    /// PassThrough, confirms a kill, then reports another hit on the same victim and resolves Stick.
    /// The native projectile already completed its authoritative collision path; creating another
    /// custom missile from that duplicate terminal callback can raise AccessViolationException.
    /// </summary>
    internal static class DuplicateVictimContinuationGuardPatch
    {
        private sealed class ShotState
        {
            internal int Generation = -1;
            internal readonly Dictionary<int, int> FirstVictimByMissile = new Dictionary<int, int>();
            internal readonly HashSet<int> PassedThrough = new HashSet<int>();
            internal readonly HashSet<int> DuplicateKilledVictimHits = new HashSet<int>();
            internal readonly HashSet<int> BlockedContinuations = new HashSet<int>();
        }

        private static readonly ConditionalWeakTable<object, ShotState> States =
            new ConditionalWeakTable<object, ShotState>();

        private static FieldInfo _generationField;
        private static FieldInfo _confirmedKillsField;
        private static FieldInfo _trackedIndexField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _confirmedKillsField = AccessTools.Field(behaviorType, "_confirmedCinematicKillCount");

            MethodInfo spawn = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            if (spawn == null) return;

            Type trackedType = spawn.GetParameters()[0].ParameterType;
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            if (_generationField == null || _confirmedKillsField == null || _trackedIndexField == null)
                return;

            MethodInfo hitPrefix = AccessTools.Method(
                typeof(DuplicateVictimContinuationGuardPatch),
                nameof(HitPrefix));
            MethodInfo reactionPrefix = AccessTools.Method(
                typeof(DuplicateVictimContinuationGuardPatch),
                nameof(ReactionPrefix));
            MethodInfo spawnPrefix = AccessTools.Method(
                typeof(DuplicateVictimContinuationGuardPatch),
                nameof(SpawnPrefix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(DuplicateVictimContinuationGuardPatch),
                nameof(ClearPrefix));

            if (hitPrefix == null || reactionPrefix == null || spawnPrefix == null || clearPrefix == null)
                return;

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "OnMissileHit" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(hitPrefix) { priority = int.MaxValue });
                }
                catch { }
            }

            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "ResolveCollisionReaction" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(reactionPrefix) { priority = int.MaxValue });
                }
                catch { }
            }

            try
            {
                harmony.Patch(
                    spawn,
                    prefix: new HarmonyMethod(spawnPrefix) { priority = int.MaxValue });
            }
            catch
            {
                return;
            }

            foreach (string methodName in new[] { "StartGuidedShot", "ResetAll" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == methodName && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(clearPrefix) { priority = Priority.First });
                    }
                    catch { }
                }
            }
        }

        private static void HitPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null) return;

            try
            {
                Agent shooter = __args.OfType<Agent>().FirstOrDefault();
                Agent victim = __args.OfType<Agent>().FirstOrDefault(agent => !ReferenceEquals(agent, shooter));
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

                int index = collision.AffectorWeaponSlotOrMissileIndex;
                int generation = (int)_generationField.GetValue(__instance);
                ShotState state = GetState(__instance, generation);
                int victimKey = RuntimeHelpers.GetHashCode(victim);

                if (!state.FirstVictimByMissile.TryGetValue(index, out int firstVictim))
                {
                    state.FirstVictimByMissile[index] = victimKey;
                    return;
                }

                if (firstVictim == victimKey &&
                    state.PassedThrough.Contains(index) &&
                    (int)_confirmedKillsField.GetValue(__instance) > 0)
                {
                    state.DuplicateKilledVictimHits.Add(index);
                }
            }
            catch { }
        }

        private static void ReactionPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 2) return;

            try
            {
                int index = Convert.ToInt32(__args[0]);
                int reaction = Convert.ToInt32(__args[1]);
                int generation = (int)_generationField.GetValue(__instance);
                ShotState state = GetState(__instance, generation);

                // MissileCollisionReaction: Stick=0, PassThrough=1.
                if (reaction == 1)
                {
                    state.PassedThrough.Add(index);
                }
                else if (reaction == 0 &&
                         state.PassedThrough.Contains(index) &&
                         state.DuplicateKilledVictimHits.Contains(index))
                {
                    state.BlockedContinuations.Add(index);
                }
            }
            catch { }
        }

        private static bool SpawnPrefix(object __instance, object[] __args, ref bool __result)
        {
            if (__instance == null || __args == null || __args.Length < 1 || __args[0] == null)
                return true;

            try
            {
                int generation = (int)_generationField.GetValue(__instance);
                ShotState state = GetState(__instance, generation);
                int index = (int)_trackedIndexField.GetValue(__args[0]);
                if (!state.BlockedContinuations.Remove(index))
                    return true;

                if (__args.Length >= 3)
                    __args[2] = null;
                __result = false;
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static ShotState GetState(object instance, int generation)
        {
            ShotState state = States.GetOrCreateValue(instance);
            if (state.Generation == generation) return state;

            state.Generation = generation;
            state.FirstVictimByMissile.Clear();
            state.PassedThrough.Clear();
            state.DuplicateKilledVictimHits.Clear();
            state.BlockedContinuations.Clear();
            return state;
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                States.Remove(__instance);
        }
    }
}
