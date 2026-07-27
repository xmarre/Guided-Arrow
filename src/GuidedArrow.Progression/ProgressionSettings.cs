using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace GuidedArrow.Progression
{
    internal sealed class ProgressionSettings : AttributeGlobalSettings<ProgressionSettings>
    {
        private bool _enableProgression;

        public override string Id => "GuidedArrow_Progression_v1";
        public override string DisplayName => "Guided Arrow - Mastery Progression";
        public override string FolderName => "GuidedArrow";
        public override string FormatType => "json2";

        [SettingPropertyBool(
            "Enable Mastery Progression",
            Order = 0,
            RequireRestart = false,
            HintText = "Enables the Guided Arrow mastery tree. Guided Arrow features are restricted by unlocked mastery nodes. This can also be changed from the mastery screen.")]
        [SettingPropertyGroup("Progression", GroupOrder = 0)]
        public bool EnableProgression
        {
            get => _enableProgression;
            set
            {
                if (_enableProgression == value) return;
                _enableProgression = value;
                OnPropertyChanged();
                ProgressionService.ApplyConfiguredEnabled(value);
            }
        }
    }
}
