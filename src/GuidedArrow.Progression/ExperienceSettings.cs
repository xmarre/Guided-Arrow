using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace GuidedArrow.Progression
{
    internal sealed class ExperienceSettings : AttributeGlobalSettings<ExperienceSettings>
    {
        public override string Id => "guided_arrow_camera_targeting_v1";
        public override string DisplayName => "Guided Arrow - Simple Controls";
        public override string FolderName => "GuidedArrow";
        public override string FormatType => "json2";

        [SettingPropertyGroup("Projectile Camera", GroupOrder = 2)]
        [SettingPropertyBool(
            "Follow the Guided Projectile",
            Order = 9,
            RequireRestart = false,
            HintText = "Controls only the normal arrow-follow camera. Disable this to remain in the player's combat view while manual steering or Autoguidance continues normally. Kill cinematics are controlled separately below. The value is captured when each guided shot starts.")]
        public bool FollowProjectileCamera { get; set; } = true;

        [SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
        [SettingPropertyBool(
            "Play Kill Cinematics",
            Order = 18,
            RequireRestart = false,
            HintText = "Shows the close-up kill camera after a confirmed guided kill. Disable this to skip the cinematic and proceed directly to the normal camera handoff. This is independent from the arrow-follow camera above. The value is captured when each guided shot starts.")]
        public bool EnableKillCinematics { get; set; } = true;

        [SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
        [SettingPropertyBool(
            "Disable Autoguidance During Sieges",
            Order = 4,
            RequireRestart = false,
            HintText = "Suppresses automatic target acquisition only during siege missions. Guided arrows and manual steering remain available. Leave disabled to keep Autoguidance active in sieges.")]
        public bool DisableAutoguidanceInSieges { get; set; } = false;

        [SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
        [SettingPropertyBool(
            "Direct-Line Siege Target Filter (Experimental)",
            Order = 5,
            RequireRestart = false,
            HintText = "Rejects siege targets whose head is blocked on a direct line from the shooter. This can be too strict around parapets even when Guided Arrow could steer around them, so it is disabled by default. The normal Autoguidance obstacle-routing system remains active without this filter.")]
        public bool UseDirectLineSiegeTargetFilter { get; set; } = false;

        // Retained only so existing json2 files containing the former setting can deserialize.
        public bool VisibleSiegeTargetsOnly { get; set; } = true;
    }
}
