using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Library;

namespace GuidedArrow.Progression
{
    internal sealed class SkillNodeVM : ViewModel
    {
        private readonly GuidedArrowMasteryVM _owner;
        internal readonly SkillDefinition Definition;
        private bool _isSelected;
        private string _buttonText;

        internal SkillNodeVM(GuidedArrowMasteryVM owner, SkillDefinition definition)
        {
            _owner = owner;
            Definition = definition;
            Refresh();
        }

        [DataSourceProperty]
        public string ButtonText
        {
            get => _buttonText;
            set
            {
                if (value == _buttonText) return;
                _buttonText = value;
                OnPropertyChangedWithValue(value, nameof(ButtonText));
            }
        }

        [DataSourceProperty]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (value == _isSelected) return;
                _isSelected = value;
                OnPropertyChangedWithValue(value, nameof(IsSelected));
            }
        }

        public void ExecuteSelect() => _owner.Select(Definition.Id);

        internal void Refresh()
        {
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            int level = progression != null ? progression.GetSkillLevel(Definition.Id) : 0;
            string reason;
            bool ready = progression != null && progression.CanInvest(Definition.Id, out reason);
            string state = level >= Definition.MaxLevel ? "MAX" : (ready ? "READY" : (level > 0 ? "ACTIVE" : "LOCKED"));
            ButtonText = Definition.Glyph + "  " + Definition.Name + "\n" + level + " / " + Definition.MaxLevel + "  •  " + state;
        }
    }

    internal sealed class GuidedArrowMasteryVM : ViewModel
    {
        private readonly Action _close;
        private readonly List<SkillNodeVM> _allNodes = new List<SkillNodeVM>();
        private SkillId _selectedId = SkillId.GuidedRelease;

        private string _masteryText;
        private string _xpText;
        private string _pointsText;
        private string _rangedText;
        private string _selectedName;
        private string _selectedLevel;
        private string _selectedRequirements;
        private string _selectedDescription;
        private string _selectedCurrentEffect;
        private string _selectedNextEffect;
        private string _selectedStatus;
        private string _progressionText;
        private string _progressionActionText;
        private string _messageText;
        private int _xpProgress;

        internal GuidedArrowMasteryVM(Action close)
        {
            _close = close;
            CoreNodes = Make("Core");
            HandNodes = Make("Hand of the Archer");
            HunterNodes = Make("Hunter's Mind");
            ChoirNodes = Make("Arrow Choir");
            PiercingNodes = Make("Piercing Doctrine");
            SynchronizedNodes = MakeSingle(SkillId.SynchronizedHunt);
            NeedleNodes = MakeSingle(SkillId.NeedleStorm);

            ProgressionService.Changed += RefreshAll;
            RefreshAll();
            Select(SkillId.GuidedRelease);
        }

        [DataSourceProperty] public string Title => "Guided Arrow Mastery";
        [DataSourceProperty] public MBBindingList<SkillNodeVM> CoreNodes { get; }
        [DataSourceProperty] public MBBindingList<SkillNodeVM> HandNodes { get; }
        [DataSourceProperty] public MBBindingList<SkillNodeVM> HunterNodes { get; }
        [DataSourceProperty] public MBBindingList<SkillNodeVM> ChoirNodes { get; }
        [DataSourceProperty] public MBBindingList<SkillNodeVM> PiercingNodes { get; }
        [DataSourceProperty] public MBBindingList<SkillNodeVM> SynchronizedNodes { get; }
        [DataSourceProperty] public MBBindingList<SkillNodeVM> NeedleNodes { get; }

        [DataSourceProperty] public string MasteryText { get => _masteryText; set => Set(ref _masteryText, value, nameof(MasteryText)); }
        [DataSourceProperty] public string XpText { get => _xpText; set => Set(ref _xpText, value, nameof(XpText)); }
        [DataSourceProperty] public string PointsText { get => _pointsText; set => Set(ref _pointsText, value, nameof(PointsText)); }
        [DataSourceProperty] public string RangedText { get => _rangedText; set => Set(ref _rangedText, value, nameof(RangedText)); }
        [DataSourceProperty] public int XpProgress { get => _xpProgress; set { if (value != _xpProgress) { _xpProgress = value; OnPropertyChangedWithValue(value, nameof(XpProgress)); } } }
        [DataSourceProperty] public string SelectedName { get => _selectedName; set => Set(ref _selectedName, value, nameof(SelectedName)); }
        [DataSourceProperty] public string SelectedLevel { get => _selectedLevel; set => Set(ref _selectedLevel, value, nameof(SelectedLevel)); }
        [DataSourceProperty] public string SelectedRequirements { get => _selectedRequirements; set => Set(ref _selectedRequirements, value, nameof(SelectedRequirements)); }
        [DataSourceProperty] public string SelectedDescription { get => _selectedDescription; set => Set(ref _selectedDescription, value, nameof(SelectedDescription)); }
        [DataSourceProperty] public string SelectedCurrentEffect { get => _selectedCurrentEffect; set => Set(ref _selectedCurrentEffect, value, nameof(SelectedCurrentEffect)); }
        [DataSourceProperty] public string SelectedNextEffect { get => _selectedNextEffect; set => Set(ref _selectedNextEffect, value, nameof(SelectedNextEffect)); }
        [DataSourceProperty] public string SelectedStatus { get => _selectedStatus; set => Set(ref _selectedStatus, value, nameof(SelectedStatus)); }
        [DataSourceProperty] public string ProgressionText { get => _progressionText; set => Set(ref _progressionText, value, nameof(ProgressionText)); }
        [DataSourceProperty] public string ProgressionActionText { get => _progressionActionText; set => Set(ref _progressionActionText, value, nameof(ProgressionActionText)); }
        [DataSourceProperty] public string MessageText { get => _messageText; set => Set(ref _messageText, value, nameof(MessageText)); }

        public void ExecuteConfirm()
        {
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null)
            {
                MessageText = "Campaign progression is unavailable.";
                return;
            }

            string reason;
            progression.Invest(_selectedId, out reason);
            MessageText = reason;
            RefreshAll();
        }

        public void ExecuteRespec()
        {
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null) return;
            progression.Respec();
            MessageText = "All invested mastery points were refunded.";
            RefreshAll();
        }

        public void ExecuteToggleProgression()
        {
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null) return;
            bool enable = !progression.Enabled;
            ProgressionService.SetEnabledFromUi(enable);
            MessageText = enable
                ? "Progression enabled. Guided Arrow now uses the invested mastery ranks as runtime limits."
                : "Progression disabled. The original Guided Arrow MCM values are unrestricted.";
            RefreshAll();
        }

        public void ExecuteClose() => _close?.Invoke();

        internal void Select(SkillId id)
        {
            _selectedId = id;
            foreach (SkillNodeVM node in _allNodes) node.IsSelected = node.Definition.Id == id;
            RefreshSelected();
        }

        internal void RefreshAll()
        {
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            if (progression == null)
            {
                MasteryText = "Mastery Rank 1 / 99";
                XpText = "XP 0 / " + SkillCatalog.GetNextThreshold(1);
                PointsText = "Points Available: 0";
                RangedText = "Ranged Skill: 0";
                XpProgress = 0;
                ProgressionText = "Progression: Unavailable";
                ProgressionActionText = "Enable Progression";
            }
            else
            {
                int rank = progression.Rank;
                int currentThreshold = SkillCatalog.GetThreshold(rank);
                int nextThreshold = SkillCatalog.GetNextThreshold(rank);
                int width = Math.Max(1, nextThreshold - currentThreshold);

                MasteryText = "Mastery Rank " + rank + " / 99";
                XpText = rank >= ProgressionBalance.MaximumMasteryRank
                    ? "XP " + progression.Xp + " • MAXIMUM RANK"
                    : "XP " + progression.Xp + " / " + nextThreshold;
                PointsText = "Points Available: " + progression.AvailablePoints + "  •  Invested: " + progression.InvestedPoints;
                RangedText = "Ranged Skill: " + progression.RangedSkill;
                XpProgress = rank >= ProgressionBalance.MaximumMasteryRank
                    ? 100
                    : Math.Max(0, Math.Min(100, (progression.Xp - currentThreshold) * 100 / width));
                ProgressionText = progression.Enabled ? "Progression: ENABLED" : "Progression: DISABLED";
                ProgressionActionText = progression.Enabled ? "Disable Progression" : "Enable Progression";
            }

            foreach (SkillNodeVM node in _allNodes) node.Refresh();
            RefreshSelected();
        }

        public override void OnFinalize()
        {
            ProgressionService.Changed -= RefreshAll;
            base.OnFinalize();
        }

        private MBBindingList<SkillNodeVM> Make(string branch)
        {
            MBBindingList<SkillNodeVM> list = new MBBindingList<SkillNodeVM>();
            foreach (SkillDefinition definition in SkillCatalog.All.Where(skill => skill.Branch == branch).OrderBy(skill => skill.TreeOrder))
                Add(list, definition);
            return list;
        }

        private MBBindingList<SkillNodeVM> MakeSingle(SkillId id)
        {
            MBBindingList<SkillNodeVM> list = new MBBindingList<SkillNodeVM>();
            Add(list, SkillCatalog.ById[id]);
            return list;
        }

        private void Add(MBBindingList<SkillNodeVM> list, SkillDefinition definition)
        {
            SkillNodeVM node = new SkillNodeVM(this, definition);
            list.Add(node);
            _allNodes.Add(node);
        }

        private void RefreshSelected()
        {
            SkillDefinition skill = SkillCatalog.ById[_selectedId];
            ProgressionCampaignBehavior progression = ProgressionService.Current;
            int level = progression != null ? progression.GetSkillLevel(skill.Id) : 0;

            SelectedName = skill.Glyph + "  " + skill.Name;
            SelectedLevel = "Level " + level + " / " + skill.MaxLevel + "  •  1 point per level";
            SelectedRequirements = SkillCatalog.GetRequirementText(skill);
            SelectedDescription = skill.Description;
            SelectedCurrentEffect = "CURRENT\n" + SkillCatalog.GetEffectText(skill.Id, level);
            SelectedNextEffect = "NEXT\n" + SkillCatalog.GetNextLevelText(skill, level);

            if (progression == null)
            {
                SelectedStatus = "Campaign progression is unavailable.";
            }
            else if (!progression.Enabled)
            {
                SelectedStatus = "Progression disabled — the original MCM configuration is unrestricted.";
            }
            else if (level >= skill.MaxLevel)
            {
                SelectedStatus = "Maximum level reached • " + skill.Branch;
            }
            else
            {
                string reason;
                progression.CanInvest(skill.Id, out reason);
                SelectedStatus = reason + " • " + skill.Branch;
            }
        }

        private void Set(ref string field, string value, string name)
        {
            if (field == value) return;
            field = value;
            OnPropertyChangedWithValue(value, name);
        }
    }
}
