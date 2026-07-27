using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Applies a narrow runtime correction around the verified v1.1.17 core's
    /// synthetic penetration continuation without rebuilding the core assembly.
    /// </summary>
    internal static class PenetrationContinuationSafetyPatch
    {
        private const float CoreContinuationOffset = 0.42f;

        private static FieldInfo _impactPositionField;
        private static FieldInfo _impactVelocityField;
        private static FieldInfo _impactDirectionField;
        private static FieldInfo _victimField;
        private static FieldInfo _missileField;

        private sealed class PatchState
        {
            internal object Context;
            internal Vec3 OriginalImpactPosition;
            internal Agent Victim;
            internal bool ImpactPositionAdjusted;
        }

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            MethodInfo method = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                    m.Name == "TrySpawnPenetrationContinuation" &&
                    m.ReturnType == typeof(bool) &&
                    m.GetParameters().Length == 3);
            if (method == null) return;

            ParameterInfo[] parameters = method.GetParameters();
            Type trackedType = parameters[0].ParameterType;
            Type contextType = parameters[1].ParameterType;

            _impactPositionField = AccessTools.Field(contextType, "ImpactPosition");
            _impactVelocityField = AccessTools.Field(contextType, "ImpactVelocity");
            _impactDirectionField = AccessTools.Field(contextType, "ImpactDirection");
            _victimField = AccessTools.Field(contextType, "Victim");
            _missileField = AccessTools.Field(trackedType, "Missile");

            if (_impactPositionField == null ||
                _impactVelocityField == null ||
                _impactDirectionField == null ||
                _victimField == null ||
                _missileField == null)
                return;

            try
            {
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PenetrationContinuationSafetyPatch), nameof(Prefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(PenetrationContinuationSafetyPatch), nameof(Postfix))));
            }
            catch
            {
                // The stable core remains untouched if a future version changes its internals.
            }
        }

        private static bool Prefix(object[] __args, ref bool __result, out PatchState __state)
        {
            __state = null;
            if (__args == null || __args.Length < 2 || __args[1] == null)
            {
                __result = false;
                return false;
            }

            object context = __args[1];
            try
            {
                Vec3 impactPosition = (Vec3)_impactPositionField.GetValue(context);
                Vec3 impactVelocity = (Vec3)_impactVelocityField.GetValue(context);
                Vec3 impactDirection = (Vec3)_impactDirectionField.GetValue(context);
                Agent victim = _victimField.GetValue(context) as Agent;

                Vec3 direction = impactVelocity;
                float speed = direction.Length;
                if (!IsFinite(speed) || speed <= 0.001f)
                {
                    direction = impactDirection;
                    speed = direction.Length;
                }
                if (!IsFinite(speed) || speed <= 0.001f)
                {
                    __result = false;
                    return false;
                }

                direction /= speed;
                if (!IsFinite(direction))
                {
                    __result = false;
                    return false;
                }

                float desiredExitDistance = 1.25f;
                if (victim != null)
                {
                    Vec3 victimDelta = victim.Position - impactPosition;
                    float centreDepth =
                        victimDelta.x * direction.x +
                        victimDelta.y * direction.y +
                        victimDelta.z * direction.z;
                    if (IsFinite(centreDepth))
                        desiredExitDistance = Clamp(centreDepth + 0.95f, 1f, 2.5f);
                }

                // The stable core already advances 0.42 m. Shift only the remainder,
                // leaving its existing continuation logic and damage packet untouched.
                float additionalOffset = Math.Max(0f, desiredExitDistance - CoreContinuationOffset);
                _impactPositionField.SetValue(context, impactPosition + direction * additionalOffset);

                __state = new PatchState
                {
                    Context = context,
                    OriginalImpactPosition = impactPosition,
                    Victim = victim,
                    ImpactPositionAdjusted = true
                };
                return true;
            }
            catch
            {
                // Do not let the uncorrected continuation spawn inside an agent.
                __state = null;
                __result = false;
                return false;
            }
        }

        private static void Postfix(object[] __args, bool __result, PatchState __state)
        {
            if (__state != null && __state.ImpactPositionAdjusted && __state.Context != null)
            {
                try { _impactPositionField.SetValue(__state.Context, __state.OriginalImpactPosition); }
                catch { }
            }

            if (!__result ||
                __args == null ||
                __args.Length < 3 ||
                __args[2] == null ||
                __state?.Victim == null)
                return;

            try
            {
                object missile = _missileField.GetValue(__args[2]);
                if (missile == null) return;

                object visuals = __state.Victim.AgentVisuals;
                if (visuals == null) return;

                MethodInfo getEntity = AccessTools.Method(visuals.GetType(), "GetEntity");
                object victimEntity = getEntity?.Invoke(visuals, null);
                if (victimEntity == null) return;

                MethodInfo passThrough = missile
                    .GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "PassThroughEntity" && m.GetParameters().Length == 1);
                passThrough?.Invoke(missile, new[] { victimEntity });
            }
            catch
            {
                // The continuation already spawned beyond the victim; this is secondary protection.
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vec3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
