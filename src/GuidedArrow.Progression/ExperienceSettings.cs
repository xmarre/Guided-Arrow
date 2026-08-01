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
            "Visible Siege Targets Only",
            Order = 4,
            RequireRestart = false,
            HintText = "During siege missions, excludes enemies hidden behind walls, towers, closed gates and other scene geometry before Autoguidance commits to them. Visible enemies and targets exposed through open approaches remain eligible. Disable only if another scene mod makes visibility ray checks unreliable.")]
        public bool VisibleSiegeTargetsOnly { get; set; } = true;
    }
}
