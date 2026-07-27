using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GuidedArrow.Progression
{
    internal sealed class ProgressionCampaignBehavior : CampaignBehaviorBase
    {
        private Dictionary<string, int> _masteryXp = new Dictionary<string, int>();
        private Dictionary<string, int> _skillLevels = new Dictionary<string, int>();

        // v1 data is retained solely for one-way save migration.
        private Dictionary<string, int> _unlockMasks = new Dictionary<string, int>();
        private bool _progressionEnabled;
        private int _dataVersion;

        private readonly Dictionary<int, ShotXpState> _shotXp = new Dictionary<int, ShotXpState>();

        private sealed class ShotXpState
        {
            internal int Awarded;
            internal int Kills;
            internal readonly HashSet<int> Victims = new HashSet<int>();
        }

        internal ProgressionCampaignBehavior()
        {
            ProgressionService.Attach(this);
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, _ => AttachAndApplySettings());
            CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, _ => AttachAndApplySettings());
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this, _ => AttachAndApplySettings());
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("_guidedArrowMasteryXp_v1", ref _masteryXp);
            dataStore.SyncData("_guidedArrowUnlockMasks_v1", ref _unlockMasks);
            dataStore.SyncData("_guidedArrowProgressionEnabled_v1", ref _progressionEnabled);
            dataStore.SyncData("_guidedArrowSkillLevels_v2", ref _skillLevels);
            dataStore.SyncData("_guidedArrowProgressionDataVersion", ref _dataVersion);

            if (_masteryXp == null) _masteryXp = new Dictionary<string, int>();
            if (_unlockMasks == null) _unlockMasks = new Dictionary<string, int>();
            if (_skillLevels == null) _skillLevels = new Dictionary<string, int>();

            if (dataStore.IsLoading) MigrateV1Unlocks();
            ProgressionService.Attach(this);
            if (dataStore.IsLoading) ProgressionService.ApplyCurrentSetting();
        }

        private void AttachAndApplySettings()
        {
            ProgressionService.Attach(this);
            ProgressionService.ApplyCurrentSetting();
        }

        internal bool Enabled => _progressionEnabled;
        internal string HeroKey => Hero.MainHero != null ? Hero.MainHero.StringId : "main_hero";

        internal int Xp
        {
            get
            {
                int value;
                return _masteryXp.TryGetValue(HeroKey, out value) ? Math.Max(0, value) : 0;
            }
        }

        internal int Rank => SkillCatalog.GetRank(Xp);

        internal int RangedSkill
        {
            get
            {
                Hero hero = Hero.MainHero;
                if (hero == null) return 0;
                return Math.Max(hero.GetSkillValue(DefaultSkills.Bow), hero.GetSkillValue(DefaultSkills.Crossbow));
            }
        }

        internal int InvestedPoints => SkillCatalog.GetInvestedPoints(GetSkillLevel);
        internal int AvailablePoints => Math.Max(0, Rank - InvestedPoints);

        internal void SetEnabled(bool enabled)
        {
            if (_progressionEnabled == enabled) return;
            _progressionEnabled = enabled;
            NotifyChanged();
        }

        internal int GetSkillLevel(SkillId id)
        {
            SkillDefinition definition = SkillCatalog.ById[id];
            int value;
            if (!_skillLevels.TryGetValue(SkillKey(HeroKey, id), out value)) return 0;
            return Math.Max(0, Math.Min(definition.MaxLevel, value));
        }

        internal bool Has(SkillId id)
        {
            if (!_progressionEnabled) return true;
            return GetSkillLevel(id) > 0;
        }

        internal bool HasPurchased(SkillId id) => GetSkillLevel(id) > 0;

        internal bool CanInvest(SkillId id, out string reason)
        {
            SkillDefinition skill = SkillCatalog.ById[id];
            int current = GetSkillLevel(id);
            if (!_progressionEnabled) { reason = "Enable progression first."; return false; }
            if (current >= skill.MaxLevel) { reason = "Maximum level reached."; return false; }
            if (Rank < skill.MasteryRank) { reason = "Requires Mastery Rank " + skill.MasteryRank + "."; return false; }
            if (RangedSkill < skill.RangedSkill) { reason = "Requires Ranged Skill " + skill.RangedSkill + "."; return false; }
            if (AvailablePoints < 1) { reason = "Requires 1 mastery point."; return false; }

            foreach (SkillRequirement prerequisite in skill.Prerequisites)
            {
                int level = GetSkillLevel(prerequisite.Id);
                if (level < prerequisite.Level)
                {
                    reason = "Requires " + SkillCatalog.ById[prerequisite.Id].Name + " level " + prerequisite.Level + ".";
                    return false;
                }
            }

            reason = "Ready to invest.";
            return true;
        }

        internal bool Invest(SkillId id, out string reason)
        {
            if (!CanInvest(id, out reason)) return false;

            SkillDefinition skill = SkillCatalog.ById[id];
            int newLevel = GetSkillLevel(id) + 1;
            _skillLevels[SkillKey(HeroKey, id)] = newLevel;
            reason = skill.Name + " reached level " + newLevel + "/" + skill.MaxLevel + ".";
            InformationManager.DisplayMessage(new InformationMessage(reason, Colors.Green));
            NotifyChanged();
            return true;
        }

        internal void Respec()
        {
            string prefix = HeroKey + "|";
            List<string> remove = new List<string>();
            foreach (string key in _skillLevels.Keys)
                if (key != null && key.StartsWith(prefix, StringComparison.Ordinal)) remove.Add(key);
            foreach (string key in remove) _skillLevels.Remove(key);
            NotifyChanged();
        }

        internal void AddXp(int amount)
        {
            if (!_progressionEnabled || amount <= 0) return;

            float multiplier = ProgressionService.XpMultiplier;
            int scaled = Math.Max(1, (int)Math.Round(amount * multiplier));
            int beforeRank = Rank;
            int maximumXp = SkillCatalog.GetThreshold(ProgressionBalance.MaximumMasteryRank);
            _masteryXp[HeroKey] = Math.Min(maximumXp, Xp + scaled);
            int afterRank = Rank;

            if (afterRank > beforeRank)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    "Guided Arrow Mastery reached rank " + afterRank + ". Mastery points available: " + AvailablePoints + ".",
                    Colors.Green));
            }
            NotifyChanged();
        }

        internal void RecordGuidedHit(int generation, int victimIndex, bool killed, float distance, float multiplier)
        {
            if (!_progressionEnabled || generation <= 0 || victimIndex < 0) return;

            ShotXpState state;
            if (!_shotXp.TryGetValue(generation, out state))
            {
                state = new ShotXpState();
                _shotXp[generation] = state;
                if (_shotXp.Count > 96)
                {
                    int oldest = int.MaxValue;
                    foreach (int key in _shotXp.Keys) if (key < oldest) oldest = key;
                    if (oldest != int.MaxValue) _shotXp.Remove(oldest);
                }
            }
            if (!state.Victims.Add(victimIndex)) return;

            int raw = 3;
            if (killed)
            {
                raw += 6;
                state.Kills++;
            }
            if (distance >= 150f) raw += 4;
            else if (distance >= 100f) raw += 3;
            else if (distance >= 60f) raw += 2;
            else if (distance >= 30f) raw += 1;
            if (killed && state.Kills > 1) raw += Math.Min(4, state.Kills - 1);

            int room = Math.Max(0, 32 - state.Awarded);
            float boundedMultiplier = Math.Max(0.1f, Math.Min(1f, multiplier));
            int award = Math.Min(room, Math.Max(1, (int)Math.Round(raw * boundedMultiplier)));
            if (award <= 0) return;

            state.Awarded += award;
            AddXp(award);
        }

        private void MigrateV1Unlocks()
        {
            if (_dataVersion >= 2) return;

            foreach (KeyValuePair<string, int> pair in _unlockMasks)
            {
                string hero = string.IsNullOrEmpty(pair.Key) ? "main_hero" : pair.Key;
                int mask = pair.Value;
                foreach (SkillDefinition skill in SkillCatalog.All)
                {
                    int bit = 1 << (int)skill.Id;
                    if ((mask & bit) == 0) continue;
                    string key = SkillKey(hero, skill.Id);
                    if (!_skillLevels.ContainsKey(key)) _skillLevels[key] = 1;
                }
            }

            _dataVersion = 2;
        }

        private static string SkillKey(string heroKey, SkillId id)
        {
            return (heroKey ?? "main_hero") + "|" + (int)id;
        }

        private void NotifyChanged() => ProgressionService.NotifyChanged();
    }

    internal static class ProgressionService
    {
        private static ProgressionCampaignBehavior _behavior;
        internal static event Action Changed;

        internal static void Attach(ProgressionCampaignBehavior behavior) => _behavior = behavior;
        internal static ProgressionCampaignBehavior Current => _behavior;
        internal static bool Enabled => _behavior != null && _behavior.Enabled;
        internal static int Level(SkillId id) => _behavior != null ? _behavior.GetSkillLevel(id) : 0;
        internal static bool Has(SkillId id) => !Enabled || Level(id) > 0;
        internal static bool ConfiguredEnabled => ProgressionSettings.Instance != null && ProgressionSettings.Instance.EnableProgression;
        internal static float XpMultiplier => ProgressionSettings.Instance != null ? ProgressionSettings.Instance.MasteryXpMultiplier : 1f;

        internal static void ApplyCurrentSetting() => ApplyConfiguredEnabled(ConfiguredEnabled);

        internal static void ApplyConfiguredEnabled(bool enabled)
        {
            if (_behavior != null) _behavior.SetEnabled(enabled);
        }

        internal static void SetEnabledFromUi(bool enabled)
        {
            ProgressionSettings settings = ProgressionSettings.Instance;
            if (settings != null) settings.EnableProgression = enabled;
            else ApplyConfiguredEnabled(enabled);
        }

        internal static void NotifyChanged() => Changed?.Invoke();

        internal static void Detach()
        {
            _behavior = null;
            Changed = null;
        }
    }
}
