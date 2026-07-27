using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GuidedArrow.Progression
{
    internal enum SkillId
    {
        GuidedRelease = 0,
        SteadyHand = 1,
        FineCorrection = 2,
        TemporalFocus = 3,
        MasterOfTheCurve = 4,
        PredatorsEye = 5,
        RelentlessLock = 6,
        Pathfinder = 7,
        BorrowedFlight = 8,
        UnblinkingEye = 9,
        SplitAwareness = 10,
        ForkedShaft = 11,
        FormationDiscipline = 12,
        ManyHeadedFlight = 13,
        DrivingShot = 14,
        ThroughTheRanks = 15,
        UnbrokenFlight = 16,
        SynchronizedHunt = 17,
        NeedleStorm = 18
    }

    internal sealed class SkillRequirement
    {
        internal SkillId Id;
        internal int Level;
    }

    internal sealed class SkillDefinition
    {
        internal SkillId Id;
        internal string Name;
        internal string Branch;
        internal string Glyph;
        internal string IconKey;
        internal string Description;
        internal int MaxLevel;
        internal int MasteryRank;
        internal int RangedSkill;
        internal int TreeOrder;
        internal SkillRequirement[] Prerequisites;
    }

    internal static class ProgressionBalance
    {
        internal const int MaximumMasteryRank = 99;

        internal static float GuidanceTimeCap(int guidedRelease, int masterOfCurve)
        {
            if (guidedRelease <= 0) return 0f;
            if (masterOfCurve >= 10) return 120f;
            return 4f + 0.4f * Math.Max(0, guidedRelease - 1) + 1.2f * Math.Max(0, masterOfCurve);
        }

        internal static float TurnRadiusFloor(int steadyHand, int fineCorrection, int masterOfCurve)
        {
            float value = 35f - 0.6f * Math.Max(0, steadyHand) - 0.7f * Math.Max(0, fineCorrection) - 0.6f * Math.Max(0, masterOfCurve);
            return Math.Max(3f, value);
        }

        internal static float TimeSpeedFloor(int temporalFocus, int masterOfCurve)
        {
            float value = 0.8f - 0.025f * Math.Max(0, temporalFocus) - 0.03f * Math.Max(0, masterOfCurve);
            return Math.Max(0.05f, value);
        }

        internal static float SpeedAdaptiveSteeringCap(int fineCorrection, int masterOfCurve)
        {
            return Math.Min(2f, 0.05f * Math.Max(0, fineCorrection) + 0.05f * Math.Max(0, masterOfCurve));
        }

        internal static int PlannedTargetCap(int predatorsEye, int relentlessLock, int unblinkingEye, int synchronizedHunt)
        {
            int value = 1;
            value += Math.Max(0, predatorsEye) / 5;
            value += Math.Max(0, relentlessLock) / 4;
            value += Math.Max(0, unblinkingEye) / 3;
            value += Math.Max(0, synchronizedHunt) / 5;
            return Math.Max(1, Math.Min(20, value));
        }

        internal static float ObstacleScanIntervalFloor(int pathfinder)
        {
            return Math.Max(0.05f, 0.32f - 0.012f * Math.Max(0, pathfinder));
        }

        internal static int FlightProfileMaximumIndex(int pathfinder)
        {
            if (pathfinder >= 20) return 6;
            if (pathfinder >= 15) return 5;
            if (pathfinder >= 10) return 4;
            if (pathfinder >= 5) return 2;
            return 0;
        }

        internal static float AutoguidedXpMultiplier(int borrowedFlight)
        {
            return Math.Min(0.65f, 0.35f + 0.06f * Math.Max(0, borrowedFlight));
        }

        internal static int NativeGuidedProjectileCap(int splitAwareness)
        {
            if (splitAwareness <= 0) return 1;
            return Math.Min(64, 1 + 4 * splitAwareness);
        }

        internal static int StandaloneSplitCountCap(int forkedShaft, int manyHeadedFlight)
        {
            if (forkedShaft <= 0) return 1;
            return Math.Min(48, 1 + Math.Max(0, forkedShaft) + 3 * Math.Max(0, manyHeadedFlight));
        }

        internal static int FormationModeMaximum(int formationDiscipline)
        {
            if (formationDiscipline >= 9) return 4;
            if (formationDiscipline >= 6) return 3;
            if (formationDiscipline >= 3) return 2;
            if (formationDiscipline >= 1) return 1;
            return 0;
        }

        internal static float FormationResponseCap(int formationDiscipline)
        {
            return 2f + 0.6f * Math.Max(0, formationDiscipline);
        }

        internal static float FormationCatchUpCap(int formationDiscipline)
        {
            return Math.Min(5f, 1.5f + 0.35f * Math.Max(0, formationDiscipline));
        }

        internal static int PenetrationCap(int drivingShot, int throughTheRanks, int unbrokenFlight)
        {
            if (drivingShot <= 0) return 0;
            int value = 1 + Math.Max(0, drivingShot - 1) / 5;
            value += Math.Max(0, throughTheRanks) / 3;
            value += 2 * Math.Max(0, unbrokenFlight);
            return Math.Min(100, value);
        }

        internal static int SynchronizedProjectileCap(int synchronizedHunt)
        {
            if (synchronizedHunt <= 0) return 1;
            return Math.Min(64, 1 + 3 * synchronizedHunt);
        }

        internal static int NeedleStormPenetrationCap(int needleStorm)
        {
            if (needleStorm <= 0) return 0;
            return Math.Min(5, 1 + Math.Max(0, needleStorm - 1) / 2);
        }
    }

    internal static class SkillCatalog
    {
        internal static readonly IReadOnlyList<SkillDefinition> All = new[]
        {
            D(SkillId.GuidedRelease, "Guided Release", "Core", "➶", "guided_release", 20, 1, 25, 0,
                "The centre of the tree. Unlocks manual guidance and steadily extends the safe real-time control window."),

            D(SkillId.SteadyHand, "Steady Hand", "Hand of the Archer", "✋", "steady_hand", 20, 2, 50, 1,
                "Reduces the minimum turn-radius floor, making deliberate corrections possible without immediately granting extreme agility.", R(SkillId.GuidedRelease, 3)),
            D(SkillId.FineCorrection, "Fine Correction", "Hand of the Archer", "⌁", "fine_correction", 20, 8, 75, 2,
                "Further lowers the turn-radius floor and unlocks progressively stronger speed-adaptive steering.", R(SkillId.SteadyHand, 5)),
            D(SkillId.TemporalFocus, "Temporal Focus", "Hand of the Archer", "⌛", "temporal_focus", 20, 18, 100, 3,
                "Unlocks guided time control and Proximity Time Dilation. Higher levels permit deeper slowdown.", R(SkillId.FineCorrection, 5)),
            D(SkillId.MasterOfTheCurve, "Master of the Curve", "Hand of the Archer", "↝", "master_of_curve", 10, 35, 150, 4,
                "A capstone that relaxes every Hand of the Archer restriction. Rank 10 releases the configured duration cap and reaches the engine-safe turn-radius floor.", R(SkillId.TemporalFocus, 10)),

            D(SkillId.PredatorsEye, "Predator's Eye", "Hunter's Mind", "◉", "predators_eye", 20, 5, 75, 1,
                "Unlocks autonomous guidance and increases the number of targets retained in its bounded route plan.", R(SkillId.GuidedRelease, 5)),
            D(SkillId.RelentlessLock, "Relentless Lock", "Hunter's Mind", "⌾", "relentless_lock", 20, 15, 100, 2,
                "Unlocks automatic reacquisition, advanced target-selection modes and larger route plans.", R(SkillId.PredatorsEye, 5)),
            D(SkillId.Pathfinder, "Pathfinder", "Hunter's Mind", "◆", "pathfinder", 20, 25, 125, 3,
                "Unlocks obstacle avoidance, faster bounded obstacle checks and progressively more advanced flight profiles.", R(SkillId.RelentlessLock, 5)),
            D(SkillId.BorrowedFlight, "Borrowed Flight", "Hunter's Mind", "➤", "borrowed_flight", 5, 40, 175, 4,
                "Unlocks allied-arrow takeover. Each rank retains more mastery experience from autonomous and borrowed shots.", R(SkillId.Pathfinder, 10)),
            D(SkillId.UnblinkingEye, "Unblinking Eye", "Hunter's Mind", "◉", "unblinking_eye", 10, 55, 225, 5,
                "Unlocks always-on guidance, battle-persistent toggling and multi-target trajectory planning in stages.", R(SkillId.BorrowedFlight, 3), R(SkillId.RelentlessLock, 10)),

            D(SkillId.SplitAwareness, "Split Awareness", "Arrow Choir", "⋔", "split_awareness", 5, 5, 75, 1,
                "Allows native and TOR multi-projectile volleys to join the guided group without replacing their original projectiles or effects.", R(SkillId.GuidedRelease, 5)),
            D(SkillId.ForkedShaft, "Forked Shaft", "Arrow Choir", "⋏", "forked_shaft", 20, 15, 100, 2,
                "Unlocks standalone splitting. Each rank raises the allowed generated-arrow total by one.", R(SkillId.SplitAwareness, 3)),
            D(SkillId.FormationDiscipline, "Formation Discipline", "Arrow Choir", "♜", "formation_discipline", 10, 25, 125, 3,
                "Unlocks formation geometries in stages and raises follower response and catch-up limits.", R(SkillId.ForkedShaft, 5)),
            D(SkillId.ManyHeadedFlight, "Many-Headed Flight", "Arrow Choir", "✥", "many_headed_flight", 10, 40, 175, 4,
                "Adds three generated-projectile capacity per rank and progressively unlocks independent split behaviour and target distribution.", R(SkillId.FormationDiscipline, 5), R(SkillId.ForkedShaft, 10)),

            D(SkillId.DrivingShot, "Driving Shot", "Piercing Doctrine", "↑", "driving_shot", 20, 5, 75, 1,
                "Unlocks controlled agent penetration. Additional ranks gradually raise the main projectile's safe penetration allowance.", R(SkillId.GuidedRelease, 5)),
            D(SkillId.ThroughTheRanks, "Through the Ranks", "Piercing Doctrine", "⇈", "through_the_ranks", 20, 15, 100, 2,
                "Adds another penetration allowance every three ranks, supporting deliberate multi-kill lines without immediately granting unlimited chains.", R(SkillId.DrivingShot, 5)),
            D(SkillId.UnbrokenFlight, "Unbroken Flight", "Piercing Doctrine", "↟", "unbroken_flight", 10, 35, 150, 3,
                "Adds two penetration allowance per rank. Rank 10 permits the configured infinite-penetration option.", R(SkillId.ThroughTheRanks, 10)),

            D(SkillId.SynchronizedHunt, "Synchronized Hunt", "Convergence", "⤨", "synchronized_hunt", 10, 55, 200, 1,
                "Combines Hunter's Mind with the Arrow Choir. Each rank allows three more split projectiles to use autonomous guidance.", R(SkillId.PredatorsEye, 10), R(SkillId.FormationDiscipline, 5)),
            D(SkillId.NeedleStorm, "Needle Storm", "Convergence", "✺", "needle_storm", 10, 55, 200, 2,
                "Combines splitting with the Piercing Doctrine. Generated followers gain one penetration at rank 1 and up to five at rank 9.", R(SkillId.ForkedShaft, 10), R(SkillId.ThroughTheRanks, 10))
        };

        internal static readonly IReadOnlyDictionary<SkillId, SkillDefinition> ById = All.ToDictionary(skill => skill.Id);

        internal static int GetThreshold(int rank)
        {
            rank = Math.Max(1, Math.Min(ProgressionBalance.MaximumMasteryRank, rank));
            long n = rank - 1;
            long late = Math.Max(0, n - 50);
            long value = 6L * n * n + 20L * n + (long)Math.Round(0.08d * late * late * late);
            return (int)Math.Min(int.MaxValue, value);
        }

        internal static int GetRank(int xp)
        {
            xp = Math.Max(0, xp);
            int low = 1;
            int high = ProgressionBalance.MaximumMasteryRank;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (xp >= GetThreshold(middle)) low = middle;
                else high = middle - 1;
            }
            return low;
        }

        internal static int GetNextThreshold(int rank)
        {
            return rank >= ProgressionBalance.MaximumMasteryRank
                ? GetThreshold(ProgressionBalance.MaximumMasteryRank)
                : GetThreshold(rank + 1);
        }

        internal static int GetInvestedPoints(Func<SkillId, int> levelResolver)
        {
            if (levelResolver == null) return 0;
            int total = 0;
            foreach (SkillDefinition skill in All)
                total += Math.Max(0, Math.Min(skill.MaxLevel, levelResolver(skill.Id)));
            return total;
        }

        internal static string GetRequirementText(SkillDefinition skill)
        {
            if (skill == null) return string.Empty;
            List<string> parts = new List<string>
            {
                "Mastery " + skill.MasteryRank,
                "Ranged Skill " + skill.RangedSkill
            };
            foreach (SkillRequirement requirement in skill.Prerequisites)
                parts.Add(ById[requirement.Id].Name + " " + requirement.Level);
            return string.Join(" • ", parts);
        }

        internal static string GetEffectText(SkillId id, int level)
        {
            level = Math.Max(0, level);
            switch (id)
            {
                case SkillId.GuidedRelease:
                    return level <= 0
                        ? "Rank 1 unlocks manual guidance with a 4.0 second control cap."
                        : "Manual guidance active. Maximum guided flight: " + F(ProgressionBalance.GuidanceTimeCap(level, 0), 1) + " seconds before Hand capstones.";
                case SkillId.SteadyHand:
                    return "Turn-radius floor contribution: -" + F(0.6f * level, 1) + " m.";
                case SkillId.FineCorrection:
                    return "Additional turn-radius reduction: -" + F(0.7f * level, 1) + " m. Speed-adaptive steering cap: " + F(0.05f * level, 2) + ".";
                case SkillId.TemporalFocus:
                    return level <= 0
                        ? "Rank 1 unlocks time control and Proximity Time Dilation."
                        : "Time control active. Minimum allowed guided time speed before capstones: " + F(ProgressionBalance.TimeSpeedFloor(level, 0), 2) + ".";
                case SkillId.MasterOfTheCurve:
                    return level >= 10
                        ? "Maximum mastery: configured guidance duration is unrestricted; turn radius may reach the 3 m engine-safe floor."
                        : "+" + F(1.2f * level, 1) + " s guidance cap, -" + F(0.6f * level, 1) + " m turn-radius floor and -" + F(0.03f * level, 2) + " time-speed floor.";
                case SkillId.PredatorsEye:
                    return level <= 0 ? "Rank 1 unlocks autonomous guidance." : "Autonomous guidance active. Predator route contribution: " + (1 + level / 5) + " target slots.";
                case SkillId.RelentlessLock:
                    return level <= 0 ? "Rank 1 unlocks automatic target reacquisition." : "Automatic reacquisition active. Additional route slots: " + (level / 4) + ". Target-selection tier: " + (level >= 10 ? 2 : (level >= 5 ? 1 : 0)) + ".";
                case SkillId.Pathfinder:
                    return level <= 0 ? "Rank 1 unlocks obstacle avoidance." : "Obstacle avoidance active. Minimum recheck interval: " + F(ProgressionBalance.ObstacleScanIntervalFloor(level), 2) + " s. Flight-profile tier: " + ProgressionBalance.FlightProfileMaximumIndex(level) + ".";
                case SkillId.BorrowedFlight:
                    return level <= 0 ? "Rank 1 unlocks allied-arrow takeover." : "Allied-arrow takeover active. Autonomous/borrowed mastery XP retention: " + F(ProgressionBalance.AutoguidedXpMultiplier(level) * 100f, 0) + "% of manual-shot XP.";
                case SkillId.UnblinkingEye:
                    return level <= 0 ? "Rank 1 unlocks always-on autoguidance." : "Always-on unlocked" + (level >= 3 ? ", battle-persistent toggle unlocked" : string.Empty) + (level >= 5 ? ", multi-target planning unlocked" : string.Empty) + ".";
                case SkillId.SplitAwareness:
                    return level <= 0 ? "Rank 1 begins native/TOR sibling guidance." : "Up to " + ProgressionBalance.NativeGuidedProjectileCap(level) + " native projectiles may join one guided volley; original effects remain native.";
                case SkillId.ForkedShaft:
                    return level <= 0 ? "Rank 1 unlocks standalone generated splitting." : "Generated split total before Many-Headed Flight: up to " + ProgressionBalance.StandaloneSplitCountCap(level, 0) + " projectiles.";
                case SkillId.FormationDiscipline:
                    return level <= 0 ? "Rank 1 unlocks Orbital Wave." : "Formation mode tier: " + ProgressionBalance.FormationModeMaximum(level) + ". Response cap: " + F(ProgressionBalance.FormationResponseCap(level), 1) + ". Catch-up cap: " + F(ProgressionBalance.FormationCatchUpCap(level), 1) + "×.";
                case SkillId.ManyHeadedFlight:
                    return level <= 0 ? "Rank 1 expands generated splitting and autonomous split control." : "+" + (3 * level) + " generated-projectile capacity. Split behaviour tier: " + (level >= 7 ? 2 : (level >= 3 ? 1 : 0)) + ".";
                case SkillId.DrivingShot:
                    return level <= 0 ? "Rank 1 unlocks one controlled penetration for the main projectile." : "Driving Shot penetration contribution: " + (1 + Math.Max(0, level - 1) / 5) + ".";
                case SkillId.ThroughTheRanks:
                    return "Additional penetration allowance: " + (level / 3) + ".";
                case SkillId.UnbrokenFlight:
                    return level >= 10 ? "+20 penetration allowance and configured infinite penetration unlocked." : "Additional penetration allowance: " + (2 * level) + ". Infinite penetration unlocks at rank 10.";
                case SkillId.SynchronizedHunt:
                    return level <= 0 ? "Rank 1 lets split projectiles participate in autonomous guidance." : "Up to " + ProgressionBalance.SynchronizedProjectileCap(level) + " projectiles may use autonomous guidance in one volley.";
                case SkillId.NeedleStorm:
                    return level <= 0 ? "Rank 1 lets generated followers penetrate one agent." : "Generated followers may penetrate up to " + ProgressionBalance.NeedleStormPenetrationCap(level) + " agents each.";
                default:
                    return string.Empty;
            }
        }

        internal static string GetNextLevelText(SkillDefinition skill, int currentLevel)
        {
            if (skill == null) return string.Empty;
            if (currentLevel >= skill.MaxLevel) return "Maximum level reached.";
            return "Next level: " + GetEffectText(skill.Id, currentLevel + 1);
        }

        private static SkillDefinition D(SkillId id, string name, string branch, string glyph, string iconKey,
            int maxLevel, int masteryRank, int rangedSkill, int treeOrder, string description, params SkillRequirement[] prerequisites)
        {
            return new SkillDefinition
            {
                Id = id,
                Name = name,
                Branch = branch,
                Glyph = glyph,
                IconKey = iconKey,
                Description = description,
                MaxLevel = maxLevel,
                MasteryRank = masteryRank,
                RangedSkill = rangedSkill,
                TreeOrder = treeOrder,
                Prerequisites = prerequisites ?? Array.Empty<SkillRequirement>()
            };
        }

        private static SkillRequirement R(SkillId id, int level)
        {
            return new SkillRequirement { Id = id, Level = level };
        }

        private static string F(float value, int decimals)
        {
            return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }
    }
}
