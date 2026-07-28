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
    /// The verified core queried the impacted missile entity and victim health after native
    /// collision processing had already started. A managed wrapper can still exist at that point
    /// while its underlying native missile or agent presentation handle is no longer safe to read.
    /// Cinematic sampling is intentionally left on the core's original live-ragdoll path; replacing
    /// it with a permanent impact-position cache makes the kill camera static.
    /// </summary>
    internal static class ConcentratedImpactSafetyPatch
    {
        private static int _collisionArgumentIndex = -1;
        private static int _victimArgumentIndex = -1;
        private static MethodInfo _agentHealthGetter;
        private static MethodInfo _collisionFatalDamageGetter;
        private static MethodInfo _missileEntityGetter;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            // Keep only the two exact OnMissileHit lifetime substitutions. Earlier diagnostics
            // proved that permanently replacing cinematic victim sampling with the collision point
            // was not the failure fix and regressed the live cinematic kill cameras.
            PatchMissileHitLifetimeReads(harmony, behaviorType);
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

            // The concrete mission wrapper is the global TaleWorlds type `Missile`, which derives
            // from MBMissile and declares Entity itself. Looking only on MBMissile misses this call.
            Type concreteMissileType = typeof(MBMissile).Assembly.GetType("Missile", false);
            _missileEntityGetter = concreteMissileType == null
                ? null
                : AccessTools.PropertyGetter(concreteMissileType, "Entity");

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
    }
}
