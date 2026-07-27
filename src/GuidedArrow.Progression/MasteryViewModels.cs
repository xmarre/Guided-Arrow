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

        [DataSourceProperty] public string ButtonText { get => _buttonText; set { if (value != _buttonText) { _buttonText = value; OnPropertyChangedWithValue(value, nameof(ButtonText)); } } }
        [DataSourceProperty] public bool IsSelected { get => _isSelected; set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } } }
        [DataSourceProperty] public string Name => Definition.Name;
        [DataSourceProperty] public string Glyph => Definition.Glyph;

        public void ExecuteSelect() => _owner.Select(Definition.Id);

        internal void Refresh()
        {
            ProgressionCampaignBehavior p = ProgressionService.Current;
            bool unlocked = p != null && p.HasPurchased(Definition.Id);
            string reason;
            bool ready = p != null && p.CanPurchase(Definition.Id, out reason);
            string state = unlocked ? "UNLOCKED" : (ready ? "AVAILABLE" : "LOCKED");
            ButtonText = Definition.Glyph + "\n" + Definition.Name + "\n" + state;
        }
    }

    internal sealed class GuidedArrowMasteryVM : ViewModel
    {
        private readonly Action _close;
        private readonly List<SkillNodeVM> _allNodes = new List<SkillNodeVM>();
        private SkillId _selectedId = SkillId.GuidedRelease;
        private string _masteryText, _xpText, _pointsText, _rangedText, _selectedName, _selectedCost, _selectedRequirements, _selectedDescription, _selectedStatus, _progressionText, _progressionActionText, _messageText;
        private int _xpProgress;

        internal GuidedArrowMasteryVM(Action close)
        {
            _close = close;
            HandNodes = Make("Hand of the Archer");
            HunterNodes = Make("Hunter's Mind");
            ChoirNodes = Make("Arrow Choir");
            PiercingNodes = Make("Piercing Doctrine");
            ConvergenceNodes = Make("Convergence");
            CoreNodes = Make("Core");
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
        [DataSourceProperty] public MBBindingList<SkillNodeVM> ConvergenceNodes { get; }

        [DataSourceProperty] public string MasteryText { get => _masteryText; set => Set(ref _masteryText, value, nameof(MasteryText)); }
        [DataSourceProperty] public string XpText { get => _xpText; set => Set(ref _xpText, value, nameof(XpText)); }
        [DataSourceProperty] public string PointsText { get => _pointsText; set => Set(ref _pointsText, value, nameof(PointsText)); }
        [DataSourceProperty] public string RangedText { get => _rangedText; set => Set(ref _rangedText, value, nameof(RangedText)); }
        [DataSourceProperty] public int XpProgress { get => _xpProgress; set { if (value != _xpProgress) { _xpProgress = value; OnPropertyChangedWithValue(value, nameof(XpProgress)); } } }
        [DataSourceProperty] public string SelectedName { get => _selectedName; set => Set(ref _selectedName, value, nameof(SelectedName)); }
        [DataSourceProperty] public string SelectedCost { get => _selectedCost; set => Set(ref _selectedCost, value, nameof(SelectedCost)); }
        [DataSourceProperty] public string SelectedRequirements { get => _selectedRequirements; set => Set(ref _selectedRequirements, value, nameof(SelectedRequirements)); }
        [DataSourceProperty] public string SelectedDescription { get => _selectedDescription; set => Set(ref _selectedDescription, value, nameof(SelectedDescription)); }
        [DataSourceProperty] public string SelectedStatus { get => _selectedStatus; set => Set(ref _selectedStatus, value, nameof(SelectedStatus)); }
        [DataSourceProperty] public string ProgressionText { get => _progressionText; set => Set(ref _progressionText, value, nameof(ProgressionText)); }
        [DataSourceProperty] public string ProgressionActionText { get => _progressionActionText; set => Set(ref _progressionActionText, value, nameof(ProgressionActionText)); }
        [DataSourceProperty] public string MessageText { get => _messageText; set => Set(ref _messageText, value, nameof(MessageText)); }

        public void ExecuteConfirm()
        {
            ProgressionCampaignBehavior p = ProgressionService.Current;
            if (p == null) { MessageText = "Campaign progression is unavailable."; return; }
            string reason;
            if (p.Purchase(_selectedId, out reason)) MessageText = reason;
            else MessageText = reason;
            RefreshAll();
        }

        public void ExecuteRespec()
        {
            ProgressionCampaignBehavior p = ProgressionService.Current;
            if (p == null) return;
            p.Respec();
            MessageText = "All mastery points refunded.";
            RefreshAll();
        }

        public void ExecuteToggleProgression()
        {
            ProgressionCampaignBehavior p = ProgressionService.Current;
            if (p == null) return;
            ProgressionService.SetEnabledFromUi(!p.Enabled);
            MessageText = p.Enabled ? "Progression enabled. Existing MCM values are now capped by unlocked skills." : "Progression disabled. Full configured Guided Arrow behaviour restored.";
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
            ProgressionCampaignBehavior p = ProgressionService.Current;
            if (p == null)
            {
                MasteryText = "Mastery Rank 0"; XpText = "XP 0 / 50"; PointsText = "Points Available: 0"; RangedText = "Ranged Skill: 0"; XpProgress = 0; ProgressionText = "Progression: Unavailable"; ProgressionActionText = "Enable Progression";
            }
            else
            {
                int rank = p.Rank;
                int currentThreshold = SkillCatalog.RankThresholds[Math.Min(rank, SkillCatalog.RankThresholds.Length - 1)];
                int nextThreshold = SkillCatalog.GetNextThreshold(rank);
                int width = Math.Max(1, nextThreshold - currentThreshold);
                MasteryText = "Mastery Rank " + rank;
                XpText = "XP " + p.Xp + " / " + nextThreshold;
                PointsText = "Points Available: " + p.AvailablePoints;
                RangedText = "Ranged Skill: " + p.RangedSkill;
                XpProgress = rank >= 15 ? 100 : Math.Max(0, Math.Min(100, (p.Xp - currentThreshold) * 100 / width));
                ProgressionText = p.Enabled ? "Progression: ENABLED" : "Progression: DISABLED";
                ProgressionActionText = p.Enabled ? "Disable Progression" : "Enable Progression";
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
            foreach (SkillDefinition definition in SkillCatalog.All.Where(x => x.Branch == branch))
            {
                SkillNodeVM vm = new SkillNodeVM(this, definition);
                list.Add(vm); _allNodes.Add(vm);
            }
            return list;
        }

        private void RefreshSelected()
        {
            SkillDefinition skill = SkillCatalog.ById[_selectedId];
            ProgressionCampaignBehavior p = ProgressionService.Current;
            SelectedName = skill.Glyph + "  " + skill.Name;
            SelectedCost = "Cost: " + skill.Cost + " Point" + (skill.Cost == 1 ? string.Empty : "s");
            string prerequisites = skill.Prerequisites.Length == 0 ? "" : " • " + string.Join(" • ", skill.Prerequisites.Select(x => SkillCatalog.ById[x].Name));
            SelectedRequirements = "Mastery " + skill.MasteryRank + " • Ranged Skill " + skill.RangedSkill + prerequisites;
            SelectedDescription = skill.Description;
            if (p == null) SelectedStatus = "Unavailable";
            else if (!p.Enabled) SelectedStatus = "Progression disabled — configured feature is unrestricted.";
            else if (p.HasPurchased(skill.Id)) SelectedStatus = "Unlocked • Branch: " + skill.Branch;
            else { string reason; p.CanPurchase(skill.Id, out reason); SelectedStatus = reason + " • Branch: " + skill.Branch; }
        }

        private void Set(ref string field, string value, string name)
        {
            if (field == value) return;
            field = value; OnPropertyChangedWithValue(value, name);
        }
    }
}
