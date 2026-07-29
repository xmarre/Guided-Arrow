using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Suppresses only the synthetic continuation produced after the core itself identifies the
    /// duplicate confirmed-kill path as OnMissileHitAlreadyDead. This avoids reconstructing victim
    /// identity from OnMissileHit arguments and keys the guard directly from the verified core event.
    /// </summary>
    internal static class DuplicateVictimContinuationGuardPatch
    {
        private sealed class ShotState
        {
            internal int Generation = -1;
            internal readonly HashSet<int> PassedThrough = new HashSet<int>();
            internal readonly HashSet<int> BlockedContinuations = new HashSet<int>();
            internal bool DuplicateConfirmedKillPending;
        }

        private static readonly ConditionalWeakTable<object, ShotState> States =
            new ConditionalWeakTable<object, ShotState>();

        private static FieldInfo _generationField;
        private static FieldInfo _trackedIndexField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");

            MethodInfo spawn = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            MethodInfo confirmedKill = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "HandleConfirmedKill" &&
                    method.GetParameters().Length == 2 &&
                    method.GetParameters()[1].ParameterType == typeof(string));
            if (spawn == null || confirmedKill == null || _generationField == null) return;

            Type trackedType = spawn.GetParameters()[0].ParameterType;
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            if (_trackedIndexField == null) return;

            MethodInfo killPrefix = AccessTools.Method(
                typeof(DuplicateVictimContinuationGuardPatch),
                nameof(ConfirmedKillPrefix));
            MethodInfo reactionPrefix = AccessTools.Method(
                typeof(DuplicateVictimContinuationGuardPatch),
                nameof(ReactionPrefix));
            MethodInfo spawnPrefix = AccessTools.Method(
                typeof(DuplicateVictimContinuationGuardPatch),
                nameof(SpawnPrefix));
            MethodInfo clearPrefix = AccessTools.Method(
                typeof(DuplicateVictimContinuationGuardPatch),
                nameof(ClearPrefix));

            if (killPrefix == null || reactionPrefix == null || spawnPrefix == null || clearPrefix == null)
                return;

            try
            {
                harmony.Patch(
                    confirmedKill,
                    prefix: new HarmonyMethod(killPrefix) { priority = int.MaxValue });
            }
            catch
            {
                return;
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

        private static void ConfirmedKillPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 2) return;

            try
            {
                string source = __args[1] as string;
                if (!string.Equals(source, "OnMissileHitAlreadyDead", StringComparison.Ordinal)) return;

                int generation = (int)_generationField.GetValue(__instance);
                GetState(__instance, generation).DuplicateConfirmedKillPending = true;
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
                    return;
                }

                if (reaction == 0 &&
                    state.DuplicateConfirmedKillPending &&
                    state.PassedThrough.Contains(index))
                {
                    state.BlockedContinuations.Add(index);
                    state.DuplicateConfirmedKillPending = false;
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
            state.PassedThrough.Clear();
            state.BlockedContinuations.Clear();
            state.DuplicateConfirmedKillPending = false;
            return state;
        }

        private static void ClearPrefix(object __instance)
        {
            if (__instance != null)
                States.Remove(__instance);
        }
    }
}
