using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GuidedArrow.Progression
{
    /// <summary>
    /// Keeps victim tracking, cinematic Agent/ragdoll sampling and camera work out of
    /// GuidedArrowBehavior.OnMissileHit.
    ///
    /// The locked core's TrackHitVictim subscribes to Agent.OnAgentHealthChanged while Bannerlord is
    /// still resolving the native missile impact. It also immediately samples victim bones through
    /// TrackCinematicSubject. Both operations are deferred to the next display tick. Confirmed-kill
    /// handling is replayed immediately afterward, where the original live-ragdoll cinematic path is
    /// safe to run and remains capable of following the moving corpse.
    /// </summary>
    internal static class ImpactCinematicDeferralPatch
    {
        private sealed class PendingVictimTrack
        {
            internal Agent Victim;
            internal int MissileIndex;
            internal int CollisionBoneIndex;
            internal GameEntity ArrowEntity;
            internal Vec3 ImpactDirection;
            internal Vec3 ImpactPosition;
        }

        private sealed class PendingVictimTrackQueue
        {
            internal readonly List<PendingVictimTrack> Items = new List<PendingVictimTrack>();
        }

        private sealed class PendingKill
        {
            internal Agent Victim;
            internal string Reason;
        }

        private sealed class PendingKillQueue
        {
            internal readonly List<PendingKill> Items = new List<PendingKill>();
        }

        private static readonly ConditionalWeakTable<object, PendingVictimTrackQueue> PendingVictimTracks =
            new ConditionalWeakTable<object, PendingVictimTrackQueue>();
        private static readonly ConditionalWeakTable<object, PendingKillQueue> PendingKills =
            new ConditionalWeakTable<object, PendingKillQueue>();

        private static Type _cinematicSubjectType;
        private static FieldInfo _cinematicSubjectsField;
        private static FieldInfo _subjectAgentField;
        private static FieldInfo _subjectLastKnownPositionField;
        private static FieldInfo _subjectHasLastKnownPositionField;
        private static MethodInfo _trackHitVictimMethod;
        private static MethodInfo _handleConfirmedKillMethod;

        [ThreadStatic]
        private static bool _insideMissileHit;

        internal static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _cinematicSubjectType = behaviorType.GetNestedType(
                "CinematicSubjectRecord",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (_cinematicSubjectType == null) return;

            _cinematicSubjectsField = AccessTools.Field(behaviorType, "_cinematicSubjects");
            _subjectAgentField = AccessTools.Field(_cinematicSubjectType, "Agent");
            _subjectLastKnownPositionField = AccessTools.Field(_cinematicSubjectType, "LastKnownPosition");
            _subjectHasLastKnownPositionField = AccessTools.Field(_cinematicSubjectType, "HasLastKnownPosition");

            MethodInfo impactMethod = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == "OnMissileHit" && !candidate.IsAbstract);
            _trackHitVictimMethod = AccessTools.Method(
                behaviorType,
                "TrackHitVictim",
                new[]
                {
                    typeof(Agent),
                    typeof(int),
                    typeof(int),
                    typeof(GameEntity),
                    typeof(Vec3),
                    typeof(Vec3)
                });
            MethodInfo trackSubjectMethod = AccessTools.Method(
                behaviorType,
                "TrackCinematicSubject",
                new[] { typeof(Agent), typeof(Vec3) });
            MethodInfo snapshotSubjectMethod = AccessTools.Method(
                behaviorType,
                "SnapshotCinematicSubject",
                new[] { typeof(Agent) });
            _handleConfirmedKillMethod = AccessTools.Method(
                behaviorType,
                "HandleConfirmedKill",
                new[] { typeof(Agent), typeof(string) });

            MethodInfo impactPrefix = AccessTools.Method(
                typeof(ImpactCinematicDeferralPatch),
                nameof(ImpactPrefix));
            MethodInfo impactFinalizer = AccessTools.Method(
                typeof(ImpactCinematicDeferralPatch),
                nameof(ImpactFinalizer));
            MethodInfo trackVictimPrefix = AccessTools.Method(
                typeof(ImpactCinematicDeferralPatch),
                nameof(TrackHitVictimPrefix));
            MethodInfo trackSubjectPrefix = AccessTools.Method(
                typeof(ImpactCinematicDeferralPatch),
                nameof(TrackSubjectPrefix));
            MethodInfo snapshotPrefix = AccessTools.Method(
                typeof(ImpactCinematicDeferralPatch),
                nameof(SnapshotSubjectPrefix));
            MethodInfo killPrefix = AccessTools.Method(
                typeof(ImpactCinematicDeferralPatch),
                nameof(HandleConfirmedKillPrefix));
            MethodInfo displayPrefix = AccessTools.Method(
                typeof(ImpactCinematicDeferralPatch),
                nameof(DisplayPrefix));
            MethodInfo resetPostfix = AccessTools.Method(
                typeof(ImpactCinematicDeferralPatch),
                nameof(ResetPostfix));

            if (impactMethod == null ||
                _trackHitVictimMethod == null ||
                trackSubjectMethod == null ||
                snapshotSubjectMethod == null ||
                _handleConfirmedKillMethod == null ||
                impactPrefix == null ||
                impactFinalizer == null ||
                trackVictimPrefix == null ||
                trackSubjectPrefix == null ||
                snapshotPrefix == null ||
                killPrefix == null ||
                displayPrefix == null ||
                resetPostfix == null ||
                _cinematicSubjectsField == null ||
                _subjectAgentField == null ||
                _subjectLastKnownPositionField == null ||
                _subjectHasLastKnownPositionField == null)
                return;

            try
            {
                harmony.Patch(
                    impactMethod,
                    prefix: new HarmonyMethod(impactPrefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(impactFinalizer) { priority = Priority.Last });
                harmony.Patch(
                    _trackHitVictimMethod,
                    prefix: new HarmonyMethod(trackVictimPrefix) { priority = Priority.First });
                harmony.Patch(
                    trackSubjectMethod,
                    prefix: new HarmonyMethod(trackSubjectPrefix) { priority = Priority.First });
                harmony.Patch(
                    snapshotSubjectMethod,
                    prefix: new HarmonyMethod(snapshotPrefix) { priority = Priority.First });
                harmony.Patch(
                    _handleConfirmedKillMethod,
                    prefix: new HarmonyMethod(killPrefix) { priority = Priority.First });
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
                    // The impact-camera transition patch runs at Priority.First. Replay victim
                    // tracking and then the kill after any pending native camera suspension has been
                    // processed on this safe display tick.
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(displayPrefix) { priority = Priority.High });
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

        private static void ImpactPrefix(out bool __state)
        {
            __state = _insideMissileHit;
            _insideMissileHit = true;
        }

        private static Exception ImpactFinalizer(Exception __exception, bool __state)
        {
            _insideMissileHit = __state;
            return __exception;
        }

        private static bool TrackHitVictimPrefix(
            object __instance,
            Agent __0,
            int __1,
            int __2,
            GameEntity __3,
            Vec3 __4,
            Vec3 __5)
        {
            if (!_insideMissileHit) return true;
            if (__instance == null || __0 == null) return false;

            // Keep an immediate managed subject record for framing, but do not subscribe to the
            // Agent health event or sample any live presentation data inside OnMissileHit.
            TryCreateOrUpdateManagedSubject(__instance, __0, __5);

            try
            {
                PendingVictimTrackQueue queue = PendingVictimTracks.GetOrCreateValue(__instance);
                for (int i = 0; i < queue.Items.Count; i++)
                {
                    PendingVictimTrack existing = queue.Items[i];
                    if (!ReferenceEquals(existing?.Victim, __0)) continue;

                    existing.MissileIndex = __1;
                    existing.CollisionBoneIndex = __2;
                    existing.ArrowEntity = null;
                    existing.ImpactDirection = __4;
                    existing.ImpactPosition = __5;
                    return false;
                }

                queue.Items.Add(new PendingVictimTrack
                {
                    Victim = __0,
                    MissileIndex = __1,
                    CollisionBoneIndex = __2,
                    // The impacted missile entity has already entered native teardown. It is optional
                    // cinematic decoration and must not survive the callback as a native handle.
                    ArrowEntity = null,
                    ImpactDirection = __4,
                    ImpactPosition = __5
                });
            }
            catch { }

            return false;
        }

        private static bool TrackSubjectPrefix(object __instance, Agent __0, Vec3 __1)
        {
            if (!_insideMissileHit) return true;
            if (__instance == null || __0 == null) return false;

            TryCreateOrUpdateManagedSubject(__instance, __0, __1);
            return false;
        }

        private static bool SnapshotSubjectPrefix()
        {
            return !_insideMissileHit;
        }

        private static bool HandleConfirmedKillPrefix(object __instance, Agent __0, string __1)
        {
            if (!_insideMissileHit) return true;
            if (__instance == null || __0 == null) return false;

            try
            {
                PendingKillQueue queue = PendingKills.GetOrCreateValue(__instance);
                for (int i = 0; i < queue.Items.Count; i++)
                {
                    if (ReferenceEquals(queue.Items[i]?.Victim, __0))
                        return false;
                }

                queue.Items.Add(new PendingKill
                {
                    Victim = __0,
                    Reason = string.IsNullOrEmpty(__1) ? "DeferredImpactKill" : __1
                });
            }
            catch { }

            return false;
        }

        private static void DisplayPrefix(object __instance)
        {
            if (__instance == null) return;

            if (PendingVictimTracks.TryGetValue(
                __instance,
                out PendingVictimTrackQueue victimQueue) &&
                victimQueue != null)
            {
                PendingVictimTracks.Remove(__instance);
                PendingVictimTrack[] pendingVictims = victimQueue.Items.ToArray();

                for (int i = 0; i < pendingVictims.Length; i++)
                {
                    PendingVictimTrack item = pendingVictims[i];
                    if (item?.Victim == null) continue;

                    try
                    {
                        _trackHitVictimMethod.Invoke(
                            __instance,
                            new object[]
                            {
                                item.Victim,
                                item.MissileIndex,
                                item.CollisionBoneIndex,
                                item.ArrowEntity,
                                item.ImpactDirection,
                                item.ImpactPosition
                            });
                    }
                    catch
                    {
                        // Fatal hits still have the independent deferred-kill path below. A non-fatal
                        // victim that disappears before this display tick requires no further tracking.
                    }
                }
            }

            if (!PendingKills.TryGetValue(__instance, out PendingKillQueue killQueue) ||
                killQueue == null)
                return;

            PendingKills.Remove(__instance);
            PendingKill[] pendingKills = killQueue.Items.ToArray();

            for (int i = 0; i < pendingKills.Length; i++)
            {
                PendingKill item = pendingKills[i];
                if (item?.Victim == null) continue;

                try
                {
                    _handleConfirmedKillMethod.Invoke(
                        __instance,
                        new object[] { item.Victim, item.Reason + "/PostImpactDisplay" });
                }
                catch
                {
                    // Existing removal fallback remains available if the victim disappeared before
                    // the display tick. Never retry inside the collision callback.
                }
            }
        }

        private static void ResetPostfix(object __instance)
        {
            if (__instance == null) return;
            PendingVictimTracks.Remove(__instance);
            PendingKills.Remove(__instance);
        }

        private static void TryCreateOrUpdateManagedSubject(object instance, Agent victim, Vec3 position)
        {
            if (instance == null || victim == null || !IsFinite(position)) return;

            try
            {
                IList subjects = _cinematicSubjectsField.GetValue(instance) as IList;
                if (subjects == null) return;

                object subject = null;
                for (int i = 0; i < subjects.Count; i++)
                {
                    object candidate = subjects[i];
                    if (candidate == null) continue;
                    if (!ReferenceEquals(_subjectAgentField.GetValue(candidate) as Agent, victim))
                        continue;

                    subject = candidate;
                    break;
                }

                if (subject == null)
                {
                    subject = Activator.CreateInstance(_cinematicSubjectType, true);
                    _subjectAgentField.SetValue(subject, victim);
                    subjects.Add(subject);
                }

                _subjectLastKnownPositionField.SetValue(subject, position);
                _subjectHasLastKnownPositionField.SetValue(subject, true);
            }
            catch { }
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
