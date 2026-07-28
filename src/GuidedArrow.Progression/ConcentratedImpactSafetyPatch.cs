using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps concentrated split-volley impacts inside managed lifetime boundaries.
    ///
    /// The verified core queried the impacted missile entity and victim skeleton after native
    /// collision processing had already started. A managed wrapper can still exist at that point
    /// while its underlying native missile or agent presentation handle is no longer safe to read.
    /// </summary>
    internal static class ConcentratedImpactSafetyPatch
    {
        private static int _collisionArgumentIndex = -1;
        private static int _victimArgumentIndex = -1;
        private static MethodInfo _agentHealthGetter;
        private static MethodInfo _collisionFatalDamageGetter;
        private static MethodInfo _missileEntityGetter;
        private static Type _cinematicSubjectType;
        private static FieldInfo _cinematicSubjectsField;
        private static FieldInfo _impactPositionField;
        private static FieldInfo _subjectAgentField;
        private static FieldInfo _subjectLastKnownPositionField;
        private static FieldInfo _subjectHasLastKnownPositionField;

        private static readonly Dictionary<Agent, Vec3> ManagedVictimPositions =
            new Dictionary<Agent, Vec3>(AgentReferenceComparer.Instance);

        private sealed class AgentReferenceComparer : IEqualityComparer<Agent>
        {
            internal static readonly AgentReferenceComparer Instance = new AgentReferenceComparer();

            public bool Equals(Agent x, Agent y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(Agent obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            PatchMissileHitLifetimeReads(harmony, behaviorType);
            PatchCinematicSampling(harmony, behaviorType);
        }

        private static void PatchMissileHitLifetimeReads(Harmony harmony, Type behaviorType)
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
            _missileEntityGetter = typeof(MBMissile)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate =>
                    candidate.Name == "get_Entity" &&
                    candidate.GetParameters().Length == 0 &&
                    candidate.ReturnType == typeof(GameEntity));

            MethodInfo transpiler = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(MissileHitTranspiler));
            if (_agentHealthGetter == null ||
                _collisionFatalDamageGetter == null ||
                _missileEntityGetter == null ||
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
            MethodInfo fatalReplacement = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(ReadFatalDamageAsHealthSentinel));
            MethodInfo entityReplacement = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(ReturnNoImpactedMissileEntity));
            if (fatalReplacement == null ||
                entityReplacement == null ||
                _agentHealthGetter == null ||
                _missileEntityGetter == null ||
                _collisionArgumentIndex < 0 ||
                _victimArgumentIndex < 0)
                return code;

            int entityReads = 0;
            int fatalReads = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (CallsMethod(code[i], _missileEntityGetter)) entityReads++;
                if (i > 0 &&
                    CallsMethod(code[i], _agentHealthGetter) &&
                    LoadsArgument(code[i - 1], _victimArgumentIndex))
                    fatalReads++;
            }

            // This patch is intentionally locked to the verified v1.1.17 OnMissileHit shape.
            if (entityReads != 1 || fatalReads != 1)
                return code;

            for (int i = 0; i < code.Count; i++)
            {
                if (CallsMethod(code[i], _missileEntityGetter))
                {
                    CodeInstruction replacement = new CodeInstruction(OpCodes.Call, entityReplacement);
                    CopyMetadata(code[i], replacement);
                    code[i] = replacement;
                    continue;
                }

                if (i == 0 ||
                    !CallsMethod(code[i], _agentHealthGetter) ||
                    !LoadsArgument(code[i - 1], _victimArgumentIndex))
                    continue;

                CodeInstruction loadCollision = CreateLoadArgumentAddress(_collisionArgumentIndex);
                CopyMetadata(code[i - 1], loadCollision);
                code[i - 1] = loadCollision;

                CodeInstruction callReplacement = new CodeInstruction(OpCodes.Call, fatalReplacement);
                CopyMetadata(code[i], callReplacement);
                code[i] = callReplacement;
            }

            return code;
        }

        private static GameEntity ReturnNoImpactedMissileEntity(MBMissile missile)
        {
            // Never dereference the impacted native missile wrapper from OnMissileHit. The arrow
            // entity is optional cinematic decoration; collision position remains authoritative.
            return null;
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
            _cinematicSubjectType = behaviorType.GetNestedType(
                "CinematicSubjectRecord",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (_cinematicSubjectType == null) return;

            _cinematicSubjectsField = AccessTools.Field(behaviorType, "_cinematicSubjects");
            _impactPositionField = AccessTools.Field(behaviorType, "_impactPosition");
            _subjectAgentField = AccessTools.Field(_cinematicSubjectType, "Agent");
            _subjectLastKnownPositionField = AccessTools.Field(_cinematicSubjectType, "LastKnownPosition");
            _subjectHasLastKnownPositionField = AccessTools.Field(_cinematicSubjectType, "HasLastKnownPosition");
            if (_cinematicSubjectsField == null ||
                _impactPositionField == null ||
                _subjectAgentField == null ||
                _subjectLastKnownPositionField == null ||
                _subjectHasLastKnownPositionField == null)
                return;

            PatchPrefix(
                harmony,
                AccessTools.Method(behaviorType, "TrackCinematicSubject", new[] { typeof(Agent), typeof(Vec3) }),
                nameof(TrackCinematicSubjectPrefix));
            PatchPrefix(
                harmony,
                AccessTools.Method(behaviorType, "SnapshotCinematicSubject", new[] { typeof(Agent) }),
                nameof(SnapshotCinematicSubjectPrefix));
            PatchPrefix(
                harmony,
                AccessTools.Method(behaviorType, "GetRagdollVisualPosition", new[] { typeof(Agent) }),
                nameof(GetRagdollVisualPositionPrefix));

            MethodInfo removalPostfix = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(AgentRemovalPostfix));
            if (removalPostfix != null)
            {
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

            MethodInfo resetPostfix = AccessTools.Method(
                typeof(ConcentratedImpactSafetyPatch),
                nameof(ResetPostfix));
            foreach (MethodInfo method in behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.Name == "ResetAll" && !candidate.IsAbstract))
            {
                if (resetPostfix == null) break;
                try
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(resetPostfix) { priority = Priority.Last });
                }
                catch { }
            }
        }

        private static void PatchPrefix(Harmony harmony, MethodInfo original, string prefixName)
        {
            MethodInfo prefix = AccessTools.Method(typeof(ConcentratedImpactSafetyPatch), prefixName);
            if (harmony == null || original == null || prefix == null) return;

            try
            {
                harmony.Patch(
                    original,
                    prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            }
            catch { }
        }

        private static bool TrackCinematicSubjectPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length < 2)
                return true;

            Agent victim = __args[0] as Agent;
            if (victim == null) return false;

            try
            {
                if (!TryFindCinematicSubject(__instance, victim, out object subject))
                {
                    IList subjects = _cinematicSubjectsField.GetValue(__instance) as IList;
                    if (subjects == null) return true;

                    subject = Activator.CreateInstance(_cinematicSubjectType, true);
                    _subjectAgentField.SetValue(subject, victim);
                    subjects.Add(subject);
                }

                Vec3 safePosition = ResolveManagedImpactPosition(__instance, __args[1], subject);
                _subjectLastKnownPositionField.SetValue(subject, safePosition);
                _subjectHasLastKnownPositionField.SetValue(subject, true);
                ManagedVictimPositions[victim] = safePosition;
                return false;
            }
            catch
            {
                // Preserve the core path only when the managed record itself could not be created.
                return true;
            }
        }

        private static bool SnapshotCinematicSubjectPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length == 0)
                return true;

            Agent victim = __args[0] as Agent;
            if (victim == null || !TryFindCinematicSubject(__instance, victim, out _))
                return true;

            // An existing subject remains on its managed cached-position path. Falling through would
            // query native bones while the impact or removal callback is still active.
            return false;
        }

        private static bool GetRagdollVisualPositionPrefix(Agent __0, ref Vec3 __result)
        {
            if (__0 == null || !ManagedVictimPositions.TryGetValue(__0, out Vec3 cachedPosition) ||
                !IsFinite(cachedPosition))
                return true;

            __result = cachedPosition;
            return false;
        }

        private static Vec3 ResolveManagedImpactPosition(object instance, object fallbackBox, object subject)
        {
            if (fallbackBox is Vec3 fallbackPosition && IsFinite(fallbackPosition))
                return fallbackPosition;

            try
            {
                object impact = _impactPositionField.GetValue(instance);
                if (impact is Vec3 impactPosition && IsFinite(impactPosition))
                    return impactPosition;
            }
            catch { }

            try
            {
                object cached = _subjectLastKnownPositionField.GetValue(subject);
                if (cached is Vec3 cachedPosition && IsFinite(cachedPosition))
                    return cachedPosition;
            }
            catch { }

            return Vec3.Zero;
        }

        private static void AgentRemovalPostfix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null) return;

            Agent victim = __args.OfType<Agent>().FirstOrDefault();
            if (victim == null || !TryFindCinematicSubject(__instance, victim, out object subject))
                return;

            try
            {
                // Keep ManagedVictimPositions until ResetAll because the cinematic path can retain
                // the Agent wrapper after native removal. Only detach the subject's live reference.
                _subjectAgentField.SetValue(subject, null);
            }
            catch { }
        }

        private static void ResetPostfix()
        {
            ManagedVictimPositions.Clear();
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
