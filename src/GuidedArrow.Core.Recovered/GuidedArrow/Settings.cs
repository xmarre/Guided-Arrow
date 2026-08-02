using System.Collections.Generic;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;
using MCM.Common;

namespace GuidedArrow;

public sealed class Settings : AttributeGlobalSettings<Settings>
{
	public override string Id => "guided_arrow_v100";

	public override string DisplayName => "Guided Arrow";

	public override string FolderName => "GuidedArrow";

	public override string FormatType => "json";

	[SettingPropertyGroup("General", GroupOrder = 0)]
	[SettingPropertyBool("Enable Guided Arrows", Order = 0, RequireRestart = false, HintText = "Guide player-fired arrows and bolts with the mouse. Q slows time, E speeds it up, Esc or right mouse cancels guidance.")]
	public bool Enabled { get; set; } = true;

	[SettingPropertyGroup("General", GroupOrder = 0)]
	[SettingPropertyFloatingInteger("Initial Guidance Time Speed", 0.05f, 1f, "0.00", Order = 1, RequireRestart = false, HintText = "Legacy fixed guidance speed used only when Proximity Time Dilation is disabled. Q/E still move through fixed speed steps as a per-shot manual override.")]
	public float InitialGuidanceTimeSpeed { get; set; } = 0.15f;

	[SettingPropertyGroup("General", GroupOrder = 0)]
	[SettingPropertyFloatingInteger("Minimum Turn Radius", 3f, 120f, "0.0", Order = 2, RequireRestart = false, HintText = "Physical steering limit in metres. Lower values allow tighter curves; faster arrows still require proportionally more angular rate.")]
	public float MinimumTurnRadius { get; set; } = 24f;

	[SettingPropertyGroup("General", GroupOrder = 0)]
	[SettingPropertyFloatingInteger("Mouse Steering Sensitivity", 0.1f, 4f, "0.00", Order = 3, RequireRestart = false, HintText = "Direct mouse steering sensitivity. Input bends the current velocity only; there is no target-heading auto-centering or forward stabilization.")]
	public float MouseSensitivity { get; set; } = 0.49829796f;

	[SettingPropertyGroup("General", GroupOrder = 0)]
	[SettingPropertyFloatingInteger("High-Speed Steering Strength", 0f, 2f, "0.00", Order = 4, RequireRestart = false, HintText = "Extra steering authority for missiles faster than Reference Missile Speed. 0 = disabled, 1 = linear boost with speed ratio, 2 = stronger squared boost.")]
	public float SpeedAdaptiveSteeringStrength { get; set; } = 0.49532947f;

	[SettingPropertyGroup("General", GroupOrder = 0)]
	[SettingPropertyFloatingInteger("Slow-Time Steering Compensation", 0f, 1.5f, "0.00", Order = 5, RequireRestart = false, HintText = "Restores steering responsiveness as mission time slows. 1.00 applies square-root compensation rather than a full inverse-time multiplier, keeping slow-motion guidance responsive without making turns instantaneous.")]
	public float SlowTimeSteeringCompensation { get; set; } = 0.5992324f;

	[SettingPropertyGroup("General", GroupOrder = 0)]
	[SettingPropertyFloatingInteger("Maximum Guidance Time", 5f, 120f, "0.0", Order = 6, RequireRestart = false, HintText = "Real-time failsafe duration before a missed projectile automatically returns the camera to the player.")]
	public float MaximumGuidanceTime { get; set; } = 64.30307f;

	[SettingPropertyGroup("General", GroupOrder = 0)]
	[SettingPropertyBool("Enable Allied Arrow Takeover", Order = 7, RequireRestart = false, HintText = "Optional deliberate allied-arrow chain mode. This uses a separate exact-index queue and never participates in normal player-shot acquisition. When enabled, friendly allied arrows fired during an active guided/cinematic chain may be queued and taken over one at a time only after the current camera return. Disabled by default.")]
	public bool EnableAlliedArrowTakeover { get; set; }

