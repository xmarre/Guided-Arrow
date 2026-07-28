using System;
using System.Collections;
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
    /// Replays the complete impact handler outside Bannerlord's native collision callback while
    /// preventing a removal callback from one shot generation from touching a later shot that reused
    /// the same native missile index.
    /// </summary>
    internal static class GenerationScopedImpactReplayPatch
    {
        private sealed class PendingImpact
        {
            internal object[] Arguments;
            internal Agent Shooter;
            internal Agent Victim;
            internal int Generation;
            internal int MissileIndex;
            internal int VictimKey;
            internal Vec3 ShotOrigin;
            internal AttackCollisionData Collision;
            internal bool HasCollision;
            internal bool Fatal;
        }

        private sealed class PendingRemoval
        {
            internal MethodInfo Method;
            internal object[] Arguments;
            internal int MissileIndex;
            internal int Generation;
            internal Agent Shooter;
            internal object IdentityEntity;
        }

        private sealed class ReplayQueue
        {
            internal readonly List<PendingImpact> Impacts = new List<PendingImpact>();
            internal readonly List<PendingRemoval> Removals = new List<PendingRemoval>();
            internal int Generation;
            internal Agent Shooter;
            internal bool Flushing;
        }

        private static readonly ConditionalWeakTable<object, ReplayQueue> Pending =
            new ConditionalWeakTable<object, ReplayQueue>();

        private static MethodInfo _impactMethod;
        private static MethodInfo _exactLiveRemovalLookup;
        private static Type _nativeRemovalType;
        private static FieldInfo _nativeRemovalIndexField;
        private static FieldInfo _nativeRemovalIdentityField;
        private static FieldInfo _nativeRemovalShooterField;
        private static FieldInfo _nativeRemovalGenerationField;

        private static FieldInfo _activeGenerationField;
        private static FieldInfo _activeShooterField;
        private static FieldInfo _shotOriginField;
        private static FieldInfo _trackedMissilesField;
        private static FieldInfo _trackedIndexField;
        private static FieldInfo _trackedGenerationField;
        private static FieldInfo _trackedShooterField;
        private static FieldInfo _trackedIdentityField;
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

            Type trackedType = behaviorType.GetNestedType("TrackedMissile", BindingFlags.NonPublic);
            _nativeRemovalType = behaviorType.GetNestedType("PendingNativeMissileRemoval", BindingFlags.NonPublic);
            if (trackedType == null || _nativeRemovalType == null) return;

            _activeGenerationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _activeShooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _shotOriginField = AccessTools.Field(behaviorType, "_pendingShotPosition");
            _trackedMissilesField = AccessTools.Field(behaviorType, "_trackedMissiles");
            _trackedIndexField = AccessTools.Field(trackedType, "Index");
            _trackedGenerationField = AccessTools.Field(trackedType, "ShotGeneration");
            _trackedShooterField = AccessTools.Field(trackedType, "OriginalShooter");
            _trackedIdentityField = AccessTools.Field(trackedType, "IdentityEntity");
            _autoguidanceRuntimeMethod = AccessTools.Method(behaviorType, "IsAutoguidanceRuntimeActive");

            _nativeRemovalIndexField = AccessTools.Field(_nativeRemovalType, "Index");
            _nativeRemovalIdentityField = AccessTools.Field(_nativeRemovalType, "IdentityEntity");
            _nativeRemovalShooterField = AccessTools.Field(_nativeRemovalType, "Shooter");
            _nativeRemovalGenerationField = AccessTools.Field(_nativeRemovalType, "ShotGeneration");
            _exactLiveRemovalLookup = AccessTools.Method(
                behaviorType,
                "FindExactLiveMissileForDeferredRemoval",
                new[] { _nativeRemovalType });

            if (_activeGenerationField == null ||
                _activeShooterField == null ||
                _shotOriginField == null ||
                _trackedMissilesField == null ||
                _trackedIndexField == null ||
                _trackedGenerationField == null ||
                _trackedShooterField == null ||
                _trackedIdentityField == null ||
                _nativeRemovalIndexField == null ||
                _nativeRemovalIdentityField == null ||
                _nativeRemovalShooterField == null ||
                _nativeRemovalGenerationField == null ||
                _exactLiveRemovalLookup == null)
                return;

            MethodInfo impactPrefix = AccessTools.Method(
                typeof(GenerationScopedImpactReplayPatch),
                nameof(ImpactPrefix));
            MethodInfo removalPrefix = AccessTools.Method(
                typeof(GenerationScopedImpactReplayPatch),
                nameof(RemovalPrefix));
            MethodInfo displayPrefix = AccessTools.Method(
                typeof(GenerationScopedImpactReplayPatch),
                nameof(DisplayPrefix));
            MethodInfo startShotPrefix = AccessTools.Method(
                typeof(GenerationScopedImpactReplayPatch),
                nameof(StartShotPrefix));
            MethodInfo resetPostfix = AccessTools.Method(
                typeof(GenerationScopedImpactReplayPatch),
                nameof(ResetPostfix));
            if (impactPrefix == null ||
                removalPrefix == null ||
                displayPrefix == null ||
                startShotPrefix == null ||
                resetPostfix == null)
                return;

            try
            {
                harmony.Patch(
                    _impactMethod,
                    prefix: new HarmonyMethod(impactPrefix) { priority = int.MaxValue });
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
                .Where(candidate => candidate.Name == "StartGuidedShot" && !candidate.IsAbstract))
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(startShotPrefix) { priority = int.MaxValue });
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

            DisableDuplicateProgressionCapture(harmony);
        }

        private static bool ImpactPrefix(object __instance, object[] __args)
        {
            if (_replayingImpact) return true;
            if (__instance == null || __args == null) return false;

            try
            {
                PendingImpact impact = CaptureImpact(__instance, (object[])__args.Clone());
                ReplayQueue queue = Pending.GetOrCreateValue(__instance);

                if (queue.Generation != impact.Generation ||
                    !ReferenceEquals(queue.Shooter, impact.Shooter))
                {
                    queue.Impacts.Clear();
                    queue.Removals.Clear();
                    queue.Generation = impact.Generation;
                    queue.Shooter = impact.Shooter;
                }

                queue.Impacts.Add(impact);
            }
            catch
            {
                // Never fall back into the synchronous impact body.
            }

            return false;
        }

        private static bool RemovalPrefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (_replayingRemoval) return true;
            if (__instance == null || __originalMethod == null || __args == null) return true;

            try
            {
                if (!Pending.TryGetValue(__instance, out ReplayQueue queue) ||
                    queue == null ||
                    queue.Impacts.Count == 0)
                    return true;

                int index = ReadFirstInt(__args);
                object tracked = FindTracked(__instance, index);
                if (tracked == null)
                    return false;

                int generation = (int)_trackedGenerationField.GetValue(tracked);
                Agent shooter = _trackedShooterField.GetValue(tracked) as Agent;
                if (generation != queue.Generation || !ReferenceEquals(shooter, queue.Shooter))
                    return false;

                queue.Removals.Add(new PendingRemoval
                {
                    Method = __originalMethod as MethodInfo,
                    Arguments = (object[])__args.Clone(),
                    MissileIndex = index,
                    Generation = generation,
                    Shooter = shooter,
                    IdentityEntity = _trackedIdentityField.GetValue(tracked)
                });
                return false;
            }
            catch
            {
                // During a pending impact, suppress an unverified removal rather than allowing an old
                // native index to remove a later shot's tracked projectile.
                return false;
            }
        }

        private static void DisplayPrefix(object __instance)
        {
            if (__instance == null ||
                !Pending.TryGetValue(__instance, out ReplayQueue queue) ||
                queue == null ||
                queue.Flushing)
                return;

            int activeGeneration = ReadActiveGeneration(__instance);
            Agent activeShooter = ReadActiveShooter(__instance);
            if (queue.Generation != activeGeneration || !ReferenceEquals(queue.Shooter, activeShooter))
            {
                Pending.Remove(__instance);
                return;
            }

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
                    if (impact?.Arguments == null ||
                        impact.Generation != activeGeneration ||
                        !ReferenceEquals(impact.Shooter, activeShooter))
                        continue;

                    try
                    {
                        _replayingImpact = true;
                        _impactMethod.Invoke(__instance, impact.Arguments);
                    }
                    catch { }
                    finally
                    {
                        _replayingImpact = false;
                    }

                    AwardProgression(__instance, impact);
                }

                for (int i = 0; i < removals.Length; i++)
                {
                    PendingRemoval removal = removals[i];
                    if (removal?.Method == null || removal.Arguments == null) continue;
                    if (removal.Generation != activeGeneration ||
                        !ReferenceEquals(removal.Shooter, activeShooter))
                        continue;

                    object tracked = FindTracked(__instance, removal.MissileIndex);
                    if (tracked == null) continue;
                    if ((int)_trackedGenerationField.GetValue(tracked) != removal.Generation ||
                        !ReferenceEquals(_trackedShooterField.GetValue(tracked) as Agent, removal.Shooter))
                        continue;

                    // A live missile with the captured identity means this was a delayed removal for
                    // an older object that reused the same integer index. Never apply it to the current
                    // shot. If no exact live missile remains, replay the managed removal callback.
                    if (HasExactLiveMissile(__instance, removal))
                        continue;

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
                Generation = ReadActiveGeneration(instance),
                Shooter = ReadActiveShooter(instance),
                VictimKey = -1,
                MissileIndex = -1
            };

            Agent[] agents = arguments.OfType<Agent>().Where(agent => agent != null).ToArray();
            if (impact.Shooter == null && agents.Length > 0) impact.Shooter = agents[0];
            if (agents.Length > 1) impact.Victim = agents[1];

            for (int i = 0; i < arguments.Length; i++)
            {
                if (!(arguments[i] is AttackCollisionData collision)) continue;
                impact.Collision = collision;
                impact.HasCollision = true;
                impact.Fatal = ConcentratedImpactSafetyPatch.ReadCollisionFatalDamage(collision);
                impact.MissileIndex = collision.AffectorWeaponSlotOrMissileIndex;
                break;
            }

            if (impact.Victim != null)
                impact.VictimKey = RuntimeHelpers.GetHashCode(impact.Victim);

            try { impact.ShotOrigin = (Vec3)_shotOriginField.GetValue(instance); }
            catch { }

            return impact;
        }

        private static object FindTracked(object instance, int index)
        {
            if (instance == null || index < 0) return null;

            try
            {
                IList tracked = _trackedMissilesField.GetValue(instance) as IList;
                if (tracked == null) return null;

                for (int i = 0; i < tracked.Count; i++)
                {
                    object candidate = tracked[i];
                    if (candidate == null) continue;
                    if ((int)_trackedIndexField.GetValue(candidate) == index)
                        return candidate;
                }
            }
            catch { }

            return null;
        }

        private static bool HasExactLiveMissile(object instance, PendingRemoval removal)
        {
            try
            {
                object probe = Activator.CreateInstance(_nativeRemovalType, true);
                _nativeRemovalIndexField.SetValue(probe, removal.MissileIndex);
                _nativeRemovalIdentityField.SetValue(probe, removal.IdentityEntity);
                _nativeRemovalShooterField.SetValue(probe, removal.Shooter);
                _nativeRemovalGenerationField.SetValue(probe, removal.Generation);
                return _exactLiveRemovalLookup.Invoke(instance, new[] { probe }) != null;
            }
            catch
            {
                // Failure to prove identity is not permission to touch a possibly reused index.
                return true;
            }
        }

        private static int ReadActiveGeneration(object instance)
        {
            try { return (int)_activeGenerationField.GetValue(instance); }
            catch { return 0; }
        }

        private static Agent ReadActiveShooter(object instance)
        {
            try { return _activeShooterField.GetValue(instance) as Agent; }
            catch { return null; }
        }

        private static int ReadFirstInt(object[] arguments)
        {
            if (arguments == null) return -1;
            for (int i = 0; i < arguments.Length; i++)
                if (arguments[i] is int value) return value;
            return -1;
        }

        private static void StartShotPrefix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
            _replayingImpact = false;
            _replayingRemoval = false;
        }

        private static void ResetPostfix(object __instance)
        {
            if (__instance != null)
                Pending.Remove(__instance);
            _replayingImpact = false;
            _replayingRemoval = false;
        }

        private static void AwardProgression(object instance, PendingImpact impact)
        {
            if (instance == null ||
                impact == null ||
                !impact.HasCollision ||
                !impact.Collision.IsMissile ||
                impact.Generation <= 0 ||
                impact.VictimKey < 0)
                return;

            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null || !progression.Enabled) return;

            try
            {
                if (!ReferenceEquals(impact.Shooter, Agent.Main) ||
                    impact.Victim == null ||
                    impact.Victim.Team == null ||
                    impact.Shooter.Team == null ||
                    !impact.Victim.Team.IsEnemyOf(impact.Shooter.Team))
                    return;

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

        private static void DisableDuplicateProgressionCapture(Harmony harmony)
        {
            try
            {
                MethodInfo noOpPrefix = AccessTools.Method(
                    typeof(GenerationScopedImpactReplayPatch),
                    nameof(SkipOriginal));
                MethodInfo capture = AccessTools.Method(
                    typeof(ProgressionImpactDeferralPatch),
                    "CapturePostfix");
                MethodInfo display = AccessTools.Method(
                    typeof(ProgressionImpactDeferralPatch),
                    "DisplayPrefix");
                if (noOpPrefix == null) return;

                if (capture != null)
                    harmony.Patch(capture, prefix: new HarmonyMethod(noOpPrefix) { priority = int.MaxValue });
                if (display != null)
                    harmony.Patch(display, prefix: new HarmonyMethod(noOpPrefix) { priority = int.MaxValue });
            }
            catch { }
        }

        private static bool SkipOriginal()
        {
            return false;
        }
    }
}
