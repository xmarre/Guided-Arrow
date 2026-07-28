using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Prevents the verified binary core from reading or writing a native missile handle while
    /// its tracked entry is waiting for Bannerlord's collision reaction.
    ///
    /// The core's OnMissionTick reads GetPosition and GetVelocity before checking
    /// AwaitingCollisionReaction. A terminal agent impact can therefore leave a managed MBMissile
    /// wrapper in the mission registry while its native handle is already no longer safe to query.
    /// </summary>
    internal static class AwaitingCollisionMissileGuardPatch
    {
        private const int ExpectedPositionReads = 2;
        private const int ExpectedVelocityReads = 2;
        private const int ExpectedVelocityWrites = 4;

        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _trackedMissileField;
        private static FieldInfo _awaitingCollisionReactionField;
        private static MethodInfo _getPositionMethod;
        private static MethodInfo _getVelocityMethod;
        private static MethodInfo _setVelocityMethod;

        [ThreadStatic]
        private static HashSet<MBMissile> _quarantinedMissiles;

        private sealed class MissileReferenceComparer : IEqualityComparer<MBMissile>
        {
            internal static readonly MissileReferenceComparer Instance = new MissileReferenceComparer();

            public bool Equals(MBMissile x, MBMissile y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(MBMissile obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            if (trackedType == null) return;

            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _trackedMissileField = AccessTools.Field(trackedType, "Missile");
            _awaitingCollisionReactionField = AccessTools.Field(trackedType, "AwaitingCollisionReaction");
            _getPositionMethod = AccessTools.Method(typeof(MBMissile), nameof(MBMissile.GetPosition), Type.EmptyTypes);
            _getVelocityMethod = AccessTools.Method(typeof(MBMissile), nameof(MBMissile.GetVelocity), Type.EmptyTypes);
            _setVelocityMethod = AccessTools.Method(
                typeof(MBMissile),
                nameof(MBMissile.SetVelocity),
                new[] { typeof(Vec3).MakeByRefType() });

            MethodInfo tickMethod = AccessTools.Method(behaviorType, "OnMissionTick", new[] { typeof(float) });
            MethodInfo prefixMethod = AccessTools.Method(typeof(AwaitingCollisionMissileGuardPatch), nameof(TickPrefix));
            MethodInfo transpilerMethod = AccessTools.Method(typeof(AwaitingCollisionMissileGuardPatch), nameof(TickTranspiler));
            MethodInfo finalizerMethod = AccessTools.Method(typeof(AwaitingCollisionMissileGuardPatch), nameof(TickFinalizer));

            if (tickMethod == null ||
                prefixMethod == null ||
                transpilerMethod == null ||
                finalizerMethod == null ||
                _trackedMissilesField == null ||
                _trackedMissileField == null ||
                _awaitingCollisionReactionField == null ||
                _getPositionMethod == null ||
                _getVelocityMethod == null ||
                _setVelocityMethod == null)
                return;

            try
            {
                harmony.Patch(
                    tickMethod,
                    prefix: new HarmonyMethod(prefixMethod) { priority = Priority.High },
                    transpiler: new HarmonyMethod(transpilerMethod) { priority = Priority.First },
                    finalizer: new HarmonyMethod(finalizerMethod) { priority = Priority.Last });
            }
            catch
            {
                // Leave the verified core untouched if its exact locked method shape changes.
            }
        }

        private static void TickPrefix(object __instance, out HashSet<MBMissile> __state)
        {
            __state = _quarantinedMissiles;
            _quarantinedMissiles = null;
            if (__instance == null) return;

            try
            {
                IList tracked = _trackedMissilesField.GetValue(__instance) as IList;
                if (tracked == null || tracked.Count == 0) return;

                HashSet<MBMissile> quarantined = null;
                for (int i = 0; i < tracked.Count; i++)
                {
                    object entry = tracked[i];
                    if (entry == null || !(bool)_awaitingCollisionReactionField.GetValue(entry))
                        continue;

                    MBMissile missile = _trackedMissileField.GetValue(entry) as MBMissile;
                    if (missile == null) continue;

                    if (quarantined == null)
                        quarantined = new HashSet<MBMissile>(MissileReferenceComparer.Instance);
                    quarantined.Add(missile);
                }

                _quarantinedMissiles = quarantined;
            }
            catch
            {
                _quarantinedMissiles = null;
            }
        }

        private static Exception TickFinalizer(Exception __exception, HashSet<MBMissile> __state)
        {
            _quarantinedMissiles = __state;
            return __exception;
        }

        private static IEnumerable<CodeInstruction> TickTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int positionReads = 0;
            int velocityReads = 0;
            int velocityWrites = 0;

            for (int i = 0; i < code.Count; i++)
            {
                if (Calls(code[i], _getPositionMethod)) positionReads++;
                else if (Calls(code[i], _getVelocityMethod)) velocityReads++;
                else if (Calls(code[i], _setVelocityMethod)) velocityWrites++;
            }

            if (positionReads != ExpectedPositionReads ||
                velocityReads != ExpectedVelocityReads ||
                velocityWrites != ExpectedVelocityWrites)
                return code;

            MethodInfo safeGetPosition = AccessTools.Method(
                typeof(AwaitingCollisionMissileGuardPatch),
                nameof(SafeGetPosition));
            MethodInfo safeGetVelocity = AccessTools.Method(
                typeof(AwaitingCollisionMissileGuardPatch),
                nameof(SafeGetVelocity));
            MethodInfo safeSetVelocity = AccessTools.Method(
                typeof(AwaitingCollisionMissileGuardPatch),
                nameof(SafeSetVelocity));
            if (safeGetPosition == null || safeGetVelocity == null || safeSetVelocity == null)
                return code;

            for (int i = 0; i < code.Count; i++)
            {
                MethodInfo replacement = null;
                if (Calls(code[i], _getPositionMethod)) replacement = safeGetPosition;
                else if (Calls(code[i], _getVelocityMethod)) replacement = safeGetVelocity;
                else if (Calls(code[i], _setVelocityMethod)) replacement = safeSetVelocity;
                if (replacement == null) continue;

                CodeInstruction guardedCall = new CodeInstruction(OpCodes.Call, replacement);
                guardedCall.labels.AddRange(code[i].labels);
                guardedCall.blocks.AddRange(code[i].blocks);
                code[i] = guardedCall;
            }

            return code;
        }

        private static Vec3 SafeGetPosition(MBMissile missile)
        {
            return IsQuarantined(missile) ? Vec3.Zero : missile.GetPosition();
        }

        private static Vec3 SafeGetVelocity(MBMissile missile)
        {
            if (!IsQuarantined(missile))
                return missile.GetVelocity();

            // The locked core validates velocity length before it checks AwaitingCollisionReaction.
            // A zero vector would bypass the pending-collision branch and make the guided swarm look
            // inactive. Use a finite non-zero managed sentinel solely to reach that existing check.
            Vec3 sentinel = Vec3.Zero;
            sentinel.y = 1f;
            return sentinel;
        }

        private static void SafeSetVelocity(MBMissile missile, ref Vec3 velocity)
        {
            if (!IsQuarantined(missile))
                missile.SetVelocity(ref velocity);
        }

        private static bool IsQuarantined(MBMissile missile)
        {
            return missile == null ||
                   (_quarantinedMissiles != null && _quarantinedMissiles.Contains(missile));
        }

        private static bool Calls(CodeInstruction instruction, MethodInfo method)
        {
            if (instruction == null || method == null) return false;
            if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
                return false;
            return Equals(instruction.operand, method);
        }
    }
}