	[SettingPropertyGroup("Projectile Overrides", GroupOrder = 1)]
	[SettingPropertyBool("Enable Standalone Split Arrows", Order = 0, RequireRestart = false, HintText = "Adds native Bannerlord missile copies to ordinary player-fired arrows and bolts, allowing split-arrow gameplay without The Old Realms. Shots that already contain a native/TOR split batch are left completely untouched; the two split systems are never stacked.")]
	public bool EnableStandaloneSplitProjectiles { get; set; }

	[SettingPropertyGroup("Projectile Overrides", GroupOrder = 1)]
	[SettingPropertyInteger("Total Projectiles per Shot", 1, 48, "0", Order = 1, RequireRestart = false, HintText = "Requested total number of projectiles for an otherwise single-projectile shot. 1 keeps a normal shot. Native/TOR split abilities keep their own projectile count and damage rules; this setting does not add copies on top of them.")]
	public int StandaloneSplitProjectileCount { get; set; } = 3;

	[SettingPropertyGroup("Projectile Overrides", GroupOrder = 1)]
	[SettingPropertyBool("Override Agent Penetration", Order = 2, RequireRestart = false, HintText = "Overrides agent-impact penetration for controlled arrows and bolts. Native terrain, shield, wall and tree collisions remain terminal. Disabled preserves the weapon/mod's native penetration rules.")]
	public bool EnablePenetrationOverride { get; set; }

	[SettingPropertyGroup("Projectile Overrides", GroupOrder = 1)]
	[SettingPropertyInteger("Maximum Agent Penetrations", 0, 100, "0", Order = 3, RequireRestart = false, HintText = "Maximum number of agents each controlled projectile may pass through when the override is enabled. 0 prevents agent penetration. Native PassThrough events count toward this limit.")]
	public int MaximumAgentPenetrations { get; set; } = 5;

