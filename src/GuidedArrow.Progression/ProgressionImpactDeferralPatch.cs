using System;
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
    /// Removes progression/campaign work from the native agent-hit callback.
    ///
    /// The original progression postfix queried Agent.Main, teams, victim index and runtime settings,
    /// then mutated campaign progression and notified UI listeners before Bannerlord had completed
    /// the missile collision. Progression being enabled therefore opened an impact-only native/UI
    /// path that did not exist when progression was disabled.
    ///
    /// This patch replaces that postfix with a managed snapshot and performs the accounting on the
    /// next display tick. It also keeps a fatal-hit fallback for the cinematic deferral: if the core's
    /// impact path did not enqueue the kill, the same collision packet can start it after impact.
    /// </summary>
    internal static class ProgressionImpactDeferralPatch
    {
        private sealed class PendingHit
        {
            internal Agent Shooter;
            internal Agent Victim;
            internal int VictimKey;
            internal int Generation;
            internal Vec3 ShotOrigin;
            internal AttackCollisionData Collision;
            internal bool Fatal;
        }

        private sealed class PendingQueue
        {
            internal readonly List<PendingHit> Items = new List<PendingHit>();
        }

        private static readonly ConditionalWeakTable<object, PendingQueue> PendingHits =
            new ConditionalWeakTable<object, PendingQueue>();

        private static FieldInfo _generationField;
        private static FieldInfo _shooterField;
        private static FieldInfo _shotOriginField;
        private static FieldInfo _stateField;
        private static MethodInfo _autoguidanceRuntimeMethod;
        private static MethodInfo _handleConfirmedKillMethod;

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void InitializeModule()
        {
            try
            {
                Assembly guidedArrowAssembly = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "GuidedArrow")
                    {
                        guidedArrowAssembly = assembly;
                        break;
                    }
                }

                Type behaviorType = guidedArrowAssembly?.GetType(
                    "GuidedArrow.GuidedArrowBehavior",
                    false);
                if (behaviorType == null) return;

                Install(
                    new Harmony("guidedarrow.progression.deferred-impact-accounting"),
                    behaviorType);
            }
            catch
            {
                // Never block the stable core if this compatibility patch cannot be installed.
            }
        }

        private static void Install(Harmony harmony, Type behaviorType)
        {
            if (harmony == null || behaviorType == null) return;

            _generationField = AccessTools.Field(behaviorType, "_activeShotGeneration");
            _shooterField = AccessTools.Field(behaviorType, "_activeShotShooter");
            _shotOriginField = AccessTools.Field(behaviorType, "_pendingShotPosition");
            _stateField = AccessTools.Field(behaviorType, "_state");
            _autoguidanceRuntimeMethod = AccessTools.Method(behaviorType, "IsAutoguidanceRuntimeActive");
            _handleConfirmedKillMethod = AccessTools.Method(
                behaviorType,
                "HandleConfirmedKill",
                new[] { typeof(Agent), typeof(string) });

            MethodInfo coreAgentHit = behaviorType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == "OnAgentHit" && !candidate.IsAbstract);
            MethodInfo capturePostfix = AccessTools.Method(
                typeof(ProgressionImpactDeferralPatch),
                nameof(CapturePostfix));
            MethodInfo displayPrefix = AccessTools.Method(
                typeof(ProgressionImpactDeferralPatch),
                nameof(DisplayPrefix));
            MethodInfo resetPostfix = AccessTools.Method(
                typeof(ProgressionImpactDeferralPatch),
                nameof(ResetPostfix));
            MethodInfo disableLegacyPrefix = AccessTools.Method(
                typeof(ProgressionImpactDeferralPatch),
                nameof(DisableLegacyProgressionPostfix));
            MethodInfo legacyProgressionPostfix = AccessTools.Method(
                typeof(GuidedArrowPatches),
                "OnAgentHitPostfix");

            if (coreAgentHit == null ||
                capturePostfix == null ||
                displayPrefix == null ||
                resetPostfix == null ||
                disableLegacyPrefix == null ||
                legacyProgressionPostfix == null ||
                _generationField == null ||
                _shooterField == null ||
                _shotOriginField == null)
                return;

            try
            {
                // GuidedArrowPatches installs this method later as a Harmony postfix. Patching the
                // method body now ensures that invocation becomes a no-op without changing the rest
                // of the progression integration.
                harmony.Patch(
                    legacyProgressionPostfix,
                    prefix: new HarmonyMethod(disableLegacyPrefix) { priority = Priority.First });
                harmony.Patch(
                    coreAgentHit,
                    postfix: new HarmonyMethod(capturePostfix) { priority = Priority.Last });
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
                    // Camera transition and cinematic deferral prefixes run at First/High. Flush at
                    // Normal so the post-impact camera state is settled before campaign/UI work.
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(displayPrefix) { priority = Priority.Normal });
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

        private static bool DisableLegacyProgressionPostfix()
        {
            return false;
        }

        private static void CapturePostfix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null) return;

            try
            {
                Agent shooter = _shooterField.GetValue(__instance) as Agent;
                if (shooter == null) return;

                Agent victim = __args
                    .OfType<Agent>()
                    .FirstOrDefault(candidate => candidate != null && !ReferenceEquals(candidate, shooter));
                if (victim == null) return;

                AttackCollisionData collision = default;
                bool foundCollision = false;
                for (int i = 0; i < __args.Length; i++)
                {
                    if (!(__args[i] is AttackCollisionData candidate)) continue;
                    collision = candidate;
                    foundCollision = true;
                    break;
                }
                if (!foundCollision || !collision.IsMissile) return;

                int generation = (int)_generationField.GetValue(__instance);
                Vec3 origin = (Vec3)_shotOriginField.GetValue(__instance);
                int victimKey = RuntimeHelpers.GetHashCode(victim);
                bool fatal = ConcentratedImpactSafetyPatch.ReadCollisionFatalDamage(collision);

                PendingQueue queue = PendingHits.GetOrCreateValue(__instance);
                queue.Items.Add(new PendingHit
                {
                    Shooter = shooter,
                    Victim = victim,
                    VictimKey = victimKey,
                    Generation = generation,
                    ShotOrigin = origin,
                    Collision = collision,
                    Fatal = fatal
                });
            }
            catch
            {
                // Capturing a hit must never interfere with Bannerlord's collision callback.
            }
        }

        private static void DisplayPrefix(object __instance)
        {
            if (__instance == null ||
                !PendingHits.TryGetValue(__instance, out PendingQueue queue) ||
                queue == null)
                return;

            PendingHits.Remove(__instance);
            PendingHit[] pending = queue.Items.ToArray();

            ProgressionCampaignBehavior progression = ProgressionService.Current;
            bool progressionEnabled = progression != null && progression.Enabled;
            bool autoguidance = false;
            if (progressionEnabled && _autoguidanceRuntimeMethod != null)
            {
                try
                {
                    object active = _autoguidanceRuntimeMethod.Invoke(__instance, null);
                    autoguidance = active is bool value && value;
                }
                catch { }
            }

            for (int i = 0; i < pending.Length; i++)
            {
                PendingHit hit = pending[i];
                if (hit == null) continue;

                if (progressionEnabled && hit.Generation > 0 && hit.VictimKey >= 0)
                {
                    try
                    {
                        // This runs after collision resolution. Campaign state changes, UI listener
                        // notifications and Agent.Main access are no longer nested in native impact.
                        if (ReferenceEquals(hit.Shooter, Agent.Main))
                        {
                            float distance = (hit.Collision.CollisionGlobalPosition - hit.ShotOrigin).Length;
                            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
                                distance = 0f;

                            float multiplier = autoguidance
                                ? ProgressionBalance.AutoguidedXpMultiplier(
                                    ProgressionService.Level(SkillId.BorrowedFlight))
                                : 1f;

                            progression.RecordGuidedHit(
                                hit.Generation,
                                hit.VictimKey,
                                hit.Fatal,
                                distance,
                                multiplier);
                        }
                    }
                    catch { }
                }

                if (!hit.Fatal || hit.Victim == null || _handleConfirmedKillMethod == null)
                    continue;

                try
                {
                    int state = _stateField == null ? -1 : (int)_stateField.GetValue(__instance);
                    if (state == 4 || state == 5) continue;

                    // ImpactCinematicDeferralPatch runs earlier on this display tick. This call is a
                    // fallback only when that path did not observe the confirmed kill.
                    _handleConfirmedKillMethod.Invoke(
                        __instance,
                        new object[] { hit.Victim, "AgentHitFatal/PostImpactDisplay" });
                }
                catch { }
            }
        }

        private static void ResetPostfix(object __instance)
        {
            if (__instance != null)
                PendingHits.Remove(__instance);
        }
    }
}
