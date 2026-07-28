using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps concentrated split-volley impacts inside managed lifetime boundaries.
    ///
    /// The verified core re-sampled native victim bones and health for every arrow striking an
    /// already-tracked victim. Repeated same-tick impacts must instead retain managed collision
    /// values once the first impact has established a cinematic subject.
    /// </summary>
    internal static class ConcentratedImpactSafetyPatch
    {
        private static int _collisionArgumentIndex = -1;
        private static int _victimArgumentIndex = -1;
        private static MethodInfo _agentHealthGetter;
        private static MethodInfo _collisionFatalDamageGetter;
        private static FieldInfo _cinematicSubjectsField;
        private static FieldInfo _subjectAgentField;
        private static FieldInfo _subjectLastKnownPositionField;
        private static FieldInfo _subjectHasLastKnownPositionField;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            PatchMissileHitFatalCheck(harmony, behaviorType);
            PatchCinematicSampling(harmony, behaviorType);
        }

        private static void PatchMissileHitFatalCheck(Harmony harmony, Type behaviorType)
        {
            MethodInfo method = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                    candidate.Name == "OnMissileHit" &&
                    !candidate.IsAbstract &&
                    candidate.GetParameters().Any(parameter =>
                        parameter.ParameterType == typeof(AttackCollisionData)));
            if (method == null) return;

            ParameterInfo[] parameters = method.GetParameters();
            int collisionParameterIndex = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(AttackCollisionData));
            int firstAgentParameterIndex = Array.FindIndex(
                parameters,
                parameter => parameter.ParameterType == typeof(Agent));
            int secondAgentParameterIndex = firstAgentParameterIndex < 0
                ? -1
                : Array.FindIndex(
                    parameters,
                    firstAgentParameterIndex + 1,
                    parameter => parameter.ParameterType == typeof(Agent));
            if (collisionParameterIndex < 0 || secondAgentParameterIndex < 0) return;

            _collisionArgumentIndex = collisionParameterIndex + 1;
            _victimArgumentIndex = secondAgentParameterIndex + 1;
            _agentHealthGetter = AccessTools.PropertyGetter(typeof(Agent), nameof(Agent.Health));
            _collisionFatalDamageGetter = AccessTools.PropertyGetter(typeof(AttackCollisionData), "IsFatalDamage");

            MethodInfo transpiler = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(MissileHitTranspiler));
            if (_agentHealthGetter == null ||
                _collisionFatalDamageGetter == null ||
                transpiler == null)
                return;

            try
            {
                harmony.Patch(
                    method,
                    transpiler: new HarmonyMethod(transpiler) { priority = Priority.First });
            }
            catch { }
        }

        private static IEnumerable<CodeInstruction> MissileHitTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo replacementMethod = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(ReadFatalDamageAsHealthSentinel));
            if (replacementMethod == null ||
                _agentHealthGetter == null ||
                _collisionArgumentIndex < 0 ||
                _victimArgumentIndex < 0)
                return code;

            for (int i = 1; i < code.Count; i++)
            {
                if (!CallsMethod(code[i], _agentHealthGetter) ||
                    !LoadsArgument(code[i - 1], _victimArgumentIndex))
                    continue;

                CodeInstruction loadCollision = CreateLoadArgumentAddress(_collisionArgumentIndex);
                CopyMetadata(code[i - 1], loadCollision);
                code[i - 1] = loadCollision;

                CodeInstruction callReplacement = new CodeInstruction(OpCodes.Call, replacementMethod);
                CopyMetadata(code[i], callReplacement);
                code[i] = callReplacement;
                break;
            }

            return code;
        }

        private static float ReadFatalDamageAsHealthSentinel(ref AttackCollisionData collision)
        {
            return ReadCollisionFatalDamage(collision) ? 0f : 1f;
        }

        internal static bool ReadCollisionFatalDamage(object collisionBox)
        {
            if (collisionBox == null) return false;

            try
            {
                if (_collisionFatalDamageGetter == null)
                    _collisionFatalDamageGetter = AccessTools.PropertyGetter(typeof(AttackCollisionData), "IsFatalDamage");

                object result = _collisionFatalDamageGetter?.Invoke(collisionBox, null);
                return result is bool fatal && fatal;
            }
            catch
            {
                return false;
            }
        }

        private static void PatchCinematicSampling(Harmony harmony, Type behaviorType)
        {
            Type subjectType = behaviorType.GetNestedType(
                "CinematicSubjectRecord",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (subjectType == null) return;

            _cinematicSubjectsField = AccessTools.Field(behaviorType, "_cinematicSubjects");
            _subjectAgentField = AccessTools.Field(subjectType, "Agent");
            _subjectLastKnownPositionField = AccessTools.Field(subjectType, "LastKnownPosition");
            _subjectHasLastKnownPositionField = AccessTools.Field(subjectType, "HasLastKnownPosition");
            if (_cinematicSubjectsField == null ||
                _subjectAgentField == null ||
                _subjectLastKnownPositionField == null ||
                _subjectHasLastKnownPositionField == null)
                return;

            MethodInfo trackMethod = AccessTools.Method(
                behaviorType,
                "TrackCinematicSubject",
                new[] { typeof(Agent), typeof(Vec3) });
            MethodInfo trackPrefix = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(TrackCinematicSubjectPrefix));
            if (trackMethod != null && trackPrefix != null)
            {
                try
                {
                    harmony.Patch(
                        trackMethod,
                        prefix: new HarmonyMethod(trackPrefix) { priority = Priority.First });
                }
                catch { }
            }

            MethodInfo snapshotMethod = AccessTools.Method(
                behaviorType,
                "SnapshotCinematicSubject",
                new[] { typeof(Agent) });
            MethodInfo snapshotPrefix = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(SnapshotCinematicSubjectPrefix));
            if (snapshotMethod != null && snapshotPrefix != null)
            {
                try
                {
                    harmony.Patch(
                        snapshotMethod,
                        prefix: new HarmonyMethod(snapshotPrefix) { priority = Priority.First });
                }
                catch { }
            }

            MethodInfo removalPostfix = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(AgentRemovalPostfix));
            if (removalPostfix == null) return;

            foreach (string callback in new[] { "OnEarlyAgentRemoved", "OnAgentRemoved", "OnAgentDeleted" })
            {
                foreach (MethodInfo method in behaviorType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(candidate => candidate.Name == callback && !candidate.IsAbstract))
                {
                    try
                    {
                        harmony.Patch(
                            method,
                            postfix: new HarmonyMethod(removalPostfix) { priority = Priority.Last });
                    }
                    catch { }
                }
            }
        }

        private static bool TrackCinematicSubjectPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 2)
                return true;

            Agent victim = __args[0] as Agent;
            if (victim == null || !TryFindCinematicSubject(__instance, victim, out object subject))
                return true;

            try
            {
                Vec3 safePosition = Vec3.Zero;
                if (__args[1] is Vec3 fallbackPosition && IsFinite(fallbackPosition))
                {
                    safePosition = fallbackPosition;
                }
                else
                {
                    object cached = _subjectLastKnownPositionField.GetValue(subject);
                    if (cached is Vec3 cachedPosition && IsFinite(cachedPosition))
                        safePosition = cachedPosition;
                }

                _subjectLastKnownPositionField.SetValue(subject, safePosition);
                _subjectHasLastKnownPositionField.SetValue(subject, true);
            }
            catch { }

            // The victim already has a managed subject record. Never re-enter skeleton/bone queries
            // for this subject, including when collision data did not contain a finite position.
            return false;
        }

        private static bool SnapshotCinematicSubjectPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length == 0)
                return true;

            Agent victim = __args[0] as Agent;
            if (victim == null || !TryFindCinematicSubject(__instance, victim, out _))
                return true;

            // An existing subject must remain on its managed cached-position path. Falling through
            // would query native visuals again while the same impact burst may be removing it.
            return false;
        }

        private static void AgentRemovalPostfix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null) return;

            Agent victim = __args.OfType<Agent>().FirstOrDefault();
            if (victim == null || !TryFindCinematicSubject(__instance, victim, out object subject))
                return;

            try
            {
                // Once removal has started, only the managed last-known impact position is safe.
                // The core's later cinematic ticks must not re-enter the removed agent's visuals.
                _subjectAgentField.SetValue(subject, null);
            }
            catch { }
        }

        private static bool TryFindCinematicSubject(object instance, Agent victim, out object subject)
        {
            subject = null;
            if (instance == null || victim == null || _cinematicSubjectsField == null)
                return false;

            try
            {
                IList subjects = _cinematicSubjectsField.GetValue(instance) as IList;
                if (subjects == null) return false;

                for (int i = 0; i < subjects.Count; i++)
                {
                    object candidate = subjects[i];
                    if (candidate == null) continue;

                    Agent candidateAgent = _subjectAgentField.GetValue(candidate) as Agent;
                    if (!ReferenceEquals(candidateAgent, victim)) continue;

                    subject = candidate;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static bool CallsMethod(CodeInstruction instruction, MethodInfo method)
        {
            if (instruction == null || method == null) return false;
            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
                return false;
            return Equals(instruction.operand, method);
        }

        private static bool LoadsArgument(CodeInstruction instruction, int argumentIndex)
        {
            if (instruction == null || argumentIndex < 0) return false;
            if (argumentIndex == 0 && instruction.opcode == OpCodes.Ldarg_0) return true;
            if (argumentIndex == 1 && instruction.opcode == OpCodes.Ldarg_1) return true;
            if (argumentIndex == 2 && instruction.opcode == OpCodes.Ldarg_2) return true;
            if (argumentIndex == 3 && instruction.opcode == OpCodes.Ldarg_3) return true;

            if (instruction.opcode != OpCodes.Ldarg && instruction.opcode != OpCodes.Ldarg_S)
                return false;

            try { return Convert.ToInt32(instruction.operand) == argumentIndex; }
            catch { return false; }
        }

        private static CodeInstruction CreateLoadArgumentAddress(int argumentIndex)
        {
            return argumentIndex <= byte.MaxValue
                ? new CodeInstruction(OpCodes.Ldarga_S, (byte)argumentIndex)
                : new CodeInstruction(OpCodes.Ldarga, (short)argumentIndex);
        }

        private static void CopyMetadata(CodeInstruction source, CodeInstruction destination)
        {
            if (source == null || destination == null) return;
            destination.labels.AddRange(source.labels);
            destination.blocks.AddRange(source.blocks);
        }

        private static bool IsFinite(Vec3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