	[SettingPropertyGroup("Projectile Overrides", GroupOrder = 1)]
	[SettingPropertyBool("Infinite Agent Penetration", Order = 4, RequireRestart = false, HintText = "Removes the penetration-count limit for controlled projectiles. Terrain and scene obstacles still stop the projectile normally.")]
	public bool InfiniteAgentPenetrations { get; set; }

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyBool("Enable Autonomous Guidance", Order = 0, RequireRestart = false, HintText = "Master switch for physically constrained predictive autoguidance. When enabled, activation is controlled by Always-On Autoguidance, the battle-persistent toggle, or the configured per-projectile hotkey mode.")]
	public bool EnableAutonomousGuidance { get; set; } = true;

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyBool("Always-On Autoguidance", Order = 12, RequireRestart = false, HintText = "Automatically enables autoguidance for every eligible guided projectile in the mission. This overrides the hotkey activation mode and battle-persistent toggle while enabled.")]
	public bool AutoguidanceAlwaysOn { get; set; }

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyBool("Persist Hotkey Toggle for Battle", Order = 13, RequireRestart = false, HintText = "In Toggle activation mode, press Ctrl+G (or the configured chord) during any guided flight to change one battle-wide autoguidance state. The current projectile and all later guided projectiles use that state until the chord is pressed again. The state resets when the mission ends.")]
	public bool AutoguidancePersistToggleForBattle { get; set; }

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyDropdown("Autoguidance Flight Profile", Order = 14, RequireRestart = false, HintText = "Selects how autoguidance shapes the physically constrained flight path. Low Strike uses the existing low-cruise toggle and rising-entry settings. Natural Ballistic preserves the launch arc and corrects only as needed. Direct Hunter uses the shortest predictive intercept. Lofted Arc climbs early and descends shallowly. Banking Flank approaches through a broad side curve. Serpentine uses a restrained S-curve. Adaptive Mix chooses a suitable profile per projectile. Close-range head reachability overrides every decorative manoeuvre.")]
	public Dropdown<string> AutoguidanceFlightProfile { get; set; } = new Dropdown<string>((IEnumerable<string>)new string[7] { "Low Strike", "Natural Ballistic", "Direct Hunter", "Lofted Arc", "Banking Flank", "Serpentine", "Adaptive Mix" }, 0);

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyText("Autoguidance Hotkey / Chord", -1, true, "", Order = 1, RequireRestart = false, HintText = "Enter one key or a multi-key chord separated by +. Examples: Ctrl+G, Ctrl+Shift+G, F8, MiddleMouseButton, X1MouseButton or Q+E. Ctrl, Shift and Alt accept either left or right modifier. The chord is read live and does not require a restart.")]
	public string AutoguidanceHotkeyName { get; set; } = "Ctrl+G";

	public int AutoguidanceHotkey { get; set; }

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyInteger("Activation Mode", 0, 1, "0", Order = 2, RequireRestart = false, HintText = "0=Toggle: press once for autonomous guidance and again for manual. 1=Hold: autoguidance is active only while the configured key is held.")]
	public int AutoguidanceActivationMode { get; set; }

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyInteger("Autoguidance Scope", 0, 2, "0", Order = 3, RequireRestart = false, HintText = "0=Main Projectile Only, 1=Split Projectiles Only, 2=Main + Split Projectiles.")]
	public int AutoguidanceScope { get; set; } = 2;

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyInteger("Target Selection", 0, 2, "0", Order = 4, RequireRestart = false, HintText = "0=Closest Reachable Enemy: nearest enemy that the missile can still intercept under its current turn authority. 1=Closest to Camera Aim: aim-biased among physically reachable enemies. 2=Absolute Closest Enemy: distance-first, with reachability still preferred over targets the missile has already overshot.")]
	public int AutoguidanceTargetSelection { get; set; }

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyBool("Automatic Target Reacquisition", Order = 5, RequireRestart = false, HintText = "Reacquires only when a cached target becomes invalid or after a pass-through consumes the current target. Searches are throttled and event-driven; valid locked targets never trigger global scans each frame.")]
	public bool AutoguidanceAutomaticReacquisition { get; set; } = true;

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyInteger("Split Target Distribution", 0, 1, "0", Order = 6, RequireRestart = false, HintText = "0=Unique Targets: distribute autonomous split arrows across separate enemies when possible. 1=Same Target: deliberately focus eligible autonomous projectiles on one target.")]
	public int AutoguidanceSplitTargetDistribution { get; set; }

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyInteger("Autoguided Split Behaviour", 0, 2, "0", Order = 7, RequireRestart = false, HintText = "0=Preserve Formation: autonomous leader guidance with followers governed by the selected formation. 1=Break Formation Near Targets: followers keep formation until terminal range, then independently guide to assigned heads. 2=Fully Independent: eligible split projectiles guide independently immediately.")]
	public int AutoguidanceSplitBehaviour { get; set; } = 1;

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Formation Break Distance", 2f, 80f, "0.0", Order = 8, RequireRestart = false, HintText = "In Break Formation Near Targets mode, an eligible split projectile detaches from formation when its assigned head target is within this distance.")]
	public float AutoguidanceFormationBreakDistance { get; set; } = 18f;

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyBool("Low-Cruise Rising Attack Profile", Order = 9, RequireRestart = false, HintText = "When physically feasible, autoguidance cruises close to terrain and begins a terminal climb late enough to strike the head from below. Close or curvature-starved shots remain direct head interceptions.")]
	public bool AutoguidanceLowRiseAttackProfile { get; set; } = true;

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Cruise Ground Clearance", 0.45f, 3f, "0.00", Order = 10, RequireRestart = false, HintText = "Preferred terrain-following height for the low-cruise phase. Lower values produce a more pronounced rising terminal strike but leave less terrain margin.")]
	public float AutoguidanceCruiseGroundClearance { get; set; } = 0.9f;

	[SettingPropertyGroup("Autonomous Guidance", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Preferred Rising Entry Angle", 2f, 20f, "0.0", Order = 11, RequireRestart = false, HintText = "Preferred upward terminal entry angle in degrees. This is a trajectory preference only: the existing minimum-turn-radius and speed limits remain authoritative, and direct head reachability overrides the profile at short range.")]
	public float AutoguidancePreferredRiseAngle { get; set; } = 8f;

	[SettingPropertyGroup("Autonomous Guidance/Trajectory Planner", GroupOrder = 1)]
	[SettingPropertyBool("Enable Multi-Target Trajectory Planning", Order = 15, RequireRestart = false, HintText = "Plans a short target chain before the first impact. The current approach is shaped to pass through the head while already aligning toward the next planned target, with a terrain-safe level exit instead of late single-target pursuit.")]
	public bool AutoguidanceMultiTargetTrajectoryPlanning { get; set; } = true;

	[SettingPropertyGroup("Autonomous Guidance/Trajectory Planner", GroupOrder = 1)]
	[SettingPropertyInteger("Targets Planned Ahead", 1, 6, "0", Order = 16, RequireRestart = false, HintText = "Maximum number of living targets retained in each projectile's event-driven route plan. Five provides enough look-ahead for penetration chains without adding per-frame agent scans.")]
	public int AutoguidancePlannedTargetCount { get; set; } = 5;

	[SettingPropertyGroup("Autonomous Guidance/Trajectory Planner", GroupOrder = 1)]
	[SettingPropertyBool("Avoid Trees and Obstacles", Order = 17, RequireRestart = false, HintText = "Uses bounded scene ray checks along the planned route and creates a temporary side or over-obstacle waypoint when a tree, wall, rock or other ray-cast obstacle blocks the path. Native collision remains authoritative.")]
	public bool AutoguidanceObstacleAvoidance { get; set; } = true;

	[SettingPropertyGroup("Autonomous Guidance/Trajectory Planner", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Obstacle Recheck Interval", 0.05f, 0.5f, "0.00", Order = 18, RequireRestart = false, HintText = "Game-time interval between obstacle rechecks for an actively guided projectile. Route construction itself remains event-driven; lower values react faster to newly exposed obstacles but perform more ray casts.")]
	public float AutoguidanceObstacleScanInterval { get; set; } = 0.12f;

	[SettingPropertyGroup("Autonomous Guidance/Trajectory Planner", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Obstacle Bypass Clearance", 1f, 8f, "0.0", Order = 19, RequireRestart = false, HintText = "Side/upward clearance in metres used when building a temporary bypass waypoint around a detected tree or static obstacle. Sideways bypasses are preferred over unnecessary climbs.")]
	public float AutoguidanceObstacleClearance { get; set; } = 3f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyBool("Enable Proximity Time Dilation", Order = 5, RequireRestart = false, HintText = "Starts guided shots at normal speed and progressively slows mission time as the guided swarm approaches the closest living enemy. Q/E activate a manual speed override for the remainder of the shot.")]
	public bool EnableProximityTimeDilation { get; set; } = true;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Far / Launch Time Speed", 0.05f, 1f, "0.00", Order = 6, RequireRestart = false, HintText = "Automatic time speed at launch, when no enemy is inside the slowdown radius, or at and beyond Slowdown Start Distance.")]
	public float ProximityFarTimeSpeed { get; set; } = 1f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Near Target Time Speed", 0.01f, 1f, "0.00", Order = 7, RequireRestart = false, HintText = "Base automatic time speed at and inside Full Slowdown Distance. High-speed compensation can moderately reduce this value further, subject to the configured cap.")]
	public float ProximityNearTimeSpeed { get; set; } = 0.20239355f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Slowdown Start Distance", 5f, 150f, "0.0", Order = 8, RequireRestart = false, HintText = "Distance in metres at which automatic slowdown begins. Farther enemies do not consume early-flight time.")]
	public float ProximitySlowdownStartDistance { get; set; } = 60f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Full Slowdown Distance", 0.5f, 30f, "0.0", Order = 9, RequireRestart = false, HintText = "Distance in metres at which Near Target Time Speed is fully reached.")]
	public float ProximityFullSlowdownDistance { get; set; } = 5.9999967f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Enemy Scan Interval", 0.02f, 0.25f, "0.00", Order = 10, RequireRestart = false, HintText = "Real-time interval between bounded proximity-map queries. The default is 0.05 seconds (20 queries per real second while guiding only).")]
	public float ProximityScanInterval { get; set; } = 0.05f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Time Response Rate", 1f, 30f, "0.0", Order = 11, RequireRestart = false, HintText = "Exponential response rate for smooth speed changes. Higher values follow the distance curve faster; lower values ease more gradually.")]
	public float ProximityTimeResponseRate { get; set; } = 30f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Reference Missile Speed", 10f, 250f, "0", Order = 12, RequireRestart = false, HintText = "Missile speed in metres/second that preserves the original distance-only slowdown curve. Faster live missiles receive additional but capped slowdown near targets. Default 70 m/s keeps ordinary arrows close to v1.0.10 behavior.")]
	public float ProximityReferenceMissileSpeed { get; set; } = 70f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Speed Compensation Strength", 0f, 2f, "0.00", Order = 13, RequireRestart = false, HintText = "Controls how quickly extra high-speed slowdown approaches its cap. 0 = disabled. Unlike v1.0.11, this never performs full inverse-speed compensation. The effect fades out toward Slowdown Start Distance.")]
	public float ProximitySpeedCompensationStrength { get; set; } = 2f;

	[SettingPropertyGroup("Proximity Time Dilation", GroupOrder = 1)]
	[SettingPropertyFloatingInteger("Maximum Extra High-Speed Slowdown", 0f, 0.75f, "0.00", Order = 14, RequireRestart = false, HintText = "Maximum fractional reduction applied on top of the normal distance curve for extremely fast missiles. Default 0.75 means speed compensation can reduce the distance-based time speed by at most 75%, preventing near-freeze behavior.")]
	public float ProximityMaximumExtraSlowdown { get; set; } = 0.75f;

	[SettingPropertyGroup("Split / Multi-Shot Formations", GroupOrder = 2)]
	[SettingPropertyInteger("Formation Mode", 0, 4, "0", Order = 0, RequireRestart = false, HintText = "0 = Shared Guidance (legacy), 1 = Orbital Wave, 2 = Horizontal Strike Line, 3 = Ring Orbit, 4 = Chevron. Formation modes keep the first/main arrow under direct mouse guidance and steer split arrows relative to it.")]
	public int SplitArrowFormationMode { get; set; } = 1;

	[SettingPropertyGroup("Split / Multi-Shot Formations", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Formation Radius / Spacing", 0.2f, 8f, "0.00", Order = 1, RequireRestart = false, HintText = "Base radius or slot spacing in metres. Used by Orbital Wave, Horizontal Strike Line, Ring Orbit and Chevron.")]
	public float SplitArrowFormationSpacing { get; set; } = 1.25f;

	[SettingPropertyGroup("Split / Multi-Shot Formations", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Formation Response", 0.5f, 20f, "0.0", Order = 2, RequireRestart = false, HintText = "Positional correction strength for secondary arrows. Followers also receive slot-motion feed-forward and adaptive turn authority so they can stay attached to a sharply steering, high-speed leader.")]
	public float SplitArrowFormationResponse { get; set; } = 4f;

	[SettingPropertyGroup("Split / Multi-Shot Formations", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Maximum Formation Catch-Up Speed", 1f, 5f, "0.0", Order = 3, RequireRestart = false, HintText = "Maximum temporary follower speed as a multiple of the leader/follower baseline when a split arrow has fallen out of its slot. The boost tapers to normal automatically as the slot error closes and never accelerates the leader itself.")]
	public float SplitArrowFormationCatchUpSpeedLimit { get; set; } = 3f;

	[SettingPropertyGroup("Split / Multi-Shot Formations", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Orbit Speed", 0f, 720f, "0", Order = 4, RequireRestart = false, HintText = "Degrees per game second for Orbital Wave and Ring Orbit. Zero freezes their angular phase while preserving the selected formation geometry.")]
	public float SplitArrowOrbitSpeed { get; set; } = 220f;

	[SettingPropertyGroup("Split / Multi-Shot Formations", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Orbital Wave Frequency", 0.1f, 4f, "0.00", Order = 5, RequireRestart = false, HintText = "Inward/outward radius oscillations per game second in Orbital Wave mode. The arrows repeatedly converge close to the main arrow and diverge again while orbiting it.")]
	public float SplitArrowWaveFrequency { get; set; } = 1.2f;

	[SettingPropertyGroup("Projectile Camera", GroupOrder = 2)]
	[SettingPropertyInteger("Camera Mode", 0, 1, "0", Order = 10, RequireRestart = false, HintText = "0 = First Person (camera travels as the arrow), 1 = Third Person (locked projectile-relative chase camera).")]
	public int ProjectileCameraMode { get; set; } = 1;

	[SettingPropertyGroup("Projectile Camera", GroupOrder = 2)]
	[SettingPropertyBool("Show Crosshair", Order = 11, RequireRestart = false, HintText = "Shows a centered crosshair while actively guiding an arrow or bolt.")]
	public bool ShowCrosshair { get; set; }

	[SettingPropertyGroup("Projectile Camera/First Person", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Rear Offset", 0.01f, 1.5f, "0.00", Order = 12, RequireRestart = false, HintText = "Places the first-person camera this far behind the projectile while looking exactly along its flight path.")]
	public float FirstPersonRearOffset { get; set; } = 0.12f;

	[SettingPropertyGroup("Projectile Camera/First Person", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Vertical Offset", -0.5f, 0.5f, "0.00", Order = 13, RequireRestart = false, HintText = "Small vertical offset in the arrow's local frame. Zero gives a true arrow-eye view.")]
	public float FirstPersonVerticalOffset { get; set; }

	[SettingPropertyGroup("Projectile Camera/Third Person", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Locked Distance", 0.5f, 15f, "0.00", Order = 14, RequireRestart = false, HintText = "Fixed chase-camera distance from the projectile.")]
	public float CameraDistance { get; set; } = 3.4f;

	[SettingPropertyGroup("Projectile Camera/Third Person", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Locked Elevation Angle", -30f, 60f, "0.0", Order = 15, RequireRestart = false, HintText = "Camera elevation relative to the arrow's flight angle. The full rig pitches and yaws with the arrow.")]
	public float ThirdPersonElevationAngle { get; set; } = 12f;

	[SettingPropertyGroup("Projectile Camera/Third Person", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Look-Ahead Distance", 0f, 25f, "0.0", Order = 16, RequireRestart = false, HintText = "How far ahead of the projectile the chase camera looks. Higher values emphasize the future flight path.")]
	public float ThirdPersonLookAhead { get; set; } = 5f;

	[SettingPropertyGroup("Projectile Camera", GroupOrder = 2)]
	[SettingPropertyBool("Use Full-Detail Projectile Models", Order = 17, RequireRestart = false, HintText = "Persistently hides Bannerlord's simplified flying render tree and overlays one full-detail arrow/bolt model with its local +Z projectile axis aligned to live missile velocity. No native missile components are removed or transferred.")]
	public bool UseFullDetailProjectileModels { get; set; } = true;

	[SettingPropertyGroup("Projectile Camera/Group Framing", GroupOrder = 2)]
	[SettingPropertyBool("Frame All Controlled Split Arrows", Order = 18, RequireRestart = false, HintText = "Uses an adaptive chase camera that keeps every still-flying controlled projectile inside the view until guidance hands off to the kill cinematic. Camera ownership may transfer only between exact siblings from the current shot.")]
	public bool FrameAllControlledSplitProjectiles { get; set; } = true;

	[SettingPropertyGroup("Projectile Camera/Group Framing", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Split-Arrow Framing Padding", 0.15f, 3f, "0.00", Order = 19, RequireRestart = false, HintText = "Additional margin around the robust average projectile group. This no longer expands to include arbitrarily distant outliers.")]
	public float SplitProjectileCameraPadding { get; set; } = 0.75f;

	[SettingPropertyGroup("Projectile Camera/Group Framing", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Split-Arrow Maximum Framing Radius", 2f, 20f, "0.0", Order = 20, RequireRestart = false, HintText = "Maximum spread that strongly influences group framing. More distant controlled arrows remain guided but receive rapidly decreasing camera weight, preventing one runaway follower from making the frame enormous.")]
	public float SplitProjectileCameraMaximumFramingRadius { get; set; } = 6f;

	[SettingPropertyGroup("Projectile Camera/Group Framing", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Split-Arrow Maximum Camera Distance", 5f, 30f, "0.0", Order = 21, RequireRestart = false, HintText = "Hard ceiling for adaptive split-projectile framing. The normal single-projectile camera distance remains unchanged.")]
	public float SplitProjectileCameraMaximumDistance { get; set; } = 14f;

	[SettingPropertyGroup("Projectile Camera/Projectile Effects", GroupOrder = 2)]
	[SettingPropertyInteger("Waywatcher / Magic Arrow Flight Effect", 0, 2, "0", Order = 22, RequireRestart = false, HintText = "0=Native flight effects. 1=Scale effects on the camera-framed projectile group. 2=Scale effects on every controlled projectile. Only particle systems attached to the flying missile are changed; the separate impact effect remains native.")]
	public int GuidedProjectileParticleEffectMode { get; set; } = 1;

	[SettingPropertyGroup("Projectile Camera/Projectile Effects", GroupOrder = 2)]
	[SettingPropertyFloatingInteger("Magic Flight Effect Size", 0f, 1f, "0.00", Order = 23, RequireRestart = false, HintText = "Scales particle-emitting child entities attached to the flying projectile. 1.00 is native size, 0.25 is subtle, and 0 pauses only the flight-attached particles. Collision/impact particles are not modified.")]
	public float GuidedProjectileFlightEffectScale { get; set; } = 0.25f;

	public float CameraHeight { get; set; } = 0.55f;

	public float CameraPositionSmoothing { get; set; } = 18f;

	public float CameraRotationSmoothing { get; set; } = 22f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyInteger("Cinematic Trigger", 0, 1, "0", Order = 19, RequireRestart = false, HintText = "0 = First Kill: start the kill cinematic immediately on the first confirmed kill, ending active guidance. 1 = Final Rest / Last Kill: keep steering through penetrations, remember the most recent confirmed kill, and start its cinematic only when the guided missile/swarm reaches its final resting place.")]
	public int CinematicTriggerMode { get; set; } = 1;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyInteger("Cinematic Mode", 0, 2, "0", Order = 20, RequireRestart = false, HintText = "0 = Fixed Duration, 1 = Until the live ragdoll settles, 2 = Until native corpse finalization. Modes 1 and 2 also obey the adjustable minimum and hard maximum durations.")]
	public int CinematicMode { get; set; } = 1;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Fixed Cinematic Duration", 0.1f, 10f, "0.0", Order = 21, RequireRestart = false, HintText = "Duration used by Cinematic Mode 0. Minimum is 0.1 seconds.")]
	public float FixedCinematicDuration { get; set; } = 5.026758f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Cinematic Time Speed", 0.05f, 1f, "0.00", Order = 22, RequireRestart = false, HintText = "Fixed mission speed while watching a confirmed kill cinematic. This is independent of missile speed and Proximity Time Dilation.")]
	public float CinematicTimeSpeed { get; set; } = 0.20105767f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Settled Motion Threshold", 0.002f, 0.2f, "0.000", Order = 23, RequireRestart = false, HintText = "Maximum movement between 0.1 second samples before a corpse is considered settled in Mode 1.")]
	public float SettledMotionThreshold { get; set; } = 0.025f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Settled Hold Time", 0.1f, 3f, "0.0", Order = 24, RequireRestart = false, HintText = "How long movement must remain below the threshold before Mode 1 ends.")]
	public float SettledHoldTime { get; set; } = 0.5f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Full Cinematic Failsafe", 5f, 60f, "0.0", Order = 25, RequireRestart = false, HintText = "Real-time failsafe for Mode 2 if the corpse never reaches Bannerlord's native NeedsDeactivation/finalization boundary.")]
	public float FullCinematicTimeout { get; set; } = 30f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Cinematic Camera Distance", 1.5f, 12f, "0.0", Order = 26, RequireRestart = false, HintText = "Base close-up distance used for a single kill. Multi-kill framing expands only when the hit group no longer fits at this distance.")]
	public float CinematicCameraDistance { get; set; } = 4f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Multi-Kill Framing Padding", 0.25f, 3f, "0.00", Order = 27, RequireRestart = false, HintText = "Body-space margin added around the adaptive multi-kill framing volume. Close victims stay near the base camera distance; wider groups automatically pan/zoom out only as much as needed.")]
	public float CinematicMultiKillFramingPadding { get; set; } = 0.70033044f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Multi-Kill Maximum Distance", 2f, 60f, "0.0", Order = 28, RequireRestart = false, HintText = "True safety ceiling for adaptive multi-kill camera distance. v1.0.12 incorrectly forced this to at least 12 m internally; this value is now respected down to the base cinematic distance.")]
	public float CinematicMultiKillMaximumDistance { get; set; } = 24.981403f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Cinematic Orbit Speed", -120f, 120f, "0.0", Order = 29, RequireRestart = false, HintText = "Degrees per second for the classic orbiting kill-camera pan. Negative values reverse the direction; zero disables orbit movement.")]
	public float CinematicOrbitSpeed { get; set; } = 32f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Cinematic Elevation Angle", -20f, 60f, "0.0", Order = 30, RequireRestart = false, HintText = "Vertical angle of the orbiting camera around the embedded arrow and moving corpse.")]
	public float CinematicElevationAngle { get; set; } = 18f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Adaptive Minimum Duration", 0.25f, 3f, "0.00", Order = 31, RequireRestart = false, HintText = "Minimum real-time viewing duration for Modes 1 and 2. Prevents an already-static agent root or an early finalization state from snapping immediately back to the player.")]
	public float AdaptiveCinematicMinimumDuration { get; set; } = 1f;

	[SettingPropertyGroup("Kill Cinematic", GroupOrder = 3)]
	[SettingPropertyFloatingInteger("Adaptive Maximum Duration", 1f, 8f, "0.00", Order = 32, RequireRestart = false, HintText = "Hard real-time maximum for Modes 1 and 2. Mode 2 still observes native corpse finalization, but never holds the camera indefinitely waiting for a stationary corpse.")]
	public float AdaptiveCinematicMaximumDuration { get; set; } = 4f;

	[SettingPropertyGroup("Return Transition", GroupOrder = 4)]
	[SettingPropertyFloatingInteger("Return Flight Duration", 0.08f, 2f, "0.00", Order = 30, RequireRestart = false, HintText = "Duration of the eased camera flight back to the player. The camera travels back instead of teleporting.")]
	public float ReturnDuration { get; set; } = 0.32f;

	[SettingPropertyGroup("Return Transition", GroupOrder = 4)]
	[SettingPropertyFloatingInteger("Miss / Non-Kill Return Duration", 0.02f, 0.5f, "0.00", Order = 31, RequireRestart = false, HintText = "Fast camera return used immediately after terrain hits, misses, stopped arrows and confirmed non-kills.")]
	public float MissReturnDuration { get; set; } = 0.1f;

	[SettingPropertyGroup("Diagnostics", GroupOrder = 4)]
	[SettingPropertyBool("Debug Logging", Order = 40, RequireRestart = false, HintText = "Writes concise state-transition diagnostics to Documents/Mount and Blade II Bannerlord/Configs/ModLogs/GuidedArrow.log.")]
	public bool DebugLogging { get; set; }
}
