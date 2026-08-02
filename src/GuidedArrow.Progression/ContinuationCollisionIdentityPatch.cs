using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Binds delayed collision work to the exact TrackedMissile object that owned the hit.
    /// Bannerlord aggressively recycles integer missile indices; an old Stick/BecomeInvisible
    /// reaction must never resolve against a newer synthetic missile that happens to reuse the
    /// same index. It also keeps camera ownership coherent while a valid replacement is pending.
    /// </summary>
    internal static class ContinuationCollisionIdentityPatch
    {
        private sealed class OwnerTag
        {
            internal object Owner;
            internal int MissileIndex = -1;
        }

        private sealed class Marker
        {
        }

        private sealed class HitScope
        {
            internal int MissileIndex = -1;
            internal bool HasVictim;
            internal bool HitShield;
        }

        private sealed class HitPatchState
        {
            internal HitScope Previous;
        }

        private static readonly ConditionalWeakTable<object, OwnerTag> ContextOwners =
            new ConditionalWeakTable<object, OwnerTag>();
        private static readonly ConditionalWeakTable<object, OwnerTag> EarlyReactionOwners =
            new ConditionalWeakTable<object, OwnerTag>();
        private static readonly ConditionalWeakTable<object, Marker> QueuedContinuationSources =
            new ConditionalWeakTable<object, Marker>();

        [ThreadStatic]
        private static HitScope _activeHit;

        [ThreadStatic]
        private static int _terminalSpawnDepth;

        private static FieldInfo _pendingCollisionContextsField;
        private static FieldInfo _earlyCollisionReactionsField;
        private static FieldInfo _pendingContinuationSpawnsField;
        private static FieldInfo _contextMissileIndexField;
        private static PropertyInfo _collisionMissileIndexProperty;
        private static PropertyInfo _collisionShieldProperty;
        private static MethodInfo _findTrackedMissileMethod;
        private static MethodInfo _hasRemainingAgentPenetrationMethod;
        private static MethodInfo _logMethod;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            Type contextType = behaviorType.GetNestedType(
                "PendingCollisionContext",
                BindingFlags.NonPublic);
            Type trackedType = behaviorType.GetNestedType(
                "TrackedMissile",
                BindingFlags.NonPublic);
            if (contextType == null || trackedType == null) return;

            _pendingCollisionContextsField = AccessTools.Field(
                behaviorType,
                "_pendingCollisionContexts");
            _earlyCollisionReactionsField = AccessTools.Field(
                behaviorType,
                "_earlyCollisionReactions");
            _pendingContinuationSpawnsField = AccessTools.Field(
                behaviorType,
                "_pendingContinuationSpawns");
            _contextMissileIndexField = AccessTools.Field(
                contextType,
                "MissileIndex");
            _collisionMissileIndexProperty = typeof(AttackCollisionData).GetProperty(
                "AffectorWeaponSlotOrMissileIndex",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _collisionShieldProperty = typeof(AttackCollisionData).GetProperty(
                "AttackBlockedWithShield",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _findTrackedMissileMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "FindTrackedMissile" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));
            _hasRemainingAgentPenetrationMethod = behaviorType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "HasRemainingAgentPenetration" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == trackedType);
            _logMethod = AccessTools.Method(
                behaviorType,
                "Log",
                new[] { typeof(string) });

            MethodInfo queueContextMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "QueuePendingCollisionContext" &&
                    method.GetParameters().Length == 6);
            MethodInfo onMissileHitMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "OnMissileHit" &&
                    !method.IsAbstract &&
                    method.GetParameters().Any(parameter =>
                        parameter.ParameterType == typeof(AttackCollisionData) ||
                        parameter.ParameterType == typeof(AttackCollisionData).MakeByRefType()));
            MethodInfo consumeMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TryConsumeEarlyCollisionReaction" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));
            MethodInfo resolveMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "ResolveCollisionReaction" &&
                    method.GetParameters().Length == 3);
            MethodInfo queueContinuationMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "QueuePenetrationContinuation" &&
                    method.GetParameters().Length == 2);
            MethodInfo spawnContinuationMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "TrySpawnPenetrationContinuation" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 3);
            MethodInfo suspendCameraMethod = AccessTools.Method(
                behaviorType,
                "SuspendProjectileCameraForCollisionReaction",
                new[] { typeof(int) });

            if (_pendingCollisionContextsField == null ||
                _earlyCollisionReactionsField == null ||
                _pendingContinuationSpawnsField == null ||
                _contextMissileIndexField == null ||
                _collisionMissileIndexProperty == null ||
                _collisionShieldProperty == null ||
                _findTrackedMissileMethod == null ||
                _hasRemainingAgentPenetrationMethod == null ||
                queueContextMethod == null ||
                onMissileHitMethod == null ||
                consumeMethod == null ||
                resolveMethod == null ||
                queueContinuationMethod == null ||
                spawnContinuationMethod == null ||
                suspendCameraMethod == null)
                return;

            try
            {
                harmony.Patch(
                    queueContextMethod,
                    postfix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(QueueContextPostfix)))
                    {
                        priority = Priority.Last
                    });

                harmony.Patch(
                    onMissileHitMethod,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(OnMissileHitPrefix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(OnMissileHitFinalizer)))
                    {
                        priority = Priority.Last
                    });

                harmony.Patch(
                    consumeMethod,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(ConsumePrefix)))
                    {
                        priority = int.MaxValue
                    });

                harmony.Patch(
                    resolveMethod,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(ResolvePrefix)))
                    {
                        priority = int.MaxValue
                    });

                harmony.Patch(
                    queueContinuationMethod,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(QueueContinuationPrefix)))
                    {
                        priority = int.MaxValue
                    });

                harmony.Patch(
                    spawnContinuationMethod,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(SpawnPrefix)))
                    {
                        priority = int.MaxValue
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(SpawnFinalizer)))
                    {
                        priority = int.MinValue
                    });

                harmony.Patch(
                    suspendCameraMethod,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(ContinuationCollisionIdentityPatch),
                            nameof(SuspendCameraPrefix)))
                    {
                        priority = int.MaxValue
                    });
            }
            catch
            {
                _activeHit = null;
                _terminalSpawnDepth = 0;
            }
        }

        private static void QueueContextPostfix(
            object __instance,
            int __0)
        {
            if (__instance == null || __0 < 0) return;

            try
            {
                object owner = FindTracked(__instance, __0);
                if (owner == null) return;

                IList contexts = _pendingCollisionContextsField.GetValue(__instance) as IList;
                if (contexts == null) return;

                for (int i = contexts.Count - 1; i >= 0; i--)
                {
                    object context = contexts[i];
                    if (context == null ||
                        Convert.ToInt32(_contextMissileIndexField.GetValue(context)) != __0)
                        continue;

                    SetOwner(ContextOwners, context, owner, __0);
                    return;
                }
            }
            catch
            {
                // An untagged context remains on the core's legacy correlation path.
            }
        }

        private static void OnMissileHitPrefix(
            object __instance,
            object[] __args,
            MethodBase __originalMethod,
            out HitPatchState __state)
        {
            __state = new HitPatchState { Previous = _activeHit };
            HitScope current = new HitScope();

            try
            {
                object collisionData = FindCollisionData(__args);
                if (collisionData != null)
                {
                    current.MissileIndex = Convert.ToInt32(
                        _collisionMissileIndexProperty.GetValue(collisionData, null));
                    current.HitShield = Convert.ToBoolean(
                        _collisionShieldProperty.GetValue(collisionData, null));
                }

                current.HasVictim = FindVictim(__args, __originalMethod) != null;
                _activeHit = current;

                if (__instance == null || current.MissileIndex < 0)
                    return;

                object owner = FindTracked(__instance, current.MissileIndex);
                if (owner == null) return;

                IList reactions = _earlyCollisionReactionsField.GetValue(__instance) as IList;
                if (reactions == null) return;

                for (int i = reactions.Count - 1; i >= 0; i--)
                {
                    object reaction = reactions[i];
                    if (reaction == null ||
                        EarlyReactionOwners.TryGetValue(reaction, out _))
                        continue;

                    SetOwner(
                        EarlyReactionOwners,
                        reaction,
                        owner,
                        current.MissileIndex);
                    return;
                }
            }
            catch
            {
                _activeHit = current;
            }
        }

        private static Exception OnMissileHitFinalizer(
            Exception __exception,
            HitPatchState __state)
        {
            _activeHit = __state?.Previous;
            return __exception;
        }

        private static void ConsumePrefix(
            object __instance,
            int missileIndex)
        {
            if (__instance == null || missileIndex < 0) return;

            try
            {
                object currentOwner = FindTracked(__instance, missileIndex);
                IList reactions = _earlyCollisionReactionsField.GetValue(__instance) as IList;
                if (reactions == null) return;

                int discarded = 0;
                for (int i = reactions.Count - 1; i >= 0; i--)
                {
                    object reaction = reactions[i];
                    if (reaction == null ||
                        !EarlyReactionOwners.TryGetValue(reaction, out OwnerTag tag) ||
                        tag == null ||
                        tag.MissileIndex != missileIndex ||
                        ReferenceEquals(tag.Owner, currentOwner))
                        continue;

                    reactions.RemoveAt(i);
                    discarded++;
                }

                if (discarded > 0)
                {
                    TryLog(
                        __instance,
                        "Discarded stale early collision reaction(s) for recycled missile index #" +
                        missileIndex + ".");
                }
            }
            catch
            {
                // The exact pending-context owner check remains the final gate.
            }
        }

        private static bool ResolvePrefix(
            object __instance,
            int missileIndex)
        {
            if (__instance == null || missileIndex < 0) return true;

            try
            {
                IList contexts = _pendingCollisionContextsField.GetValue(__instance) as IList;
                if (contexts == null) return true;

                object context = null;
                int contextPosition = -1;
                for (int i = 0; i < contexts.Count; i++)
                {
                    object candidate = contexts[i];
                    if (candidate == null ||
                        Convert.ToInt32(_contextMissileIndexField.GetValue(candidate)) != missileIndex)
                        continue;

                    context = candidate;
                    contextPosition = i;
                    break;
                }

                if (context == null ||
                    !ContextOwners.TryGetValue(context, out OwnerTag tag) ||
                    tag == null ||
                    tag.Owner == null)
                    return true;

                object currentOwner = FindTracked(__instance, missileIndex);
                if (ReferenceEquals(currentOwner, tag.Owner))
                    return true;

                if (contextPosition >= 0 && contextPosition < contexts.Count &&
                    ReferenceEquals(contexts[contextPosition], context))
                {
                    contexts.RemoveAt(contextPosition);
                }
                else
                {
                    contexts.Remove(context);
                }

                TryLog(
                    __instance,
                    "Discarded stale collision reaction for recycled missile index #" +
                    missileIndex + ".");
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool QueueContinuationPrefix(
            object __instance,
            object __0)
        {
            if (__0 == null) return true;

            try
            {
                if (QueuedContinuationSources.TryGetValue(__0, out _))
                {
                    TryLog(
                        __instance,
                        "Ignored duplicate continuation request for the same tracked missile.");
                    return false;
                }

                QueuedContinuationSources.Add(__0, new Marker());
            }
            catch
            {
                // Prefer the core's request if the weak-table operation itself fails.
            }

            return true;
        }

        private static void SpawnPrefix(out bool __state)
        {
            __state = true;
            if (_terminalSpawnDepth < int.MaxValue)
                _terminalSpawnDepth++;
        }

        private static Exception SpawnFinalizer(
            Exception __exception,
            bool __state)
        {
            if (__state && _terminalSpawnDepth > 0)
                _terminalSpawnDepth--;
            return __exception;
        }

        private static bool SuspendCameraPrefix(
            object __instance,
            int missileIndex)
        {
            if (__instance == null || missileIndex < 0)
                return true;

            try
            {
                if (ReadCount(_pendingContinuationSpawnsField, __instance) > 0)
                    return false;

                HitScope hit = _activeHit;
                if (hit == null ||
                    hit.MissileIndex != missileIndex ||
                    !hit.HasVictim ||
                    hit.HitShield)
                    return true;

                object tracked = FindTracked(__instance, missileIndex);
                if (tracked == null)
                    return _terminalSpawnDepth <= 0;

                object remaining = _hasRemainingAgentPenetrationMethod.Invoke(
                    null,
                    new[] { tracked });
                if (!(remaining is bool canContinue) || !canContinue)
                    return true;

                if (NativeVolleyPenetrationIsolationPatch
                    .ShouldBlockSyntheticContinuation(__instance, tracked))
                    return true;

                return false;
            }
            catch
            {
                return true;
            }
        }

        private static object FindCollisionData(object[] args)
        {
            if (args == null) return null;

            for (int i = 0; i < args.Length; i++)
            {
                object value = args[i];
                if (value != null && value.GetType() == typeof(AttackCollisionData))
                    return value;
            }

            return null;
        }

        private static Agent FindVictim(
            object[] args,
            MethodBase originalMethod)
        {
            if (args == null) return null;

            try
            {
                ParameterInfo[] parameters = originalMethod?.GetParameters();
                if (parameters != null)
                {
                    for (int i = 0; i < parameters.Length && i < args.Length; i++)
                    {
                        string name = parameters[i].Name ?? string.Empty;
                        if (args[i] is Agent agent &&
                            name.IndexOf(
                                "victim",
                                StringComparison.OrdinalIgnoreCase) >= 0)
                            return agent;
                    }
                }
            }
            catch
            {
            }

            int seenAgents = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (!(args[i] is Agent agent)) continue;
                seenAgents++;
                if (seenAgents == 2) return agent;
            }

            return null;
        }

        private static object FindTracked(
            object instance,
            int missileIndex)
        {
            if (instance == null || missileIndex < 0) return null;

            try
            {
                return _findTrackedMissileMethod.Invoke(
                    instance,
                    new object[] { missileIndex });
            }
            catch
            {
                return null;
            }
        }

        private static int ReadCount(
            FieldInfo field,
            object instance)
        {
            if (field == null || instance == null) return -1;

            try
            {
                object value = field.GetValue(instance);
                if (value is ICollection collection) return collection.Count;

                PropertyInfo countProperty = value?.GetType().GetProperty(
                    "Count",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object count = countProperty?.GetValue(value, null);
                return count is int integer ? integer : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static void SetOwner(
            ConditionalWeakTable<object, OwnerTag> table,
            object key,
            object owner,
            int missileIndex)
        {
            if (table == null || key == null || owner == null) return;

            try { table.Remove(key); }
            catch { }

            table.Add(
                key,
                new OwnerTag
                {
                    Owner = owner,
                    MissileIndex = missileIndex
                });
        }

        private static void TryLog(
            object instance,
            string message)
        {
            if (_logMethod == null ||
                instance == null ||
                string.IsNullOrEmpty(message))
                return;

            try { _logMethod.Invoke(instance, new object[] { message }); }
            catch { }
        }
    }
}
