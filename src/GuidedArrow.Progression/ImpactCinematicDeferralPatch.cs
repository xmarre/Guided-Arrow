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
    /// Keeps cinematic Agent, ragdoll and camera work out of GuidedArrowBehavior.OnMissileHit.
    ///
    /// The locked core samples victim bones when TrackCinematicSubject runs, samples them again when
    /// a kill is confirmed, and can start the full cinematic camera before Bannerlord has finished
    /// the native missile-impact callback. This patch stores only the collision position during that
    /// callback and replays confirmed-kill handling on the next display tick. Later cinematic ticks
    /// use the original live ragdoll tracking, so the kill camera is not pinned to a static point.
    /// </summary>
    internal static class ImpactCinematicDeferralPatch
    {
        private sealed class PendingKill
        {
            internal Agent Victim;
            internal string Reason;
        }

        private sealed class PendingKillQueue
        {
            internal readonly List<PendingKill> Items = new List<PendingKill>();
        }

        private static readonly ConditionalWeakTable<object, PendingKillQueue> PendingKills =
            new ConditionalWeakTable<object, PendingKillQueue>();

        private static Type _cinematicSubjectType;
        private static FieldInfo _cinematicSubjectsField;
        private static FieldInfo _subjectAgentField;
        private static FieldInfo _subjectLastKnownPositionField;
        private static FieldInfo _subjectHasLastKnownPositionField;
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
            MethodInfo trackPrefix = AccessTools.Method(
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
                trackSubjectMethod == null ||
                snapshotSubjectMethod == null ||
                _handleConfirmedKillMethod == null ||
                impactPrefix == null ||
                impactFinalizer == null ||
                trackPrefix == null ||
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
                    trackSubjectMethod,
                    prefix: new HarmonyMethod(trackPrefix) { priority = Priority.First });
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
                    // The impact-camera transition patch runs at Priority.First. Replay the kill
                    // afterward so any pending native camera suspension completes before the
                    // cinematic takes ownership of the camera on this safe display tick.
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

        private static bool TrackSubjectPrefix(object __instance, Agent __0, Vec3 __1)
        {
            if (!_insideMissileHit) return true;
            if (__instance == null || __0 == null) return false;

            // During the native collision callback the managed collision position is authoritative.
            // Do not query Agent.Monster, Skeleton or AgentVisuals here.
            TryCreateOrUpdateManagedSubject(__instance, __0, __1);
            return false;
        }

        private static bool SnapshotSubjectPrefix()
        {
            // The subject already contains the collision position. Live ragdoll sampling resumes as
            // soon as the original cinematic code runs outside OnMissileHit.
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

            // Do not run CleanupTrackedMissiles, bone sampling, time control or SetMissionCamera
            // from the native collision callback.
            return false;
        }

        private static void DisplayPrefix(object __instance)
        {
            if (__instance == null ||
                !PendingKills.TryGetValue(__instance, out PendingKillQueue queue) ||
                queue == null)
                return;

            PendingKills.Remove(__instance);
            PendingKill[] pending = queue.Items.ToArray();

            for (int i = 0; i < pending.Length; i++)
            {
                PendingKill item = pending[i];
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
            if (__instance != null)
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
