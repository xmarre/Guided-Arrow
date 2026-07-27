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
        private Dictionary<string, int> _unlockMasks = new Dictionary<string, int>();
        private bool _progressionEnabled;
        private readonly Dictionary<int, ShotXpState> _shotXp = new Dictionary<int, ShotXpState>();

        private sealed class ShotXpState
        {
            public int Awarded;
            public readonly HashSet<int> Victims = new HashSet<int>();
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
            if (_masteryXp == null) _masteryXp = new Dictionary<string, int>();
            if (_unlockMasks == null) _unlockMasks = new Dictionary<string, int>();
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

        internal int UnlockMask
        {
            get
            {
                int value;
                return _unlockMasks.TryGetValue(HeroKey, out value) ? value : 0;
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
        internal int AvailablePoints => Math.Max(0, Rank - SkillCatalog.GetSpentPoints(UnlockMask));

        internal void SetEnabled(bool enabled)
        {
            _progressionEnabled = enabled;
            if (enabled && Xp < SkillCatalog.RankThresholds[1])
                _masteryXp[HeroKey] = SkillCatalog.RankThresholds[1];
            NotifyChanged();
        }

        internal bool Has(SkillId id)
        {
            if (!_progressionEnabled) return true;
            return (UnlockMask & SkillCatalog.Bit(id)) != 0;
        }

        internal bool HasPurchased(SkillId id) => (UnlockMask & SkillCatalog.Bit(id)) != 0;

        internal bool CanPurchase(SkillId id, out string reason)
        {
            SkillDefinition skill = SkillCatalog.ById[id];
            if (HasPurchased(id)) { reason = "Already unlocked."; return false; }
            if (!_progressionEnabled) { reason = "Enable progression first."; return false; }
            if (Rank < skill.MasteryRank) { reason = "Requires Mastery " + skill.MasteryRank + "."; return false; }
            if (RangedSkill < skill.RangedSkill) { reason = "Requires Ranged Skill " + skill.RangedSkill + "."; return false; }
            if (AvailablePoints < skill.Cost) { reason = "Requires " + skill.Cost + " mastery point" + (skill.Cost == 1 ? "." : "s."); return false; }
            foreach (SkillId prerequisite in skill.Prerequisites)
            {
                if (!HasPurchased(prerequisite))
                {
                    reason = "Requires " + SkillCatalog.ById[prerequisite].Name + ".";
                    return false;
                }
            }
            reason = "Ready to unlock.";
            return true;
        }

        internal bool Purchase(SkillId id, out string reason)
        {
            if (!CanPurchase(id, out reason)) return false;
            _unlockMasks[HeroKey] = UnlockMask | SkillCatalog.Bit(id);
            reason = SkillCatalog.ById[id].Name + " unlocked.";
            InformationManager.DisplayMessage(new InformationMessage(reason, Colors.Green));
            NotifyChanged();
            return true;
        }

        internal void Respec()
        {
            _unlockMasks[HeroKey] = 0;
            NotifyChanged();
        }

        internal void AddXp(int amount)
        {
            if (!_progressionEnabled || amount <= 0) return;
            int beforeRank = Rank;
            _masteryXp[HeroKey] = Math.Min(SkillCatalog.RankThresholds[SkillCatalog.RankThresholds.Length - 1], Xp + amount);
            int afterRank = Rank;
            if (afterRank > beforeRank)
                InformationManager.DisplayMessage(new InformationMessage("Guided Arrow Mastery reached rank " + afterRank + ".", Colors.Green));
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
                if (_shotXp.Count > 64)
                {
                    int oldest = int.MaxValue;
                    foreach (int key in _shotXp.Keys) if (key < oldest) oldest = key;
                    if (oldest != int.MaxValue) _shotXp.Remove(oldest);
                }
            }
            if (!state.Victims.Add(victimIndex)) return;

            int raw = 2;
            if (killed) raw += 4;
            if (distance >= 120f) raw += 3;
            else if (distance >= 80f) raw += 2;
            else if (distance >= 40f) raw += 1;
            if (killed && state.Victims.Count > 1) raw += 2;

            int room = Math.Max(0, 16 - state.Awarded);
            int award = Math.Min(room, Math.Max(1, (int)Math.Round(raw * Math.Max(0.1f, Math.Min(1f, multiplier)))));
            if (award <= 0) return;
            state.Awarded += award;
            AddXp(award);
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
        internal static bool Has(SkillId id) => !Enabled || (_behavior != null && _behavior.Has(id));
        internal static bool ConfiguredEnabled => ProgressionSettings.Instance != null && ProgressionSettings.Instance.EnableProgression;
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
        internal static void Detach() { _behavior = null; Changed = null; }
    }
}
