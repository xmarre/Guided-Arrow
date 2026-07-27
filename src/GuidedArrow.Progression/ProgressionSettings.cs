using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace GuidedArrow.Progression
{
    internal sealed class ProgressionSettings : AttributeGlobalSettings<ProgressionSettings>
    {
        private bool _enableProgression;
        private float _masteryXpMultiplier = 1f;

        public override string Id => "GuidedArrow_Progression_v1";
        public override string DisplayName => "Guided Arrow - Mastery Progression";
        public override string FolderName => "GuidedArrow";
        public override string FormatType => "json2";

        [SettingPropertyBool(
            "Enable Mastery Progression",
            Order = 0,
            RequireRestart = false,
            HintText = "Enables the level-99 Guided Arrow mastery tree. Runtime features are limited by invested skill ranks; the original Guided Arrow MCM values remain your upper limits.")]
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

        [SettingPropertyFloatingInteger(
            "Mastery XP Multiplier",
            0.25f,
            3f,
            "0.00",
            Order = 1,
            RequireRestart = false,
            HintText = "Scales mastery XP only. 1.00 is balanced for a long Bannerlord campaign; lower values slow progression and higher values shorten it.")]
        [SettingPropertyGroup("Progression", GroupOrder = 0)]
        public float MasteryXpMultiplier
        {
            get => _masteryXpMultiplier;
            set
            {
                float clamped = value < 0.25f ? 0.25f : (value > 3f ? 3f : value);
                if (_masteryXpMultiplier == clamped) return;
                _masteryXpMultiplier = clamped;
                OnPropertyChanged();
            }
        }
    }
}
