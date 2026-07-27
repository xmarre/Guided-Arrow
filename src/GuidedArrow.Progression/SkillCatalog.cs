using System;
using System.Collections.Generic;
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

    internal sealed class SkillDefinition
    {
        public SkillId Id;
        public string Name;
        public string Branch;
        public string Glyph;
        public string IconKey;
        public string Description;
        public int Cost;
        public int MasteryRank;
        public int RangedSkill;
        public SkillId[] Prerequisites;
    }

    internal static class SkillCatalog
    {
        internal static readonly int[] RankThresholds =
        {
            0, 50, 120, 220, 360, 550, 800, 1100, 1450, 1850, 2300, 2800, 3350, 3950, 4600, 5300
        };

        internal static readonly IReadOnlyList<SkillDefinition> All = new[]
        {
            D(SkillId.GuidedRelease, "Guided Release", "Core", "➶", "guided_release", 1, 1, 50,
                "Unlocks manual guidance for one player-fired arrow or bolt. Guidance is capped at 4 seconds, a 35 m minimum turn radius and 0.8 minimum time speed."),
            D(SkillId.SteadyHand, "Steady Hand", "Hand of the Archer", "✋", "steady_hand", 1, 2, 75,
                "Extends guided flight to 8 seconds and lowers the effective turn-radius floor to 22 m.", SkillId.GuidedRelease),
            D(SkillId.FineCorrection, "Fine Correction", "Hand of the Archer", "⌁", "fine_correction", 1, 4, 125,
                "Lowers the effective turn-radius floor to 14 m and unlocks speed-adaptive steering.", SkillId.SteadyHand),
            D(SkillId.TemporalFocus, "Temporal Focus", "Hand of the Archer", "⌛", "temporal_focus", 2, 5, 150,
                "Unlocks Q/E time control and Proximity Time Dilation. Minimum guided time speed becomes 0.5.", SkillId.FineCorrection),
            D(SkillId.MasterOfTheCurve, "Master of the Curve", "Hand of the Archer", "↝", "master_of_curve", 3, 9, 200,
                "Removes progression caps from configured guidance duration, turn radius and time-speed settings.", SkillId.TemporalFocus),
            D(SkillId.PredatorsEye, "Predator's Eye", "Hunter's Mind", "◉", "predators_eye", 2, 4, 125,
                "Unlocks hotkey autoguidance for the main projectile with closest-target selection.", SkillId.GuidedRelease),
            D(SkillId.RelentlessLock, "Relentless Lock", "Hunter's Mind", "⌾", "relentless_lock", 2, 6, 160,
                "Adds automatic target reacquisition and configurable target selection.", SkillId.PredatorsEye),
            D(SkillId.Pathfinder, "Pathfinder", "Hunter's Mind", "◆", "pathfinder", 1, 7, 175,
                "Unlocks obstacle avoidance and advanced autonomous flight profiles.", SkillId.RelentlessLock),
            D(SkillId.BorrowedFlight, "Borrowed Flight", "Hunter's Mind", "➤", "borrowed_flight", 2, 8, 200,
                "Allows allied-arrow takeover, limited to one queued takeover until further mastery.", SkillId.Pathfinder),
            D(SkillId.UnblinkingEye, "Unblinking Eye", "Hunter's Mind", "◉", "unblinking_eye", 3, 10, 225,
                "Unlocks always-on autoguidance, battle-persistent toggling and multi-target trajectory planning.", SkillId.BorrowedFlight),
            D(SkillId.SplitAwareness, "Split Awareness", "Arrow Choir", "⋔", "split_awareness", 1, 3, 100,
                "Guides native or TOR split siblings through shared guidance without creating extra arrows.", SkillId.GuidedRelease),
            D(SkillId.ForkedShaft, "Forked Shaft", "Arrow Choir", "⋏", "forked_shaft", 2, 5, 140,
                "Enables standalone splitting with up to two additional projectiles.", SkillId.SplitAwareness),
            D(SkillId.FormationDiscipline, "Formation Discipline", "Arrow Choir", "♜", "formation_discipline", 2, 7, 175,
                "Unlocks Orbital Wave, Horizontal Strike Line, Ring Orbit and Chevron formations.", SkillId.ForkedShaft),
            D(SkillId.ManyHeadedFlight, "Many-Headed Flight", "Arrow Choir", "✥", "many_headed_flight", 3, 10, 225,
                "Unlocks configured split count, formation breaking, independent guidance and target distribution.", SkillId.FormationDiscipline),
            D(SkillId.DrivingShot, "Driving Shot", "Piercing Doctrine", "↑", "driving_shot", 1, 4, 125,
                "Allows one controlled agent penetration for the main projectile.", SkillId.GuidedRelease),
            D(SkillId.ThroughTheRanks, "Through the Ranks", "Piercing Doctrine", "⇈", "through_the_ranks", 2, 7, 175,
                "Allows up to three controlled penetrations.", SkillId.DrivingShot),
            D(SkillId.UnbrokenFlight, "Unbroken Flight", "Piercing Doctrine", "↟", "unbroken_flight", 3, 11, 250,
                "Uses the configured penetration limit and permits infinite penetration.", SkillId.ThroughTheRanks),
            D(SkillId.SynchronizedHunt, "Synchronized Hunt", "Convergence", "⤨", "synchronized_hunt", 2, 9, 200,
                "Autoguidance may include split projectiles. Without this node, autonomous guidance remains on the main arrow.", SkillId.PredatorsEye, SkillId.FormationDiscipline),
            D(SkillId.NeedleStorm, "Needle Storm", "Convergence", "✺", "needle_storm", 2, 10, 225,
                "Allows the penetration doctrine to combine with generated split projectiles.", SkillId.ForkedShaft, SkillId.ThroughTheRanks)
        };

        internal static readonly IReadOnlyDictionary<SkillId, SkillDefinition> ById = All.ToDictionary(x => x.Id);

        internal static int GetRank(int xp)
        {
            int rank = 0;
            for (int i = 1; i < RankThresholds.Length; i++)
            {
                if (xp < RankThresholds[i]) break;
                rank = i;
            }
            return rank;
        }

        internal static int GetNextThreshold(int rank)
        {
            if (rank >= RankThresholds.Length - 1) return RankThresholds[RankThresholds.Length - 1];
            return RankThresholds[Math.Max(1, rank + 1)];
        }

        internal static int GetSpentPoints(int mask)
        {
            int spent = 0;
            foreach (SkillDefinition skill in All)
                if ((mask & Bit(skill.Id)) != 0) spent += skill.Cost;
            return spent;
        }

        internal static int Bit(SkillId id) => 1 << (int)id;

        private static SkillDefinition D(SkillId id, string name, string branch, string glyph, string iconKey,
            int cost, int masteryRank, int rangedSkill, string description, params SkillId[] prerequisites)
        {
            return new SkillDefinition
            {
                Id = id,
                Name = name,
                Branch = branch,
                Glyph = glyph,
                IconKey = iconKey,
                Cost = cost,
                MasteryRank = masteryRank,
                RangedSkill = rangedSkill,
                Description = description,
                Prerequisites = prerequisites ?? Array.Empty<SkillId>()
            };
        }
    }
}
