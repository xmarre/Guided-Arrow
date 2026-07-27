using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using MCM.Abstractions.Base.Global;
using TaleWorlds.Core;
using TaleWorlds.DotNet;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

namespace GuidedArrow;

public sealed class GuidedArrowBehavior : MissionView
{
	private enum State
	{
		Idle,
		WaitingForMissile,
		Guiding,
		ImpactPending,
		Cinematic,
		Returning
	}

	private enum AutoguidanceFlightProfile
	{
		LowStrike,
		NaturalBallistic,
		DirectHunter,
		LoftedArc,
		BankingFlank,
		Serpentine,
		AdaptiveMix
	}

	private sealed class PendingShotSeed
	{
		public Agent Shooter;

		public int ShotGeneration;

		public int ForcedIndex;

		public Vec3 Position;

		public Vec3 Velocity;

		public float CreatedAtAcquireElapsed;

		public bool Resolved;
	}

	private sealed class QueuedAlliedShot
	{
		public Agent Shooter;

		public int ForcedIndex;

		public Vec3 Position;

		public Vec3 Velocity;

		public long QueuedTimestamp;
	}

	private sealed class NativeVisibilityState
	{
		public GameEntity Entity;

		public bool WasVisible;
	}

	private sealed class ParticleScaleState
	{
		public GameEntity Entity;

		public MatrixFrame OriginalLocalFrame;

		public bool OriginalFrameValid;

		public bool PausedByGuidedArrow;
	}

	private sealed class TrackedMissile
	{
		public Mission.Missile Missile;

		public Agent OriginalShooter;

		public int ShotGeneration;

		public int Index;

		public int FormationSlot;

		public bool AwaitingCollisionReaction;

		public GameEntity IdentityEntity;

		public ItemObject IdentityItem;

		public Vec3 LastFormationTarget;

		public bool LastFormationTargetValid;

		public GameEntity NativeEntity;

		public GameEntity VisualSourceEntity;

		public GameEntity VisualEntity;

		public MetaMesh FullDetailMesh;

		public readonly List<NativeVisibilityState> HiddenNativeEntities = new List<NativeVisibilityState>();

		public Agent GuidanceTarget;

		public int GuidanceHeadBoneIndex = -1;

		public Vec3 GuidanceSmoothedHead;

		public bool GuidanceSmoothedHeadValid;

		public Vec3 GuidanceLastRawHead;

		public bool GuidanceLastRawHeadValid;

		public Vec3 GuidanceTargetVelocity;

		public bool GuidanceTargetVelocityValid;

		public bool GuidanceBrokenFromFormation;

		public readonly List<Agent> GuidanceConsumedTargets = new List<Agent>();

		public float GuidanceTerrainSampleCountdown;

		public float GuidanceCruiseGroundZ;

		public bool GuidanceCruiseGroundValid;

		public float GuidanceSafetyTerrainSampleCountdown;

		public float GuidanceSafetyCurrentGroundZ;

		public float GuidanceSafetyMaximumGroundZ;

		public bool GuidanceSafetyGroundValid;

		public Vec3 GuidanceSafetySampleDirection;

		public bool GuidanceSafetySampleDirectionValid;

		public Vec3 GuidanceLaunchPosition;

		public Vec3 GuidanceLaunchVelocity;

		public bool GuidanceLaunchStateValid;

		public float GuidanceFlightElapsed;

		public int GuidanceConfiguredProfileIndex = -1;

		public int GuidanceResolvedProfileIndex;

		public bool GuidanceResolvedProfileValid;

		public bool GuidanceRecoveryActive;

		public Vec3 GuidanceRecoveryTurnAxis;

		public bool GuidanceRecoveryTurnAxisValid;

		public float GuidanceRecoveryTurnedRadians;

		public int GuidanceRecoveryReplanCount;

		public bool GuidanceForceDirectIntercept;

		public float EstimatedGravityZ = -9.81f;

		public Vec3 LastCommandedVelocity;

		public bool LastCommandedVelocityValid;

		public Vec3 ManualSteeringRight;

		public bool ManualSteeringRightValid;

		public readonly List<Agent> GuidanceRouteTargets = new List<Agent>();

		public Vec3 GuidanceObstacleWaypoint;

		public bool GuidanceObstacleWaypointValid;

		public Vec3 GuidanceObstacleGoal;

		public bool GuidanceObstacleGoalValid;

		public float GuidanceObstacleRecheckCountdown;

		public bool GuidanceRouteReplanRequested;

		public float GuidanceNoProgressElapsed;

		public float GuidanceLastTargetDistance;

		public bool GuidanceLastTargetDistanceValid;

		public Vec3 GuidanceFallbackDirection;

		public bool GuidanceFallbackDirectionValid;

		public MissionWeapon SpawnWeapon;

		public bool SpawnWeaponValid;

		public MissileDamageBridge.ResolvedLaunchData ResolvedLaunchData;

		public Mat3 SpawnOrientation;

		public bool SpawnOrientationValid;

		public float SpawnBaseSpeed;

		public bool SpawnHasRigidBody;

		public int PenetrationsUsed;

		public bool SyntheticProjectile;

		public float ProjectileParticlePolicyCountdown;

		public float AppliedProjectileParticleScale = -1f;

		public bool ParticleDiscoveryLockedAfterImpact;

		public readonly List<ParticleScaleState> ScaledParticleEntities = new List<ParticleScaleState>();
	}

	private sealed class PendingHitRecord
	{
		public Agent Victim;

		public int MissileIndex = -1;

		public int CollisionBoneIndex;

		public GameEntity ArrowEntity;

		public Vec3 ImpactDirection;

		public Vec3 ImpactPosition;
	}

	private sealed class PendingCollisionContext
	{
		public int MissileIndex = -1;

		public Agent Attacker;

		public Agent Victim;

		public Vec3 ImpactPosition;

		public Vec3 ImpactVelocity;

		public Vec3 ImpactDirection;

		public long CreatedTimestamp;
	}

	private sealed class EarlyCollisionReaction
	{
		public Mission.MissileCollisionReaction Reaction;

		public Agent Attacker;

		public Agent AttachedAgent;

		public long CreatedTimestamp;
	}

	private sealed class PendingContinuationSpawn
	{
		public TrackedMissile Source;

		public PendingCollisionContext Collision;

		public bool WasCameraOwner;

		public bool WasFormationLeader;

		public int PenetrationsUsed;
	}

	private sealed class PendingNativeMissileRemoval
	{
		public int Index = -1;

		public GameEntity IdentityEntity;

		public Agent Shooter;

		public int ShotGeneration;
	}

	private sealed class CinematicSubjectRecord
	{
		public Agent Agent;

		public Vec3 LastKnownPosition;

		public bool HasLastKnownPosition;

		public Vec3 LastSamplePosition;

		public bool LastSampleValid;

		public bool ConfirmedKill;
	}

	private sealed class CrosshairViewModel : ViewModel
	{
	}

	private sealed class AutoguidanceHotkeyChord
	{
		public string SourceText;

		public bool RequireControl;

		public bool RequireShift;

		public bool RequireAlt;

		public InputKey[] Keys = (InputKey[])(object)new InputKey[0];

		public bool IsValid;
	}

	private const int GuidanceTimeRequestId = 1195463255;

	private const int CinematicTimeRequestId = 1195459401;

	private const float MissileAcquireWindow = 0.25f;

	private const float SplitSiblingAcquireWindow = 0.1f;

	private const float SplitSiblingMaximumTravel = 12f;

	private const float RestPollInterval = 0.1f;

	private const float ImpactKillConfirmationWindow = 0.04f;

	private const float AutomaticMinimumTimeSpeed = 0.01f;

	private const float MovingMissileSpeedThresholdSquared = 0.0625f;

	private const float CollisionReactionTimeoutSeconds = 0.05f;

	private const int MaxPendingCollisionContexts = 32;

	private const int MaxTrackedSwarmMissiles = 64;

	private const int MaxQueuedAlliedTakeovers = 8;

	private const float AlliedTakeoverMaximumQueueAge = 6f;

	private const float MaximumShotOriginDistance = 6f;

	private const int MaxAutoguidanceCandidates = 96;

	private const float AutoguidanceAcquisitionRadius = 150f;

	private const float AutoguidanceReacquireInterval = 0.15f;

	private const float AutoguidanceTerrainSampleInterval = 0.08f;

	private const float AutoguidanceMinimumPostImpactTravel = 8f;

	private const float AutoguidanceMinimumReachabilityReserve = 0.75f;

	private const float AutoguidanceRecoveryReserve = 2f;

	private const int MaxAutoguidanceRouteCandidatePool = 24;

	private const float AutoguidancePostImpactPreviewDistance = 11f;

	private const float AutoguidancePostImpactGroundClearance = 1.15f;

	private const float AutoguidanceObstacleTargetMargin = 0.9f;

	private const float AutoguidanceNoProgressReplanSeconds = 1.1f;

	private const float AutoguidanceProgressEpsilon = 0.18f;

	private const float ParticlePolicyRefreshInterval = 0.2f;

	private const float SyntheticSplitMaximumSpreadRadians = 0.0105f;

	private const float SyntheticPenetrationSpawnOffset = 0.42f;

	private const float SplitCameraMinimumGroundClearance = 0.85f;

	private const float DefaultGravityZ = -9.81f;

	private const float Tiny = 1E-06f;

	private static Type _cachedBoneFrameSkeletonType;

	private static MethodInfo _cachedBoneFrameMethod;

	private static Type _cachedRayCastSceneType;

	private static MethodInfo[] _cachedRayCastMethods;

	private static readonly float[] SpeedSteps = new float[12]
	{
		0.01f, 0.02f, 0.03f, 0.04f, 0.05f, 0.1f, 0.15f, 0.25f, 0.4f, 0.6f,
		0.8f, 1f
	};

	private State _state;

	private Mission.Missile _missile;

	private int _missileIndex = -1;

	private int _cameraMissileIndex = -1;

	private readonly List<TrackedMissile> _trackedMissiles = new List<TrackedMissile>();

	private readonly List<PendingShotSeed> _pendingShotSeeds = new List<PendingShotSeed>();

	private readonly List<QueuedAlliedShot> _queuedAlliedShots = new List<QueuedAlliedShot>();

	private Agent _activeShotShooter;

	private int _shotGeneration;

	private int _activeShotGeneration;

	private bool _alliedTakeoverChainArmed;

	private Vec3 _pendingShotPosition;

	private Vec3 _pendingShotVelocity;

	private Mat3 _pendingShotOrientation;

	private bool _pendingShotHasRigidBody;

	private bool _standaloneSplitSpawned;

	private bool _standaloneSplitDamagePacketWaitLogged;

	private bool _nativeSplitBatchDetected;

	private float _pendingAcquireElapsed;

	private long _splitSiblingAcquireStartTimestamp;

	private bool _splitSiblingAcquisitionClosed;

	private float _guidanceRealElapsed;

	private float _impactConfirmElapsed;

	private long _impactConfirmLastTimestamp;

	private Vec3 _shotDirection;

	private Vec3 _lastMissileDirection;

	private float _pendingYawInput;

	private float _pendingPitchInput;

	private float _formationElapsed;

	private readonly MBList<Agent> _autoguidanceCandidates = new MBList<Agent>();

	private readonly List<Agent> _autoguidanceRankCandidates = new List<Agent>();

	private readonly List<Vec3> _autoguidanceCandidateHeads = new List<Vec3>();

	private readonly List<Agent> _autoguidanceAssignedTargets = new List<Agent>();

	private readonly Dictionary<long, bool> _autoguidanceRouteObstacleCache = new Dictionary<long, bool>();

	private readonly Dictionary<long, float> _autoguidanceRouteTerrainCache = new Dictionary<long, float>();

	private bool _autoguidanceToggleActive;

	private bool _autoguidanceBattleToggleActive;

	private bool _autoguidanceEffectiveActive;

	private bool _autoguidanceHadAnyTarget;

	private float _autoguidanceReacquireCountdown;

	private AutoguidanceHotkeyChord _autoguidanceHotkeyChord;

	private readonly MBList<Agent> _proximityCandidates = new MBList<Agent>();

	private float _proximityScanCountdown;

	private float _proximityTargetSpeed = 1f;

	private float _proximityCurrentSpeed = 1f;

	private float _closestProximityDistance = float.PositiveInfinity;

	private Agent _closestProximityEnemy;

	private float _proximityLiveMissileSpeed;

	private bool _manualTimeOverride;

	private long _guidanceLastTimestamp;

	private MatrixFrame _cameraFrame;

	private bool _cameraFrameValid;

	private MatrixFrame _returnStartFrame;

	private float _returnElapsed;

	private float _returnDurationOverride;

	private bool _releaseCameraAfterOverride;

	private bool _releaseCustomCameraNextDisplay;

	private Camera _ownedCustomCamera;

	private Camera _previousCustomCamera;

	private bool _ownsCustomCamera;

	private bool _cameraOwnershipFailureLogged;

	private GauntletLayer _crosshairLayer;

	private GauntletMovieIdentifier _crosshairMovie;

	private CrosshairViewModel _crosshairDataSource;

	private MissionScreen _crosshairScreen;

	private readonly List<MissionView> _suspendedNativeCrosshairViews = new List<MissionView>();

	private bool _nativeCrosshairViewsSuspended;

	private float _returnLocalRight;

	private float _returnLocalForward;

	private float _returnLocalUp;

	private Vec3 _returnViewForwardLocal;

	private Vec3 _returnViewUpLocal;

	private Agent _hitVictim;

	private readonly List<PendingHitRecord> _pendingHitVictims = new List<PendingHitRecord>();

	private readonly List<PendingCollisionContext> _pendingCollisionContexts = new List<PendingCollisionContext>();

	private readonly List<EarlyCollisionReaction> _earlyCollisionReactions = new List<EarlyCollisionReaction>();

	private readonly List<PendingContinuationSpawn> _pendingContinuationSpawns = new List<PendingContinuationSpawn>();

	private readonly List<PendingNativeMissileRemoval> _pendingNativeMissileRemovals = new List<PendingNativeMissileRemoval>();

	private readonly List<CinematicSubjectRecord> _cinematicSubjects = new List<CinematicSubjectRecord>();

	private int _confirmedCinematicKillCount;

	private Agent _deferredCinematicVictim;

	private GameEntity _deferredCinematicArrowEntity;

	private int _deferredCinematicCollisionBoneIndex = -1;

	private Vec3 _deferredImpactDirection;

	private Vec3 _deferredImpactPosition;

	private Agent _cinematicVictim;

	private GameEntity _cinematicArrowEntity;

	private int _cinematicCollisionBoneIndex = -1;

	private Vec3 _impactDirection;

	private Vec3 _impactPosition;

	private float _cinematicElapsed;

	private long _cinematicLastTimestamp;

	private float _restPollCountdown;

	private Vec3 _lastRestPosition;

	private bool _lastRestPositionValid;

	private float _settledElapsed;

	private bool _cinematicSawActiveRagdoll;

	private bool _timeRequestActive;

	private float _requestedSpeed = 1f;

	private bool _cinematicTimeRequestActive;

	private float _cinematicRequestedSpeed = 1f;

	private static readonly Vec3 WorldUp = new Vec3(0f, 0f, 1f, -1f);

	public override MissionBehaviorType BehaviorType => (MissionBehaviorType)1;

	private bool Enabled => GlobalSettings<Settings>.Instance?.Enabled ?? true;

	private static string LogPath
	{
		get
		{
			string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
			return System.IO.Path.Combine(folderPath, "Mount and Blade II Bannerlord", "Configs", "ModLogs", "GuidedArrow.log");
		}
	}

	public override void OnBehaviorInitialize()
	{
		_autoguidanceBattleToggleActive = false;
		ResetAll(behaviorRemoving: false);
		Log("Mission behavior initialized.");
	}

	public override void OnRemoveBehavior()
	{
		_autoguidanceBattleToggleActive = false;
		MissileDamageBridge.ClearMission(((MissionBehavior)this).Mission);
		ResetAll(behaviorRemoving: true);
		Log("Mission behavior removed.");
	}

	public override void OnAgentShootMissile(Agent shooterAgent, EquipmentIndex weaponIndex, Vec3 position, Vec3 velocity, Mat3 orientation, bool hasRigidBody, int forcedMissileIndex)
	{
		if (!Enabled || ((MissionBehavior)this).Mission == null || shooterAgent == null)
		{
			return;
		}
		Agent mainAgent = ((MissionBehavior)this).Mission.MainAgent;
		if (mainAgent == null)
		{
			return;
		}
		bool isPlayerShot = shooterAgent == mainAgent;
		Settings instance = GlobalSettings<Settings>.Instance;
		bool isAlliedTakeover = instance != null && instance.EnableAlliedArrowTakeover && _alliedTakeoverChainArmed && IsFriendlyAlliedShooter(shooterAgent, mainAgent);
		if (_state != State.Idle)
		{
			if (isAlliedTakeover)
			{
				QueueAlliedTakeover(shooterAgent, forcedMissileIndex, position, velocity);
			}
			else if (shooterAgent == _activeShotShooter &&
				_state != State.ImpactPending &&
				_state != State.Cinematic &&
				_state != State.Returning &&
				IsShotOriginConsistentWithShooter(shooterAgent, position))
			{
				AddPendingShotSeed(shooterAgent, _activeShotGeneration, forcedMissileIndex, position, velocity);
				_nativeSplitBatchDetected = true;
				_splitSiblingAcquisitionClosed = false;
				_splitSiblingAcquireStartTimestamp = Stopwatch.GetTimestamp();
				TryAcquireMissiles();
				Log("Captured an additional native multi-shot projectile callback for guided-shot generation " + _activeShotGeneration + ".");
			}
			else
			{
				Log("Ignored unrelated missile callback while guided-shot generation " + _activeShotGeneration + " remained active.");
			}
		}
		else if (isPlayerShot)
		{
			if (!IsShotOriginConsistentWithShooter(shooterAgent, position))
			{
				Log("Rejected idle missile callback whose launch origin did not match the player.");
			}
			else
			{
				StartGuidedShot(shooterAgent, forcedMissileIndex, position, velocity, orientation, hasRigidBody, armAlliedChain: true);
			}
		}
	}

	private void StartGuidedShot(Agent shooterAgent, int forcedMissileIndex, Vec3 position, Vec3 velocity, Mat3 orientation, bool hasRigidBody, bool armAlliedChain)
	{
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0154: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		if (((MissionBehavior)this).Mission != null && shooterAgent != null)
		{
			ResetTrackedVictim();
			ResetCinematicSubjects();
			ClearDeferredCinematicKill();
			RemoveOwnTimeRequest();
			ResetGuidanceTimeControl();
			ResetAutoguidanceState(notify: false);
			HideCrosshair();
			CleanupTrackedMissiles(removeVisuals: true);
			ReleaseCustomCameraOwnership("NewShotSuperseded");
			_pendingShotSeeds.Clear();
			_pendingCollisionContexts.Clear();
			_earlyCollisionReactions.Clear();
			if (armAlliedChain)
			{
				_queuedAlliedShots.Clear();
			}
			_activeShotShooter = shooterAgent;
			_activeShotGeneration = ++_shotGeneration;
			AddPendingShotSeed(shooterAgent, _activeShotGeneration, forcedMissileIndex, position, velocity);
			_pendingShotPosition = position;
			_pendingShotVelocity = velocity;
			_pendingShotOrientation = orientation;
			_pendingShotHasRigidBody = hasRigidBody;
			_standaloneSplitSpawned = false;
			_standaloneSplitDamagePacketWaitLogged = false;
			_nativeSplitBatchDetected = false;
			_pendingContinuationSpawns.Clear();
			_pendingNativeMissileRemovals.Clear();
			_pendingAcquireElapsed = 0f;
			_splitSiblingAcquireStartTimestamp = Stopwatch.GetTimestamp();
			_splitSiblingAcquisitionClosed = false;
			_guidanceRealElapsed = 0f;
			_impactConfirmElapsed = 0f;
			_impactConfirmLastTimestamp = 0L;
			_releaseCameraAfterOverride = false;
			_releaseCustomCameraNextDisplay = false;
			_returnDurationOverride = 0f;
			_shotDirection = NormalizeSafe(velocity, shooterAgent.LookDirection);
			_lastMissileDirection = _shotDirection;
			_pendingYawInput = 0f;
			_pendingPitchInput = 0f;
			_formationElapsed = 0f;
			_cameraMissileIndex = -1;
			_cinematicArrowEntity = null;
			ResetCinematicBoneAnchor();
			CaptureReturnPose(((MissionBehavior)this).Mission.MainAgent);
			_state = State.WaitingForMissile;
			if (armAlliedChain)
			{
				_alliedTakeoverChainArmed = true;
			}
			TryAcquireMissiles();
		}
	}

	private bool IsFriendlyAlliedShooter(Agent shooter, Agent player)
	{
		if (shooter == null || player == null || shooter == player)
		{
			return false;
		}
		try
		{
			return shooter.Team != null && player.Team != null && shooter.Team == player.Team;
		}
		catch
		{
			return false;
		}
	}

	private bool IsShotOriginConsistentWithShooter(Agent shooter, Vec3 position)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		if (shooter == null || !IsFinite(position))
		{
			return false;
		}
		try
		{
			Vec3 val = position - shooter.Position;
			float lengthSquared = val.LengthSquared;
			return IsFinite(lengthSquared) && lengthSquared <= 36f;
		}
		catch
		{
			return false;
		}
	}

	private bool IsSplitSiblingAcquisitionOpen()
	{
		if (_splitSiblingAcquisitionClosed || _state == State.ImpactPending || _state == State.Cinematic || _state == State.Returning)
		{
			return false;
		}
		if (_splitSiblingAcquireStartTimestamp <= 0)
		{
			return false;
		}
		double elapsed = (double)(Stopwatch.GetTimestamp() - _splitSiblingAcquireStartTimestamp) / (double)Stopwatch.Frequency;
		if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0.0 || elapsed >= 0.75)
		{
			CloseSplitSiblingAcquisition("RealTimeLaunchWindowExpired");
			return false;
		}
		TrackedMissile leader = FindTrackedMissile(_missileIndex);
		if (leader != null)
		{
			try
			{
				Vec3 travelled = ((MBMissile)leader.Missile).GetPosition() - _pendingShotPosition;
				float distanceSquared = travelled.LengthSquared;
				if (IsFinite(distanceSquared) && distanceSquared > 1296f)
				{
					CloseSplitSiblingAcquisition("LeaderLeftLaunchEnvelope");
					return false;
				}
			}
			catch
			{
			}
		}
		return true;
	}

	private void CloseSplitSiblingAcquisition(string reason)
	{
		if (!_splitSiblingAcquisitionClosed)
		{
			_splitSiblingAcquisitionClosed = true;
			_pendingShotSeeds.Clear();
			Log("Split-shot sibling acquisition closed: " + reason + ".");
		}
	}

	private void QueueAlliedTakeover(Agent shooter, int forcedMissileIndex, Vec3 position, Vec3 velocity)
	{
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance == null || !instance.EnableAlliedArrowTakeover || !_alliedTakeoverChainArmed || shooter == null)
		{
			return;
		}
		if (forcedMissileIndex < 0)
		{
			Log("Allied takeover ignored because the callback did not provide an exact missile index.");
			return;
		}
		for (int i = 0; i < _queuedAlliedShots.Count; i++)
		{
			if (_queuedAlliedShots[i].ForcedIndex == forcedMissileIndex)
			{
				return;
			}
		}
		while (_queuedAlliedShots.Count >= 8)
		{
			_queuedAlliedShots.RemoveAt(0);
		}
		_queuedAlliedShots.Add(new QueuedAlliedShot
		{
			Shooter = shooter,
			ForcedIndex = forcedMissileIndex,
			Position = position,
			Velocity = velocity,
			QueuedTimestamp = Stopwatch.GetTimestamp()
		});
		Log("Queued allied arrow #" + forcedMissileIndex + " for optional post-cinematic takeover.");
	}

	private bool TryStartQueuedAlliedTakeover()
	{
		//IL_0179: Unknown result type (might be due to invalid IL or missing references)
		//IL_017e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_0186: Unknown result type (might be due to invalid IL or missing references)
		//IL_019a: Unknown result type (might be due to invalid IL or missing references)
		//IL_019c: Unknown result type (might be due to invalid IL or missing references)
		//IL_019e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0131: Unknown result type (might be due to invalid IL or missing references)
		//IL_0136: Unknown result type (might be due to invalid IL or missing references)
		//IL_0138: Unknown result type (might be due to invalid IL or missing references)
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance == null || !instance.EnableAlliedArrowTakeover || !_alliedTakeoverChainArmed || ((MissionBehavior)this).Mission == null)
		{
			_queuedAlliedShots.Clear();
			return false;
		}
		while (_queuedAlliedShots.Count > 0)
		{
			QueuedAlliedShot queuedAlliedShot = _queuedAlliedShots[0];
			_queuedAlliedShots.RemoveAt(0);
			if (queuedAlliedShot == null || queuedAlliedShot.Shooter == null || queuedAlliedShot.ForcedIndex < 0)
			{
				continue;
			}
			double num = ((queuedAlliedShot.QueuedTimestamp > 0) ? ((double)(Stopwatch.GetTimestamp() - queuedAlliedShot.QueuedTimestamp) / (double)Stopwatch.Frequency) : double.MaxValue);
			if (double.IsNaN(num) || double.IsInfinity(num) || num < 0.0 || num > 6.0)
			{
				continue;
			}
			Agent mainAgent = ((MissionBehavior)this).Mission.MainAgent;
			if (!IsFriendlyAlliedShooter(queuedAlliedShot.Shooter, mainAgent))
			{
				continue;
			}
			Mission.Missile val = null;
			try
			{
				foreach (Mission.Missile item in (List<Mission.Missile>)(object)((MissionBehavior)this).Mission.MissilesList)
				{
					if (item != null && ((MBMissile)item).Index == queuedAlliedShot.ForcedIndex && item.ShooterAgent == queuedAlliedShot.Shooter && IsArrowOrBolt(item))
					{
						Vec3 velocity = ((MBMissile)item).GetVelocity();
						if (IsFinite(velocity) && !(velocity.LengthSquared <= 0.0625f))
						{
							val = item;
							break;
						}
					}
				}
			}
			catch
			{
				val = null;
			}
			if (val != null)
			{
				Vec3 position;
				Vec3 velocity2;
				try
				{
					position = ((MBMissile)val).GetPosition();
					velocity2 = ((MBMissile)val).GetVelocity();
				}
				catch
				{
					continue;
				}
				StartGuidedShot(queuedAlliedShot.Shooter, queuedAlliedShot.ForcedIndex, position, velocity2, Mat3.Identity, hasRigidBody: true, armAlliedChain: false);
				Log("Optional allied arrow takeover started for missile #" + queuedAlliedShot.ForcedIndex + ".");
				return true;
			}
		}
		_alliedTakeoverChainArmed = false;
		return false;
	}

	public override void OnMissileHit(Agent attacker, Agent victim, bool isCanceled, AttackCollisionData collisionData)
	{
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		if (_state != State.Guiding || isCanceled || attacker == null || ((MissionBehavior)this).Mission == null || attacker != _activeShotShooter || !collisionData.IsMissile)
		{
			return;
		}
		CloseSplitSiblingAcquisition("FirstMissileImpact");
		_standaloneSplitSpawned = true;
		_impactPosition = collisionData.CollisionGlobalPosition;
		if (IsFinite(collisionData.MissileVelocity))
		{
			Vec3 missileVelocity = collisionData.MissileVelocity;
			if (missileVelocity.LengthSquared > 1E-06f)
			{
				_impactDirection = NormalizeSafe(collisionData.MissileVelocity, _lastMissileDirection);
				goto IL_0099;
			}
		}
		_impactDirection = _lastMissileDirection;
		goto IL_0099;
		IL_0099:
		_cinematicCollisionBoneIndex = ((victim != null) ? collisionData.CollisionBoneIndex : (-1));
		int affectorWeaponSlotOrMissileIndex = collisionData.AffectorWeaponSlotOrMissileIndex;
		TrackedMissile trackedMissile = FindTrackedMissile(affectorWeaponSlotOrMissileIndex);
		GameEntity val = null;
		if (trackedMissile != null)
		{
			try
			{
				Mission.Missile missile = trackedMissile.Missile;
				val = ((missile != null) ? missile.Entity : null);
			}
			catch
			{
				val = null;
			}
			_cinematicArrowEntity = val;
			trackedMissile.AwaitingCollisionReaction = true;
			trackedMissile.ParticleDiscoveryLockedAfterImpact = true;
			AbandonNativePresentationHandlesAfterImpact(trackedMissile);
			trackedMissile.LastCommandedVelocityValid = false;
			QueuePendingCollisionContext(affectorWeaponSlotOrMissileIndex, attacker, victim, _impactPosition, collisionData.MissileVelocity, _impactDirection);
			if (affectorWeaponSlotOrMissileIndex == _cameraMissileIndex && !TryPromoteCameraOwnerWithinSwarm(affectorWeaponSlotOrMissileIndex))
			{
				SuspendProjectileCameraForCollisionReaction(affectorWeaponSlotOrMissileIndex);
			}
		}
		else
		{
			Log("Ignored missile hit for untracked exact missile index #" + affectorWeaponSlotOrMissileIndex + ".");
		}
		_impactConfirmElapsed = 0f;
		_impactConfirmLastTimestamp = 0L;
		if (victim == null)
		{
			if (trackedMissile != null)
			{
				trackedMissile.AwaitingCollisionReaction = false;
				RemovePendingCollisionContext(affectorWeaponSlotOrMissileIndex);
			}
			bool num = affectorWeaponSlotOrMissileIndex == _cameraMissileIndex;
			RemoveTrackedMissile(trackedMissile, removeVisual: true);
			if (num)
			{
				TryPromoteCameraOwnerWithinSwarm(affectorWeaponSlotOrMissileIndex);
			}
			if (_trackedMissiles.Count == 0)
			{
				HandleGuidedSwarmTerminal("AllMissilesWorldImpact");
				return;
			}
			UpdateCrosshairVisibility();
			Log("One split projectile hit the world; camera ownership remains with a moving sibling.");
			return;
		}
		TrackHitVictim(victim, affectorWeaponSlotOrMissileIndex, _cinematicCollisionBoneIndex, val, _impactDirection, _impactPosition);
		bool flag = false;
		try
		{
			float health = victim.Health;
			flag = IsFinite(health) && health <= 0f;
		}
		catch
		{
		}
		if (flag)
		{
			HandleConfirmedKill(victim, "OnMissileHitAlreadyDead");
		}
		if (_state == State.Guiding && trackedMissile != null && trackedMissile.AwaitingCollisionReaction)
		{
			TryConsumeEarlyCollisionReaction(affectorWeaponSlotOrMissileIndex);
		}
		if (_state == State.Guiding)
		{
			UpdateCrosshairVisibility();
			Log("Mission.Missile hit registered; awaiting native collision reaction.");
		}
	}

	public override void OnMissileRemoved(int missileIndex)
	{
		TrackedMissile trackedMissile = FindTrackedMissile(missileIndex);
		if (trackedMissile != null)
		{
			bool flag = trackedMissile.Index == _cameraMissileIndex;
			AbandonNativePresentationHandlesAfterImpact(trackedMissile);
			RemoveTrackedMissile(trackedMissile, removeVisual: true);
			if (_state == State.Guiding && flag)
			{
				TryPromoteCameraOwnerWithinSwarm(missileIndex);
			}
			if (_state == State.Guiding && _trackedMissiles.Count == 0)
			{
				HandleGuidedSwarmTerminal("SwarmRemoved");
			}
		}
	}

	public override void OnEarlyAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		ConfirmRemovalKillFallback(affectedAgent, affectorAgent, agentState, "OnEarlyAgentRemoved");
	}

	public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		ConfirmRemovalKillFallback(affectedAgent, affectorAgent, agentState, "OnAgentRemoved");
	}

	public override void OnAgentDeleted(Agent affectedAgent)
	{
		if (affectedAgent == null)
		{
			return;
		}
		SnapshotCinematicSubject(affectedAgent);
		RemoveTrackedVictim(affectedAgent);
		if (affectedAgent == _deferredCinematicVictim)
		{
			Agent val = FindReplacementCinematicVictim(affectedAgent);
			if (val != null)
			{
				_deferredCinematicVictim = val;
			}
		}
		if (affectedAgent == _cinematicVictim && _state == State.Cinematic)
		{
			Agent val2 = FindReplacementCinematicVictim(affectedAgent);
			if (val2 != null)
			{
				_cinematicVictim = val2;
			}
			else
			{
				BeginReturn("CinematicVictimDeleted");
			}
		}
	}

	public override bool UpdateOverridenCamera(float dt)
	{
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		if (!_cameraFrameValid || (_state != State.Guiding && _state != State.Cinematic && _state != State.Returning))
		{
			return ((MissionView)this).UpdateOverridenCamera(dt);
		}
		MatrixFrame cameraFrame = _cameraFrame;
		try
		{
			MissionScreen val = ResolveMissionScreen();
			if (val != null)
			{
				EnsureCustomCameraOwnership(val);
				if ((NativeObject)(object)_ownedCustomCamera != (NativeObject)null)
				{
					_ownedCustomCamera.Frame = cameraFrame;
				}
			}
			Mission mission = ((MissionBehavior)this).Mission;
			if (mission != null)
			{
				mission.SetCameraFrame(ref cameraFrame, 1f);
			}
		}
		catch (Exception ex)
		{
			Log("Camera override failed: " + ex.GetType().Name);
			RemoveOwnTimeRequest();
			_state = State.Idle;
			_cameraFrameValid = false;
			_releaseCameraAfterOverride = false;
			ReleaseCustomCameraOwnership("CameraOverrideFailure");
			ResumeNativeCrosshairViews();
			return ((MissionView)this).UpdateOverridenCamera(dt);
		}
		if (_releaseCameraAfterOverride)
		{
			_releaseCameraAfterOverride = false;
			_releaseCustomCameraNextDisplay = true;
		}
		return true;
	}

	public override void OnMissionTick(float dt)
	{
		//IL_0326: Unknown result type (might be due to invalid IL or missing references)
		//IL_032b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0334: Unknown result type (might be due to invalid IL or missing references)
		//IL_0339: Unknown result type (might be due to invalid IL or missing references)
		//IL_0344: Unknown result type (might be due to invalid IL or missing references)
		//IL_034d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0378: Unknown result type (might be due to invalid IL or missing references)
		//IL_037c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0381: Unknown result type (might be due to invalid IL or missing references)
		//IL_0386: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_06c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_06c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0424: Unknown result type (might be due to invalid IL or missing references)
		//IL_0427: Unknown result type (might be due to invalid IL or missing references)
		//IL_042c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0431: Unknown result type (might be due to invalid IL or missing references)
		//IL_044f: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_03f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0408: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_06d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_06d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01df: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_06fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0705: Unknown result type (might be due to invalid IL or missing references)
		//IL_070a: Unknown result type (might be due to invalid IL or missing references)
		//IL_06e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_06e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_06eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_06f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0209: Unknown result type (might be due to invalid IL or missing references)
		//IL_0713: Unknown result type (might be due to invalid IL or missing references)
		//IL_0717: Unknown result type (might be due to invalid IL or missing references)
		//IL_071c: Unknown result type (might be due to invalid IL or missing references)
		//IL_072e: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_04bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_04c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0238: Unknown result type (might be due to invalid IL or missing references)
		//IL_023c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0241: Unknown result type (might be due to invalid IL or missing references)
		//IL_0243: Unknown result type (might be due to invalid IL or missing references)
		//IL_0245: Unknown result type (might be due to invalid IL or missing references)
		//IL_024a: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_04f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_04f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_04f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0530: Unknown result type (might be due to invalid IL or missing references)
		//IL_0532: Unknown result type (might be due to invalid IL or missing references)
		//IL_053f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0541: Unknown result type (might be due to invalid IL or missing references)
		//IL_0545: Unknown result type (might be due to invalid IL or missing references)
		//IL_054a: Unknown result type (might be due to invalid IL or missing references)
		//IL_054f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0551: Unknown result type (might be due to invalid IL or missing references)
		//IL_0553: Unknown result type (might be due to invalid IL or missing references)
		//IL_0555: Unknown result type (might be due to invalid IL or missing references)
		//IL_055a: Unknown result type (might be due to invalid IL or missing references)
		//IL_050b: Unknown result type (might be due to invalid IL or missing references)
		//IL_050f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0514: Unknown result type (might be due to invalid IL or missing references)
		//IL_051a: Unknown result type (might be due to invalid IL or missing references)
		//IL_051f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0521: Unknown result type (might be due to invalid IL or missing references)
		//IL_052a: Unknown result type (might be due to invalid IL or missing references)
		//IL_052c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0272: Unknown result type (might be due to invalid IL or missing references)
		//IL_0274: Unknown result type (might be due to invalid IL or missing references)
		//IL_029b: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0281: Unknown result type (might be due to invalid IL or missing references)
		//IL_0283: Unknown result type (might be due to invalid IL or missing references)
		//IL_0288: Unknown result type (might be due to invalid IL or missing references)
		//IL_028d: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_02cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_073a: Unknown result type (might be due to invalid IL or missing references)
		//IL_073e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0743: Unknown result type (might be due to invalid IL or missing references)
		//IL_0755: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_06b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_06bb: Unknown result type (might be due to invalid IL or missing references)
		if (_state != State.Guiding || dt <= 0f)
		{
			return;
		}
		ProcessDeferredNativeMissileWork();
		EnsureStandaloneSplitProjectiles();
		if (_trackedMissiles.Count == 0)
		{
			if (_pendingContinuationSpawns.Count == 0 && _pendingNativeMissileRemovals.Count == 0)
			{
				HandleGuidedSwarmTerminal("NoTrackedOrDeferredMissiles");
			}
			return;
		}
		PruneInvalidTrackedMissiles();
		if (_cameraMissileIndex >= 0 && FindTrackedMissile(_cameraMissileIndex) == null)
		{
			TryPromoteCameraOwnerWithinSwarm(_cameraMissileIndex);
		}
		if (_trackedMissiles.Count == 0)
		{
			HandleGuidedSwarmTerminal("TrackedMissileIdentityExpired");
			return;
		}
		ExpirePendingCollisionReactions();
		if (_state != State.Guiding || _trackedMissiles.Count == 0)
		{
			return;
		}
		float pendingYawInput = _pendingYawInput;
		float pendingPitchInput = _pendingPitchInput;
		_pendingYawInput = 0f;
		_pendingPitchInput = 0f;
		_formationElapsed += dt;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			TrackedMissile trackedMissile = _trackedMissiles[i];
			if (trackedMissile != null)
			{
				trackedMissile.GuidanceFlightElapsed = Clamp(trackedMissile.GuidanceFlightElapsed + dt, 0f, 180f);
			}
		}
		int num = GlobalSettings<Settings>.Instance?.SplitArrowFormationMode ?? 0;
		bool flag = num > 0 && _trackedMissiles.Count > 1;
		bool flag2 = Math.Abs(pendingYawInput) > 1E-06f || Math.Abs(pendingPitchInput) > 1E-06f;
		bool flag3 = IsAutoguidanceRuntimeActive();
		if (!flag2 && !flag && !flag3)
		{
			return;
		}
		TrackedMissile trackedMissile2 = FindTrackedMissile(_missileIndex);
		if (trackedMissile2 == null && _trackedMissiles.Count > 0)
		{
			trackedMissile2 = _trackedMissiles[0];
			PromoteFormationLeader(trackedMissile2);
		}
		Vec3 val = Vec3.Zero;
		Vec3 val2 = Vec3.Zero;
		bool flag4 = false;
		if (trackedMissile2 != null)
		{
			try
			{
				val = ((MBMissile)trackedMissile2.Missile).GetPosition();
				val2 = ((MBMissile)trackedMissile2.Missile).GetVelocity();
				float lengthSquared = val2.LengthSquared;
				if (IsFinite(val) && IsFinite(val2) && IsFinite(lengthSquared) && lengthSquared > 1E-06f)
				{
					float num2 = (float)Math.Sqrt(lengthSquared);
					Vec3 val3 = val2 / num2;
					Vec3 val4 = val3;
					UpdateEstimatedProjectileGravity(trackedMissile2, val2, dt);
					if (!trackedMissile2.AwaitingCollisionReaction)
					{
						bool num3 = flag3 && IsAutoguidanceEligibleMissile(trackedMissile2);
						bool flag5 = flag;
						if (num3)
						{
							if (TryGetAutoguidanceSteeringDirection(trackedMissile2, val, val2, dt, out var desiredDirection))
							{
								val4 = ApplyLimitedDirectionSteering(val3, desiredDirection, num2, dt);
								flag5 = true;
							}
						}
						else if (flag2)
						{
							val4 = ApplyLimitedDirectSteering(trackedMissile2, val3, pendingYawInput, pendingPitchInput, num2, dt);
							flag5 = true;
						}
						if (flag5)
						{
							val2 = val4 * num2;
							((MBMissile)trackedMissile2.Missile).SetVelocity(ref val2);
							RecordCommandedVelocity(trackedMissile2, val2);
						}
					}
					_lastMissileDirection = val4;
					flag4 = true;
				}
			}
			catch
			{
				RemoveTrackedMissile(trackedMissile2, removeVisual: true);
				trackedMissile2 = null;
			}
		}
		bool flag6 = flag4;
		for (int num4 = _trackedMissiles.Count - 1; num4 >= 0; num4--)
		{
			TrackedMissile trackedMissile3 = _trackedMissiles[num4];
			if (trackedMissile3 != trackedMissile2)
			{
				try
				{
					Vec3 position = ((MBMissile)trackedMissile3.Missile).GetPosition();
					Vec3 velocity = ((MBMissile)trackedMissile3.Missile).GetVelocity();
					float lengthSquared2 = velocity.LengthSquared;
					if (IsFinite(position) && IsFinite(velocity) && IsFinite(lengthSquared2) && !(lengthSquared2 <= 1E-06f))
					{
						float num5 = (float)Math.Sqrt(lengthSquared2);
						Vec3 val5 = velocity / num5;
						UpdateEstimatedProjectileGravity(trackedMissile3, velocity, dt);
						if (trackedMissile3.AwaitingCollisionReaction)
						{
							flag6 = true;
						}
						else
						{
							bool flag7 = flag3 && IsAutoguidanceEligibleMissile(trackedMissile3);
							if (flag7 && (!flag || ShouldBreakFormationForAutoguidance(trackedMissile3, position)))
							{
								trackedMissile3.LastFormationTargetValid = false;
								if (TryGetAutoguidanceSteeringDirection(trackedMissile3, position, velocity, dt, out var desiredDirection2))
								{
									Vec3 velocity2 = ApplyLimitedDirectionSteering(val5, desiredDirection2, num5, dt) * num5;
									((MBMissile)trackedMissile3.Missile).SetVelocity(ref velocity2);
									RecordCommandedVelocity(trackedMissile3, velocity2);
								}
								flag6 = true;
							}
							else
							{
								float num6 = num5;
								if (flag && flag4)
								{
									Vec3 val6 = NormalizeSafe(val2, _lastMissileDirection);
									BuildSplitArrowFormation(num, trackedMissile3.FormationSlot, Math.Max(1, _trackedMissiles.Count - 1), val6, _formationElapsed, out var offset, out var relativeVelocity);
									float val7 = Clamp(GlobalSettings<Settings>.Instance?.SplitArrowFormationSpacing ?? 1.25f, 0.2f, 8f);
									float num7 = Clamp(GlobalSettings<Settings>.Instance?.SplitArrowFormationResponse ?? 4f, 0.5f, 20f);
									Vec3 val8 = val + offset;
									Vec3 val9 = val8 - position;
									float lengthSquared3 = val9.LengthSquared;
									float num8 = ((IsFinite(lengthSquared3) && lengthSquared3 > 1E-06f) ? ((float)Math.Sqrt(lengthSquared3)) : 0f);
									Vec3 val10 = val2 + relativeVelocity;
									if (trackedMissile3.LastFormationTargetValid && dt > 1E-06f)
									{
										Vec3 val11 = (val8 - trackedMissile3.LastFormationTarget) / dt;
										if (IsFinite(val11))
										{
											val10 = val11;
										}
									}
									trackedMissile3.LastFormationTarget = val8;
									trackedMissile3.LastFormationTargetValid = true;
									Vec3 value = val10 + val9 * num7;
									Vec3 val12 = NormalizeSafe(value, val6);
									float lengthSquared4 = value.LengthSquared;
									float v = ((IsFinite(lengthSquared4) && lengthSquared4 > 1E-06f) ? ((float)Math.Sqrt(lengthSquared4)) : num5);
									float lengthSquared5 = val2.LengthSquared;
									float num9 = ((IsFinite(lengthSquared5) && lengthSquared5 > 1E-06f) ? ((float)Math.Sqrt(lengthSquared5)) : num5);
									float num10 = Clamp(GlobalSettings<Settings>.Instance?.SplitArrowFormationCatchUpSpeedLimit ?? 3f, 1f, 5f);
									float num11 = num8 / Math.Max(0.2f, val7);
									float num12 = 1f + Math.Min(num10 - 1f, num11 * 0.85f);
									float max = Math.Max(num5, num9) * num12;
									float min = Math.Max(0.25f, Math.Min(num5, num9) * 0.45f);
									num6 = Clamp(v, min, max);
									float num13 = Math.Max(GetAdaptiveSteeringMultiplier(num9), 1f + Math.Min(15f, num11 * 5f));
									float num14 = Clamp(GlobalSettings<Settings>.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f);
									float num15 = Math.Max(num5, num9);
									float num16 = Math.Min(1.45f, num15 / num14 * dt * num13);
									if (num16 > 1E-06f)
									{
										val12 = RotateTowards(val5, val12, num16);
									}
									Vec3 velocity3 = val12 * num6;
									((MBMissile)trackedMissile3.Missile).SetVelocity(ref velocity3);
									RecordCommandedVelocity(trackedMissile3, velocity3);
									flag6 = true;
								}
								else
								{
									trackedMissile3.LastFormationTargetValid = false;
									Vec3 val13 = val5;
									bool flag8 = false;
									if (flag7)
									{
										if (TryGetAutoguidanceSteeringDirection(trackedMissile3, position, velocity, dt, out var desiredDirection3))
										{
											val13 = ApplyLimitedDirectionSteering(val5, desiredDirection3, num5, dt);
											flag8 = true;
										}
									}
									else if (flag2)
									{
										val13 = ApplyLimitedDirectSteering(trackedMissile3, val5, pendingYawInput, pendingPitchInput, num5, dt);
										flag8 = true;
									}
									if (flag8)
									{
										Vec3 velocity4 = val13 * num5;
										((MBMissile)trackedMissile3.Missile).SetVelocity(ref velocity4);
										RecordCommandedVelocity(trackedMissile3, velocity4);
									}
									flag6 = true;
								}
							}
						}
					}
				}
				catch
				{
					RemoveTrackedMissile(trackedMissile3, removeVisual: true);
				}
			}
		}
		if (!flag6 && _trackedMissiles.Count == 0)
		{
			BeginReturn("SwarmSteeringUnavailable", fastReturn: true);
		}
	}

	public override void OnPreDisplayMissionTick(float dt)
	{
		if (((MissionBehavior)this).Mission == null || dt < 0f)
		{
			return;
		}
		if (_releaseCustomCameraNextDisplay)
		{
			_releaseCustomCameraNextDisplay = false;
			ReleaseCustomCameraOwnership("ReturnComplete");
			_releaseCameraAfterOverride = false;
			_state = State.Idle;
			_cameraFrameValid = false;
			ResetCinematicSubjects();
			Log("Return flight complete.");
			if (!TryStartQueuedAlliedTakeover())
			{
				ResumeNativeCrosshairViews();
				_alliedTakeoverChainArmed = false;
			}
			return;
		}
		switch (_state)
		{
		case State.WaitingForMissile:
			_pendingAcquireElapsed += dt;
			if (!TryAcquireMissiles() && _pendingAcquireElapsed >= 0.25f)
			{
				ResetAll(behaviorRemoving: false);
			}
			break;
		case State.Guiding:
			TickGuidanceDisplay(dt);
			break;
		case State.ImpactPending:
			TickImpactPendingDisplay(dt);
			break;
		case State.Cinematic:
			TickCinematicDisplay(dt);
			break;
		case State.Returning:
			TickReturnDisplay(dt);
			break;
		}
	}

	private bool TryAcquireMissiles()
	{
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_016e: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_018b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0194: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_026d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0270: Unknown result type (might be due to invalid IL or missing references)
		//IL_0275: Unknown result type (might be due to invalid IL or missing references)
		//IL_027a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		if ((_state != State.WaitingForMissile && _state != State.Guiding) || ((MissionBehavior)this).Mission == null || _activeShotShooter == null)
		{
			return _trackedMissiles.Count > 0;
		}
		for (int i = 0; i < _pendingShotSeeds.Count && _trackedMissiles.Count < 64; i++)
		{
			PendingShotSeed pendingShotSeed = _pendingShotSeeds[i];
			if (pendingShotSeed == null || pendingShotSeed.Resolved || pendingShotSeed.ShotGeneration != _activeShotGeneration || pendingShotSeed.Shooter != _activeShotShooter)
			{
				continue;
			}
			Mission.Missile val = FindCandidateForShotSeed(pendingShotSeed);
			if (val == null)
			{
				continue;
			}
			int index;
			Vec3 velocity;
			Vec3 position;
			try
			{
				index = ((MBMissile)val).Index;
				velocity = ((MBMissile)val).GetVelocity();
				position = ((MBMissile)val).GetPosition();
			}
			catch
			{
				continue;
			}
			if (FindTrackedMissile(index) != null)
			{
				pendingShotSeed.Resolved = true;
				continue;
			}
			GameEntity identityEntity = null;
			ItemObject identityItem = null;
			MissionWeapon weapon;
			try
			{
				identityEntity = val.Entity;
				weapon = val.Weapon;
				identityItem = weapon.Item;
			}
			catch
			{
			}
			MissileDamageBridge.ResolvedLaunchData data = null;
			MissileDamageBridge.TryGetResolvedLaunchForShot(((MissionBehavior)this).Mission, index, pendingShotSeed.Shooter, pendingShotSeed.Position, pendingShotSeed.Velocity, out data);
			TrackedMissile obj3 = new TrackedMissile
			{
				Missile = val,
				OriginalShooter = pendingShotSeed.Shooter,
				ShotGeneration = pendingShotSeed.ShotGeneration,
				Index = index,
				FormationSlot = _trackedMissiles.Count,
				AwaitingCollisionReaction = false,
				IdentityEntity = identityEntity,
				IdentityItem = identityItem,
				LastFormationTarget = Vec3.Zero,
				LastFormationTargetValid = false,
				GuidanceLaunchPosition = position,
				GuidanceLaunchVelocity = velocity,
				GuidanceLaunchStateValid = (IsFinite(position) && IsFinite(velocity)),
				SpawnWeapon = val.Weapon
			};
			weapon = val.Weapon;
			obj3.SpawnWeaponValid = weapon.Item != null;
			obj3.ResolvedLaunchData = data;
			obj3.SpawnOrientation = _pendingShotOrientation;
			obj3.SpawnOrientationValid = true;
			obj3.SpawnBaseSpeed = ((data != null && IsFinite(data.BaseSpeed)) ? data.BaseSpeed : (IsFinite(velocity.Length) ? velocity.Length : 0f));
			obj3.SpawnHasRigidBody = _pendingShotHasRigidBody;
			obj3.PenetrationsUsed = 0;
			obj3.SyntheticProjectile = false;
			TrackedMissile trackedMissile = obj3;
			_trackedMissiles.Add(trackedMissile);
			pendingShotSeed.Resolved = true;
			if (_missile == null)
			{
				_missile = val;
				_missileIndex = index;
				_cameraMissileIndex = index;
				_lastMissileDirection = NormalizeSafe(velocity, _shotDirection);
			}
			ApplyFullDetailVisual(trackedMissile);
			ApplyGuidedProjectileParticlePolicy(trackedMissile);
			Log("Added exact guided-shot missile #" + index + " for callback seed #" + i + ".");
		}
		AcquireSplitShotSiblings();
		EnsureStandaloneSplitProjectiles();
		ResolvePrimaryMissileFromFirstShotSeed();
		if (_trackedMissiles.Count == 0)
		{
			return false;
		}
		if (_state == State.WaitingForMissile)
		{
			_pendingYawInput = 0f;
			_pendingPitchInput = 0f;
			_state = State.Guiding;
			_cameraFrameValid = false;
			InitializeAutoguidanceForCurrentShot();
			SuspendNativeCrosshairViews();
			AcquireCustomCameraOwnership();
			UpdateCrosshairVisibility();
			InitializeGuidanceTimeControl();
			Log("Guidance started for a callback-bounded swarm of " + _trackedMissiles.Count + " missile(s).");
		}
		return true;
	}

	private void AcquireSplitShotSiblings()
	{
		//IL_02ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_02d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_030b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0310: Unknown result type (might be due to invalid IL or missing references)
		//IL_0318: Unknown result type (might be due to invalid IL or missing references)
		//IL_031d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0338: Unknown result type (might be due to invalid IL or missing references)
		//IL_033d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		//IL_0158: Unknown result type (might be due to invalid IL or missing references)
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0191: Unknown result type (might be due to invalid IL or missing references)
		//IL_0193: Unknown result type (might be due to invalid IL or missing references)
		//IL_0194: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_024a: Unknown result type (might be due to invalid IL or missing references)
		//IL_024f: Unknown result type (might be due to invalid IL or missing references)
		int count = _trackedMissiles.Count;
		Mission mission = ((MissionBehavior)this).Mission;
		Agent val = ((mission != null) ? mission.MainAgent : null);
		if (mission == null || val == null || _activeShotShooter == null || _activeShotShooter != val || _trackedMissiles.Count == 0 || !IsSplitSiblingAcquisitionOpen())
		{
			return;
		}
		Vec3 val2 = _pendingShotPosition;
		try
		{
			if (_trackedMissiles.Count > 0 && _trackedMissiles[0]?.Missile != null)
			{
				Vec3 position = ((MBMissile)_trackedMissiles[0].Missile).GetPosition();
				if (IsFinite(position))
				{
					val2 = position;
				}
			}
		}
		catch
		{
		}
		float lengthSquared = _pendingShotVelocity.LengthSquared;
		float num = ((IsFinite(lengthSquared) && lengthSquared > 1E-06f) ? ((float)Math.Sqrt(lengthSquared)) : 0f);
		float num2 = Clamp(4f + num * 0.05f, 5f, 16f);
		float num3 = num2 * num2;
		Vec3 val3 = NormalizeSafe(_pendingShotVelocity, _shotDirection);
		try
		{
			foreach (Mission.Missile item in (List<Mission.Missile>)(object)mission.MissilesList)
			{
				if (_trackedMissiles.Count >= 64)
				{
					break;
				}
				if (item == null)
				{
					continue;
				}
				Agent shooterAgent;
				int index;
				Vec3 position2;
				Vec3 velocity;
				try
				{
					shooterAgent = item.ShooterAgent;
					index = ((MBMissile)item).Index;
					position2 = ((MBMissile)item).GetPosition();
					velocity = ((MBMissile)item).GetVelocity();
				}
				catch
				{
					continue;
				}
				if (shooterAgent != _activeShotShooter || !IsArrowOrBolt(item) || FindTrackedMissile(index) != null)
				{
					continue;
				}
				Vec3 val4 = position2 - val2;
				float lengthSquared2 = val4.LengthSquared;
				if (!IsFinite(lengthSquared2) || lengthSquared2 > num3)
				{
					continue;
				}
				float num4 = Dot(NormalizeSafe(velocity, val3), val3);
				if (!IsFinite(num4) || num4 < 0.45f)
				{
					continue;
				}
				float lengthSquared3 = velocity.LengthSquared;
				if (num > 1E-06f && IsFinite(lengthSquared3) && lengthSquared3 > 1E-06f)
				{
					float num5 = (float)Math.Sqrt(lengthSquared3) / num;
					if (!IsFinite(num5) || num5 < 0.3f || num5 > 2.25f)
					{
						continue;
					}
				}
				GameEntity identityEntity = null;
				ItemObject identityItem = null;
				MissionWeapon weapon;
				try
				{
					identityEntity = item.Entity;
					weapon = item.Weapon;
					identityItem = weapon.Item;
				}
				catch
				{
				}
				MissileDamageBridge.ResolvedLaunchData data = null;
				MissileDamageBridge.TryGetResolvedLaunch(((MissionBehavior)this).Mission, index, _activeShotShooter, out data);
				TrackedMissile obj4 = new TrackedMissile
				{
					Missile = item,
					OriginalShooter = _activeShotShooter,
					ShotGeneration = _activeShotGeneration,
					Index = index,
					FormationSlot = _trackedMissiles.Count,
					AwaitingCollisionReaction = false,
					IdentityEntity = identityEntity,
					IdentityItem = identityItem,
					LastFormationTarget = Vec3.Zero,
					LastFormationTargetValid = false,
					GuidanceLaunchPosition = position2,
					GuidanceLaunchVelocity = velocity,
					GuidanceLaunchStateValid = (IsFinite(position2) && IsFinite(velocity)),
					SpawnWeapon = item.Weapon
				};
				weapon = item.Weapon;
				obj4.SpawnWeaponValid = weapon.Item != null;
				obj4.ResolvedLaunchData = data;
				obj4.SpawnOrientation = _pendingShotOrientation;
				obj4.SpawnOrientationValid = true;
				obj4.SpawnBaseSpeed = ((data != null && IsFinite(data.BaseSpeed)) ? data.BaseSpeed : (IsFinite(velocity.Length) ? velocity.Length : 0f));
				obj4.SpawnHasRigidBody = _pendingShotHasRigidBody;
				obj4.PenetrationsUsed = 0;
				obj4.SyntheticProjectile = false;
				TrackedMissile trackedMissile = obj4;
				_trackedMissiles.Add(trackedMissile);
				ApplyFullDetailVisual(trackedMissile);
				ApplyGuidedProjectileParticlePolicy(trackedMissile);
				Log("Added bounded split-shot sibling missile #" + index + " to generation " + _activeShotGeneration + ".");
			}
		}
		catch
		{
		}
		if (_trackedMissiles.Count > count)
		{
			_nativeSplitBatchDetected = true;
		}
		if (_trackedMissiles.Count > 1)
		{
			CloseSplitSiblingAcquisition("NativeSplitBatchCaptured");
			if (IsAutoguidanceRuntimeActive())
			{
				AssignAutoguidanceTargets(clearExisting: false);
			}
		}
	}

	private void EnsureStandaloneSplitProjectiles()
	{
		//IL_03ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0401: Unknown result type (might be due to invalid IL or missing references)
		//IL_0405: Unknown result type (might be due to invalid IL or missing references)
		//IL_02dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_02df: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0305: Unknown result type (might be due to invalid IL or missing references)
		//IL_030a: Unknown result type (might be due to invalid IL or missing references)
		//IL_030f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0311: Unknown result type (might be due to invalid IL or missing references)
		//IL_0316: Unknown result type (might be due to invalid IL or missing references)
		//IL_0318: Unknown result type (might be due to invalid IL or missing references)
		//IL_0319: Unknown result type (might be due to invalid IL or missing references)
		//IL_0320: Unknown result type (might be due to invalid IL or missing references)
		//IL_0325: Unknown result type (might be due to invalid IL or missing references)
		//IL_032a: Unknown result type (might be due to invalid IL or missing references)
		//IL_033b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0340: Unknown result type (might be due to invalid IL or missing references)
		//IL_0345: Unknown result type (might be due to invalid IL or missing references)
		//IL_0356: Unknown result type (might be due to invalid IL or missing references)
		//IL_035b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0360: Unknown result type (might be due to invalid IL or missing references)
		//IL_039a: Unknown result type (might be due to invalid IL or missing references)
		//IL_039f: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_018b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0190: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		//IL_019c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01af: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01da: Unknown result type (might be due to invalid IL or missing references)
		//IL_01de: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_020a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0220: Unknown result type (might be due to invalid IL or missing references)
		//IL_0225: Unknown result type (might be due to invalid IL or missing references)
		//IL_022a: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0242: Unknown result type (might be due to invalid IL or missing references)
		//IL_0247: Unknown result type (might be due to invalid IL or missing references)
		//IL_024c: Unknown result type (might be due to invalid IL or missing references)
		//IL_024e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0250: Unknown result type (might be due to invalid IL or missing references)
		//IL_0252: Unknown result type (might be due to invalid IL or missing references)
		//IL_0257: Unknown result type (might be due to invalid IL or missing references)
		//IL_025c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0261: Unknown result type (might be due to invalid IL or missing references)
		//IL_0274: Unknown result type (might be due to invalid IL or missing references)
		//IL_026c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0279: Unknown result type (might be due to invalid IL or missing references)
		if (_standaloneSplitSpawned)
		{
			return;
		}
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance == null || !instance.EnableStandaloneSplitProjectiles || ((MissionBehavior)this).Mission == null || _trackedMissiles.Count == 0 || _activeShotShooter == null || !_splitSiblingAcquisitionClosed)
		{
			return;
		}
		if (_nativeSplitBatchDetected || _trackedMissiles.Count > 1)
		{
			_standaloneSplitSpawned = true;
			Log("Standalone split skipped because this shot already contains a native/TOR split batch.");
			return;
		}
		int num = Math.Max(1, Math.Min(48, GlobalSettings<Settings>.Instance?.StandaloneSplitProjectileCount ?? 1));
		int num2 = Math.Min(64 - _trackedMissiles.Count, num - _trackedMissiles.Count);
		if (num2 <= 0)
		{
			return;
		}
		TrackedMissile trackedMissile = _trackedMissiles[0];
		if (trackedMissile == null || !trackedMissile.SpawnWeaponValid)
		{
			return;
		}
		if (trackedMissile.ResolvedLaunchData == null)
		{
			if (!MissileDamageBridge.TryGetResolvedLaunchForShot(((MissionBehavior)this).Mission, trackedMissile.Index, trackedMissile.OriginalShooter ?? _activeShotShooter, _pendingShotPosition, _pendingShotVelocity, out var data))
			{
				if (!_standaloneSplitDamagePacketWaitLogged)
				{
					_standaloneSplitDamagePacketWaitLogged = true;
					Log("Standalone split is waiting for the original shot's resolved native damage packet" + (MissileDamageBridge.IsInstalled ? "." : (": " + MissileDamageBridge.InstallFailure)));
				}
				return;
			}
			trackedMissile.ResolvedLaunchData = data;
			if (IsFinite(data.BaseSpeed) && data.BaseSpeed > 1E-06f)
			{
				trackedMissile.SpawnBaseSpeed = data.BaseSpeed;
			}
			_standaloneSplitDamagePacketWaitLogged = false;
			Log("Late-bound the original shot's resolved native damage packet before standalone splitting.");
		}
		Vec3 position;
		Vec3 velocity;
		try
		{
			position = ((MBMissile)trackedMissile.Missile).GetPosition();
			velocity = ((MBMissile)trackedMissile.Missile).GetVelocity();
		}
		catch
		{
			return;
		}
		float length = velocity.Length;
		if (!IsFinite(position) || !IsFinite(velocity) || !IsFinite(length) || length <= 1E-06f)
		{
			return;
		}
		_standaloneSplitSpawned = true;
		Vec3 val = velocity / length;
		Vec3 val2 = Cross(val, WorldUp);
		if (!IsFinite(val2) || val2.LengthSquared <= 0.0001f)
		{
			val2 = Cross(val, new Vec3(1f, 0f, 0f, -1f));
		}
		val2 = NormalizeSafe(val2, new Vec3(1f, 0f, 0f, -1f));
		Vec3 val3 = NormalizeSafe(Cross(val2, val), WorldUp);
		Mat3 val4 = (trackedMissile.SpawnOrientationValid ? trackedMissile.SpawnOrientation : _pendingShotOrientation);
		float num3 = ((trackedMissile.SpawnBaseSpeed > 1E-06f) ? trackedMissile.SpawnBaseSpeed : length);
		for (int i = 0; i < num2; i++)
		{
			float num4 = (float)(_trackedMissiles.Count + i) * 2.3999631f;
			float num5 = 0.0105f * (0.35f + 0.65f * (((float)i + 1f) / Math.Max(1f, num2)));
			Vec3 val5 = NormalizeSafe(val + val2 * ((float)Math.Cos(num4) * num5) + val3 * ((float)Math.Sin(num4) * num5), val);
			Vec3 val6 = position + val5 * 0.06f + val2 * ((float)Math.Cos(num4) * 0.025f) + val3 * ((float)Math.Sin(num4) * 0.025f);
			Mission.Missile missile = null;
			try
			{
				using IDisposable disposable = MissileDamageBridge.OverrideNextSyntheticMissile(((MissionBehavior)this).Mission, _activeShotShooter, trackedMissile.ResolvedLaunchData);
				if (disposable == null)
				{
					throw new InvalidOperationException("Resolved missile damage bridge unavailable");
				}
				missile = ((MissionBehavior)this).Mission.AddCustomMissile(_activeShotShooter, trackedMissile.SpawnWeapon, val6, val5, val4, num3, length, trackedMissile.SpawnHasRigidBody, (MissionObject)null, -1);
			}
			catch (Exception ex)
			{
				Log("Standalone split spawn failed: " + ex.GetType().Name + ".");
			}
			if (ValidateSyntheticMissileEntity(missile, "standalone split"))
			{
				TrackedMissile trackedMissile2 = CreateTrackedMissileFromSpawn(missile, trackedMissile, val6, val5 * length, continuation: false);
				if (trackedMissile2 != null)
				{
					_trackedMissiles.Add(trackedMissile2);
					ApplyFullDetailVisual(trackedMissile2);
					ApplyGuidedProjectileParticlePolicy(trackedMissile2);
					Log("Added standalone split projectile #" + trackedMissile2.Index + ".");
				}
			}
		}
		if (_trackedMissiles.Count > 1)
		{
			CloseSplitSiblingAcquisition("StandaloneSplitBatchCreated");
		}
	}

	private bool ValidateSyntheticMissileEntity(Mission.Missile missile, string source)
	{
		if (missile == null || ((MissionBehavior)this).Mission == null)
		{
			return false;
		}
		int num = -1;
		try
		{
			num = ((MBMissile)missile).Index;
			if (missile.Entity != (GameEntity)null)
			{
				return true;
			}
		}
		catch
		{
		}
		Log("Rejected " + source + " missile #" + num + " because Bannerlord did not return its required native collision entity.");
		if (num >= 0)
		{
			try
			{
				((MissionBehavior)this).Mission.RemoveMissileAsClient(num);
			}
			catch
			{
			}
			MissileDamageBridge.Forget(((MissionBehavior)this).Mission, num);
		}
		return false;
	}

	private TrackedMissile CreateTrackedMissileFromSpawn(Mission.Missile missile, TrackedMissile source, Vec3 launchPosition, Vec3 launchVelocity, bool continuation)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_011f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_013a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0192: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		if (missile == null || source == null)
		{
			return null;
		}
		GameEntity identityEntity = null;
		ItemObject identityItem = null;
		MissionWeapon spawnWeapon = source.SpawnWeapon;
		bool spawnWeaponValid = source.SpawnWeaponValid;
		MissileDamageBridge.ResolvedLaunchData data = null;
		try
		{
			identityEntity = missile.Entity;
			MissionWeapon weapon = missile.Weapon;
			identityItem = weapon.Item;
			spawnWeapon = missile.Weapon;
			weapon = missile.Weapon;
			spawnWeaponValid = weapon.Item != null;
		}
		catch
		{
		}
		if (!MissileDamageBridge.TryGetResolvedLaunch(((MissionBehavior)this).Mission, ((MBMissile)missile).Index, source.OriginalShooter, out data))
		{
			data = source.ResolvedLaunchData?.Clone();
		}
		TrackedMissile trackedMissile = new TrackedMissile
		{
			Missile = missile,
			OriginalShooter = source.OriginalShooter,
			ShotGeneration = source.ShotGeneration,
			Index = ((MBMissile)missile).Index,
			FormationSlot = (continuation ? source.FormationSlot : _trackedMissiles.Count),
			AwaitingCollisionReaction = false,
			IdentityEntity = identityEntity,
			IdentityItem = identityItem,
			LastFormationTarget = Vec3.Zero,
			LastFormationTargetValid = false,
			GuidanceLaunchPosition = launchPosition,
			GuidanceLaunchVelocity = launchVelocity,
			GuidanceLaunchStateValid = (IsFinite(launchPosition) && IsFinite(launchVelocity)),
			SpawnWeapon = spawnWeapon,
			SpawnWeaponValid = spawnWeaponValid,
			ResolvedLaunchData = data,
			SpawnOrientation = source.SpawnOrientation,
			SpawnOrientationValid = source.SpawnOrientationValid,
			SpawnBaseSpeed = source.SpawnBaseSpeed,
			SpawnHasRigidBody = source.SpawnHasRigidBody,
			PenetrationsUsed = (continuation ? source.PenetrationsUsed : 0),
			SyntheticProjectile = true,
			GuidanceBrokenFromFormation = (continuation && source.GuidanceBrokenFromFormation),
			GuidanceFallbackDirection = source.GuidanceFallbackDirection,
			GuidanceFallbackDirectionValid = source.GuidanceFallbackDirectionValid
		};
		if (continuation)
		{
			CopyAutoguidanceContinuity(source, trackedMissile);
		}
		return trackedMissile;
	}

	private static void CopyAutoguidanceContinuity(TrackedMissile source, TrackedMissile destination)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		if (source != null && destination != null)
		{
			destination.GuidanceTarget = source.GuidanceTarget;
			destination.GuidanceHeadBoneIndex = source.GuidanceHeadBoneIndex;
			destination.GuidanceSmoothedHead = source.GuidanceSmoothedHead;
			destination.GuidanceSmoothedHeadValid = source.GuidanceSmoothedHeadValid;
			destination.GuidanceLastRawHead = source.GuidanceLastRawHead;
			destination.GuidanceLastRawHeadValid = source.GuidanceLastRawHeadValid;
			destination.GuidanceTargetVelocity = source.GuidanceTargetVelocity;
			destination.GuidanceTargetVelocityValid = source.GuidanceTargetVelocityValid;
			destination.GuidanceBrokenFromFormation = source.GuidanceBrokenFromFormation;
			destination.GuidanceConfiguredProfileIndex = source.GuidanceConfiguredProfileIndex;
			destination.GuidanceResolvedProfileIndex = source.GuidanceResolvedProfileIndex;
			destination.GuidanceResolvedProfileValid = source.GuidanceResolvedProfileValid;
			destination.GuidanceForceDirectIntercept = source.GuidanceForceDirectIntercept;
			destination.EstimatedGravityZ = source.EstimatedGravityZ;
			destination.GuidanceRouteReplanRequested = source.GuidanceRouteReplanRequested;
			destination.GuidanceNoProgressElapsed = 0f;
			destination.GuidanceLastTargetDistanceValid = false;
			destination.GuidanceFallbackDirection = source.GuidanceFallbackDirection;
			destination.GuidanceFallbackDirectionValid = source.GuidanceFallbackDirectionValid;
			for (int i = 0; i < source.GuidanceConsumedTargets.Count; i++)
			{
				destination.GuidanceConsumedTargets.Add(source.GuidanceConsumedTargets[i]);
			}
			for (int j = 0; j < source.GuidanceRouteTargets.Count; j++)
			{
				destination.GuidanceRouteTargets.Add(source.GuidanceRouteTargets[j]);
			}
		}
	}

	private Mission.Missile FindCandidateForShotSeed(PendingShotSeed seed)
	{
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_017c: Unknown result type (might be due to invalid IL or missing references)
		//IL_017f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0190: Unknown result type (might be due to invalid IL or missing references)
		//IL_0195: Unknown result type (might be due to invalid IL or missing references)
		//IL_019a: Unknown result type (might be due to invalid IL or missing references)
		//IL_019c: Unknown result type (might be due to invalid IL or missing references)
		Mission mission = ((MissionBehavior)this).Mission;
		Agent val = seed?.Shooter;
		if (seed == null || mission == null || val == null || seed.ShotGeneration != _activeShotGeneration)
		{
			return null;
		}
		Mission.Missile result = null;
		float num = float.MaxValue;
		float num2 = Clamp(_pendingAcquireElapsed - seed.CreatedAtAcquireElapsed, 0f, 0.25f);
		Vec3 val2 = seed.Position + seed.Velocity * num2;
		float lengthSquared = seed.Velocity.LengthSquared;
		float num3 = ((IsFinite(lengthSquared) && lengthSquared > 1E-06f) ? ((float)Math.Sqrt(lengthSquared)) : 0f);
		float num4 = Clamp(1.25f + num3 * 0.025f, 1.25f, 7f);
		float num5 = num4 * num4;
		try
		{
			foreach (Mission.Missile item in (List<Mission.Missile>)(object)mission.MissilesList)
			{
				if (item == null)
				{
					continue;
				}
				Agent shooterAgent;
				int index;
				Vec3 position;
				Vec3 velocity;
				try
				{
					shooterAgent = item.ShooterAgent;
					index = ((MBMissile)item).Index;
					position = ((MBMissile)item).GetPosition();
					velocity = ((MBMissile)item).GetVelocity();
				}
				catch
				{
					continue;
				}
				if (shooterAgent != seed.Shooter || !IsArrowOrBolt(item))
				{
					continue;
				}
				if (seed.ForcedIndex >= 0)
				{
					if (index == seed.ForcedIndex)
					{
						return item;
					}
				}
				else
				{
					if (FindTrackedMissile(index) != null)
					{
						continue;
					}
					Vec3 val3 = position - val2;
					float lengthSquared2 = val3.LengthSquared;
					if (!IsFinite(lengthSquared2) || lengthSquared2 > num5)
					{
						continue;
					}
					Vec3 a = NormalizeSafe(velocity, _shotDirection);
					Vec3 b = NormalizeSafe(seed.Velocity, _shotDirection);
					float num6 = Dot(a, b);
					if (!IsFinite(num6) || num6 < 0.92f)
					{
						continue;
					}
					float lengthSquared3 = velocity.LengthSquared;
					if (num3 > 1E-06f && IsFinite(lengthSquared3) && lengthSquared3 > 1E-06f)
					{
						float num7 = (float)Math.Sqrt(lengthSquared3) / num3;
						if (!IsFinite(num7) || num7 < 0.45f || num7 > 1.8f)
						{
							continue;
						}
					}
					float num8 = lengthSquared2 + (1f - num6) * 16f;
					if (num8 < num)
					{
						num = num8;
						result = item;
					}
				}
			}
		}
		catch
		{
		}
		return result;
	}

	private void TickGuidanceDisplay(float dt)
	{
		//IL_0234: Unknown result type (might be due to invalid IL or missing references)
		//IL_0235: Unknown result type (might be due to invalid IL or missing references)
		//IL_0237: Unknown result type (might be due to invalid IL or missing references)
		//IL_023a: Unknown result type (might be due to invalid IL or missing references)
		//IL_023f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0244: Unknown result type (might be due to invalid IL or missing references)
		//IL_028b: Unknown result type (might be due to invalid IL or missing references)
		//IL_028c: Unknown result type (might be due to invalid IL or missing references)
		//IL_028e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0293: Unknown result type (might be due to invalid IL or missing references)
		//IL_0296: Unknown result type (might be due to invalid IL or missing references)
		//IL_029f: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0270: Unknown result type (might be due to invalid IL or missing references)
		//IL_0272: Unknown result type (might be due to invalid IL or missing references)
		//IL_0274: Unknown result type (might be due to invalid IL or missing references)
		//IL_0276: Unknown result type (might be due to invalid IL or missing references)
		//IL_0279: Unknown result type (might be due to invalid IL or missing references)
		//IL_027b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0281: Unknown result type (might be due to invalid IL or missing references)
		//IL_0286: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f3: Unknown result type (might be due to invalid IL or missing references)
		PruneInvalidTrackedMissiles();
		if (_trackedMissiles.Count == 0)
		{
			if (_pendingContinuationSpawns.Count <= 0 && _pendingNativeMissileRemovals.Count <= 0)
			{
				HandleGuidedSwarmTerminal("TrackedMissileIdentityExpired");
			}
			return;
		}
		float guidanceRealDelta = GetGuidanceRealDelta(dt);
		_pendingAcquireElapsed += guidanceRealDelta;
		if (IsSplitSiblingAcquisitionOpen())
		{
			TryAcquireMissiles();
		}
		if (_trackedMissiles.Count == 0)
		{
			BeginReturn("MissingTrackedSwarm", fastReturn: true);
			return;
		}
		_guidanceRealElapsed += guidanceRealDelta;
		float num = Clamp(GlobalSettings<Settings>.Instance?.MaximumGuidanceTime ?? 35f, 5f, 120f);
		if (_guidanceRealElapsed >= num)
		{
			BeginReturn("GuidanceTimeout", fastReturn: true);
			return;
		}
		if (((MissionBehavior)this).Mission.InputManager != null)
		{
			UpdateAutoguidanceRuntimeInput();
			TickAutoguidanceReacquisition(guidanceRealDelta);
			if (((MissionBehavior)this).Mission.InputManager.IsKeyPressed((InputKey)1) || ((MissionBehavior)this).Mission.InputManager.IsKeyPressed((InputKey)225))
			{
				BeginReturn("UserCancelled");
				return;
			}
			if (((MissionBehavior)this).Mission.InputManager.IsKeyPressed((InputKey)16))
			{
				StepTimeSpeed(-1);
			}
			if (((MissionBehavior)this).Mission.InputManager.IsKeyPressed((InputKey)18))
			{
				StepTimeSpeed(1);
			}
			float num2 = Clamp(GlobalSettings<Settings>.Instance?.MouseSensitivity ?? 1f, 0.1f, 4f);
			float yaw = (0f - ((MissionBehavior)this).Mission.InputManager.GetMouseMoveX()) * 0.0026f * num2;
			float pitch = (0f - ((MissionBehavior)this).Mission.InputManager.GetMouseMoveY()) * 0.0026f * num2;
			QueueDirectSteeringInput(yaw, pitch);
		}
		UpdateFullDetailVisuals();
		TrackedMissile trackedMissile = FindTrackedMissile(_cameraMissileIndex);
		if (trackedMissile == null || trackedMissile.AwaitingCollisionReaction || !TryReadMovingMissile(trackedMissile, out var position, out var velocity))
		{
			if (!TryPromoteCameraOwnerWithinSwarm(trackedMissile?.Index ?? _cameraMissileIndex))
			{
				ExpirePendingCollisionReactions();
				if (_trackedMissiles.Count == 0)
				{
					HandleGuidedSwarmTerminal("NoMovingCameraProjectile");
				}
				return;
			}
			trackedMissile = FindTrackedMissile(_cameraMissileIndex);
			if (!TryReadMovingMissile(trackedMissile, out position, out velocity))
			{
				return;
			}
		}
		Vec3 swarmAnchor = position;
		Vec3 val = NormalizeSafe(velocity, _lastMissileDirection);
		Settings instance = GlobalSettings<Settings>.Instance;
		MatrixFrame desired;
		if ((instance == null || instance.FrameAllControlledSplitProjectiles) && TryGetSwarmCameraData(out var centroid, out var forward, out var movingCount, out var lateralRadius, out var depthExtent) && movingCount > 1)
		{
			swarmAnchor = centroid;
			val = forward;
			desired = BuildSplitProjectileGroupCameraFrame(centroid, forward, lateralRadius, depthExtent);
		}
		else
		{
			desired = BuildProjectileCameraFrame(position, val);
		}
		UpdateGuidanceTimeControl(swarmAnchor, guidanceRealDelta);
		_lastMissileDirection = val;
		float positionRate = Clamp(GlobalSettings<Settings>.Instance?.CameraPositionSmoothing ?? 18f, 4f, 60f);
		float rotationRate = Clamp(GlobalSettings<Settings>.Instance?.CameraRotationSmoothing ?? 22f, 4f, 60f);
		ApplySmoothedCamera(desired, guidanceRealDelta, positionRate, rotationRate);
		UpdateCrosshairVisibility();
	}

	private bool TryReadMovingMissile(TrackedMissile tracked, out Vec3 position, out Vec3 velocity)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		position = Vec3.Zero;
		velocity = Vec3.Zero;
		if (tracked == null || tracked.AwaitingCollisionReaction)
		{
			return false;
		}
		try
		{
			position = ((MBMissile)tracked.Missile).GetPosition();
			velocity = ((MBMissile)tracked.Missile).GetVelocity();
			float lengthSquared = velocity.LengthSquared;
			return IsFinite(position) && IsFinite(velocity) && IsFinite(lengthSquared) && lengthSquared > 0.0625f;
		}
		catch
		{
			return false;
		}
	}

	private bool TryPromoteCameraOwnerWithinSwarm(int excludedIndex = -1)
	{
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		TrackedMissile trackedMissile = null;
		float num = -1f;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			TrackedMissile trackedMissile2 = _trackedMissiles[i];
			if (trackedMissile2 == null || trackedMissile2.Index == excludedIndex || trackedMissile2.ShotGeneration != _activeShotGeneration || trackedMissile2.AwaitingCollisionReaction)
			{
				continue;
			}
			try
			{
				Vec3 velocity = ((MBMissile)trackedMissile2.Missile).GetVelocity();
				float lengthSquared = velocity.LengthSquared;
				if (IsFinite(velocity) && IsFinite(lengthSquared) && !(lengthSquared <= 0.0625f) && (trackedMissile == null || lengthSquared > num))
				{
					trackedMissile = trackedMissile2;
					num = lengthSquared;
				}
			}
			catch
			{
			}
		}
		if (trackedMissile == null)
		{
			return false;
		}
		if (_cameraMissileIndex != trackedMissile.Index)
		{
			Log("Transferred projectile camera ownership to sibling #" + trackedMissile.Index + ".");
		}
		_cameraMissileIndex = trackedMissile.Index;
		return true;
	}

	private bool TryGetSwarmCameraData(out Vec3 centroid, out Vec3 forward, out int movingCount, out float lateralRadius, out float depthExtent)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_021b: Unknown result type (might be due to invalid IL or missing references)
		//IL_021f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0224: Unknown result type (might be due to invalid IL or missing references)
		//IL_022a: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		//IL_022e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0233: Unknown result type (might be due to invalid IL or missing references)
		//IL_017c: Unknown result type (might be due to invalid IL or missing references)
		//IL_017e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_0187: Unknown result type (might be due to invalid IL or missing references)
		//IL_0189: Unknown result type (might be due to invalid IL or missing references)
		//IL_01be: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01de: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0275: Unknown result type (might be due to invalid IL or missing references)
		//IL_0277: Unknown result type (might be due to invalid IL or missing references)
		//IL_0278: Unknown result type (might be due to invalid IL or missing references)
		//IL_027d: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_02fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0300: Unknown result type (might be due to invalid IL or missing references)
		centroid = Vec3.Zero;
		forward = _lastMissileDirection;
		movingCount = 0;
		lateralRadius = 0f;
		depthExtent = 0f;
		TrackedMissile tracked = FindTrackedMissile(_cameraMissileIndex);
		Vec3 position = Vec3.Zero;
		Vec3 velocity = Vec3.Zero;
		bool flag = TryReadMovingMissile(tracked, out position, out velocity);
		if (!flag)
		{
			for (int i = 0; i < _trackedMissiles.Count; i++)
			{
				if (TryReadMovingMissile(_trackedMissiles[i], out position, out velocity))
				{
					tracked = _trackedMissiles[i];
					flag = true;
					break;
				}
			}
		}
		if (!flag)
		{
			return false;
		}
		Vec3 val = NormalizeSafe(velocity, _lastMissileDirection);
		float num = Clamp(GlobalSettings<Settings>.Instance?.SplitProjectileCameraMaximumFramingRadius ?? 6f, 2f, 20f);
		float num2 = num * num;
		Vec3 val2 = Vec3.Zero;
		Vec3 val3 = Vec3.Zero;
		float num3 = 0f;
		for (int j = 0; j < _trackedMissiles.Count; j++)
		{
			TrackedMissile trackedMissile = _trackedMissiles[j];
			if (TryReadMovingMissile(trackedMissile, out var position2, out var velocity2))
			{
				Vec3 val4 = position2 - position;
				float num4 = Math.Max(0f, val4.LengthSquared);
				float num5 = ((num2 > 1E-06f) ? (num4 / num2) : 0f);
				float num6 = 1f / (1f + num5 * num5);
				float num7 = ((trackedMissile != null && trackedMissile.Index == _cameraMissileIndex) ? 3f : 1f);
				Vec3 val5 = NormalizeSafe(velocity2, val);
				float num8 = Clamp((Dot(val5, val) + 1f) * 0.5f, 0.1f, 1f);
				float num9 = Math.Max(0.02f, num6 * num7);
				val2 += position2 * num9;
				val3 += val5 * (num9 * num8);
				num3 += num9;
				movingCount++;
			}
		}
		if (movingCount <= 0 || num3 <= 1E-06f)
		{
			return false;
		}
		centroid = val2 / num3;
		forward = NormalizeSafe(val3, val);
		float num10 = 0f;
		float num11 = 0f;
		float num12 = 0f;
		for (int k = 0; k < _trackedMissiles.Count; k++)
		{
			TrackedMissile trackedMissile2 = _trackedMissiles[k];
			if (TryReadMovingMissile(trackedMissile2, out var position3, out var _))
			{
				Vec3 val6 = position3 - position;
				float num13 = Math.Max(0f, val6.LengthSquared);
				float num14 = ((num2 > 1E-06f) ? (num13 / num2) : 0f);
				float num15 = 1f / (1f + num14 * num14);
				float num16 = ((trackedMissile2 != null && trackedMissile2.Index == _cameraMissileIndex) ? 3f : 1f);
				float num17 = Math.Max(0.02f, num15 * num16);
				Vec3 a = position3 - centroid;
				float num18 = Dot(a, forward);
				float num19 = Math.Max(0f, a.LengthSquared - num18 * num18);
				num10 += num19 * num17;
				num11 += num18 * num18 * num17;
				num12 += num17;
			}
		}
		if (num12 > 1E-06f)
		{
			lateralRadius = Math.Min(num, (float)Math.Sqrt(Math.Max(0f, num10 / num12)) * 1.65f);
			depthExtent = Math.Min(num, (float)Math.Sqrt(Math.Max(0f, num11 / num12)) * 1.35f);
		}
		return true;
	}

	private MatrixFrame BuildSplitProjectileGroupCameraFrame(Vec3 centroid, Vec3 flightForward, float lateralRadius, float depthExtent)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_010b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0110: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Unknown result type (might be due to invalid IL or missing references)
		//IL_0127: Unknown result type (might be due to invalid IL or missing references)
		//IL_012c: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0145: Unknown result type (might be due to invalid IL or missing references)
		//IL_0148: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_014c: Unknown result type (might be due to invalid IL or missing references)
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Unknown result type (might be due to invalid IL or missing references)
		flightForward = NormalizeSafe(flightForward, _lastMissileDirection);
		BuildFlightBasis(flightForward, out var _, out var localUp);
		float num = Clamp(GlobalSettings<Settings>.Instance?.CameraDistance ?? 3.4f, 0.5f, 15f);
		float num2 = Clamp(GlobalSettings<Settings>.Instance?.SplitProjectileCameraPadding ?? 0.75f, 0.15f, 3f);
		float max = Clamp(GlobalSettings<Settings>.Instance?.SplitProjectileCameraMaximumDistance ?? 14f, 5f, 30f);
		float num3 = Clamp(Math.Max(num, lateralRadius * 1.75f + depthExtent * 0.55f + num2), num, max);
		float num4 = Clamp(GlobalSettings<Settings>.Instance?.ThirdPersonElevationAngle ?? 12f, -10f, 60f) * ((float)Math.PI / 180f);
		float num5 = Clamp(GlobalSettings<Settings>.Instance?.ThirdPersonLookAhead ?? 5f, 0f, 25f);
		Vec3 cameraPosition = centroid - flightForward * (num3 * (float)Math.Cos(num4)) + localUp * (num3 * (float)Math.Sin(num4));
		Vec3 lookTarget = centroid + flightForward * (num5 + depthExtent * 0.15f);
		return BuildTerrainSafeCameraFrame(cameraPosition, lookTarget, flightForward, localUp);
	}

	private MatrixFrame BuildTerrainSafeCameraFrame(Vec3 cameraPosition, Vec3 lookTarget, Vec3 fallbackForward, Vec3 cameraUp)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		if (!IsFinite(cameraPosition) || !IsFinite(lookTarget))
		{
			return MakeCameraFrame(cameraPosition, fallbackForward, cameraUp);
		}
		float num = 0f;
		for (int i = 0; i < 4; i++)
		{
			float t = (float)i * 0.2f;
			Vec3 val = Lerp(cameraPosition, lookTarget, t);
			if (TryGetTerrainHeight(val, out var height))
			{
				float num2 = height + 0.85f - val.z;
				if (IsFinite(num2) && num2 > num)
				{
					num = num2;
				}
			}
		}
		if (num > 0f)
		{
			cameraPosition.z += num;
		}
		Vec3 viewForward = NormalizeSafe(lookTarget - cameraPosition, fallbackForward);
		return MakeCameraFrame(cameraPosition, viewForward, cameraUp);
	}

	private void TickImpactPendingDisplay(float dt)
	{
		float impactConfirmationRealDelta = GetImpactConfirmationRealDelta(dt);
		_impactConfirmElapsed += impactConfirmationRealDelta;
		for (int num = _pendingHitVictims.Count - 1; num >= 0; num--)
		{
			Agent val = _pendingHitVictims[num]?.Victim;
			if (val != null)
			{
				try
				{
					float health = val.Health;
					if (IsFinite(health) && health <= 0f)
					{
						HandleConfirmedKill(val, "ImpactConfirmationHealthCheck");
						if (_state == State.Cinematic || _state == State.Returning)
						{
							return;
						}
					}
				}
				catch
				{
				}
			}
		}
		if (_impactConfirmElapsed >= 0.04f)
		{
			if (_deferredCinematicVictim != null)
			{
				BeginDeferredKillCinematic("ImpactConfirmationTerminal");
			}
			else
			{
				BeginReturn("ImpactWithoutKill", fastReturn: true);
			}
		}
	}

	private float GetImpactConfirmationRealDelta(float fallback)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long impactConfirmLastTimestamp = _impactConfirmLastTimestamp;
		_impactConfirmLastTimestamp = timestamp;
		if (impactConfirmLastTimestamp <= 0 || timestamp <= impactConfirmLastTimestamp)
		{
			return Clamp(fallback, 0f, 0.1f);
		}
		double num = (double)(timestamp - impactConfirmLastTimestamp) / (double)Stopwatch.Frequency;
		if (double.IsNaN(num) || double.IsInfinity(num))
		{
			return Clamp(fallback, 0f, 0.1f);
		}
		return Clamp((float)num, 0f, 0.1f);
	}

	private MatrixFrame BuildProjectileCameraFrame(Vec3 projectilePosition, Vec3 flightForward)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0145: Unknown result type (might be due to invalid IL or missing references)
		//IL_0150: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_015a: Unknown result type (might be due to invalid IL or missing references)
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0165: Unknown result type (might be due to invalid IL or missing references)
		//IL_016a: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Unknown result type (might be due to invalid IL or missing references)
		//IL_016f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_0172: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Unknown result type (might be due to invalid IL or missing references)
		flightForward = NormalizeSafe(flightForward, _lastMissileDirection);
		BuildFlightBasis(flightForward, out var _, out var localUp);
		if ((GlobalSettings<Settings>.Instance?.ProjectileCameraMode ?? 1) <= 0)
		{
			float num = Clamp(GlobalSettings<Settings>.Instance?.FirstPersonRearOffset ?? 0.12f, 0.01f, 1.5f);
			float num2 = Clamp(GlobalSettings<Settings>.Instance?.FirstPersonVerticalOffset ?? 0f, -0.5f, 0.5f);
			Vec3 cameraPosition = projectilePosition - flightForward * num + localUp * num2;
			Vec3 lookTarget = projectilePosition + flightForward * 0.5f;
			return BuildTerrainSafeCameraFrame(cameraPosition, lookTarget, flightForward, localUp);
		}
		float num3 = Clamp(GlobalSettings<Settings>.Instance?.CameraDistance ?? 3.4f, 0.5f, 15f);
		float num4 = Clamp(GlobalSettings<Settings>.Instance?.ThirdPersonElevationAngle ?? 12f, -30f, 60f) * ((float)Math.PI / 180f);
		float num5 = Clamp(GlobalSettings<Settings>.Instance?.ThirdPersonLookAhead ?? 5f, 0f, 25f);
		Vec3 cameraPosition2 = projectilePosition - flightForward * (num3 * (float)Math.Cos(num4)) + localUp * (num3 * (float)Math.Sin(num4));
		Vec3 lookTarget2 = projectilePosition + flightForward * num5;
		return BuildTerrainSafeCameraFrame(cameraPosition2, lookTarget2, flightForward, localUp);
	}

	private void BuildFlightBasis(Vec3 flightForward, out Vec3 right, out Vec3 localUp)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		flightForward = NormalizeSafe(flightForward, new Vec3(0f, 1f, 0f, -1f));
		right = Cross(flightForward, WorldUp);
		if (!IsFinite(right) || right.LengthSquared <= 0.0001f)
		{
			if (_cameraFrameValid && IsFinite(_cameraFrame.rotation.s) && _cameraFrame.rotation.s.LengthSquared > 1E-06f)
			{
				right = _cameraFrame.rotation.s;
			}
			else
			{
				right = Cross(flightForward, new Vec3(0f, 1f, 0f, -1f));
			}
		}
		right = NormalizeSafe(right, new Vec3(1f, 0f, 0f, -1f));
		localUp = NormalizeSafe(Cross(right, flightForward), WorldUp);
	}

	private void SuspendNativeCrosshairViews()
	{
		if (_nativeCrosshairViewsSuspended || ((MissionBehavior)this).Mission == null)
		{
			return;
		}
		_nativeCrosshairViewsSuspended = true;
		_suspendedNativeCrosshairViews.Clear();
		try
		{
			List<MissionBehavior> missionBehaviors = ((MissionBehavior)this).Mission.MissionBehaviors;
			if (missionBehaviors == null)
			{
				return;
			}
			for (int i = 0; i < missionBehaviors.Count; i++)
			{
				MissionBehavior obj = missionBehaviors[i];
				MissionView val = (MissionView)(object)((obj is MissionView) ? obj : null);
				if (val == null || (object)val == this || IsMissionViewSuspended(val))
				{
					continue;
				}
				string fullName = ((object)val).GetType().FullName;
				if (string.Equals(fullName, "TaleWorlds.MountAndBlade.View.MissionViews.MissionCrosshair", StringComparison.Ordinal) || string.Equals(fullName, "TaleWorlds.MountAndBlade.GauntletUI.Mission.MissionGauntletCrosshair", StringComparison.Ordinal))
				{
					try
					{
						val.SuspendView();
						_suspendedNativeCrosshairViews.Add(val);
					}
					catch
					{
					}
				}
			}
			Log("Suspended " + _suspendedNativeCrosshairViews.Count + " native crosshair view(s) for guided camera ownership.");
		}
		catch (Exception ex)
		{
			Log("Native crosshair suspension unavailable: " + ex.GetType().Name);
		}
	}

	private static bool IsMissionViewSuspended(MissionView view)
	{
		if (view == null)
		{
			return true;
		}
		try
		{
			PropertyInfo property = typeof(MissionView).GetProperty("IsViewSuspended", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property == null || property.PropertyType != typeof(bool))
			{
				return true;
			}
			object value = property.GetValue(view, null);
			return value is bool && (bool)value;
		}
		catch
		{
			return true;
		}
	}

	private void ResumeNativeCrosshairViews()
	{
		if (!_nativeCrosshairViewsSuspended)
		{
			return;
		}
		_nativeCrosshairViewsSuspended = false;
		for (int num = _suspendedNativeCrosshairViews.Count - 1; num >= 0; num--)
		{
			MissionView val = _suspendedNativeCrosshairViews[num];
			if (val != null)
			{
				try
				{
					val.ResumeView();
				}
				catch
				{
				}
			}
		}
		_suspendedNativeCrosshairViews.Clear();
		Log("Restored native crosshair view(s) after guided camera ownership.");
	}

	private void UpdateCrosshairVisibility()
	{
		if (_state == State.Guiding)
		{
			Settings instance = GlobalSettings<Settings>.Instance;
			if (instance != null && instance.ShowCrosshair)
			{
				ShowCrosshair();
				return;
			}
		}
		HideCrosshair();
	}

	private void ShowCrosshair()
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Expected O, but got Unknown
		if (_crosshairLayer != null)
		{
			return;
		}
		MissionScreen val = ResolveMissionScreen();
		if (val == null)
		{
			return;
		}
		GauntletLayer val2 = null;
		try
		{
			val2 = new GauntletLayer("GuidedArrowCrosshair", 10000, false);
			_crosshairDataSource = new CrosshairViewModel();
			_crosshairMovie = val2.LoadMovie("GuidedArrowCrosshair", (ViewModel)(object)_crosshairDataSource);
			((ScreenBase)val).AddLayer((ScreenLayer)(object)val2);
			_crosshairLayer = val2;
			_crosshairScreen = val;
			Log("Crosshair overlay loaded.");
		}
		catch (Exception ex)
		{
			try
			{
				if (val2 != null)
				{
					((ScreenBase)val).RemoveLayer((ScreenLayer)(object)val2);
				}
			}
			catch
			{
			}
			Log("Crosshair overlay unavailable: " + ex.GetType().Name + " " + ex.Message);
			_crosshairLayer = null;
			_crosshairMovie = null;
			_crosshairDataSource = null;
			_crosshairScreen = null;
		}
	}

	private void HideCrosshair()
	{
		GauntletLayer crosshairLayer = _crosshairLayer;
		GauntletMovieIdentifier crosshairMovie = _crosshairMovie;
		MissionScreen crosshairScreen = _crosshairScreen;
		_crosshairLayer = null;
		_crosshairMovie = null;
		_crosshairDataSource = null;
		_crosshairScreen = null;
		if (crosshairLayer == null)
		{
			return;
		}
		try
		{
			if (crosshairMovie != null)
			{
				crosshairLayer.ReleaseMovie(crosshairMovie);
			}
		}
		catch
		{
		}
		try
		{
			if (crosshairScreen != null)
			{
				((ScreenBase)crosshairScreen).RemoveLayer((ScreenLayer)(object)crosshairLayer);
			}
		}
		catch
		{
		}
	}

	private void UpdateAutoguidanceRuntimeInput()
	{
		Mission mission = ((MissionBehavior)this).Mission;
		if (((mission != null) ? mission.InputManager : null) == null)
		{
			return;
		}
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance != null && !instance.EnableAutonomousGuidance)
		{
			if (_autoguidanceEffectiveActive)
			{
				SetAutoguidanceEffectiveState(active: false, notify: true);
			}
			_autoguidanceToggleActive = false;
			_autoguidanceBattleToggleActive = false;
			return;
		}
		bool num = GlobalSettings<Settings>.Instance?.AutoguidanceAlwaysOn ?? false;
		int num2 = GlobalSettings<Settings>.Instance?.AutoguidanceActivationMode ?? 0;
		bool flag = num2 <= 0 && (GlobalSettings<Settings>.Instance?.AutoguidancePersistToggleForBattle ?? false);
		if (num)
		{
			_autoguidanceToggleActive = false;
			SetAutoguidanceEffectiveState(active: true, notify: false);
			return;
		}
		if (!flag && _autoguidanceBattleToggleActive)
		{
			_autoguidanceBattleToggleActive = false;
			SetAutoguidanceEffectiveState(active: false, notify: false);
		}
		AutoguidanceHotkeyChord autoguidanceHotkeyChord = ResolveAutoguidanceHotkeyChord(GlobalSettings<Settings>.Instance?.AutoguidanceHotkeyName, GlobalSettings<Settings>.Instance?.AutoguidanceHotkey ?? 0);
		if (autoguidanceHotkeyChord == null || !autoguidanceHotkeyChord.IsValid)
		{
			if (flag)
			{
				SetAutoguidanceEffectiveState(_autoguidanceBattleToggleActive, notify: false);
			}
			else if (num2 <= 0)
			{
				SetAutoguidanceEffectiveState(_autoguidanceToggleActive, notify: false);
			}
			else
			{
				SetAutoguidanceEffectiveState(active: false, notify: false);
			}
		}
		else if (num2 <= 0)
		{
			if (IsAutoguidanceChordPressed(autoguidanceHotkeyChord))
			{
				if (flag)
				{
					_autoguidanceBattleToggleActive = !_autoguidanceBattleToggleActive;
					_autoguidanceToggleActive = false;
					SetAutoguidanceEffectiveState(_autoguidanceBattleToggleActive, notify: false);
					ShowAutoguidanceMessage(_autoguidanceBattleToggleActive ? "Autoguidance: ON (Battle)" : "Autoguidance: OFF (Battle)");
					Log("Battle-persistent autoguidance toggled " + (_autoguidanceBattleToggleActive ? "ON." : "OFF."));
				}
				else
				{
					_autoguidanceToggleActive = !_autoguidanceToggleActive;
					SetAutoguidanceEffectiveState(_autoguidanceToggleActive, notify: true);
				}
			}
			else if (flag)
			{
				SetAutoguidanceEffectiveState(_autoguidanceBattleToggleActive, notify: false);
			}
			else
			{
				SetAutoguidanceEffectiveState(_autoguidanceToggleActive, notify: false);
			}
		}
		else
		{
			bool active = IsAutoguidanceChordDown(autoguidanceHotkeyChord);
			_autoguidanceToggleActive = false;
			SetAutoguidanceEffectiveState(active, notify: true);
		}
	}

	private void InitializeAutoguidanceForCurrentShot()
	{
		_autoguidanceToggleActive = false;
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance != null && !instance.EnableAutonomousGuidance)
		{
			SetAutoguidanceEffectiveState(active: false, notify: false);
			return;
		}
		Settings instance2 = GlobalSettings<Settings>.Instance;
		if (instance2 != null && instance2.AutoguidanceAlwaysOn)
		{
			SetAutoguidanceEffectiveState(active: true, notify: false);
			return;
		}
		bool flag = (GlobalSettings<Settings>.Instance?.AutoguidanceActivationMode ?? 0) <= 0 && (GlobalSettings<Settings>.Instance?.AutoguidancePersistToggleForBattle ?? false);
		SetAutoguidanceEffectiveState(flag && _autoguidanceBattleToggleActive, notify: false);
	}

	private AutoguidanceHotkeyChord ResolveAutoguidanceHotkeyChord(string configuredName, int legacySelection)
	{
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		string text = configuredName?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			text = ((legacySelection > 0) ? ResolveLegacyAutoguidanceHotkeyName(legacySelection) : "Ctrl+G");
		}
		if (_autoguidanceHotkeyChord != null && string.Equals(_autoguidanceHotkeyChord.SourceText, text, StringComparison.OrdinalIgnoreCase))
		{
			return _autoguidanceHotkeyChord;
		}
		AutoguidanceHotkeyChord autoguidanceHotkeyChord = new AutoguidanceHotkeyChord
		{
			SourceText = text
		};
		List<InputKey> list = new List<InputKey>(4);
		string[] array = text.Split(new char[1] { '+' }, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text2 = array[i].Trim();
			if (text2.Length == 0)
			{
				continue;
			}
			if (string.Equals(text2, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(text2, "Control", StringComparison.OrdinalIgnoreCase))
			{
				autoguidanceHotkeyChord.RequireControl = true;
				continue;
			}
			if (string.Equals(text2, "Shift", StringComparison.OrdinalIgnoreCase))
			{
				autoguidanceHotkeyChord.RequireShift = true;
				continue;
			}
			if (string.Equals(text2, "Alt", StringComparison.OrdinalIgnoreCase))
			{
				autoguidanceHotkeyChord.RequireAlt = true;
				continue;
			}
			if (text2.Length == 1 && char.IsDigit(text2[0]))
			{
				text2 = "D" + text2;
			}
			if (string.Equals(text2, "Mouse4", StringComparison.OrdinalIgnoreCase))
			{
				text2 = "X1MouseButton";
			}
			else if (string.Equals(text2, "Mouse5", StringComparison.OrdinalIgnoreCase))
			{
				text2 = "X2MouseButton";
			}
			if (!Enum.TryParse<InputKey>(text2, true, out InputKey result) || list.Contains(result) || list.Count >= 4)
			{
				autoguidanceHotkeyChord.IsValid = false;
				autoguidanceHotkeyChord.Keys = (InputKey[])(object)new InputKey[0];
				_autoguidanceHotkeyChord = autoguidanceHotkeyChord;
				return autoguidanceHotkeyChord;
			}
			list.Add(result);
		}
		autoguidanceHotkeyChord.Keys = list.ToArray();
		autoguidanceHotkeyChord.IsValid = autoguidanceHotkeyChord.Keys.Length != 0;
		_autoguidanceHotkeyChord = autoguidanceHotkeyChord;
		return autoguidanceHotkeyChord;
	}

	private static string ResolveLegacyAutoguidanceHotkeyName(int selection)
	{
		return selection switch
		{
			1 => "H", 
			2 => "J", 
			3 => "K", 
			4 => "L", 
			5 => "V", 
			6 => "B", 
			7 => "N", 
			8 => "M", 
			_ => "Ctrl+G", 
		};
	}

	private bool IsAutoguidanceChordDown(AutoguidanceHotkeyChord chord)
	{
		Mission mission = ((MissionBehavior)this).Mission;
		IInputContext val = ((mission != null) ? mission.InputManager : null);
		if (val == null || chord == null || !chord.IsValid)
		{
			return false;
		}
		if (chord.RequireControl && !val.IsControlDown())
		{
			return false;
		}
		if (chord.RequireShift && !val.IsShiftDown())
		{
			return false;
		}
		if (chord.RequireAlt && !val.IsAltDown())
		{
			return false;
		}
		for (int i = 0; i < chord.Keys.Length; i++)
		{
			if (!val.IsKeyDown(chord.Keys[i]))
			{
				return false;
			}
		}
		return true;
	}

	private bool IsAutoguidanceChordPressed(AutoguidanceHotkeyChord chord)
	{
		Mission mission = ((MissionBehavior)this).Mission;
		IInputContext val = ((mission != null) ? mission.InputManager : null);
		if (val == null || !IsAutoguidanceChordDown(chord))
		{
			return false;
		}
		for (int i = 0; i < chord.Keys.Length; i++)
		{
			if (val.IsKeyPressed(chord.Keys[i]))
			{
				return true;
			}
		}
		return false;
	}

	private bool IsAutoguidanceRuntimeActive()
	{
		if (_autoguidanceEffectiveActive)
		{
			Settings instance = GlobalSettings<Settings>.Instance;
			if (instance == null || instance.EnableAutonomousGuidance)
			{
				return _state == State.Guiding;
			}
		}
		return false;
	}

	private void SetAutoguidanceEffectiveState(bool active, bool notify)
	{
		if (_autoguidanceEffectiveActive == active)
		{
			return;
		}
		_autoguidanceEffectiveActive = active;
		_autoguidanceReacquireCountdown = 0f;
		if (active)
		{
			AssignAutoguidanceTargets(clearExisting: true);
			if (notify)
			{
				ShowAutoguidanceMessage("Autoguidance: ON");
			}
			Log("Autoguidance enabled for guided-shot generation " + _activeShotGeneration + ".");
		}
		else
		{
			ClearAutoguidanceTargets();
			if (notify)
			{
				ShowAutoguidanceMessage("Autoguidance: OFF");
			}
			Log("Autoguidance disabled for guided-shot generation " + _activeShotGeneration + ".");
		}
	}

	private void ResetAutoguidanceState(bool notify)
	{
		bool autoguidanceEffectiveActive = _autoguidanceEffectiveActive;
		_autoguidanceToggleActive = false;
		_autoguidanceEffectiveActive = false;
		_autoguidanceHadAnyTarget = false;
		_autoguidanceReacquireCountdown = 0f;
		ClearAutoguidanceTargets();
		((List<Agent>)(object)_autoguidanceCandidates).Clear();
		_autoguidanceRankCandidates.Clear();
		_autoguidanceCandidateHeads.Clear();
		_autoguidanceRouteObstacleCache.Clear();
		_autoguidanceRouteTerrainCache.Clear();
		_autoguidanceAssignedTargets.Clear();
		if (notify && autoguidanceEffectiveActive)
		{
			ShowAutoguidanceMessage("Autoguidance: OFF");
		}
	}

	private void ClearAutoguidanceTargets()
	{
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			ClearAutoguidanceTarget(_trackedMissiles[i]);
		}
		_autoguidanceHadAnyTarget = false;
	}

	private static void ClearAutoguidanceTarget(TrackedMissile tracked)
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		if (tracked != null)
		{
			tracked.GuidanceTarget = null;
			tracked.GuidanceHeadBoneIndex = -1;
			tracked.GuidanceSmoothedHead = Vec3.Zero;
			tracked.GuidanceSmoothedHeadValid = false;
			tracked.GuidanceLastRawHead = Vec3.Zero;
			tracked.GuidanceLastRawHeadValid = false;
			tracked.GuidanceTargetVelocity = Vec3.Zero;
			tracked.GuidanceTargetVelocityValid = false;
			tracked.GuidanceBrokenFromFormation = false;
			tracked.GuidanceTerrainSampleCountdown = 0f;
			tracked.GuidanceCruiseGroundZ = 0f;
			tracked.GuidanceCruiseGroundValid = false;
			tracked.GuidanceSafetyTerrainSampleCountdown = 0f;
			tracked.GuidanceSafetyCurrentGroundZ = 0f;
			tracked.GuidanceSafetyMaximumGroundZ = 0f;
			tracked.GuidanceSafetyGroundValid = false;
			tracked.GuidanceSafetySampleDirection = Vec3.Zero;
			tracked.GuidanceSafetySampleDirectionValid = false;
			tracked.GuidanceRecoveryActive = false;
			tracked.GuidanceRecoveryTurnAxis = Vec3.Zero;
			tracked.GuidanceRecoveryTurnAxisValid = false;
			tracked.GuidanceRecoveryTurnedRadians = 0f;
			tracked.GuidanceRecoveryReplanCount = 0;
			tracked.GuidanceForceDirectIntercept = false;
			tracked.GuidanceRouteTargets.Clear();
			tracked.GuidanceObstacleWaypoint = Vec3.Zero;
			tracked.GuidanceObstacleWaypointValid = false;
			tracked.GuidanceObstacleGoal = Vec3.Zero;
			tracked.GuidanceObstacleGoalValid = false;
			tracked.GuidanceObstacleRecheckCountdown = 0f;
			tracked.GuidanceRouteReplanRequested = false;
			tracked.GuidanceNoProgressElapsed = 0f;
			tracked.GuidanceLastTargetDistance = 0f;
			tracked.GuidanceLastTargetDistanceValid = false;
			tracked.GuidanceFallbackDirection = Vec3.Zero;
			tracked.GuidanceFallbackDirectionValid = false;
		}
	}

	private void ShowAutoguidanceMessage(string message)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Expected O, but got Unknown
		if (string.IsNullOrEmpty(message))
		{
			return;
		}
		try
		{
			InformationManager.DisplayMessage(new InformationMessage(message));
		}
		catch
		{
		}
	}

	private void TickAutoguidanceReacquisition(float realDt)
	{
		if (!IsAutoguidanceRuntimeActive())
		{
			return;
		}
		bool flag = false;
		bool flag2 = false;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			TrackedMissile trackedMissile = _trackedMissiles[i];
			if (!IsAutoguidanceEligibleMissile(trackedMissile))
			{
				continue;
			}
			if (trackedMissile.GuidanceRouteReplanRequested)
			{
				flag = true;
			}
			if (!IsAutoguidanceTargetValid(trackedMissile.GuidanceTarget))
			{
				Agent guidanceTarget = trackedMissile.GuidanceTarget;
				if (!TryAdvanceAutoguidanceRoute(trackedMissile, guidanceTarget, impactConfirmed: false))
				{
					trackedMissile.GuidanceTarget = null;
					trackedMissile.GuidanceHeadBoneIndex = -1;
					trackedMissile.GuidanceRouteReplanRequested = true;
					flag = true;
				}
				else
				{
					flag2 = true;
				}
			}
			else
			{
				flag2 = true;
			}
		}
		if (!flag2 && _autoguidanceHadAnyTarget)
		{
			_autoguidanceHadAnyTarget = false;
			ShowAutoguidanceMessage("Autoguidance: Target Lost");
		}
		if (!flag)
		{
			return;
		}
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance == null || instance.AutoguidanceAutomaticReacquisition)
		{
			_autoguidanceReacquireCountdown -= Math.Max(0f, realDt);
			if (!(_autoguidanceReacquireCountdown > 0f))
			{
				_autoguidanceReacquireCountdown = 0.15f;
				AssignAutoguidanceTargets(clearExisting: false);
			}
		}
	}

	private bool IsAutoguidanceEligibleMissile(TrackedMissile tracked)
	{
		if (tracked == null)
		{
			return false;
		}
		bool flag = tracked.Index == _cameraMissileIndex;
		int num = GlobalSettings<Settings>.Instance?.AutoguidanceScope ?? 2;
		if (num <= 0)
		{
			return flag;
		}
		if (num == 1)
		{
			return !flag;
		}
		return true;
	}

	private bool IsAutoguidanceTargetValid(Agent target)
	{
		if (target == null)
		{
			return false;
		}
		try
		{
			return target.IsActive() && target.Health > 0f;
		}
		catch
		{
			return false;
		}
	}

	private void AssignAutoguidanceTargets(bool clearExisting)
	{
		if (!IsAutoguidanceRuntimeActive() || ((MissionBehavior)this).Mission == null || _trackedMissiles.Count == 0)
		{
			return;
		}
		if (clearExisting)
		{
			for (int i = 0; i < _trackedMissiles.Count; i++)
			{
				ClearAutoguidanceTarget(_trackedMissiles[i]);
			}
		}
		if (!CollectAutoguidanceCandidates())
		{
			return;
		}
		_autoguidanceAssignedTargets.Clear();
		if ((GlobalSettings<Settings>.Instance?.AutoguidanceSplitTargetDistribution ?? 0) == 1)
		{
			TrackedMissile trackedMissile = FindTrackedMissile(_cameraMissileIndex);
			if (trackedMissile == null || !IsAutoguidanceEligibleMissile(trackedMissile))
			{
				trackedMissile = null;
				for (int j = 0; j < _trackedMissiles.Count; j++)
				{
					if (IsAutoguidanceEligibleMissile(_trackedMissiles[j]))
					{
						trackedMissile = _trackedMissiles[j];
						break;
					}
				}
			}
			int num = ((trackedMissile != null) ? FindBestAutoguidanceCandidate(trackedMissile, requireUnused: false) : (-1));
			if (num >= 0)
			{
				Agent val = _autoguidanceRankCandidates[num];
				for (int k = 0; k < _trackedMissiles.Count; k++)
				{
					TrackedMissile trackedMissile2 = _trackedMissiles[k];
					if (IsAutoguidanceEligibleMissile(trackedMissile2) && (!IsAutoguidanceTargetValid(trackedMissile2.GuidanceTarget) || trackedMissile2.GuidanceRouteReplanRequested))
					{
						if (WasAutoguidanceTargetConsumed(trackedMissile2, val))
						{
							PlanAndAssignAutoguidanceRoute(trackedMissile2, requireUnused: false, null);
						}
						else
						{
							PlanAndAssignAutoguidanceRoute(trackedMissile2, requireUnused: false, val);
						}
					}
				}
			}
		}
		else
		{
			for (int l = 0; l < _trackedMissiles.Count; l++)
			{
				TrackedMissile trackedMissile3 = _trackedMissiles[l];
				if (IsAutoguidanceEligibleMissile(trackedMissile3) && IsAutoguidanceTargetValid(trackedMissile3.GuidanceTarget) && !trackedMissile3.GuidanceRouteReplanRequested)
				{
					AddAssignedTargetIfMissing(trackedMissile3.GuidanceTarget);
				}
			}
			for (int m = 0; m < _trackedMissiles.Count; m++)
			{
				TrackedMissile trackedMissile4 = _trackedMissiles[m];
				if (IsAutoguidanceEligibleMissile(trackedMissile4) && (!IsAutoguidanceTargetValid(trackedMissile4.GuidanceTarget) || trackedMissile4.GuidanceRouteReplanRequested))
				{
					Agent forcedFirstTarget = (IsAutoguidanceTargetValid(trackedMissile4.GuidanceTarget) ? trackedMissile4.GuidanceTarget : null);
					if (PlanAndAssignAutoguidanceRoute(trackedMissile4, requireUnused: true, forcedFirstTarget) || PlanAndAssignAutoguidanceRoute(trackedMissile4, requireUnused: false, forcedFirstTarget) || PlanAndAssignAutoguidanceRoute(trackedMissile4, requireUnused: true, null) || PlanAndAssignAutoguidanceRoute(trackedMissile4, requireUnused: false, null))
					{
						AddAssignedTargetIfMissing(trackedMissile4.GuidanceTarget);
					}
				}
			}
		}
		bool autoguidanceHadAnyTarget = false;
		for (int n = 0; n < _trackedMissiles.Count; n++)
		{
			if (IsAutoguidanceEligibleMissile(_trackedMissiles[n]) && IsAutoguidanceTargetValid(_trackedMissiles[n].GuidanceTarget))
			{
				autoguidanceHadAnyTarget = true;
				break;
			}
		}
		_autoguidanceHadAnyTarget = autoguidanceHadAnyTarget;
	}

	private bool CollectAutoguidanceCandidates()
	{
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		((List<Agent>)(object)_autoguidanceCandidates).Clear();
		_autoguidanceRankCandidates.Clear();
		_autoguidanceCandidateHeads.Clear();
		_autoguidanceRouteObstacleCache.Clear();
		_autoguidanceRouteTerrainCache.Clear();
		Mission mission = ((MissionBehavior)this).Mission;
		Agent val = ((mission != null) ? mission.MainAgent : null);
		Team val2 = ((val != null) ? val.Team : null);
		if (mission == null || val == null || val2 == null)
		{
			return false;
		}
		Vec3 val3 = Vec3.Zero;
		TrackedMissile trackedMissile = FindTrackedMissile(_cameraMissileIndex);
		try
		{
			if (trackedMissile != null)
			{
				val3 = ((MBMissile)trackedMissile.Missile).GetPosition();
			}
			else if (_trackedMissiles.Count > 0)
			{
				val3 = ((MBMissile)_trackedMissiles[0].Missile).GetPosition();
			}
		}
		catch
		{
			return false;
		}
		if (!IsFinite(val3))
		{
			return false;
		}
		float num = 0f;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			try
			{
				Vec3 val4 = ((MBMissile)_trackedMissiles[i].Missile).GetPosition() - val3;
				float length = val4.Length;
				if (IsFinite(length) && length > num)
				{
					num = length;
				}
			}
			catch
			{
			}
		}
		try
		{
			mission.GetNearbyEnemyAgents(new Vec2(val3.x, val3.y), 150f + num, val2, _autoguidanceCandidates);
		}
		catch
		{
			((List<Agent>)(object)_autoguidanceCandidates).Clear();
			return false;
		}
		int num2 = Math.Min(96, ((List<Agent>)(object)_autoguidanceCandidates).Count);
		for (int j = 0; j < num2; j++)
		{
			Agent val5 = ((List<Agent>)(object)_autoguidanceCandidates)[j];
			if (IsAutoguidanceTargetValid(val5) && val5 != val)
			{
				int headBoneIndex = ResolveGuidanceHeadBoneIndex(val5);
				if (TryGetGuidanceHeadPosition(val5, headBoneIndex, out var position))
				{
					_autoguidanceRankCandidates.Add(val5);
					_autoguidanceCandidateHeads.Add(position);
				}
			}
		}
		return _autoguidanceRankCandidates.Count > 0;
	}

	private int FindBestAutoguidanceCandidate(TrackedMissile tracked, bool requireUnused)
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0200: Unknown result type (might be due to invalid IL or missing references)
		//IL_0204: Unknown result type (might be due to invalid IL or missing references)
		//IL_0295: Unknown result type (might be due to invalid IL or missing references)
		//IL_0296: Unknown result type (might be due to invalid IL or missing references)
		//IL_0298: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a7: Unknown result type (might be due to invalid IL or missing references)
		if (tracked?.Missile == null)
		{
			return -1;
		}
		Vec3 position;
		Vec3 velocity;
		try
		{
			position = ((MBMissile)tracked.Missile).GetPosition();
			velocity = ((MBMissile)tracked.Missile).GetVelocity();
		}
		catch
		{
			return -1;
		}
		float lengthSquared = velocity.LengthSquared;
		if (!IsFinite(position) || !IsFinite(velocity) || !IsFinite(lengthSquared) || lengthSquared <= 1E-06f)
		{
			return -1;
		}
		float num = (float)Math.Sqrt(lengthSquared);
		Vec3 val = velocity / num;
		Vec3 a = val;
		int num2 = GlobalSettings<Settings>.Instance?.AutoguidanceTargetSelection ?? 0;
		if (num2 == 1 && _cameraFrameValid)
		{
			a = NormalizeSafe(-_cameraFrame.rotation.u, val);
		}
		float num3 = Clamp(GlobalSettings<Settings>.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f);
		float adaptiveSteeringMultiplier = GetAdaptiveSteeringMultiplier(num);
		float num4 = num3 / Math.Max(1f, adaptiveSteeringMultiplier);
		int num5 = -1;
		float num6 = float.PositiveInfinity;
		int num7 = -1;
		float num8 = float.PositiveInfinity;
		int result = -1;
		float num9 = float.PositiveInfinity;
		float autoguidanceReachabilityReserve = GetAutoguidanceReachabilityReserve(num4, num, 0f);
		for (int i = 0; i < _autoguidanceRankCandidates.Count; i++)
		{
			Agent val2 = _autoguidanceRankCandidates[i];
			if ((requireUnused && IsAssignedAutoguidanceTarget(val2)) || WasAutoguidanceTargetConsumed(tracked, val2))
			{
				continue;
			}
			Vec3 val3 = _autoguidanceCandidateHeads[i];
			Vec3 val4 = val3 - position;
			float lengthSquared2 = val4.LengthSquared;
			if (!IsFinite(val4) || !IsFinite(lengthSquared2) || lengthSquared2 <= 1E-06f)
			{
				continue;
			}
			float num10 = (float)Math.Sqrt(lengthSquared2);
			Vec3 b = val4 / num10;
			float num11 = (float)Math.Acos(Clamp(Dot(val, b), -1f, 1f));
			float num12 = (float)Math.Acos(Clamp(Dot(a, b), -1f, 1f));
			bool num13 = IsAutoguidanceWaypointReachable(position, val, val3, num4, autoguidanceReachabilityReserve);
			EvaluateAutoguidanceApproachGeometry(tracked, val2, position, val, num4, val3, out var profileFeasible, out var postImpactSafe, out var expectedEntryAngle, out var resolvedProfile);
			float num14;
			if (num2 == 1)
			{
				num14 = num12 * 40f + num10 * 0.1f;
			}
			else
			{
				float num15 = Math.Abs(GetPreferredEntryAngleForProfile(resolvedProfile) - expectedEntryAngle);
				num14 = num10 + num11 * 0.05f + Math.Min(1.5f, num15 * 3f);
				if (!profileFeasible)
				{
					num14 += 0.75f;
				}
			}
			if (num13)
			{
				if (postImpactSafe)
				{
					if (num14 < num6)
					{
						num6 = num14;
						num5 = i;
					}
				}
				else if (num14 < num8)
				{
					num8 = num14;
					num7 = i;
				}
				continue;
			}
			Vec3 recoveryTurnAxis;
			float num16 = EstimateAutoguidanceTravelDistance(position, val, val3, num4, autoguidanceReachabilityReserve, out recoveryTurnAxis);
			if (IsFinite(recoveryTurnAxis) && recoveryTurnAxis.LengthSquared > 0.0001f && IsFinite(num16))
			{
				float num17 = num16;
				if (num2 == 1)
				{
					num17 += num12 * 40f;
				}
				if (!postImpactSafe)
				{
					num17 += num4 * 0.75f;
				}
				if (num17 < num9)
				{
					num9 = num17;
					result = i;
				}
			}
		}
		if (num5 >= 0)
		{
			return num5;
		}
		if (num7 >= 0)
		{
			return num7;
		}
		return result;
	}

	private bool PlanAndAssignAutoguidanceRoute(TrackedMissile tracked, bool requireUnused, Agent forcedFirstTarget)
	{
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0127: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_0132: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_013d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_0159: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0174: Unknown result type (might be due to invalid IL or missing references)
		//IL_0179: Unknown result type (might be due to invalid IL or missing references)
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_017c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_018c: Unknown result type (might be due to invalid IL or missing references)
		//IL_018e: Unknown result type (might be due to invalid IL or missing references)
		if (tracked?.Missile == null || _autoguidanceRankCandidates.Count == 0)
		{
			return false;
		}
		Vec3 position;
		Vec3 velocity;
		try
		{
			position = ((MBMissile)tracked.Missile).GetPosition();
			velocity = ((MBMissile)tracked.Missile).GetVelocity();
		}
		catch
		{
			return false;
		}
		float lengthSquared = velocity.LengthSquared;
		if (!IsFinite(position) || !IsFinite(velocity) || !IsFinite(lengthSquared) || lengthSquared <= 1E-06f)
		{
			return false;
		}
		float num = (float)Math.Sqrt(lengthSquared);
		Vec3 val = velocity / num;
		float effectiveTurnRadius = Clamp(GlobalSettings<Settings>.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f) / Math.Max(1f, GetAdaptiveSteeringMultiplier(num));
		int val2 = GlobalSettings<Settings>.Instance?.AutoguidancePlannedTargetCount ?? 5;
		int num2 = Math.Max(1, Math.Min(6, val2));
		List<int> list = BuildAutoguidanceRouteCandidatePool(tracked, position, val, requireUnused, forcedFirstTarget);
		if (list.Count == 0)
		{
			return false;
		}
		int num3 = ((forcedFirstTarget != null) ? FindAutoguidanceCandidateIndex(forcedFirstTarget) : SelectRouteAwareFirstCandidate(tracked, list, position, val, num, effectiveTurnRadius, requireUnused));
		if (num3 < 0)
		{
			return false;
		}
		List<int> list2 = new List<int>(num2) { num3 };
		Vec3 val3 = position;
		Vec3 val4 = _autoguidanceCandidateHeads[num3];
		Vec3 val5 = NormalizeSafe(val4 - val3, val);
		int currentIndex = num3;
		while (list2.Count < num2)
		{
			int num4 = SelectNextPlannedRouteTarget(tracked, list, list2, currentIndex, val4, val5, num, effectiveTurnRadius);
			if (num4 < 0)
			{
				break;
			}
			Vec3 val6 = _autoguidanceCandidateHeads[num4];
			val5 = NormalizeSafe(val6 - val4, val5);
			val3 = val4;
			val4 = val6;
			currentIndex = num4;
			list2.Add(num4);
		}
		tracked.GuidanceRouteTargets.Clear();
		for (int i = 0; i < list2.Count; i++)
		{
			Agent val7 = _autoguidanceRankCandidates[list2[i]];
			if (IsAutoguidanceTargetValid(val7) && !WasAutoguidanceTargetConsumed(tracked, val7))
			{
				tracked.GuidanceRouteTargets.Add(val7);
			}
		}
		if (tracked.GuidanceRouteTargets.Count == 0)
		{
			return false;
		}
		AssignAutoguidanceTarget(tracked, tracked.GuidanceRouteTargets[0]);
		return true;
	}

	private List<int> BuildAutoguidanceRouteCandidatePool(TrackedMissile tracked, Vec3 position, Vec3 currentDirection, bool requireUnused, Agent forcedFirstTarget)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		List<int> list = new List<int>();
		for (int i = 0; i < _autoguidanceRankCandidates.Count; i++)
		{
			Agent target = _autoguidanceRankCandidates[i];
			if (IsAutoguidanceTargetValid(target) && !WasAutoguidanceTargetConsumed(tracked, target) && (!requireUnused || forcedFirstTarget != null || !IsAssignedAutoguidanceTarget(target)))
			{
				list.Add(i);
			}
		}
		list.Sort(delegate(int left, int right)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_0024: Unknown result type (might be due to invalid IL or missing references)
			//IL_002a: Unknown result type (might be due to invalid IL or missing references)
			//IL_003b: Unknown result type (might be due to invalid IL or missing references)
			float autoguidanceRoutePoolScore = GetAutoguidanceRoutePoolScore(position, currentDirection, _autoguidanceCandidateHeads[left]);
			float autoguidanceRoutePoolScore2 = GetAutoguidanceRoutePoolScore(position, currentDirection, _autoguidanceCandidateHeads[right]);
			return autoguidanceRoutePoolScore.CompareTo(autoguidanceRoutePoolScore2);
		});
		if (list.Count > 24)
		{
			list.RemoveRange(24, list.Count - 24);
		}
		if (forcedFirstTarget != null)
		{
			int num = FindAutoguidanceCandidateIndex(forcedFirstTarget);
			if (num >= 0 && !list.Contains(num))
			{
				list.Insert(0, num);
			}
		}
		return list;
	}

	private static float GetAutoguidanceRoutePoolScore(Vec3 position, Vec3 currentDirection, Vec3 head)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = head - position;
		float length = val.Length;
		if (!IsFinite(val) || !IsFinite(length) || length <= 1E-06f)
		{
			return float.PositiveInfinity;
		}
		Vec3 b = val / length;
		float num = (float)Math.Acos(Clamp(Dot(currentDirection, b), -1f, 1f));
		return length + num * 8f;
	}

	private int SelectRouteAwareFirstCandidate(TrackedMissile tracked, List<int> pool, Vec3 position, Vec3 currentDirection, float speed, float effectiveTurnRadius, bool requireUnused)
	{
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_012a: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0150: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a3: Unknown result type (might be due to invalid IL or missing references)
		int num = FindBestAutoguidanceCandidate(tracked, requireUnused);
		int result = ((num >= 0) ? num : pool[0]);
		float num2 = float.PositiveInfinity;
		int num3 = Math.Min(12, pool.Count);
		int num4 = GlobalSettings<Settings>.Instance?.AutoguidanceTargetSelection ?? 0;
		Vec3 a = currentDirection;
		if (num4 == 1 && _cameraFrameValid)
		{
			a = NormalizeSafe(-_cameraFrame.rotation.u, currentDirection);
		}
		for (int i = 0; i < num3; i++)
		{
			int num5 = pool[i];
			Agent candidate = _autoguidanceRankCandidates[num5];
			Vec3 val = _autoguidanceCandidateHeads[num5];
			Vec3 val2 = val - position;
			float length = val2.Length;
			if (!IsFinite(val2) || !IsFinite(length) || length <= 1E-06f)
			{
				continue;
			}
			Vec3 val3 = val2 / length;
			float num6 = (float)Math.Acos(Clamp(Dot(currentDirection, val3), -1f, 1f));
			if (IsAutoguidanceWaypointReachable(position, currentDirection, val, effectiveTurnRadius, GetAutoguidanceReachabilityReserve(effectiveTurnRadius, speed, 0f)) || num5 == num)
			{
				EvaluateAutoguidanceApproachGeometry(tracked, candidate, position, currentDirection, effectiveTurnRadius, val, out var profileFeasible, out var postImpactSafe, out var _, out var _);
				float num7 = ((num4 == 1) ? ((float)Math.Acos(Clamp(Dot(a, val3), -1f, 1f)) * 40f + length * 0.1f) : (length + num6 * 0.2f));
				if (!profileFeasible)
				{
					num7 += 1f;
				}
				if (!postImpactSafe)
				{
					num7 += 20f;
				}
				float num8 = EstimateBestRouteContinuationScore(tracked, pool, num5, val, val3, speed, effectiveTurnRadius);
				num7 += num8 * 0.35f;
				if (num7 < num2)
				{
					num2 = num7;
					result = num5;
				}
			}
		}
		return result;
	}

	private float EstimateBestRouteContinuationScore(TrackedMissile tracked, List<int> pool, int currentIndex, Vec3 currentHead, Vec3 incomingDirection, float speed, float effectiveTurnRadius)
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		float num = float.PositiveInfinity;
		for (int i = 0; i < pool.Count; i++)
		{
			int num2 = pool[i];
			if (num2 != currentIndex)
			{
				bool safe;
				float num3 = EvaluatePlannedRouteTransition(tracked, currentIndex, num2, currentHead, incomingDirection, speed, effectiveTurnRadius, out safe);
				if (safe && num3 < num)
				{
					num = num3;
				}
			}
		}
		if (!IsFinite(num))
		{
			return 60f;
		}
		return num;
	}

	private int SelectNextPlannedRouteTarget(TrackedMissile tracked, List<int> pool, List<int> routeIndices, int currentIndex, Vec3 currentHead, Vec3 incomingDirection, float speed, float effectiveTurnRadius)
	{
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		int result = -1;
		float num = float.PositiveInfinity;
		for (int i = 0; i < pool.Count; i++)
		{
			int num2 = pool[i];
			if (routeIndices.Contains(num2))
			{
				continue;
			}
			Agent target = _autoguidanceRankCandidates[num2];
			if (IsAutoguidanceTargetValid(target) && !WasAutoguidanceTargetConsumed(tracked, target))
			{
				bool safe;
				float num3 = EvaluatePlannedRouteTransition(tracked, currentIndex, num2, currentHead, incomingDirection, speed, effectiveTurnRadius, out safe);
				if (safe && num3 < num)
				{
					num = num3;
					result = num2;
				}
			}
		}
		return result;
	}

	private float EvaluatePlannedRouteTransition(TrackedMissile tracked, int fromIndex, int toIndex, Vec3 fromHead, Vec3 incomingDirection, float speed, float effectiveTurnRadius, out bool safe)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = _autoguidanceCandidateHeads[toIndex];
		Vec3 val2 = val - fromHead;
		float length = val2.Length;
		safe = false;
		if (!IsFinite(val2) || !IsFinite(length) || length <= 1.5f)
		{
			return float.PositiveInfinity;
		}
		Vec3 val3 = val2 / length;
		float num = (float)Math.Acos(Clamp(Dot(incomingDirection, val3), -1f, 1f));
		float num2 = EstimateMinimumReachableDistance(effectiveTurnRadius, num);
		float num3 = num * effectiveTurnRadius * 1.3f;
		float num4 = Math.Max(0f, num2 + 2f - length) * 18f;
		float num5 = Math.Max(0f, val3.z) * 12f;
		float plannedRouteTerrainPenalty = GetPlannedRouteTerrainPenalty(fromIndex, toIndex, fromHead, val, speed);
		bool flag = IsPlannedRouteTransitionObstructed(fromIndex, toIndex, fromHead, val, tracked);
		float num6 = (flag ? 500f : 0f);
		safe = num4 <= 0.01f && plannedRouteTerrainPenalty <= 0.01f && !flag;
		return length + num3 + num4 + num5 + plannedRouteTerrainPenalty + num6;
	}

	private float GetPlannedRouteTerrainPenalty(int fromIndex, int toIndex, Vec3 fromHead, Vec3 toHead, float speed)
	{
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		long key = ((long)fromIndex + 1L << 32) ^ (uint)(toIndex + 1);
		if (_autoguidanceRouteTerrainCache.TryGetValue(key, out var value))
		{
			return value;
		}
		Vec3 val = toHead - fromHead;
		float length = val.Length;
		if (!IsFinite(val) || !IsFinite(length) || length <= 1.5f)
		{
			return 1000f;
		}
		Vec3 val2 = val / length;
		float num = Math.Min(11f, Math.Max(2f, length - 1f));
		float num2 = 0f;
		for (int i = 1; i <= 4; i++)
		{
			float num3 = num * (float)i / 4f;
			Vec3 val3 = fromHead + val2 * num3;
			if (TryGetTerrainHeight(val3, out var height))
			{
				float num4 = num3 / Math.Max(1f, speed);
				float num5 = Math.Max(0f, 9.81f) * 0.5f * num4 * num4;
				float num6 = height + 1.15f + num5;
				if (val3.z < num6)
				{
					num2 += (num6 - val3.z) * 80f;
				}
			}
		}
		_autoguidanceRouteTerrainCache[key] = num2;
		return num2;
	}

	private bool IsPlannedRouteTransitionObstructed(int fromIndex, int toIndex, Vec3 fromHead, Vec3 toHead, TrackedMissile tracked)
	{
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance != null && !instance.AutoguidanceObstacleAvoidance)
		{
			return false;
		}
		long key = ((long)fromIndex + 1L << 32) ^ (uint)(toIndex + 1);
		if (_autoguidanceRouteObstacleCache.TryGetValue(key, out var value))
		{
			return value;
		}
		Vec3 hitPoint;
		bool flag = IsAutoguidanceSegmentObstructed(fromHead, toHead, tracked, out hitPoint, stopBeforeTarget: true);
		_autoguidanceRouteObstacleCache[key] = flag;
		return flag;
	}

	private int FindAutoguidanceCandidateIndex(Agent target)
	{
		if (target == null)
		{
			return -1;
		}
		for (int i = 0; i < _autoguidanceRankCandidates.Count; i++)
		{
			if (_autoguidanceRankCandidates[i] == target)
			{
				return i;
			}
		}
		return -1;
	}

	private void EvaluateAutoguidanceApproachGeometry(TrackedMissile tracked, Agent candidate, Vec3 projectilePosition, Vec3 currentDirection, float effectiveTurnRadius, Vec3 targetHead, out bool profileFeasible, out bool postImpactSafe, out float expectedEntryAngle, out AutoguidanceFlightProfile resolvedProfile)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0200: Unknown result type (might be due to invalid IL or missing references)
		//IL_0202: Unknown result type (might be due to invalid IL or missing references)
		//IL_0259: Unknown result type (might be due to invalid IL or missing references)
		//IL_025b: Unknown result type (might be due to invalid IL or missing references)
		//IL_025e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0263: Unknown result type (might be due to invalid IL or missing references)
		//IL_0268: Unknown result type (might be due to invalid IL or missing references)
		//IL_026c: Unknown result type (might be due to invalid IL or missing references)
		//IL_027d: Unknown result type (might be due to invalid IL or missing references)
		//IL_027e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0280: Unknown result type (might be due to invalid IL or missing references)
		//IL_02ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_030e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0313: Unknown result type (might be due to invalid IL or missing references)
		//IL_0318: Unknown result type (might be due to invalid IL or missing references)
		//IL_0347: Unknown result type (might be due to invalid IL or missing references)
		//IL_0349: Unknown result type (might be due to invalid IL or missing references)
		//IL_034c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0351: Unknown result type (might be due to invalid IL or missing references)
		//IL_0356: Unknown result type (might be due to invalid IL or missing references)
		//IL_035d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0362: Unknown result type (might be due to invalid IL or missing references)
		//IL_0367: Unknown result type (might be due to invalid IL or missing references)
		//IL_036b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0377: Unknown result type (might be due to invalid IL or missing references)
		//IL_0390: Unknown result type (might be due to invalid IL or missing references)
		//IL_0391: Unknown result type (might be due to invalid IL or missing references)
		//IL_0393: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_03a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_03af: Unknown result type (might be due to invalid IL or missing references)
		//IL_0143: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0400: Unknown result type (might be due to invalid IL or missing references)
		//IL_0402: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d1: Unknown result type (might be due to invalid IL or missing references)
		profileFeasible = true;
		postImpactSafe = true;
		expectedEntryAngle = 0f;
		Vec3 agentVisualPositionSafe = GetAgentVisualPositionSafe(candidate, targetHead - WorldUp * 0.95f);
		Vec3 val = targetHead - projectilePosition;
		val.z = 0f;
		float length = val.Length;
		if (!IsFinite(length) || length <= 0.05f)
		{
			resolvedProfile = AutoguidanceFlightProfile.DirectHunter;
			return;
		}
		Vec3 val2 = val / length;
		float num = (float)Math.Atan2(targetHead.z - projectilePosition.z, length);
		bool flag = IsAutoguidanceEntrySafe(targetHead, agentVisualPositionSafe, num);
		bool flag2 = length <= Math.Max(6f, effectiveTurnRadius * 0.35f);
		AutoguidanceFlightProfile configuredAutoguidanceFlightProfile = GetConfiguredAutoguidanceFlightProfile();
		resolvedProfile = ((configuredAutoguidanceFlightProfile == AutoguidanceFlightProfile.AdaptiveMix) ? ResolveAdaptiveFlightProfile(tracked, projectilePosition, currentDirection, targetHead, agentVisualPositionSafe, effectiveTurnRadius) : configuredAutoguidanceFlightProfile);
		switch (resolvedProfile)
		{
		case AutoguidanceFlightProfile.LowStrike:
		{
			float num5 = Clamp(GlobalSettings<Settings>.Instance?.AutoguidanceCruiseGroundClearance ?? 0.9f, 0.45f, 3f);
			float num6 = DegreesToRadians(Clamp(GlobalSettings<Settings>.Instance?.AutoguidancePreferredRiseAngle ?? 8f, 2f, 20f));
			float num7 = agentVisualPositionSafe.z + num5;
			float num8 = Math.Max(0.15f, targetHead.z - num7);
			float val7 = num8 / Math.Max(0.035f, (float)Math.Tan(num6));
			float num9 = EstimateMinimumReachableDistance(effectiveTurnRadius, num6);
			float num10 = Clamp(Math.Max(val7, num9 * 1.15f), 4f, 24f);
			profileFeasible = false;
			if (length > num10 + 0.5f)
			{
				Vec3 waypoint = targetHead - val2 * num10;
				waypoint.z = num7;
				profileFeasible = IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, waypoint, effectiveTurnRadius);
			}
			expectedEntryAngle = (profileFeasible ? ((float)Math.Atan2(num8, num10)) : num);
			postImpactSafe = profileFeasible || flag || flag2;
			break;
		}
		case AutoguidanceFlightProfile.LoftedArc:
		{
			float loftedArcDescentAngle = GetLoftedArcDescentAngle(targetHead, agentVisualPositionSafe);
			float num11 = Clamp(Math.Max(8f, effectiveTurnRadius * 0.65f), 8f, 30f);
			float num12 = Clamp((float)Math.Tan(loftedArcDescentAngle) * num11, 1f, 6f);
			profileFeasible = false;
			if (length > num11 + 0.75f)
			{
				Vec3 waypoint2 = targetHead - val2 * num11;
				waypoint2.z = targetHead.z + num12;
				profileFeasible = IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, waypoint2, effectiveTurnRadius);
			}
			expectedEntryAngle = (profileFeasible ? (0f - loftedArcDescentAngle) : num);
			postImpactSafe = (profileFeasible && IsAutoguidanceEntrySafe(targetHead, agentVisualPositionSafe, 0f - loftedArcDescentAngle)) || flag || flag2;
			break;
		}
		case AutoguidanceFlightProfile.BankingFlank:
		{
			float num2 = Clamp(Math.Max(10f, effectiveTurnRadius * 0.7f), 8f, 28f);
			float num3 = num;
			profileFeasible = false;
			if (length > num2 + 1f)
			{
				Vec3 val3 = NormalizeSafe(Cross(val2, WorldUp), new Vec3(1f, 0f, 0f, -1f));
				float autoguidanceProfileSide = GetAutoguidanceProfileSide(tracked);
				float num4 = Clamp(length * 0.14f, 2.5f, Math.Min(9f, effectiveTurnRadius * 0.55f));
				Vec3 val4 = targetHead - val2 * num2 + val3 * (num4 * autoguidanceProfileSide);
				val4.z = Math.Max(agentVisualPositionSafe.z + 1.05f, targetHead.z - 0.15f);
				profileFeasible = IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, val4, effectiveTurnRadius);
				if (profileFeasible)
				{
					Vec3 val5 = targetHead - val4;
					Vec3 val6 = val5;
					val6.z = 0f;
					float length2 = val6.Length;
					if (IsFinite(length2) && length2 > 0.05f)
					{
						num3 = (float)Math.Atan2(val5.z, length2);
					}
				}
			}
			expectedEntryAngle = (profileFeasible ? num3 : num);
			postImpactSafe = (profileFeasible && IsAutoguidanceEntrySafe(targetHead, agentVisualPositionSafe, num3)) || flag || flag2;
			break;
		}
		case AutoguidanceFlightProfile.Serpentine:
			profileFeasible = length > Math.Max(12f, effectiveTurnRadius * 0.45f);
			expectedEntryAngle = num;
			postImpactSafe = flag || flag2;
			break;
		default:
			profileFeasible = true;
			expectedEntryAngle = num;
			postImpactSafe = flag || flag2;
			break;
		}
	}

	private AutoguidanceFlightProfile GetConfiguredAutoguidanceFlightProfile()
	{
		int valueOrDefault = (GlobalSettings<Settings>.Instance?.AutoguidanceFlightProfile?.SelectedIndex).GetValueOrDefault();
		valueOrDefault = Math.Max(0, Math.Min(6, valueOrDefault));
		AutoguidanceFlightProfile autoguidanceFlightProfile = (AutoguidanceFlightProfile)valueOrDefault;
		if (autoguidanceFlightProfile == AutoguidanceFlightProfile.LowStrike)
		{
			Settings instance = GlobalSettings<Settings>.Instance;
			if (instance != null && !instance.AutoguidanceLowRiseAttackProfile)
			{
				return AutoguidanceFlightProfile.DirectHunter;
			}
		}
		return autoguidanceFlightProfile;
	}

	private AutoguidanceFlightProfile ResolveTrackedAutoguidanceFlightProfile(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, Vec3 predictedHead, Vec3 predictedTargetBase, float effectiveTurnRadius)
	{
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		int valueOrDefault = (GlobalSettings<Settings>.Instance?.AutoguidanceFlightProfile?.SelectedIndex).GetValueOrDefault();
		valueOrDefault = Math.Max(0, Math.Min(6, valueOrDefault));
		if (tracked == null)
		{
			return GetConfiguredAutoguidanceFlightProfile();
		}
		if (tracked.GuidanceConfiguredProfileIndex != valueOrDefault)
		{
			tracked.GuidanceConfiguredProfileIndex = valueOrDefault;
			tracked.GuidanceResolvedProfileValid = false;
			tracked.GuidanceCruiseGroundValid = false;
			tracked.GuidanceTerrainSampleCountdown = 0f;
		}
		AutoguidanceFlightProfile autoguidanceFlightProfile = (AutoguidanceFlightProfile)valueOrDefault;
		if (autoguidanceFlightProfile == AutoguidanceFlightProfile.LowStrike)
		{
			Settings instance = GlobalSettings<Settings>.Instance;
			if (instance != null && !instance.AutoguidanceLowRiseAttackProfile)
			{
				return AutoguidanceFlightProfile.DirectHunter;
			}
		}
		if (autoguidanceFlightProfile != AutoguidanceFlightProfile.AdaptiveMix)
		{
			return autoguidanceFlightProfile;
		}
		if (!tracked.GuidanceResolvedProfileValid)
		{
			AutoguidanceFlightProfile guidanceResolvedProfileIndex = ResolveAdaptiveFlightProfile(tracked, projectilePosition, currentDirection, predictedHead, predictedTargetBase, effectiveTurnRadius);
			tracked.GuidanceResolvedProfileIndex = (int)guidanceResolvedProfileIndex;
			tracked.GuidanceResolvedProfileValid = true;
		}
		return (AutoguidanceFlightProfile)Math.Max(0, Math.Min(5, tracked.GuidanceResolvedProfileIndex));
	}

	private AutoguidanceFlightProfile ResolveAdaptiveFlightProfile(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, Vec3 targetHead, Vec3 targetBase, float effectiveTurnRadius)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = targetHead - projectilePosition;
		Vec3 val2 = val;
		val2.z = 0f;
		float length = val2.Length;
		if (!IsFinite(length) || length <= Math.Max(9f, effectiveTurnRadius * 0.4f))
		{
			return AutoguidanceFlightProfile.DirectHunter;
		}
		float num = (float)Math.Atan2(val.z, length);
		Settings instance = GlobalSettings<Settings>.Instance;
		if ((instance == null || instance.AutoguidanceLowRiseAttackProfile) && num < DegreesToRadians(-4f) && length > Math.Max(20f, effectiveTurnRadius * 0.8f))
		{
			return AutoguidanceFlightProfile.LowStrike;
		}
		if (num > DegreesToRadians(4f) || length > 70f)
		{
			return AutoguidanceFlightProfile.LoftedArc;
		}
		Vec3 b = NormalizeSafe(val, currentDirection);
		float num2 = Dot(GetNaturalBallisticReferenceDirection(tracked, currentDirection), b);
		if (IsFinite(num2) && num2 > 0.965f)
		{
			return AutoguidanceFlightProfile.NaturalBallistic;
		}
		if ((float)Math.Acos(Clamp(Dot(currentDirection, b), -1f, 1f)) > DegreesToRadians(16f) && length > 26f)
		{
			return AutoguidanceFlightProfile.BankingFlank;
		}
		if ((((tracked?.Index ?? 0) + (tracked?.ShotGeneration ?? 0)) & 1) != 0)
		{
			return AutoguidanceFlightProfile.NaturalBallistic;
		}
		return AutoguidanceFlightProfile.Serpentine;
	}

	private static float GetPreferredEntryAngleForProfile(AutoguidanceFlightProfile profile)
	{
		return profile switch
		{
			AutoguidanceFlightProfile.LowStrike => DegreesToRadians(Clamp(GlobalSettings<Settings>.Instance?.AutoguidancePreferredRiseAngle ?? 8f, 2f, 20f)), 
			AutoguidanceFlightProfile.LoftedArc => 0f - DegreesToRadians(4f), 
			_ => 0f, 
		};
	}

	private static bool IsAutoguidanceEntrySafe(Vec3 targetHead, Vec3 targetBase, float entryAngle)
	{
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		if (entryAngle >= -0.01f)
		{
			return true;
		}
		float num = 0f - (float)Math.Sin(entryAngle);
		if (!IsFinite(num) || num <= 1E-06f)
		{
			return true;
		}
		float num2 = Math.Max(0.15f, targetHead.z - (targetBase.z + 0.15f)) / num;
		if (IsFinite(num2))
		{
			return num2 >= 8f;
		}
		return false;
	}

	private static bool IsAutoguidanceWaypointReachable(Vec3 projectilePosition, Vec3 currentDirection, Vec3 waypoint, float effectiveTurnRadius)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		return IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, waypoint, effectiveTurnRadius, 0f);
	}

	private static bool IsAutoguidanceWaypointReachable(Vec3 projectilePosition, Vec3 currentDirection, Vec3 waypoint, float effectiveTurnRadius, float reserve)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = waypoint - projectilePosition;
		float length = val.Length;
		if (!IsFinite(val) || !IsFinite(length))
		{
			return false;
		}
		if (length <= 0.35f)
		{
			return true;
		}
		Vec3 b = val / length;
		float num = (float)Math.Acos(Clamp(Dot(currentDirection, b), -1f, 1f));
		float num2 = EstimateMinimumReachableDistance(effectiveTurnRadius, num);
		float v = (float)Math.Sin(Math.Min(num, (float)Math.PI / 2f));
		float num3 = Math.Max(0f, reserve) * Clamp(v, 0f, 1f);
		return length + 0.001f >= num2 + num3;
	}

	private static float GetAutoguidanceReachabilityReserve(float effectiveTurnRadius, float speed, float dt)
	{
		float num = Math.Max(0.75f, Math.Max(0.1f, effectiveTurnRadius) * 0.08f);
		float num2 = ((IsFinite(speed) && IsFinite(dt) && speed > 0f && dt > 0f) ? (speed * dt * 1.5f) : 0f);
		return num + num2;
	}

	private static float GetAutoguidanceRecoveryReengageReserve(float effectiveTurnRadius, float speed, float dt)
	{
		float autoguidanceReachabilityReserve = GetAutoguidanceReachabilityReserve(effectiveTurnRadius, speed, dt);
		float val = Math.Max(2f, Math.Max(0.1f, effectiveTurnRadius) * 0.35f);
		return Math.Max(autoguidanceReachabilityReserve, val);
	}

	private static float NormalizePositiveRadians(float angle)
	{
		float num = (float)Math.PI * 2f;
		angle %= num;
		if (!(angle < 0f))
		{
			return angle;
		}
		return angle + num;
	}

	private static bool TryGetAutoguidanceRecoveryPlaneNormal(Vec3 currentDirection, Vec3 targetDirection, out Vec3 planeNormal)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0110: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_011a: Unknown result type (might be due to invalid IL or missing references)
		//IL_015a: Unknown result type (might be due to invalid IL or missing references)
		//IL_015f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0169: Unknown result type (might be due to invalid IL or missing references)
		//IL_016f: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		currentDirection = NormalizeSafe(currentDirection, new Vec3(0f, 1f, 0f, -1f));
		targetDirection = NormalizeSafe(targetDirection, -currentDirection);
		if (Clamp(Dot(currentDirection, targetDirection), -1f, 1f) < -0.82f)
		{
			Vec3 val = WorldUp - currentDirection * Dot(WorldUp, currentDirection);
			if (IsFinite(val) && val.LengthSquared > 0.0001f)
			{
				planeNormal = NormalizeSafe(val, WorldUp);
				return true;
			}
		}
		planeNormal = Cross(currentDirection, targetDirection);
		if (!IsFinite(planeNormal) || planeNormal.LengthSquared <= 0.0001f)
		{
			planeNormal = Cross(currentDirection, new Vec3(1f, 0f, 0f, -1f));
			if (!IsFinite(planeNormal) || planeNormal.LengthSquared <= 0.0001f)
			{
				planeNormal = Cross(currentDirection, new Vec3(0f, 1f, 0f, -1f));
			}
			if (!IsFinite(planeNormal) || planeNormal.LengthSquared <= 0.0001f)
			{
				planeNormal = new Vec3(1f, 0f, 0f, -1f);
			}
		}
		planeNormal = NormalizeSafe(planeNormal, WorldUp);
		return IsFinite(planeNormal);
	}

	private static float EstimateAutoguidanceTurnThenStraightDistance(Vec3 projectilePosition, Vec3 currentDirection, Vec3 target, float effectiveTurnRadius, Vec3 turnAxis)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0145: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0154: Unknown result type (might be due to invalid IL or missing references)
		//IL_0159: Unknown result type (might be due to invalid IL or missing references)
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_015f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_016a: Unknown result type (might be due to invalid IL or missing references)
		//IL_016f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0174: Unknown result type (might be due to invalid IL or missing references)
		//IL_0176: Unknown result type (might be due to invalid IL or missing references)
		//IL_0178: Unknown result type (might be due to invalid IL or missing references)
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_017f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_0186: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		//IL_018d: Unknown result type (might be due to invalid IL or missing references)
		//IL_018f: Unknown result type (might be due to invalid IL or missing references)
		currentDirection = NormalizeSafe(currentDirection, new Vec3(0f, 1f, 0f, -1f));
		turnAxis = NormalizeSafe(turnAxis, WorldUp);
		if (!IsFinite(currentDirection) || !IsFinite(turnAxis) || Math.Abs(Dot(currentDirection, turnAxis)) > 0.01f)
		{
			return float.PositiveInfinity;
		}
		float num = Math.Max(0.1f, effectiveTurnRadius);
		Vec3 val = NormalizeSafe(Cross(currentDirection, turnAxis), new Vec3(1f, 0f, 0f, -1f));
		Vec3 val2 = projectilePosition - val * num;
		Vec3 val3 = target - val2;
		Vec3 val4 = val3 - turnAxis * Dot(val3, turnAxis);
		float length = val4.Length;
		if (!IsFinite(val4) || !IsFinite(length) || length <= num + 0.001f)
		{
			return float.PositiveInfinity;
		}
		float num2 = (float)Math.Atan2(Dot(val4, currentDirection), Dot(val4, val));
		float num3 = (float)Math.Acos(Clamp(num / length, -1f, 1f));
		float num4 = float.PositiveInfinity;
		for (int i = 0; i < 2; i++)
		{
			float num5 = num2 + ((i == 0) ? num3 : (0f - num3));
			Vec3 val5 = NormalizeSafe(val * (float)Math.Cos(num5) + currentDirection * (float)Math.Sin(num5), val);
			Vec3 val6 = val2 + val5 * num;
			Vec3 a = NormalizeSafe(Cross(turnAxis, val5), currentDirection);
			Vec3 b = target - val6;
			if (!(Dot(a, b) <= 0f))
			{
				float num6 = NormalizePositiveRadians(num5);
				float length2 = b.Length;
				float num7 = num6 * num + length2;
				if (IsFinite(num7) && num7 < num4)
				{
					num4 = num7;
				}
			}
		}
		return num4;
	}

	private static float EstimateAutoguidanceTravelDistance(Vec3 projectilePosition, Vec3 currentDirection, Vec3 target, float effectiveTurnRadius, float directReachabilityReserve, out Vec3 recoveryTurnAxis)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_0121: Unknown result type (might be due to invalid IL or missing references)
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_012d: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0153: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Unknown result type (might be due to invalid IL or missing references)
		//IL_016f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_0172: Unknown result type (might be due to invalid IL or missing references)
		//IL_0177: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a8: Unknown result type (might be due to invalid IL or missing references)
		recoveryTurnAxis = Vec3.Zero;
		Vec3 val = target - projectilePosition;
		float length = val.Length;
		if (!IsFinite(val) || !IsFinite(length))
		{
			return float.PositiveInfinity;
		}
		if (length <= 0.35f)
		{
			return length;
		}
		currentDirection = NormalizeSafe(currentDirection, val);
		Vec3 targetDirection = val / length;
		bool flag = IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, target, effectiveTurnRadius, Math.Max(0f, directReachabilityReserve));
		if (!TryGetAutoguidanceRecoveryPlaneNormal(currentDirection, targetDirection, out var planeNormal))
		{
			return length;
		}
		float num = EstimateAutoguidanceTurnThenStraightDistance(projectilePosition, currentDirection, target, effectiveTurnRadius, planeNormal);
		float num2 = EstimateAutoguidanceTurnThenStraightDistance(projectilePosition, currentDirection, target, effectiveTurnRadius, -planeNormal);
		float num3;
		Vec3 val2;
		if (num <= num2)
		{
			num3 = num;
			val2 = (IsFinite(num) ? planeNormal : Vec3.Zero);
		}
		else
		{
			num3 = num2;
			val2 = (IsFinite(num2) ? (-planeNormal) : Vec3.Zero);
		}
		if (flag)
		{
			if (!IsFinite(num3))
			{
				return length;
			}
			return Math.Max(length, num3);
		}
		recoveryTurnAxis = val2;
		if (!IsFinite(num3))
		{
			Vec3 val3 = NormalizeSafe(Cross(currentDirection, planeNormal), new Vec3(1f, 0f, 0f, -1f));
			Vec3 val4 = NormalizeSafe(Cross(currentDirection, -planeNormal), -val3);
			Vec3 val5 = projectilePosition - val3 * Math.Max(0.1f, effectiveTurnRadius);
			Vec3 val6 = projectilePosition - val4 * Math.Max(0.1f, effectiveTurnRadius);
			Vec3 val7 = target - val5;
			float lengthSquared = val7.LengthSquared;
			val7 = target - val6;
			float lengthSquared2 = val7.LengthSquared;
			recoveryTurnAxis = ((lengthSquared >= lengthSquared2) ? planeNormal : (-planeNormal));
			num3 = length + Math.Max(0.1f, effectiveTurnRadius) * (float)Math.PI;
		}
		return num3;
	}

	private Vec3 PreferTerrainSafeRecoveryAxis(Vec3 projectilePosition, Vec3 currentDirection, Vec3 recoveryTurnAxis)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		recoveryTurnAxis = NormalizeSafe(recoveryTurnAxis, WorldUp);
		if (!TryGetTerrainHeight(projectilePosition, out var height))
		{
			return recoveryTurnAxis;
		}
		float num = projectilePosition.z - height;
		if (!IsFinite(num) || num > 4f)
		{
			return recoveryTurnAxis;
		}
		Vec3 val = Cross(recoveryTurnAxis, currentDirection);
		if (IsFinite(val) && val.z < 0.05f)
		{
			recoveryTurnAxis = -recoveryTurnAxis;
		}
		return recoveryTurnAxis;
	}

	private static Vec3 ComputeAutoguidanceRecoveryDirection(Vec3 currentDirection, Vec3 recoveryTurnAxis)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		currentDirection = NormalizeSafe(currentDirection, new Vec3(0f, 1f, 0f, -1f));
		recoveryTurnAxis = NormalizeSafe(recoveryTurnAxis, WorldUp);
		return NormalizeSafe(RotateAroundAxis(currentDirection, recoveryTurnAxis, (float)Math.PI / 2f), currentDirection);
	}

	private static bool ShouldForceDirectTerminalIntercept(Vec3 projectilePosition, Vec3 currentDirection, Vec3 predictedHead, float effectiveTurnRadius)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = predictedHead - projectilePosition;
		float length = val.Length;
		if (!IsFinite(val) || !IsFinite(length) || length <= 0.35f)
		{
			return true;
		}
		Vec3 b = val / length;
		float headingAngle = (float)Math.Acos(Clamp(Dot(currentDirection, b), -1f, 1f));
		float num = EstimateMinimumReachableDistance(effectiveTurnRadius, headingAngle);
		float num2 = Math.Max(6f, effectiveTurnRadius * 0.45f);
		return length <= num + num2;
	}

	private static float GetLoftedArcDescentAngle(Vec3 targetHead, Vec3 targetBase)
	{
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		float val = DegreesToRadians(Clamp((GlobalSettings<Settings>.Instance?.AutoguidancePreferredRiseAngle ?? 8f) * 0.55f, 2f, 8f));
		float val2 = (float)Math.Atan2(Math.Max(0.15f, targetHead.z - (targetBase.z + 0.15f)), 8.0) * 0.8f;
		return Clamp(Math.Min(val, val2), DegreesToRadians(0.75f), DegreesToRadians(8f));
	}

	private static float GetAutoguidanceProfileSide(TrackedMissile tracked)
	{
		if (((((tracked?.Index ?? 0) * 397) ^ ((tracked?.ShotGeneration ?? 0) * 17) ^ (tracked?.FormationSlot ?? 0)) & 1) != 0)
		{
			return 1f;
		}
		return -1f;
	}

	private static Vec3 GetNaturalBallisticReferenceDirection(TrackedMissile tracked, Vec3 fallback)
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		if (tracked == null || !tracked.GuidanceLaunchStateValid)
		{
			return fallback;
		}
		float num = (IsFinite(tracked.EstimatedGravityZ) ? Clamp(tracked.EstimatedGravityZ, -40f, 5f) : (-9.81f));
		return NormalizeSafe(tracked.GuidanceLaunchVelocity + new Vec3(0f, 0f, num, -1f) * tracked.GuidanceFlightElapsed, fallback);
	}

	private static float EstimateMinimumReachableDistance(float effectiveTurnRadius, float headingAngle)
	{
		effectiveTurnRadius = Math.Max(0.1f, effectiveTurnRadius);
		headingAngle = Clamp(headingAngle, 0f, (float)Math.PI);
		float num = (float)Math.PI / 2f;
		if (headingAngle <= num)
		{
			return 2f * effectiveTurnRadius * (float)Math.Sin(headingAngle);
		}
		return 2f * effectiveTurnRadius + effectiveTurnRadius * (headingAngle - num);
	}

	private static float DegreesToRadians(float degrees)
	{
		return degrees * ((float)Math.PI / 180f);
	}

	private static Vec3 GetAgentVisualPositionSafe(Agent agent, Vec3 fallback)
	{
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		if (agent == null)
		{
			return fallback;
		}
		try
		{
			Vec3 visualPosition = GetVisualPosition(agent);
			return IsFinite(visualPosition) ? visualPosition : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	private static bool WasAutoguidanceTargetConsumed(TrackedMissile tracked, Agent target)
	{
		if (tracked == null || target == null)
		{
			return false;
		}
		for (int i = 0; i < tracked.GuidanceConsumedTargets.Count; i++)
		{
			if (tracked.GuidanceConsumedTargets[i] == target)
			{
				return true;
			}
		}
		return false;
	}

	private static void MarkAutoguidanceTargetConsumed(TrackedMissile tracked, Agent target)
	{
		if (tracked != null && target != null && !WasAutoguidanceTargetConsumed(tracked, target))
		{
			tracked.GuidanceConsumedTargets.Add(target);
		}
	}

	private void AssignAutoguidanceTarget(TrackedMissile tracked, Agent target)
	{
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_0172: Unknown result type (might be due to invalid IL or missing references)
		if (tracked != null && IsAutoguidanceTargetValid(target))
		{
			if (tracked.GuidanceRouteTargets.Count == 0 || tracked.GuidanceRouteTargets[0] != target)
			{
				tracked.GuidanceRouteTargets.Clear();
				tracked.GuidanceRouteTargets.Add(target);
			}
			tracked.GuidanceTarget = target;
			tracked.GuidanceHeadBoneIndex = ResolveGuidanceHeadBoneIndex(target);
			tracked.GuidanceSmoothedHeadValid = false;
			tracked.GuidanceLastRawHeadValid = false;
			tracked.GuidanceTargetVelocity = GetAutoguidanceTargetVelocity(target, out var valid);
			tracked.GuidanceTargetVelocityValid = valid;
			tracked.GuidanceTerrainSampleCountdown = 0f;
			tracked.GuidanceCruiseGroundZ = 0f;
			tracked.GuidanceCruiseGroundValid = false;
			tracked.GuidanceSafetyTerrainSampleCountdown = 0f;
			tracked.GuidanceSafetyCurrentGroundZ = 0f;
			tracked.GuidanceSafetyMaximumGroundZ = 0f;
			tracked.GuidanceSafetyGroundValid = false;
			tracked.GuidanceSafetySampleDirection = Vec3.Zero;
			tracked.GuidanceSafetySampleDirectionValid = false;
			tracked.GuidanceRecoveryActive = false;
			tracked.GuidanceRecoveryTurnAxis = Vec3.Zero;
			tracked.GuidanceRecoveryTurnAxisValid = false;
			tracked.GuidanceRecoveryTurnedRadians = 0f;
			tracked.GuidanceRecoveryReplanCount = 0;
			tracked.GuidanceForceDirectIntercept = false;
			tracked.GuidanceObstacleWaypoint = Vec3.Zero;
			tracked.GuidanceObstacleWaypointValid = false;
			tracked.GuidanceObstacleGoal = Vec3.Zero;
			tracked.GuidanceObstacleGoalValid = false;
			tracked.GuidanceObstacleRecheckCountdown = 0f;
			tracked.GuidanceRouteReplanRequested = false;
			tracked.GuidanceNoProgressElapsed = 0f;
			tracked.GuidanceLastTargetDistance = 0f;
			tracked.GuidanceLastTargetDistanceValid = false;
			if (TryGetGuidanceHeadPosition(target, tracked.GuidanceHeadBoneIndex, out var position))
			{
				tracked.GuidanceSmoothedHead = position;
				tracked.GuidanceSmoothedHeadValid = true;
				tracked.GuidanceLastRawHead = position;
				tracked.GuidanceLastRawHeadValid = true;
			}
		}
	}

	private bool TryAdvanceAutoguidanceRoute(TrackedMissile tracked, Agent completedOrInvalidTarget, bool impactConfirmed)
	{
		if (tracked == null)
		{
			return false;
		}
		if (impactConfirmed && completedOrInvalidTarget != null)
		{
			MarkAutoguidanceTargetConsumed(tracked, completedOrInvalidTarget);
		}
		for (int num = tracked.GuidanceRouteTargets.Count - 1; num >= 0; num--)
		{
			Agent val = tracked.GuidanceRouteTargets[num];
			if (val == null || val == completedOrInvalidTarget || !IsAutoguidanceTargetValid(val) || WasAutoguidanceTargetConsumed(tracked, val))
			{
				tracked.GuidanceRouteTargets.RemoveAt(num);
			}
		}
		if (tracked.GuidanceRouteTargets.Count == 0)
		{
			return false;
		}
		AssignAutoguidanceTarget(tracked, tracked.GuidanceRouteTargets[0]);
		return true;
	}

	private Agent GetNextPlannedRouteTarget(TrackedMissile tracked)
	{
		if (tracked == null)
		{
			return null;
		}
		for (int num = tracked.GuidanceRouteTargets.Count - 1; num >= 1; num--)
		{
			Agent target = tracked.GuidanceRouteTargets[num];
			if (!IsAutoguidanceTargetValid(target) || WasAutoguidanceTargetConsumed(tracked, target))
			{
				tracked.GuidanceRouteTargets.RemoveAt(num);
			}
		}
		if (tracked.GuidanceRouteTargets.Count <= 1)
		{
			return null;
		}
		return tracked.GuidanceRouteTargets[1];
	}

	private void AddAssignedTargetIfMissing(Agent target)
	{
		if (target != null && !IsAssignedAutoguidanceTarget(target))
		{
			_autoguidanceAssignedTargets.Add(target);
		}
	}

	private bool IsAssignedAutoguidanceTarget(Agent target)
	{
		for (int i = 0; i < _autoguidanceAssignedTargets.Count; i++)
		{
			if (_autoguidanceAssignedTargets[i] == target)
			{
				return true;
			}
		}
		return false;
	}

	private bool ShouldBreakFormationForAutoguidance(TrackedMissile tracked, Vec3 missilePosition)
	{
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		if (tracked == null || tracked.Index == _cameraMissileIndex)
		{
			return true;
		}
		int num = GlobalSettings<Settings>.Instance?.AutoguidanceSplitBehaviour ?? 1;
		if (num >= 2)
		{
			return true;
		}
		if (num <= 0)
		{
			return (GlobalSettings<Settings>.Instance?.AutoguidanceScope ?? 2) == 1;
		}
		if (tracked.GuidanceBrokenFromFormation)
		{
			return true;
		}
		if (!IsAutoguidanceTargetValid(tracked.GuidanceTarget))
		{
			return false;
		}
		Vec3 position = tracked.GuidanceSmoothedHead;
		if (!tracked.GuidanceSmoothedHeadValid && !TryGetGuidanceHeadPosition(tracked.GuidanceTarget, tracked.GuidanceHeadBoneIndex, out position))
		{
			return false;
		}
		Vec3 val = position - missilePosition;
		float lengthSquared = val.LengthSquared;
		float num2 = Clamp(GlobalSettings<Settings>.Instance?.AutoguidanceFormationBreakDistance ?? 18f, 2f, 80f);
		if (IsFinite(lengthSquared) && lengthSquared <= num2 * num2)
		{
			tracked.GuidanceBrokenFromFormation = true;
			tracked.LastFormationTargetValid = false;
			return true;
		}
		return false;
	}

	private bool TryGetAutoguidanceSteeringDirection(TrackedMissile tracked, Vec3 projectilePosition, Vec3 projectileVelocity, float dt, out Vec3 desiredDirection)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_013d: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0172: Unknown result type (might be due to invalid IL or missing references)
		//IL_0174: Unknown result type (might be due to invalid IL or missing references)
		//IL_0179: Unknown result type (might be due to invalid IL or missing references)
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_017f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_01dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_020f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_0215: Unknown result type (might be due to invalid IL or missing references)
		//IL_021a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0242: Unknown result type (might be due to invalid IL or missing references)
		//IL_0247: Unknown result type (might be due to invalid IL or missing references)
		//IL_024f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0254: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_025d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0262: Unknown result type (might be due to invalid IL or missing references)
		//IL_0264: Unknown result type (might be due to invalid IL or missing references)
		//IL_0265: Unknown result type (might be due to invalid IL or missing references)
		//IL_0267: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_02cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0282: Unknown result type (might be due to invalid IL or missing references)
		//IL_0284: Unknown result type (might be due to invalid IL or missing references)
		//IL_0285: Unknown result type (might be due to invalid IL or missing references)
		//IL_028a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0383: Unknown result type (might be due to invalid IL or missing references)
		//IL_0384: Unknown result type (might be due to invalid IL or missing references)
		//IL_0386: Unknown result type (might be due to invalid IL or missing references)
		//IL_07b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_07b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_07c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_07c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_07ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_07cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_07da: Unknown result type (might be due to invalid IL or missing references)
		//IL_07ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_0784: Unknown result type (might be due to invalid IL or missing references)
		//IL_0789: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_03c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_03d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_03e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_03eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_03ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_03fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0400: Unknown result type (might be due to invalid IL or missing references)
		//IL_0402: Unknown result type (might be due to invalid IL or missing references)
		//IL_0407: Unknown result type (might be due to invalid IL or missing references)
		//IL_040e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0554: Unknown result type (might be due to invalid IL or missing references)
		//IL_0555: Unknown result type (might be due to invalid IL or missing references)
		//IL_0557: Unknown result type (might be due to invalid IL or missing references)
		//IL_0566: Unknown result type (might be due to invalid IL or missing references)
		//IL_0567: Unknown result type (might be due to invalid IL or missing references)
		//IL_0569: Unknown result type (might be due to invalid IL or missing references)
		//IL_056b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0570: Unknown result type (might be due to invalid IL or missing references)
		//IL_0572: Unknown result type (might be due to invalid IL or missing references)
		//IL_0826: Unknown result type (might be due to invalid IL or missing references)
		//IL_0827: Unknown result type (might be due to invalid IL or missing references)
		//IL_0829: Unknown result type (might be due to invalid IL or missing references)
		//IL_043f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0441: Unknown result type (might be due to invalid IL or missing references)
		//IL_06fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_06fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0702: Unknown result type (might be due to invalid IL or missing references)
		//IL_0707: Unknown result type (might be due to invalid IL or missing references)
		//IL_070b: Unknown result type (might be due to invalid IL or missing references)
		//IL_070c: Unknown result type (might be due to invalid IL or missing references)
		//IL_070e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0710: Unknown result type (might be due to invalid IL or missing references)
		//IL_0717: Unknown result type (might be due to invalid IL or missing references)
		//IL_071c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0720: Unknown result type (might be due to invalid IL or missing references)
		//IL_0727: Unknown result type (might be due to invalid IL or missing references)
		//IL_072c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0733: Unknown result type (might be due to invalid IL or missing references)
		//IL_0479: Unknown result type (might be due to invalid IL or missing references)
		//IL_0457: Unknown result type (might be due to invalid IL or missing references)
		//IL_046d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0472: Unknown result type (might be due to invalid IL or missing references)
		//IL_074b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0750: Unknown result type (might be due to invalid IL or missing references)
		//IL_0752: Unknown result type (might be due to invalid IL or missing references)
		//IL_0757: Unknown result type (might be due to invalid IL or missing references)
		//IL_075e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0741: Unknown result type (might be due to invalid IL or missing references)
		//IL_0743: Unknown result type (might be due to invalid IL or missing references)
		//IL_06dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_06e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_06e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_06d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0884: Unknown result type (might be due to invalid IL or missing references)
		//IL_0871: Unknown result type (might be due to invalid IL or missing references)
		//IL_0872: Unknown result type (might be due to invalid IL or missing references)
		//IL_0876: Unknown result type (might be due to invalid IL or missing references)
		//IL_0878: Unknown result type (might be due to invalid IL or missing references)
		//IL_087a: Unknown result type (might be due to invalid IL or missing references)
		//IL_087d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0842: Unknown result type (might be due to invalid IL or missing references)
		//IL_0843: Unknown result type (might be due to invalid IL or missing references)
		//IL_0845: Unknown result type (might be due to invalid IL or missing references)
		//IL_084d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0852: Unknown result type (might be due to invalid IL or missing references)
		//IL_0856: Unknown result type (might be due to invalid IL or missing references)
		//IL_0857: Unknown result type (might be due to invalid IL or missing references)
		//IL_0859: Unknown result type (might be due to invalid IL or missing references)
		//IL_085b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0862: Unknown result type (might be due to invalid IL or missing references)
		//IL_0867: Unknown result type (might be due to invalid IL or missing references)
		//IL_047e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0480: Unknown result type (might be due to invalid IL or missing references)
		//IL_06e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_06ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_0591: Unknown result type (might be due to invalid IL or missing references)
		//IL_0593: Unknown result type (might be due to invalid IL or missing references)
		//IL_0598: Unknown result type (might be due to invalid IL or missing references)
		//IL_059e: Unknown result type (might be due to invalid IL or missing references)
		//IL_05a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_05a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0886: Unknown result type (might be due to invalid IL or missing references)
		//IL_0888: Unknown result type (might be due to invalid IL or missing references)
		//IL_0889: Unknown result type (might be due to invalid IL or missing references)
		//IL_088b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0497: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_04b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0604: Unknown result type (might be due to invalid IL or missing references)
		//IL_0609: Unknown result type (might be due to invalid IL or missing references)
		//IL_0627: Unknown result type (might be due to invalid IL or missing references)
		//IL_0629: Unknown result type (might be due to invalid IL or missing references)
		//IL_062b: Unknown result type (might be due to invalid IL or missing references)
		//IL_062c: Unknown result type (might be due to invalid IL or missing references)
		//IL_05ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_05cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_08a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_08a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_08a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_08ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_08af: Unknown result type (might be due to invalid IL or missing references)
		//IL_08b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_08b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_08ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_08c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_08c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_08c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_08cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_08d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_08d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_08e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_08ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_08f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_08f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_089f: Unknown result type (might be due to invalid IL or missing references)
		//IL_08a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_04ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_04bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_04c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_04c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_04cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_067d: Unknown result type (might be due to invalid IL or missing references)
		//IL_067e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0680: Unknown result type (might be due to invalid IL or missing references)
		//IL_0682: Unknown result type (might be due to invalid IL or missing references)
		//IL_0687: Unknown result type (might be due to invalid IL or missing references)
		//IL_068c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0691: Unknown result type (might be due to invalid IL or missing references)
		//IL_0698: Unknown result type (might be due to invalid IL or missing references)
		//IL_063a: Unknown result type (might be due to invalid IL or missing references)
		//IL_063c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0910: Unknown result type (might be due to invalid IL or missing references)
		//IL_0915: Unknown result type (might be due to invalid IL or missing references)
		//IL_0917: Unknown result type (might be due to invalid IL or missing references)
		//IL_091c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0923: Unknown result type (might be due to invalid IL or missing references)
		//IL_0906: Unknown result type (might be due to invalid IL or missing references)
		//IL_0908: Unknown result type (might be due to invalid IL or missing references)
		//IL_0674: Unknown result type (might be due to invalid IL or missing references)
		//IL_0652: Unknown result type (might be due to invalid IL or missing references)
		//IL_0668: Unknown result type (might be due to invalid IL or missing references)
		//IL_066d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0679: Unknown result type (might be due to invalid IL or missing references)
		desiredDirection = NormalizeSafe(projectileVelocity, new Vec3(0f, 1f, 0f, -1f));
		if (tracked == null)
		{
			return false;
		}
		if (!IsAutoguidanceTargetValid(tracked.GuidanceTarget))
		{
			if (tracked.GuidanceFallbackDirectionValid)
			{
				desiredDirection = NormalizeSafe(tracked.GuidanceFallbackDirection, desiredDirection);
				return IsFinite(desiredDirection);
			}
			return false;
		}
		if (!TryGetGuidanceHeadPosition(tracked.GuidanceTarget, tracked.GuidanceHeadBoneIndex, out var position))
		{
			tracked.GuidanceRouteReplanRequested = true;
			if (tracked.GuidanceFallbackDirectionValid)
			{
				desiredDirection = NormalizeSafe(tracked.GuidanceFallbackDirection, desiredDirection);
				return IsFinite(desiredDirection);
			}
			return false;
		}
		float num = Clamp(dt, 0.0001f, 0.1f);
		tracked.GuidanceSmoothedHead = position;
		tracked.GuidanceSmoothedHeadValid = true;
		bool valid;
		Vec3 autoguidanceTargetVelocity = GetAutoguidanceTargetVelocity(tracked.GuidanceTarget, out valid);
		if (valid)
		{
			if (!tracked.GuidanceTargetVelocityValid)
			{
				tracked.GuidanceTargetVelocity = autoguidanceTargetVelocity;
				tracked.GuidanceTargetVelocityValid = true;
			}
			else
			{
				float t = 1f - (float)Math.Exp(-12f * num);
				tracked.GuidanceTargetVelocity = Lerp(tracked.GuidanceTargetVelocity, autoguidanceTargetVelocity, t);
			}
		}
		else
		{
			tracked.GuidanceTargetVelocity = Vec3.Zero;
			tracked.GuidanceTargetVelocityValid = false;
		}
		tracked.GuidanceLastRawHead = position;
		tracked.GuidanceLastRawHeadValid = true;
		float lengthSquared = projectileVelocity.LengthSquared;
		if (!IsFinite(lengthSquared) || lengthSquared <= 1E-06f)
		{
			return false;
		}
		float num2 = (float)Math.Sqrt(lengthSquared);
		Vec3 val = (desiredDirection = NormalizeSafe(projectileVelocity, tracked.GuidanceSmoothedHead - projectilePosition));
		float num3 = Clamp(GlobalSettings<Settings>.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f);
		float adaptiveSteeringMultiplier = GetAdaptiveSteeringMultiplier(num2);
		float num4 = num3 / Math.Max(1f, adaptiveSteeringMultiplier);
		Vec3 val2 = (tracked.GuidanceTargetVelocityValid ? tracked.GuidanceTargetVelocity : Vec3.Zero);
		float gravityZ = (IsFinite(tracked.EstimatedGravityZ) ? Clamp(tracked.EstimatedGravityZ, -40f, 5f) : (-9.81f));
		Vec3 val3 = tracked.GuidanceSmoothedHead - projectilePosition;
		float num5 = Clamp(val3.Length / Math.Max(1f, num2), 0.01f, 6f);
		Vec3 val4 = tracked.GuidanceSmoothedHead;
		for (int i = 0; i < 4; i++)
		{
			val4 = tracked.GuidanceSmoothedHead + val2 * num5;
			Vec3 recoveryTurnAxis;
			float num6 = EstimateAutoguidanceTravelDistance(projectilePosition, val, val4, num4, 0f, out recoveryTurnAxis);
			if (!IsFinite(num6))
			{
				val3 = val4 - projectilePosition;
				num6 = val3.Length;
			}
			num5 = Clamp(num6 / Math.Max(1f, num2), 0.01f, 6f);
		}
		Vec3 val5 = val4;
		val3 = val5 - projectilePosition;
		float length = val3.Length;
		if (IsFinite(length))
		{
			if (!tracked.GuidanceLastTargetDistanceValid || length + 0.18f < tracked.GuidanceLastTargetDistance)
			{
				tracked.GuidanceNoProgressElapsed = 0f;
			}
			else if (tracked.GuidanceRecoveryActive)
			{
				tracked.GuidanceNoProgressElapsed += num;
				if (tracked.GuidanceNoProgressElapsed >= 1.1f)
				{
					tracked.GuidanceRouteReplanRequested = true;
					tracked.GuidanceNoProgressElapsed = 0f;
					tracked.GuidanceRecoveryTurnAxisValid = false;
					tracked.GuidanceRecoveryTurnedRadians = 0f;
				}
			}
			tracked.GuidanceLastTargetDistance = length;
			tracked.GuidanceLastTargetDistanceValid = true;
		}
		float autoguidanceReachabilityReserve = GetAutoguidanceReachabilityReserve(num4, num2, num);
		float autoguidanceRecoveryReengageReserve = GetAutoguidanceRecoveryReengageReserve(num4, num2, num);
		float num7 = (tracked.GuidanceRecoveryActive ? autoguidanceRecoveryReengageReserve : autoguidanceReachabilityReserve);
		if (!IsAutoguidanceWaypointReachable(projectilePosition, val, val5, num4, num7))
		{
			bool num8 = !tracked.GuidanceRecoveryActive;
			if (num8)
			{
				tracked.GuidanceRecoveryTurnedRadians = 0f;
				tracked.GuidanceRecoveryReplanCount = 0;
			}
			if (num8 || !tracked.GuidanceRecoveryTurnAxisValid)
			{
				EstimateAutoguidanceTravelDistance(projectilePosition, val, val5, num4, num7, out var recoveryTurnAxis2);
				if (!IsFinite(recoveryTurnAxis2) || recoveryTurnAxis2.LengthSquared <= 0.0001f)
				{
					TryGetAutoguidanceRecoveryPlaneNormal(val, val5 - projectilePosition, out recoveryTurnAxis2);
				}
				tracked.GuidanceRecoveryTurnAxis = PreferTerrainSafeRecoveryAxis(projectilePosition, val, recoveryTurnAxis2);
				tracked.GuidanceRecoveryTurnAxisValid = IsFinite(tracked.GuidanceRecoveryTurnAxis) && tracked.GuidanceRecoveryTurnAxis.LengthSquared > 0.0001f;
				if (!tracked.GuidanceRecoveryTurnAxisValid)
				{
					Vec3 val6 = ((Math.Abs(Dot(val, WorldUp)) < 0.9f) ? WorldUp : Cross(val, new Vec3(1f, 0f, 0f, -1f)));
					if (!IsFinite(val6) || val6.LengthSquared <= 0.0001f)
					{
						val6 = Cross(val, new Vec3(0f, 1f, 0f, -1f));
					}
					tracked.GuidanceRecoveryTurnAxis = NormalizeSafe(val6, WorldUp);
					tracked.GuidanceRecoveryTurnAxisValid = IsFinite(tracked.GuidanceRecoveryTurnAxis) && tracked.GuidanceRecoveryTurnAxis.LengthSquared > 0.0001f;
				}
			}
			tracked.GuidanceRecoveryActive = true;
			tracked.GuidanceForceDirectIntercept = true;
			tracked.GuidanceRecoveryTurnedRadians += Math.Max(0f, num2 / Math.Max(0.1f, num4) * num);
			float num9 = 5.497787f;
			float num10 = ((tracked.GuidanceRecoveryReplanCount > 0) ? 3.9269907f : 6.911504f);
			if (tracked.GuidanceRecoveryTurnedRadians >= num9)
			{
				EstimateAutoguidanceTravelDistance(projectilePosition, val, val5, num4, num7, out var recoveryTurnAxis3);
				recoveryTurnAxis3 = PreferTerrainSafeRecoveryAxis(projectilePosition, val, recoveryTurnAxis3);
				if (IsFinite(recoveryTurnAxis3) && recoveryTurnAxis3.LengthSquared > 0.0001f && (!tracked.GuidanceRecoveryTurnAxisValid || Dot(NormalizeSafe(recoveryTurnAxis3, WorldUp), NormalizeSafe(tracked.GuidanceRecoveryTurnAxis, WorldUp)) < 0.75f) && tracked.GuidanceRecoveryReplanCount == 0)
				{
					tracked.GuidanceRecoveryTurnAxis = recoveryTurnAxis3;
					tracked.GuidanceRecoveryTurnAxisValid = true;
					tracked.GuidanceRecoveryTurnedRadians = 0f;
					tracked.GuidanceRecoveryReplanCount = 1;
				}
				else if (tracked.GuidanceRecoveryTurnedRadians >= num10)
				{
					tracked.GuidanceRouteReplanRequested = true;
					tracked.GuidanceRecoveryTurnAxis = Vec3.Zero;
					tracked.GuidanceRecoveryTurnAxisValid = false;
					tracked.GuidanceRecoveryTurnedRadians = 0f;
					tracked.GuidanceRecoveryReplanCount = 0;
					if (!TryGetAutoguidanceRecoveryPlaneNormal(val, val5 - projectilePosition, out var planeNormal))
					{
						planeNormal = ((Math.Abs(Dot(val, WorldUp)) < 0.9f) ? WorldUp : Cross(val, new Vec3(1f, 0f, 0f, -1f)));
					}
					tracked.GuidanceRecoveryTurnAxis = NormalizeSafe(PreferTerrainSafeRecoveryAxis(projectilePosition, val, planeNormal), WorldUp);
					tracked.GuidanceRecoveryTurnAxisValid = IsFinite(tracked.GuidanceRecoveryTurnAxis) && tracked.GuidanceRecoveryTurnAxis.LengthSquared > 0.0001f;
				}
			}
			if (!tracked.GuidanceRecoveryTurnAxisValid)
			{
				tracked.GuidanceRouteReplanRequested = true;
				desiredDirection = (tracked.GuidanceFallbackDirectionValid ? NormalizeSafe(tracked.GuidanceFallbackDirection, val) : val);
				return IsFinite(desiredDirection);
			}
			Vec3 desiredDirection2 = ComputeAutoguidanceRecoveryDirection(val, tracked.GuidanceRecoveryTurnAxis);
			desiredDirection2 = ApplyAutoguidanceTerrainSafety(tracked, projectilePosition, val, desiredDirection2, val5, num2, gravityZ, num);
			desiredDirection = ApplyContinuousGuidanceGravityCompensation(desiredDirection2, num2, gravityZ, num);
			if (!IsFinite(desiredDirection))
			{
				desiredDirection = val;
			}
			tracked.GuidanceFallbackDirection = NormalizeSafe(desiredDirection, val);
			tracked.GuidanceFallbackDirectionValid = IsFinite(tracked.GuidanceFallbackDirection);
			return tracked.GuidanceFallbackDirectionValid;
		}
		if (tracked.GuidanceRecoveryActive)
		{
			tracked.GuidanceRecoveryActive = false;
			tracked.GuidanceRecoveryTurnAxis = Vec3.Zero;
			tracked.GuidanceRecoveryTurnAxisValid = false;
			tracked.GuidanceRecoveryTurnedRadians = 0f;
			tracked.GuidanceRecoveryReplanCount = 0;
			tracked.GuidanceForceDirectIntercept = true;
		}
		Vec3 agentVisualPositionSafe = GetAgentVisualPositionSafe(tracked.GuidanceTarget, val4 - WorldUp * 0.95f);
		agentVisualPositionSafe.x += val2.x * num5;
		agentVisualPositionSafe.y += val2.y * num5;
		Settings instance = GlobalSettings<Settings>.Instance;
		int num11;
		if (instance == null)
		{
			num11 = 1;
		}
		else
		{
			num11 = (instance.AutoguidanceMultiTargetTrajectoryPlanning ? 1 : 0);
			if (num11 == 0)
			{
				goto IL_081e;
			}
		}
		if (GetNextPlannedRouteTarget(tracked) == null)
		{
			goto IL_081e;
		}
		int num12 = 0;
		goto IL_0838;
		IL_081e:
		num12 = ((tracked.GuidanceForceDirectIntercept || ShouldForceDirectTerminalIntercept(projectilePosition, val, val5, num4)) ? 1 : 0);
		goto IL_0838;
		IL_0838:
		bool flag = (byte)num12 != 0;
		Vec3 val7;
		if (num11 != 0 && !flag)
		{
			Vec3 plannedExitDirection = ComputePlannedPostImpactDirection(tracked, projectilePosition, val, val5, num5, num2, gravityZ);
			val7 = ComputeRouteAwareTrajectoryAimPoint(tracked, projectilePosition, val, val5, plannedExitDirection, num2, num4, num);
		}
		else
		{
			val7 = (flag ? val5 : ComputeAutoguidanceAimPoint(tracked, projectilePosition, val, num2, val5, val4, agentVisualPositionSafe, num));
		}
		if (!IsAutoguidanceWaypointReachable(projectilePosition, val, val7, num4, autoguidanceReachabilityReserve))
		{
			tracked.GuidanceForceDirectIntercept = true;
			val7 = val5;
		}
		desiredDirection = NormalizeSafe(val7 - projectilePosition, val5 - projectilePosition);
		desiredDirection = ApplyAutoguidanceTerrainSafety(tracked, projectilePosition, val, desiredDirection, val5, num2, gravityZ, num);
		desiredDirection = ApplyContinuousGuidanceGravityCompensation(desiredDirection, num2, gravityZ, num);
		if (!IsFinite(desiredDirection))
		{
			desiredDirection = val;
		}
		tracked.GuidanceFallbackDirection = NormalizeSafe(desiredDirection, val);
		tracked.GuidanceFallbackDirectionValid = IsFinite(tracked.GuidanceFallbackDirection);
		return tracked.GuidanceFallbackDirectionValid;
	}

	private Vec3 ComputePlannedPostImpactDirection(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, Vec3 predictedHead, float currentInterceptTime, float speed, float gravityZ)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		Agent nextPlannedRouteTarget = GetNextPlannedRouteTarget(tracked);
		Vec3 val = currentDirection;
		val.z = 0f;
		if (nextPlannedRouteTarget != null)
		{
			int headBoneIndex = ResolveGuidanceHeadBoneIndex(nextPlannedRouteTarget);
			if (TryGetGuidanceHeadPosition(nextPlannedRouteTarget, headBoneIndex, out var position))
			{
				bool valid;
				Vec3 autoguidanceTargetVelocity = GetAutoguidanceTargetVelocity(nextPlannedRouteTarget, out valid);
				Vec3 val2 = position - predictedHead;
				float num = val2.Length / Math.Max(1f, speed);
				float num2 = Clamp(currentInterceptTime + num, 0f, 6f);
				if (valid)
				{
					position += autoguidanceTargetVelocity * num2;
				}
				val = position - predictedHead;
			}
		}
		if (!IsFinite(val) || val.LengthSquared <= 1E-06f)
		{
			val = currentDirection;
			val.z = 0f;
		}
		Vec3 requestedDirection = NormalizeSafe(val, currentDirection);
		return MakePostImpactDirectionTerrainSafe(predictedHead, requestedDirection, currentDirection, speed, gravityZ);
	}

	private Vec3 MakePostImpactDirectionTerrainSafe(Vec3 impactHead, Vec3 requestedDirection, Vec3 fallbackDirection, float speed, float gravityZ)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_010b: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0121: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0172: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Unknown result type (might be due to invalid IL or missing references)
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_017f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		requestedDirection = NormalizeSafe(requestedDirection, fallbackDirection);
		Vec3 val = requestedDirection;
		val.z = 0f;
		if (!IsFinite(val) || val.LengthSquared <= 0.0001f)
		{
			val = fallbackDirection;
			val.z = 0f;
		}
		val = NormalizeSafe(val, new Vec3(0f, 1f, 0f, -1f));
		float num = 0f;
		float num2 = 11f;
		for (int i = 1; i <= 4; i++)
		{
			float num3 = num2 * (float)i / 4f;
			Vec3 position = impactHead + val * num3;
			if (TryGetTerrainHeight(position, out var height))
			{
				float num4 = num3 / Math.Max(1f, speed);
				float num5 = Math.Max(0f, 0f - Clamp(gravityZ, -40f, 5f)) * 0.5f * num4 * num4;
				float num6 = height + 1.15f + num5;
				num = Math.Max(num, (num6 - impactHead.z) / Math.Max(0.5f, num3));
			}
		}
		float val2 = requestedDirection.z / Math.Max(0.05f, (float)Math.Sqrt(Math.Max(1E-06f, requestedDirection.x * requestedDirection.x + requestedDirection.y * requestedDirection.y)));
		float num7 = Math.Max(0f, Math.Max(num, val2));
		if (num <= 0.35f)
		{
			num7 = Math.Min(num7, 0.35f);
		}
		return NormalizeSafe(val + WorldUp * num7, val);
	}

	private Vec3 ComputeRouteAwareTrajectoryAimPoint(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, Vec3 predictedHead, Vec3 plannedExitDirection, float speed, float effectiveTurnRadius, float dt)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Unknown result type (might be due to invalid IL or missing references)
		//IL_014b: Unknown result type (might be due to invalid IL or missing references)
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Unknown result type (might be due to invalid IL or missing references)
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_0158: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		if (TryGetAutoguidanceObstacleWaypoint(tracked, projectilePosition, currentDirection, predictedHead, speed, effectiveTurnRadius, dt, out var waypoint))
		{
			return waypoint;
		}
		Vec3 val = predictedHead - projectilePosition;
		float length = val.Length;
		if (!IsFinite(val) || !IsFinite(length) || length <= 0.35f)
		{
			return predictedHead;
		}
		if (length <= 1.5f)
		{
			return predictedHead;
		}
		currentDirection = NormalizeSafe(currentDirection, val);
		plannedExitDirection = NormalizeSafe(plannedExitDirection, currentDirection);
		float num = Math.Min(length * 0.35f, Math.Min(Math.Max(3f, speed * 0.11f), effectiveTurnRadius * 0.65f));
		float num2 = Math.Min(length * 0.46f, Math.Min(Math.Max(4f, effectiveTurnRadius * 0.42f), 14f));
		Vec3 p = projectilePosition + currentDirection * num;
		Vec3 val2 = predictedHead - plannedExitDirection * num2;
		if (TryGetTerrainHeight(val2, out var height))
		{
			val2.z = Math.Max(val2.z, height + 1.15f);
		}
		Vec3 val3 = CubicBezierPoint(t: Clamp(Clamp(speed * 0.11f + 1.25f, 2f, 9f) / Math.Max(0.5f, length), 0.1f, 0.58f), p0: projectilePosition, p1: p, p2: val2, p3: predictedHead);
		if (!IsFinite(val3) || !IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, val3, effectiveTurnRadius))
		{
			return predictedHead;
		}
		return val3;
	}

	private static Vec3 CubicBezierPoint(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, float t)
	{
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		t = Clamp(t, 0f, 1f);
		float num = 1f - t;
		float num2 = num * num * num;
		float num3 = 3f * num * num * t;
		float num4 = 3f * num * t * t;
		float num5 = t * t * t;
		return p0 * num2 + p1 * num3 + p2 * num4 + p3 * num5;
	}

	private bool TryGetAutoguidanceObstacleWaypoint(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, Vec3 goal, float speed, float effectiveTurnRadius, float dt, out Vec3 waypoint)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_0125: Unknown result type (might be due to invalid IL or missing references)
		//IL_012a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0131: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		waypoint = Vec3.Zero;
		if (tracked != null)
		{
			Settings instance = GlobalSettings<Settings>.Instance;
			if (instance == null || instance.AutoguidanceObstacleAvoidance)
			{
				Vec3 val;
				if (tracked.GuidanceObstacleWaypointValid)
				{
					val = tracked.GuidanceObstacleWaypoint - projectilePosition;
					if (val.LengthSquared <= 2.25f)
					{
						tracked.GuidanceObstacleWaypointValid = false;
					}
				}
				tracked.GuidanceObstacleRecheckCountdown -= Math.Max(0f, dt);
				int num;
				if (tracked.GuidanceObstacleGoalValid)
				{
					val = tracked.GuidanceObstacleGoal - goal;
					num = ((val.LengthSquared > 9f) ? 1 : 0);
				}
				else
				{
					num = 1;
				}
				bool flag = (byte)num != 0;
				if (tracked.GuidanceObstacleRecheckCountdown <= 0f || flag)
				{
					tracked.GuidanceObstacleRecheckCountdown = Clamp(GlobalSettings<Settings>.Instance?.AutoguidanceObstacleScanInterval ?? 0.12f, 0.05f, 0.5f);
					tracked.GuidanceObstacleGoal = goal;
					tracked.GuidanceObstacleGoalValid = true;
					tracked.GuidanceObstacleWaypointValid = false;
					if (IsAutoguidanceSegmentObstructed(projectilePosition, goal, tracked, out var hitPoint, stopBeforeTarget: true))
					{
						tracked.GuidanceObstacleWaypointValid = TryBuildAutoguidanceObstacleBypass(tracked, projectilePosition, currentDirection, goal, hitPoint, speed, effectiveTurnRadius, out tracked.GuidanceObstacleWaypoint);
					}
				}
				if (!tracked.GuidanceObstacleWaypointValid)
				{
					return false;
				}
				waypoint = tracked.GuidanceObstacleWaypoint;
				return IsFinite(waypoint);
			}
		}
		if (tracked != null)
		{
			tracked.GuidanceObstacleWaypointValid = false;
		}
		return false;
	}

	private bool TryBuildAutoguidanceObstacleBypass(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, Vec3 goal, Vec3 hitPoint, float speed, float effectiveTurnRadius, out Vec3 waypoint)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		//IL_0125: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0154: Unknown result type (might be due to invalid IL or missing references)
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Unknown result type (might be due to invalid IL or missing references)
		//IL_016f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0187: Unknown result type (might be due to invalid IL or missing references)
		//IL_019e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0289: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_0201: Unknown result type (might be due to invalid IL or missing references)
		//IL_0203: Unknown result type (might be due to invalid IL or missing references)
		//IL_0204: Unknown result type (might be due to invalid IL or missing references)
		//IL_0209: Unknown result type (might be due to invalid IL or missing references)
		//IL_0212: Unknown result type (might be due to invalid IL or missing references)
		//IL_0214: Unknown result type (might be due to invalid IL or missing references)
		//IL_0216: Unknown result type (might be due to invalid IL or missing references)
		//IL_021b: Unknown result type (might be due to invalid IL or missing references)
		//IL_022e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0235: Unknown result type (might be due to invalid IL or missing references)
		//IL_0268: Unknown result type (might be due to invalid IL or missing references)
		//IL_026a: Unknown result type (might be due to invalid IL or missing references)
		waypoint = Vec3.Zero;
		Vec3 val = Cross(NormalizeSafe(goal - projectilePosition, currentDirection), WorldUp);
		if (!IsFinite(val) || val.LengthSquared <= 0.0001f)
		{
			val = Cross(currentDirection, new Vec3(1f, 0f, 0f, -1f));
		}
		val = NormalizeSafe(val, new Vec3(1f, 0f, 0f, -1f));
		float num = Math.Max(Clamp(GlobalSettings<Settings>.Instance?.AutoguidanceObstacleClearance ?? 3f, 1f, 8f), Math.Min(6f, effectiveTurnRadius * 0.16f));
		Vec3[] array = (Vec3[])(object)new Vec3[5]
		{
			hitPoint + val * num + WorldUp * 0.75f,
			hitPoint - val * num + WorldUp * 0.75f,
			hitPoint + val * (num * 1.35f) + WorldUp * 1.5f,
			hitPoint - val * (num * 1.35f) + WorldUp * 1.5f,
			hitPoint + WorldUp * (num + 2f)
		};
		float num2 = float.PositiveInfinity;
		for (int i = 0; i < array.Length; i++)
		{
			Vec3 val2 = array[i];
			if (TryGetTerrainHeight(val2, out var height))
			{
				val2.z = Math.Max(val2.z, height + 1.15f + 0.5f);
			}
			if (IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, val2, effectiveTurnRadius) && !IsAutoguidanceSegmentObstructed(projectilePosition, val2, tracked, out var _, stopBeforeTarget: false) && !IsAutoguidanceSegmentObstructed(val2, goal, tracked, out var _, stopBeforeTarget: true))
			{
				Vec3 val3 = val2 - projectilePosition;
				float length = val3.Length;
				val3 = goal - val2;
				float num3 = length + val3.Length;
				num3 += Math.Max(0f, val2.z - hitPoint.z) * 1.75f;
				if (i == array.Length - 1)
				{
					num3 += 12f;
				}
				if (num3 < num2)
				{
					num2 = num3;
					waypoint = val2;
				}
			}
		}
		if (IsFinite(num2))
		{
			return IsFinite(waypoint);
		}
		return false;
	}

	private bool IsAutoguidanceSegmentObstructed(Vec3 start, Vec3 end, TrackedMissile tracked, out Vec3 hitPoint, bool stopBeforeTarget)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_0107: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		hitPoint = Vec3.Zero;
		Vec3 val = end - start;
		float length = val.Length;
		if (!IsFinite(val) || !IsFinite(length) || length <= 2f)
		{
			return false;
		}
		Vec3 val2 = val / length;
		Vec3 val3 = start + val2 * Math.Min(0.75f, length * 0.15f);
		float val4 = (stopBeforeTarget ? 0.9f : 0.2f);
		Vec3 val5 = end - val2 * Math.Min(val4, length * 0.2f);
		Vec3 val6 = val5 - val3;
		float length2 = val6.Length;
		if (!IsFinite(length2) || length2 <= 0.5f)
		{
			return false;
		}
		GameEntity val7 = null;
		try
		{
			object obj;
			if (tracked == null)
			{
				obj = null;
			}
			else
			{
				Mission.Missile missile = tracked.Missile;
				obj = ((missile != null) ? missile.Entity : null);
			}
			val7 = (GameEntity)obj;
		}
		catch
		{
			val7 = null;
		}
		if (!TryRayCastForAutoguidanceObstacle(val3, val5, val7, out var collisionDistance))
		{
			return false;
		}
		collisionDistance = Clamp(collisionDistance, 0f, length2);
		hitPoint = val3 + val2 * collisionDistance;
		return collisionDistance < length2 - 0.15f;
	}

	private bool TryRayCastForAutoguidanceObstacle(Vec3 start, Vec3 end, GameEntity ignoredEntity, out float collisionDistance)
	{
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_015f: Unknown result type (might be due to invalid IL or missing references)
		//IL_027f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0280: Unknown result type (might be due to invalid IL or missing references)
		//IL_0281: Unknown result type (might be due to invalid IL or missing references)
		//IL_0286: Unknown result type (might be due to invalid IL or missing references)
		collisionDistance = 0f;
		Mission mission = ((MissionBehavior)this).Mission;
		Scene val = ((mission != null) ? mission.Scene : null);
		if ((NativeObject)(object)val == (NativeObject)null)
		{
			return false;
		}
		try
		{
			Type type = ((object)val).GetType();
			if (_cachedRayCastSceneType != type || _cachedRayCastMethods == null)
			{
				_cachedRayCastSceneType = type;
				List<MethodInfo> list = new List<MethodInfo>();
				MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public);
				for (int i = 0; i < methods.Length; i++)
				{
					string name = methods[i].Name;
					if (name == "RayCastForClosestEntityOrTerrainIgnoreEntity" || name == "RayCastForClosestEntityOrTerrain")
					{
						list.Add(methods[i]);
					}
				}
				list.Sort(delegate(MethodInfo left, MethodInfo right)
				{
					bool flag3 = left.Name.IndexOf("IgnoreEntity", StringComparison.Ordinal) >= 0;
					bool flag4 = right.Name.IndexOf("IgnoreEntity", StringComparison.Ordinal) >= 0;
					if (flag3 == flag4)
					{
						return left.GetParameters().Length.CompareTo(right.GetParameters().Length);
					}
					return (!flag3) ? 1 : (-1);
				});
				_cachedRayCastMethods = list.ToArray();
			}
			for (int num = 0; num < _cachedRayCastMethods.Length; num++)
			{
				MethodInfo methodInfo = _cachedRayCastMethods[num];
				ParameterInfo[] parameters = methodInfo.GetParameters();
				object[] array = new object[parameters.Length];
				int num2 = 0;
				bool flag = true;
				for (int num3 = 0; num3 < parameters.Length; num3++)
				{
					Type parameterType = parameters[num3].ParameterType;
					bool isByRef = parameterType.IsByRef;
					Type type2 = (isByRef ? parameterType.GetElementType() : parameterType);
					if (type2 == typeof(Vec3))
					{
						if (isByRef)
						{
							array[num3] = Vec3.Zero;
						}
						else
						{
							array[num3] = ((num2++ == 0) ? start : end);
						}
						continue;
					}
					if (type2 == typeof(float))
					{
						array[num3] = (isByRef ? 0f : 0.06f);
						continue;
					}
					if (typeof(GameEntity).IsAssignableFrom(type2))
					{
						array[num3] = ignoredEntity;
						continue;
					}
					if (type2 == typeof(bool))
					{
						array[num3] = false;
						continue;
					}
					if (type2 == typeof(int))
					{
						array[num3] = 0;
						continue;
					}
					if (type2.IsEnum)
					{
						array[num3] = Activator.CreateInstance(type2);
						continue;
					}
					flag = false;
					break;
				}
				if (!flag || num2 < 2)
				{
					continue;
				}
				object obj = methodInfo.Invoke(val, array);
				bool flag2 = obj is bool && (bool)obj;
				if (!flag2 && obj != null && typeof(GameEntity).IsAssignableFrom(obj.GetType()))
				{
					flag2 = true;
				}
				if (!flag2)
				{
					continue;
				}
				Vec3 val2 = end - start;
				float length = val2.Length;
				collisionDistance = length * 0.5f;
				for (int num4 = 0; num4 < parameters.Length; num4++)
				{
					Type parameterType2 = parameters[num4].ParameterType;
					if (parameterType2.IsByRef && !(parameterType2.GetElementType() != typeof(float)))
					{
						float num5 = Convert.ToSingle(array[num4]);
						if (IsFinite(num5) && num5 >= 0f)
						{
							collisionDistance = num5;
							break;
						}
					}
				}
				return true;
			}
		}
		catch
		{
		}
		collisionDistance = 0f;
		return false;
	}

	private Vec3 ComputeAutoguidanceAimPoint(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, float speed, Vec3 predictedHead, Vec3 predictedPhysicalHead, Vec3 predictedTargetBase, float dt)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = predictedPhysicalHead - projectilePosition;
		val.z = 0f;
		float length = val.Length;
		if (!IsFinite(length) || length <= 0.05f)
		{
			return predictedHead;
		}
		float effectiveTurnRadius = Clamp(GlobalSettings<Settings>.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f) / Math.Max(1f, GetAdaptiveSteeringMultiplier(speed));
		return (Vec3)(ResolveTrackedAutoguidanceFlightProfile(tracked, projectilePosition, currentDirection, predictedPhysicalHead, predictedTargetBase, effectiveTurnRadius) switch
		{
			AutoguidanceFlightProfile.NaturalBallistic => ComputeNaturalBallisticAimPoint(tracked, projectilePosition, currentDirection, speed, predictedHead, effectiveTurnRadius), 
			AutoguidanceFlightProfile.LoftedArc => ComputeLoftedArcAimPoint(projectilePosition, currentDirection, predictedHead, predictedPhysicalHead, predictedTargetBase, effectiveTurnRadius), 
			AutoguidanceFlightProfile.BankingFlank => ComputeBankingFlankAimPoint(tracked, projectilePosition, currentDirection, predictedHead, predictedPhysicalHead, predictedTargetBase, effectiveTurnRadius), 
			AutoguidanceFlightProfile.Serpentine => ComputeSerpentineAimPoint(tracked, projectilePosition, currentDirection, speed, predictedHead, predictedPhysicalHead, effectiveTurnRadius), 
			AutoguidanceFlightProfile.LowStrike => ComputeLowStrikeAimPoint(tracked, projectilePosition, currentDirection, speed, predictedHead, predictedPhysicalHead, predictedTargetBase, effectiveTurnRadius, dt), 
			_ => predictedHead, 
		});
	}

	private Vec3 ComputeNaturalBallisticAimPoint(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, float speed, Vec3 predictedHead, float effectiveTurnRadius)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Unknown result type (might be due to invalid IL or missing references)
		//IL_015a: Unknown result type (might be due to invalid IL or missing references)
		//IL_015f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = predictedHead - projectilePosition;
		float length = val.Length;
		if (!IsFinite(val) || !IsFinite(length) || length <= 0.1f)
		{
			return predictedHead;
		}
		float num = Math.Max(8f, effectiveTurnRadius * 0.32f);
		if (length <= num)
		{
			return predictedHead;
		}
		Vec3 val2 = val / length;
		Vec3 naturalBallisticReferenceDirection = GetNaturalBallisticReferenceDirection(tracked, currentDirection);
		float num2 = (float)Math.Acos(Clamp(Dot(naturalBallisticReferenceDirection, val2), -1f, 1f));
		float num3 = EstimateMinimumReachableDistance(effectiveTurnRadius, num2);
		float num4 = length - num3;
		float num5 = Math.Max(14f, effectiveTurnRadius * 1.75f);
		float val3 = 1f - Clamp(num4 / num5, 0f, 1f);
		float num6 = Clamp(num2 / DegreesToRadians(24f), 0f, 1f);
		float val4 = 1f - Clamp((length - num) / 28f, 0f, 1f);
		float t = Clamp(0.12f + Math.Max(val3, Math.Max(num6 * 0.72f, val4)) * 0.88f, 0.12f, 1f);
		Vec3 val5 = NormalizeSafe(Lerp(naturalBallisticReferenceDirection, val2, t), val2);
		float num7 = Clamp(speed * 0.18f, 5f, Math.Min(24f, length));
		return projectilePosition + val5 * num7;
	}

	private Vec3 ComputeLowStrikeAimPoint(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, float speed, Vec3 predictedHead, Vec3 predictedPhysicalHead, Vec3 predictedTargetBase, float effectiveTurnRadius, float dt)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Unknown result type (might be due to invalid IL or missing references)
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_017a: Unknown result type (might be due to invalid IL or missing references)
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_017e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0197: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_021b: Unknown result type (might be due to invalid IL or missing references)
		//IL_021c: Unknown result type (might be due to invalid IL or missing references)
		//IL_021f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0224: Unknown result type (might be due to invalid IL or missing references)
		//IL_0229: Unknown result type (might be due to invalid IL or missing references)
		//IL_024e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0277: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_0290: Unknown result type (might be due to invalid IL or missing references)
		//IL_0268: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = predictedPhysicalHead - projectilePosition;
		val.z = 0f;
		float length = val.Length;
		if (!IsFinite(length) || length <= 0.05f)
		{
			return predictedHead;
		}
		Vec3 val2 = val / length;
		float num = Clamp(GlobalSettings<Settings>.Instance?.AutoguidanceCruiseGroundClearance ?? 0.9f, 0.45f, 3f);
		float num2 = DegreesToRadians(Clamp(GlobalSettings<Settings>.Instance?.AutoguidancePreferredRiseAngle ?? 8f, 2f, 20f));
		float num3 = predictedTargetBase.z + num;
		float val3 = Math.Max(0.15f, predictedPhysicalHead.z - num3) / Math.Max(0.035f, (float)Math.Tan(num2));
		float num4 = EstimateMinimumReachableDistance(effectiveTurnRadius, num2);
		float num5 = Clamp(Math.Max(val3, num4 * 1.15f), 4f, 24f);
		if (length <= num5 + 0.5f)
		{
			return predictedHead;
		}
		Vec3 waypoint = predictedPhysicalHead - val2 * num5;
		waypoint.z = num3;
		if (!IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, waypoint, effectiveTurnRadius))
		{
			return predictedHead;
		}
		tracked.GuidanceTerrainSampleCountdown -= dt;
		if (!tracked.GuidanceCruiseGroundValid || tracked.GuidanceTerrainSampleCountdown <= 0f)
		{
			tracked.GuidanceTerrainSampleCountdown = 0.08f;
			float num6 = Clamp(speed * 0.12f, 3f, Math.Min(12f, Math.Max(3f, length - num5)));
			Vec3 position = projectilePosition + val2 * num6;
			float num7 = predictedTargetBase.z;
			bool flag = false;
			if (TryGetTerrainHeight(projectilePosition, out var height))
			{
				num7 = height;
				flag = true;
			}
			if (TryGetTerrainHeight(position, out var height2))
			{
				num7 = (flag ? Math.Max(num7, height2) : height2);
				flag = true;
			}
			tracked.GuidanceCruiseGroundZ = num7;
			tracked.GuidanceCruiseGroundValid = flag;
		}
		if (!tracked.GuidanceCruiseGroundValid)
		{
			return predictedHead;
		}
		float z = tracked.GuidanceCruiseGroundZ + num;
		float num8 = Clamp(speed * 0.12f, 3f, Math.Min(12f, Math.Max(3f, length - num5)));
		Vec3 val4 = projectilePosition + val2 * num8;
		val4.z = z;
		float num9 = tracked.GuidanceCruiseGroundZ + Math.Max(0.45f, num * 0.7f);
		if (projectilePosition.z < num9 || (projectilePosition.z < num9 + 0.5f && currentDirection.z < -0.02f))
		{
			val4.z = Math.Max(val4.z, num9 + 0.65f);
		}
		return val4;
	}

	private Vec3 ComputeLoftedArcAimPoint(Vec3 projectilePosition, Vec3 currentDirection, Vec3 predictedHead, Vec3 predictedPhysicalHead, Vec3 predictedTargetBase, float effectiveTurnRadius)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0088: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = predictedPhysicalHead - projectilePosition;
		val.z = 0f;
		float length = val.Length;
		if (!IsFinite(length) || length <= 0.05f)
		{
			return predictedHead;
		}
		float num = Clamp(Math.Max(8f, effectiveTurnRadius * 0.65f), 8f, 30f);
		if (length <= num + 0.75f)
		{
			return predictedHead;
		}
		Vec3 val2 = val / length;
		float num2 = Clamp((float)Math.Tan(GetLoftedArcDescentAngle(predictedPhysicalHead, predictedTargetBase)) * num, 1f, 6f);
		Vec3 val3 = predictedPhysicalHead - val2 * num;
		val3.z = predictedPhysicalHead.z + num2;
		if (!IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, val3, effectiveTurnRadius))
		{
			return predictedHead;
		}
		return val3;
	}

	private Vec3 ComputeBankingFlankAimPoint(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, Vec3 predictedHead, Vec3 predictedPhysicalHead, Vec3 predictedTargetBase, float effectiveTurnRadius)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_0107: Unknown result type (might be due to invalid IL or missing references)
		//IL_0108: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0121: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0133: Unknown result type (might be due to invalid IL or missing references)
		//IL_0138: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = predictedPhysicalHead - projectilePosition;
		val.z = 0f;
		float length = val.Length;
		if (!IsFinite(length) || length <= 0.05f)
		{
			return predictedHead;
		}
		float num = Clamp(Math.Max(10f, effectiveTurnRadius * 0.7f), 8f, 28f);
		if (length <= num + 1f)
		{
			return predictedHead;
		}
		Vec3 val2 = val / length;
		Vec3 val3 = NormalizeSafe(Cross(val2, WorldUp), new Vec3(1f, 0f, 0f, -1f));
		float autoguidanceProfileSide = GetAutoguidanceProfileSide(tracked);
		float num2 = Clamp(length * 0.14f, 2.5f, Math.Min(9f, effectiveTurnRadius * 0.55f));
		Vec3 val4 = predictedPhysicalHead - val2 * num + val3 * (num2 * autoguidanceProfileSide);
		val4.z = Math.Max(predictedTargetBase.z + 1.05f, predictedPhysicalHead.z - 0.15f);
		if (!IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, val4, effectiveTurnRadius))
		{
			val4 = predictedPhysicalHead - val2 * num + val3 * (num2 * 0.45f * autoguidanceProfileSide);
			val4.z = Math.Max(predictedTargetBase.z + 1.05f, predictedPhysicalHead.z - 0.15f);
			if (!IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, val4, effectiveTurnRadius))
			{
				return predictedHead;
			}
		}
		return val4;
	}

	private Vec3 ComputeSerpentineAimPoint(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, float speed, Vec3 predictedHead, Vec3 predictedPhysicalHead, float effectiveTurnRadius)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_0158: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0165: Unknown result type (might be due to invalid IL or missing references)
		//IL_016c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_0176: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0219: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01de: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0209: Unknown result type (might be due to invalid IL or missing references)
		//IL_020a: Unknown result type (might be due to invalid IL or missing references)
		//IL_020b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0216: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = predictedPhysicalHead - projectilePosition;
		val.z = 0f;
		float length = val.Length;
		if (!IsFinite(length) || length <= 0.05f)
		{
			return predictedHead;
		}
		float num = Math.Max(12f, effectiveTurnRadius * 0.45f);
		if (length <= num)
		{
			return predictedHead;
		}
		Vec3 val2 = val / length;
		Vec3 val3 = NormalizeSafe(Cross(val2, WorldUp), new Vec3(1f, 0f, 0f, -1f));
		float num2;
		if (tracked == null || !tracked.GuidanceLaunchStateValid)
		{
			num2 = speed * (tracked?.GuidanceFlightElapsed ?? 0f);
		}
		else
		{
			Vec3 val4 = projectilePosition - tracked.GuidanceLaunchPosition;
			num2 = val4.Length;
		}
		float num3 = Clamp(effectiveTurnRadius * 1.65f, 18f, 48f);
		float num4 = ((GetAutoguidanceProfileSide(tracked) < 0f) ? 0f : ((float)Math.PI));
		float num5 = (float)Math.Sin(num2 / num3 * ((float)Math.PI * 2f) + num4);
		float num6 = Clamp((length - num) / 32f, 0f, 1f);
		float num7 = Clamp(length * 0.035f, 0.6f, 2.6f) * num6;
		float num8 = Clamp(speed * 0.16f, 4f, Math.Min(14f, length - num * 0.35f));
		Vec3 val5 = projectilePosition + val2 * num8 + val3 * (num7 * num5);
		float num9 = Clamp(num8 / Math.Max(0.1f, length), 0f, 1f);
		val5.z = projectilePosition.z + (predictedPhysicalHead.z - projectilePosition.z) * num9;
		if (!IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, val5, effectiveTurnRadius))
		{
			val5 = projectilePosition + val2 * num8 + val3 * (num7 * 0.35f * num5);
			val5.z = projectilePosition.z + (predictedPhysicalHead.z - projectilePosition.z) * num9;
			if (!IsAutoguidanceWaypointReachable(projectilePosition, currentDirection, val5, effectiveTurnRadius))
			{
				return predictedHead;
			}
		}
		return val5;
	}

	private bool TryGetTerrainHeight(Vec3 position, out float height)
	{
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		height = 0f;
		Mission mission = ((MissionBehavior)this).Mission;
		Scene val = ((mission != null) ? mission.Scene : null);
		if ((NativeObject)(object)val == (NativeObject)null)
		{
			return false;
		}
		try
		{
			if (!val.ContainsTerrain)
			{
				return false;
			}
			height = val.GetTerrainHeight(new Vec2(position.x, position.y), true);
			return IsFinite(height);
		}
		catch
		{
			height = 0f;
			return false;
		}
	}

	private Vec3 ApplyAutoguidanceTerrainSafety(TrackedMissile tracked, Vec3 projectilePosition, Vec3 currentDirection, Vec3 desiredDirection, Vec3 predictedHead, float speed, float gravityZ, float dt)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_013c: Unknown result type (might be due to invalid IL or missing references)
		//IL_013d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		//IL_0149: Unknown result type (might be due to invalid IL or missing references)
		//IL_014e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0151: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0205: Unknown result type (might be due to invalid IL or missing references)
		//IL_021c: Unknown result type (might be due to invalid IL or missing references)
		//IL_021d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_022b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0242: Unknown result type (might be due to invalid IL or missing references)
		//IL_0244: Unknown result type (might be due to invalid IL or missing references)
		//IL_0245: Unknown result type (might be due to invalid IL or missing references)
		//IL_024a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_026e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0273: Unknown result type (might be due to invalid IL or missing references)
		//IL_0278: Unknown result type (might be due to invalid IL or missing references)
		//IL_027a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0298: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c8: Unknown result type (might be due to invalid IL or missing references)
		desiredDirection = NormalizeSafe(desiredDirection, currentDirection);
		if (tracked == null || !IsFinite(speed) || speed <= 1E-06f)
		{
			return desiredDirection;
		}
		Vec3 val = predictedHead - projectilePosition;
		float length = val.Length;
		if (!IsFinite(length) || length <= 0.75f)
		{
			return desiredDirection;
		}
		float num = length / Math.Max(1f, speed);
		float num2 = Math.Min(0.55f, num * 0.9f);
		if (!IsFinite(num2) || num2 <= 0.04f)
		{
			return desiredDirection;
		}
		tracked.GuidanceSafetyTerrainSampleCountdown -= Math.Max(0f, dt);
		bool flag = !tracked.GuidanceSafetySampleDirectionValid || Dot(NormalizeSafe(tracked.GuidanceSafetySampleDirection, desiredDirection), desiredDirection) < 0.94f;
		if (!tracked.GuidanceSafetyGroundValid || tracked.GuidanceSafetyTerrainSampleCountdown <= 0f || flag)
		{
			tracked.GuidanceSafetyTerrainSampleCountdown = 0.08f;
			tracked.GuidanceSafetySampleDirection = desiredDirection;
			tracked.GuidanceSafetySampleDirectionValid = true;
			tracked.GuidanceSafetyGroundValid = false;
			tracked.GuidanceSafetyMaximumGroundZ = float.NegativeInfinity;
			tracked.GuidanceSafetyCurrentGroundZ = 0f;
			float num3 = Clamp(gravityZ, -40f, 5f);
			for (int i = 1; i <= 4; i++)
			{
				float num4 = num2 * (float)i / 4f;
				Vec3 position = projectilePosition + desiredDirection * (speed * num4);
				if (TryGetTerrainHeight(position, out var height))
				{
					if (i == 1)
					{
						tracked.GuidanceSafetyCurrentGroundZ = height;
					}
					float val2 = (height + 0.6f - projectilePosition.z - 0.5f * num3 * num4 * num4) / Math.Max(0.01f, num4) / Math.Max(1f, speed);
					tracked.GuidanceSafetyMaximumGroundZ = Math.Max(tracked.GuidanceSafetyMaximumGroundZ, val2);
					tracked.GuidanceSafetyGroundValid = true;
				}
			}
		}
		if (!tracked.GuidanceSafetyGroundValid)
		{
			return desiredDirection;
		}
		float guidanceSafetyMaximumGroundZ = tracked.GuidanceSafetyMaximumGroundZ;
		if (!IsFinite(guidanceSafetyMaximumGroundZ) || desiredDirection.z >= guidanceSafetyMaximumGroundZ)
		{
			return desiredDirection;
		}
		Vec3 val3 = desiredDirection;
		val3.z = 0f;
		if (!IsFinite(val3) || val3.LengthSquared <= 0.0001f)
		{
			val3 = currentDirection;
			val3.z = 0f;
		}
		if (!IsFinite(val3) || val3.LengthSquared <= 0.0001f)
		{
			val3 = predictedHead - projectilePosition;
			val3.z = 0f;
		}
		val3 = NormalizeSafe(val3, new Vec3(0f, 1f, 0f, -1f));
		float num5 = projectilePosition.z - tracked.GuidanceSafetyCurrentGroundZ;
		float num6 = guidanceSafetyMaximumGroundZ + 0.03f;
		if (num5 < 2f || currentDirection.z < -0.12f)
		{
			num6 = Math.Max(num6, 0.18f);
		}
		return NormalizeSafe(val3 + WorldUp * num6, desiredDirection);
	}

	private static Vec3 ApplyContinuousGuidanceGravityCompensation(Vec3 desiredDirection, float speed, float gravityZ, float dt)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		desiredDirection = NormalizeSafe(desiredDirection, new Vec3(0f, 1f, 0f, -1f));
		if (!IsFinite(speed) || speed <= 1E-06f || !IsFinite(gravityZ))
		{
			return desiredDirection;
		}
		float num = Clamp(dt, 0.0001f, 0.1f);
		return NormalizeSafe(desiredDirection * speed - new Vec3(0f, 0f, Clamp(gravityZ, -40f, 5f), -1f) * num, desiredDirection);
	}

	private static void ComputeSteeringAngularError(Vec3 currentDirection, Vec3 desiredDirection, out float yaw, out float pitch)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		currentDirection = NormalizeSafe(currentDirection, new Vec3(0f, 1f, 0f, -1f));
		desiredDirection = NormalizeSafe(desiredDirection, currentDirection);
		Vec3 val = Cross(currentDirection, WorldUp);
		if (!IsFinite(val) || val.LengthSquared <= 0.0001f)
		{
			val = Cross(currentDirection, new Vec3(0f, 1f, 0f, -1f));
		}
		val = NormalizeSafe(val, new Vec3(1f, 0f, 0f, -1f));
		Vec3 b = NormalizeSafe(Cross(val, currentDirection), WorldUp);
		float num = Dot(desiredDirection, val);
		float num2 = Dot(desiredDirection, currentDirection);
		float num3 = Dot(desiredDirection, b);
		yaw = 0f - (float)Math.Atan2(num, num2);
		float num4 = (float)Math.Sqrt(Math.Max(1E-06f, num * num + num2 * num2));
		pitch = (float)Math.Atan2(num3, num4);
	}

	private void UpdateEstimatedProjectileGravity(TrackedMissile tracked, Vec3 currentVelocity, float dt)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		if (tracked == null || !tracked.LastCommandedVelocityValid || dt <= 1E-06f)
		{
			return;
		}
		tracked.LastCommandedVelocityValid = false;
		if (!IsFinite(currentVelocity) || !IsFinite(tracked.LastCommandedVelocity))
		{
			return;
		}
		Vec3 val = (currentVelocity - tracked.LastCommandedVelocity) / dt;
		if (IsFinite(val))
		{
			float num = (float)Math.Sqrt(val.x * val.x + val.y * val.y);
			float length = tracked.LastCommandedVelocity.Length;
			float length2 = currentVelocity.Length;
			float num2 = Math.Max(3f, Math.Abs(val.z) * 0.3f);
			float num3 = Math.Max(1.5f, Math.Max(length, length2) * 0.06f);
			if (IsFinite(num) && !(num > num2) && IsFinite(length) && IsFinite(length2) && !(Math.Abs(length2 - length) > num3))
			{
				float num4 = Clamp(val.z, -40f, 5f);
				tracked.EstimatedGravityZ += (num4 - tracked.EstimatedGravityZ) * 0.15f;
			}
		}
	}

	private static void RecordCommandedVelocity(TrackedMissile tracked, Vec3 velocity)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		if (tracked != null && IsFinite(velocity))
		{
			tracked.LastCommandedVelocity = velocity;
			tracked.LastCommandedVelocityValid = true;
		}
	}

	private static int ResolveGuidanceHeadBoneIndex(Agent target)
	{
		if (target == null)
		{
			return -1;
		}
		int num = TryGetSkeletonBoneIndexByName(target, "head");
		int monsterBoneIndex = GetMonsterBoneIndex(target, "HeadLookDirectionBoneIndex");
		int num2 = TryGetSkeletonBoneIndexByName(target, "neck");
		int monsterBoneIndex2 = GetMonsterBoneIndex(target, "NeckRootBoneIndex");
		int monsterBoneIndex3 = GetMonsterBoneIndex(target, "SpineUpperBoneIndex");
		int[] array = new int[5] { num, monsterBoneIndex, num2, monsterBoneIndex2, monsterBoneIndex3 };
		foreach (int num3 in array)
		{
			if (num3 >= 0 && TryGetBoneCollisionCenterWorld(target, num3, out var _))
			{
				return num3;
			}
		}
		for (int j = 0; j < array.Length; j++)
		{
			if (array[j] >= 0)
			{
				return array[j];
			}
		}
		return -1;
	}

	private static int TryGetSkeletonBoneIndexByName(Agent target, string boneName)
	{
		if (target == null || string.IsNullOrEmpty(boneName))
		{
			return -1;
		}
		try
		{
			MBAgentVisuals agentVisuals = target.AgentVisuals;
			Skeleton val = ((agentVisuals != null) ? agentVisuals.GetSkeleton() : null);
			if ((NativeObject)(object)val == (NativeObject)null)
			{
				return -1;
			}
			MethodInfo method = ((object)val).GetType().GetMethod("GetBoneIndexFromName", BindingFlags.Instance | BindingFlags.Public, null, new Type[1] { typeof(string) }, null);
			if (method != null)
			{
				object obj = method.Invoke(val, new object[1] { boneName });
				int num = ((obj != null) ? Convert.ToInt32(obj) : (-1));
				if (num >= 0)
				{
					return num;
				}
			}
			int num2 = Convert.ToInt32(val.GetBoneCount());
			for (int i = 0; i < num2; i++)
			{
				if (IsGuidanceBoneNameMatch(val.GetBoneName((sbyte)i), boneName))
				{
					return i;
				}
			}
		}
		catch
		{
		}
		return -1;
	}

	private static bool IsGuidanceBoneNameMatch(string candidateName, string requestedName)
	{
		if (string.IsNullOrEmpty(candidateName) || string.IsNullOrEmpty(requestedName))
		{
			return false;
		}
		if (string.Equals(candidateName, requestedName, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string text = candidateName.Replace(" ", string.Empty).Replace("_", string.Empty).Replace(":", string.Empty)
			.Replace("-", string.Empty);
		string value = requestedName.Replace(" ", string.Empty).Replace("_", string.Empty).Replace(":", string.Empty)
			.Replace("-", string.Empty);
		return text.EndsWith(value, StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryGetGuidanceHeadPosition(Agent target, int headBoneIndex, out Vec3 position)
	{
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		if (TryGetBoneCollisionCenterWorld(target, headBoneIndex, out position))
		{
			return true;
		}
		if (TryGetBoneWorldPosition(target, headBoneIndex, out position))
		{
			return true;
		}
		try
		{
			position = GetVisualPosition(target) + WorldUp * 0.95f;
			return IsFinite(position);
		}
		catch
		{
			position = Vec3.Zero;
			return false;
		}
	}

	private static Vec3 GetAutoguidanceTargetVelocity(Agent target, out bool valid)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		valid = false;
		if (target == null)
		{
			return Vec3.Zero;
		}
		try
		{
			Vec3 velocity = target.Velocity;
			float lengthSquared = velocity.LengthSquared;
			if (IsFinite(velocity) && IsFinite(lengthSquared) && lengthSquared <= 1600f)
			{
				valid = true;
				return velocity;
			}
		}
		catch
		{
		}
		return Vec3.Zero;
	}

	private static bool TryGetBoneCollisionCenterWorld(Agent target, int boneIndex, out Vec3 position)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0107: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_012d: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0132: Unknown result type (might be due to invalid IL or missing references)
		//IL_0137: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Unknown result type (might be due to invalid IL or missing references)
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Unknown result type (might be due to invalid IL or missing references)
		//IL_014b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0154: Unknown result type (might be due to invalid IL or missing references)
		//IL_0156: Unknown result type (might be due to invalid IL or missing references)
		//IL_0158: Unknown result type (might be due to invalid IL or missing references)
		//IL_015d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_016d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c9: Unknown result type (might be due to invalid IL or missing references)
		position = Vec3.Zero;
		if (target == null || boneIndex < 0)
		{
			return false;
		}
		try
		{
			MBAgentVisuals agentVisuals = target.AgentVisuals;
			Skeleton val = ((agentVisuals != null) ? agentVisuals.GetSkeleton() : null);
			if ((NativeObject)(object)agentVisuals == (NativeObject)null || (NativeObject)(object)val == (NativeObject)null || !TryGetBoneEntitialFrame(val, boneIndex, out var frame))
			{
				return false;
			}
			CapsuleData val2 = default(CapsuleData);
			val.GetBoneBody((sbyte)boneIndex, ref val2);
			if (!IsFinite(val2.P1) || !IsFinite(val2.P2) || !IsFinite(val2.Radius) || val2.Radius <= 1E-06f)
			{
				return false;
			}
			Vec3 val3 = (val2.P1 + val2.P2) * 0.5f;
			MatrixFrame globalFrame = agentVisuals.GetGlobalFrame();
			Vec3 val4 = TransformEntitialPointToWorld(globalFrame, frame.origin);
			Vec3 entitialPoint = frame.origin + frame.rotation.s * val3.x + frame.rotation.f * val3.y + frame.rotation.u * val3.z;
			Vec3 val5 = TransformEntitialPointToWorld(globalFrame, entitialPoint);
			Vec3 val6 = TransformEntitialPointToWorld(globalFrame, val3);
			Vec3 val7 = val5 - val4;
			float lengthSquared = val7.LengthSquared;
			val7 = val6 - val4;
			float lengthSquared2 = val7.LengthSquared;
			Vec3 val8 = ((lengthSquared <= lengthSquared2) ? val5 : val6);
			float num = Math.Min(lengthSquared, lengthSquared2);
			float num2 = Clamp(Math.Max(0.75f, val2.Radius * 6f), 0.75f, 2.5f);
			if (!IsFinite(val8) || !IsFinite(num) || num > num2 * num2)
			{
				return false;
			}
			position = val8;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static Vec3 TransformEntitialPointToWorld(MatrixFrame rootFrame, Vec3 entitialPoint)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		return rootFrame.origin + rootFrame.rotation.s * entitialPoint.x + rootFrame.rotation.f * entitialPoint.y + rootFrame.rotation.u * entitialPoint.z;
	}

	private void QueueDirectSteeringInput(float yaw, float pitch)
	{
		if (IsFinite(yaw))
		{
			_pendingYawInput += yaw;
		}
		if (IsFinite(pitch))
		{
			_pendingPitchInput += pitch;
		}
	}

	private Vec3 ApplyLimitedDirectionSteering(Vec3 currentDirection, Vec3 desiredDirection, float speed, float dt)
	{
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		float num = Clamp(GlobalSettings<Settings>.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f);
		float adaptiveSteeringMultiplier = GetAdaptiveSteeringMultiplier(speed);
		float num2 = Math.Min(1.2f, speed / num * dt * adaptiveSteeringMultiplier);
		if (num2 <= 1E-06f)
		{
			return NormalizeSafe(currentDirection, desiredDirection);
		}
		return RotateTowards(currentDirection, desiredDirection, num2);
	}

	private Vec3 ApplyLimitedDirectSteering(TrackedMissile tracked, Vec3 currentDirection, float yaw, float pitch, float speed, float dt)
	{
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		float num = Clamp(GlobalSettings<Settings>.Instance?.MinimumTurnRadius ?? 24f, 3f, 120f);
		float adaptiveSteeringMultiplier = GetAdaptiveSteeringMultiplier(speed);
		float num2 = Math.Min(1.2f, speed / num * dt * adaptiveSteeringMultiplier);
		if (num2 <= 1E-06f)
		{
			return currentDirection;
		}
		float num3 = yaw * adaptiveSteeringMultiplier;
		float num4 = pitch * adaptiveSteeringMultiplier;
		float num5 = (float)Math.Sqrt(num3 * num3 + num4 * num4);
		if (num5 > num2)
		{
			float num6 = num2 / num5;
			num3 *= num6;
			num4 *= num6;
		}
		return ApplyDirectSteering(tracked, currentDirection, num3, num4);
	}

	private float GetAdaptiveSteeringMultiplier(float speed)
	{
		float num = 1f;
		if (IsFinite(speed) && speed > 1E-06f)
		{
			float num2 = Clamp(GlobalSettings<Settings>.Instance?.ProximityReferenceMissileSpeed ?? 70f, 10f, 250f);
			float num3 = Clamp(GlobalSettings<Settings>.Instance?.SpeedAdaptiveSteeringStrength ?? 1f, 0f, 2f);
			if (num3 > 1E-06f && speed > num2)
			{
				num = (float)Math.Pow(Clamp(speed / num2, 1f, 8f), num3);
			}
		}
		float v = 1f;
		if (_state == State.Guiding)
		{
			if (_timeRequestActive)
			{
				v = _requestedSpeed;
			}
			else
			{
				Settings instance = GlobalSettings<Settings>.Instance;
				if (instance == null || instance.EnableProximityTimeDilation)
				{
					v = _proximityCurrentSpeed;
				}
			}
		}
		v = Clamp(v, 0.01f, 1f);
		float num4 = Clamp(GlobalSettings<Settings>.Instance?.SlowTimeSteeringCompensation ?? 1f, 0f, 1.5f);
		float num5 = ((num4 <= 1E-06f) ? 1f : ((float)Math.Pow(1f / v, 0.5f * num4)));
		return Clamp(num * num5, 1f, 8f);
	}

	private static Vec3 ApplyDirectSteering(TrackedMissile tracked, Vec3 currentDirection, float yaw, float pitch)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_011d: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0131: Unknown result type (might be due to invalid IL or missing references)
		//IL_0136: Unknown result type (might be due to invalid IL or missing references)
		//IL_013b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Unknown result type (might be due to invalid IL or missing references)
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_011a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0168: Unknown result type (might be due to invalid IL or missing references)
		//IL_0144: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_015c: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = NormalizeSafe(currentDirection, new Vec3(0f, 1f, 0f, -1f));
		Vec3 val2 = ((tracked != null && tracked.ManualSteeringRightValid) ? tracked.ManualSteeringRight : Cross(val, WorldUp));
		val2 -= val * Dot(val2, val);
		if (!IsFinite(val2) || val2.LengthSquared <= 0.0001f)
		{
			val2 = Cross(val, (Vec3)((Math.Abs(val.z) > 0.9f) ? new Vec3(0f, 1f, 0f, -1f) : WorldUp));
		}
		val2 = NormalizeSafe(val2, new Vec3(1f, 0f, 0f, -1f));
		Vec3 axis = NormalizeSafe(Cross(val2, val), WorldUp);
		if (Math.Abs(yaw) > 1E-06f)
		{
			val = NormalizeSafe(RotateAroundAxis(val, axis, yaw), val);
			val2 = NormalizeSafe(RotateAroundAxis(val2, axis, yaw), val2);
		}
		if (Math.Abs(pitch) > 1E-06f)
		{
			val = NormalizeSafe(RotateAroundAxis(val, val2, pitch), val);
		}
		val2 -= val * Dot(val2, val);
		val2 = NormalizeSafe(val2, Cross(val, WorldUp));
		if (tracked != null && IsFinite(val2) && val2.LengthSquared > 0.0001f)
		{
			tracked.ManualSteeringRight = val2;
			tracked.ManualSteeringRightValid = true;
		}
		return val;
	}

	private void BuildSplitArrowFormation(int mode, int formationSlot, int secondaryCount, Vec3 leaderForward, float elapsed, out Vec3 offset, out Vec3 relativeVelocity)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0218: Unknown result type (might be due to invalid IL or missing references)
		//IL_021d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0222: Unknown result type (might be due to invalid IL or missing references)
		//IL_0227: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0231: Unknown result type (might be due to invalid IL or missing references)
		//IL_0238: Unknown result type (might be due to invalid IL or missing references)
		//IL_0241: Unknown result type (might be due to invalid IL or missing references)
		//IL_0246: Unknown result type (might be due to invalid IL or missing references)
		//IL_024e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0253: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Unknown result type (might be due to invalid IL or missing references)
		//IL_013f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0145: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0150: Unknown result type (might be due to invalid IL or missing references)
		//IL_0155: Unknown result type (might be due to invalid IL or missing references)
		//IL_015a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0161: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_0175: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Unknown result type (might be due to invalid IL or missing references)
		//IL_0189: Unknown result type (might be due to invalid IL or missing references)
		//IL_018e: Unknown result type (might be due to invalid IL or missing references)
		//IL_027e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0287: Unknown result type (might be due to invalid IL or missing references)
		//IL_028c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0299: Unknown result type (might be due to invalid IL or missing references)
		//IL_029e: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a3: Unknown result type (might be due to invalid IL or missing references)
		offset = Vec3.Zero;
		relativeVelocity = Vec3.Zero;
		if (formationSlot > 0 && secondaryCount > 0)
		{
			leaderForward = NormalizeSafe(leaderForward, _lastMissileDirection);
			BuildFlightBasis(leaderForward, out var right, out var localUp);
			float num = Clamp(GlobalSettings<Settings>.Instance?.SplitArrowFormationSpacing ?? 1.25f, 0.2f, 8f);
			int num2 = formationSlot - 1;
			float num3 = (float)Math.PI * 2f * (float)num2 / (float)Math.Max(1, secondaryCount);
			switch (mode)
			{
			case 1:
			{
				float num8 = Clamp(GlobalSettings<Settings>.Instance?.SplitArrowOrbitSpeed ?? 220f, 0f, 720f) * ((float)Math.PI / 180f);
				float num9 = Clamp(GlobalSettings<Settings>.Instance?.SplitArrowWaveFrequency ?? 1.2f, 0.1f, 4f) * ((float)Math.PI * 2f);
				float num10 = num3 + num8 * elapsed;
				float num11 = num9 * elapsed + num3 * 0.5f;
				float num12 = num * 0.15f;
				float num13 = (num - num12) * 0.5f;
				float num14 = num12 + num13 + num13 * (float)Math.Cos(num11);
				float num15 = (0f - num13) * num9 * (float)Math.Sin(num11);
				float num16 = (float)Math.Cos(num10);
				float num17 = (float)Math.Sin(num10);
				offset = right * (num16 * num14) + localUp * (num17 * num14);
				relativeVelocity = right * (num15 * num16 - num14 * num8 * num17) + localUp * (num15 * num17 + num14 * num8 * num16);
				break;
			}
			case 2:
			{
				int num6 = num2 / 2 + 1;
				float num7 = (((num2 & 1) == 0) ? (-1f) : 1f);
				offset = right * (num7 * (float)num6 * num);
				break;
			}
			case 3:
			{
				float num18 = Clamp(GlobalSettings<Settings>.Instance?.SplitArrowOrbitSpeed ?? 220f, 0f, 720f) * ((float)Math.PI / 180f);
				float num19 = num3 + num18 * elapsed;
				float num20 = (float)Math.Cos(num19);
				float num21 = (float)Math.Sin(num19);
				offset = right * (num20 * num) + localUp * (num21 * num);
				relativeVelocity = right * ((0f - num21) * num * num18) + localUp * (num20 * num * num18);
				break;
			}
			case 4:
			{
				int num4 = num2 / 2 + 1;
				float num5 = (((num2 & 1) == 0) ? (-1f) : 1f);
				offset = right * (num5 * (float)num4 * num) - leaderForward * ((float)num4 * num * 0.7f);
				break;
			}
			}
		}
	}

	private void ResolvePrimaryMissileFromFirstShotSeed()
	{
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		if (_trackedMissiles.Count == 0 || _pendingShotSeeds.Count == 0)
		{
			return;
		}
		PendingShotSeed pendingShotSeed = _pendingShotSeeds[0];
		if (pendingShotSeed.ForcedIndex < 0 && _missile != null)
		{
			return;
		}
		TrackedMissile trackedMissile = null;
		float num = float.MaxValue;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			TrackedMissile trackedMissile2 = _trackedMissiles[i];
			if (pendingShotSeed.ForcedIndex >= 0 && trackedMissile2.Index == pendingShotSeed.ForcedIndex)
			{
				trackedMissile = trackedMissile2;
				break;
			}
			try
			{
				Vec3 position = ((MBMissile)trackedMissile2.Missile).GetPosition();
				Vec3 velocity = ((MBMissile)trackedMissile2.Missile).GetVelocity();
				Vec3 val = position - pendingShotSeed.Position;
				float lengthSquared = val.LengthSquared;
				float num2 = Dot(NormalizeSafe(velocity, _shotDirection), NormalizeSafe(pendingShotSeed.Velocity, _shotDirection));
				float num3 = lengthSquared + (1f - num2) * 4f;
				if (num3 < num)
				{
					num = num3;
					trackedMissile = trackedMissile2;
				}
			}
			catch
			{
			}
		}
		if (trackedMissile != null)
		{
			_cameraMissileIndex = trackedMissile.Index;
			if (trackedMissile.Index != _missileIndex)
			{
				PromoteFormationLeader(trackedMissile);
			}
		}
	}

	private void PromoteFormationLeader(TrackedMissile leader)
	{
		if (leader == null)
		{
			return;
		}
		_missile = leader.Missile;
		_missileIndex = leader.Index;
		leader.FormationSlot = 0;
		leader.LastFormationTargetValid = false;
		int num = 1;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			TrackedMissile trackedMissile = _trackedMissiles[i];
			if (trackedMissile != leader)
			{
				trackedMissile.FormationSlot = num++;
				trackedMissile.LastFormationTargetValid = false;
			}
		}
	}

	private void AddPendingShotSeed(Agent shooter, int shotGeneration, int forcedIndex, Vec3 position, Vec3 velocity)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		_pendingShotSeeds.Add(new PendingShotSeed
		{
			Shooter = shooter,
			ShotGeneration = shotGeneration,
			ForcedIndex = forcedIndex,
			Position = position,
			Velocity = velocity,
			CreatedAtAcquireElapsed = _pendingAcquireElapsed,
			Resolved = false
		});
		if (_pendingShotSeeds.Count != 1)
		{
			return;
		}
		_pendingShotPosition = position;
		_pendingShotVelocity = velocity;
		Vec3 fallback;
		if (shooter == null)
		{
			Mission mission = ((MissionBehavior)this).Mission;
			Vec3? obj;
			if (mission == null)
			{
				obj = null;
			}
			else
			{
				Agent mainAgent = mission.MainAgent;
				obj = ((mainAgent != null) ? new Vec3?(mainAgent.LookDirection) : ((Vec3?)null));
			}
			fallback = obj ?? new Vec3(0f, 1f, 0f, -1f);
		}
		else
		{
			fallback = shooter.LookDirection;
		}
		_shotDirection = NormalizeSafe(velocity, fallback);
	}

	private bool IsTrackedMissileIdentityValid(TrackedMissile tracked)
	{
		if (tracked == null || tracked.OriginalShooter == null || ((MissionBehavior)this).Mission == null)
		{
			return false;
		}
		if (tracked.ShotGeneration != _activeShotGeneration || tracked.OriginalShooter != _activeShotShooter)
		{
			return false;
		}
		if (tracked.AwaitingCollisionReaction)
		{
			return true;
		}
		Mission.Missile liveMissile;
		return TryRefreshTrackedMissileHandle(tracked, out liveMissile);
	}

	private bool TryRefreshTrackedMissileHandle(TrackedMissile tracked, out Mission.Missile liveMissile)
	{
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		liveMissile = null;
		if (tracked == null || tracked.OriginalShooter == null || ((MissionBehavior)this).Mission == null)
		{
			return false;
		}
		try
		{
			foreach (Mission.Missile item2 in (List<Mission.Missile>)(object)((MissionBehavior)this).Mission.MissilesList)
			{
				if (item2 != null && ((MBMissile)item2).Index == tracked.Index)
				{
					if (item2.ShooterAgent != tracked.OriginalShooter)
					{
						return false;
					}
					GameEntity entity = item2.Entity;
					if (tracked.IdentityEntity != (GameEntity)null && entity != tracked.IdentityEntity)
					{
						return false;
					}
					MissionWeapon weapon = item2.Weapon;
					ItemObject item = weapon.Item;
					if (tracked.IdentityItem != null && item != tracked.IdentityItem)
					{
						return false;
					}
					tracked.Missile = item2;
					liveMissile = item2;
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private void SuspendProjectileCameraForCollisionReaction(int missileIndex)
	{
		if (missileIndex == _cameraMissileIndex)
		{
			_cameraFrameValid = false;
			_releaseCameraAfterOverride = false;
			ReleaseCustomCameraOwnership("AwaitingNativeCollisionReaction");
			RestoreNativeCameraAfterGuidance();
			Log("Projectile camera suspended at impact until an authoritative native collision reaction arrives.");
		}
	}

	private bool ResumeProjectileCameraAfterPassThrough(TrackedMissile tracked)
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		if (tracked == null)
		{
			return false;
		}
		if (!TryRefreshTrackedMissileHandle(tracked, out var liveMissile) || liveMissile == null)
		{
			return false;
		}
		tracked.ParticleDiscoveryLockedAfterImpact = false;
		RemoveFullDetailVisual(tracked);
		ApplyFullDetailVisual(tracked);
		ApplyGuidedProjectileParticlePolicy(tracked);
		try
		{
			Vec3 velocity = ((MBMissile)liveMissile).GetVelocity();
			float lengthSquared = velocity.LengthSquared;
			if (!IsFinite(velocity) || !IsFinite(lengthSquared) || lengthSquared <= 0.0625f)
			{
				return tracked.Index != _cameraMissileIndex || TryPromoteCameraOwnerWithinSwarm(tracked.Index);
			}
			if (tracked.Index == _cameraMissileIndex)
			{
				_lastMissileDirection = NormalizeSafe(velocity, _lastMissileDirection);
			}
			Log("Projectile guidance resumed after native PassThrough.");
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void PruneInvalidTrackedMissiles()
	{
		for (int num = _trackedMissiles.Count - 1; num >= 0; num--)
		{
			TrackedMissile trackedMissile = _trackedMissiles[num];
			if (!IsTrackedMissileIdentityValid(trackedMissile))
			{
				Log("Rejected stale/recycled missile identity for tracked index #" + ((trackedMissile != null) ? trackedMissile.Index.ToString() : "?") + ".");
				AbandonNativePresentationHandlesAfterImpact(trackedMissile);
				RemoveTrackedMissile(trackedMissile, removeVisual: true);
			}
		}
	}

	public override void OnMissileCollisionReaction(Mission.MissileCollisionReaction collisionReaction, Agent attackerAgent, Agent attachedAgent, sbyte attachedBoneIndex)
	{
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		if (_state == State.Guiding && attackerAgent != null && attackerAgent == _activeShotShooter)
		{
			PendingCollisionContext pendingCollisionContext = FindPendingCollisionContext(attackerAgent, attachedAgent);
			if (pendingCollisionContext == null)
			{
				QueueEarlyCollisionReaction(collisionReaction, attackerAgent, attachedAgent);
			}
			else
			{
				ResolveCollisionReaction(pendingCollisionContext.MissileIndex, collisionReaction, "NativeCollisionReaction");
			}
		}
	}

	private void QueuePendingCollisionContext(int missileIndex, Agent attacker, Agent victim, Vec3 impactPosition, Vec3 impactVelocity, Vec3 impactDirection)
	{
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		RemovePendingCollisionContext(missileIndex);
		while (_pendingCollisionContexts.Count >= 32)
		{
			_pendingCollisionContexts.RemoveAt(0);
		}
		_pendingCollisionContexts.Add(new PendingCollisionContext
		{
			MissileIndex = missileIndex,
			Attacker = attacker,
			Victim = victim,
			ImpactPosition = impactPosition,
			ImpactVelocity = impactVelocity,
			ImpactDirection = impactDirection,
			CreatedTimestamp = Stopwatch.GetTimestamp()
		});
	}

	private void RemovePendingCollisionContext(int missileIndex)
	{
		for (int num = _pendingCollisionContexts.Count - 1; num >= 0; num--)
		{
			if (_pendingCollisionContexts[num] != null && _pendingCollisionContexts[num].MissileIndex == missileIndex)
			{
				_pendingCollisionContexts.RemoveAt(num);
			}
		}
	}

	private PendingCollisionContext FindPendingCollisionContext(Agent attacker, Agent attachedAgent)
	{
		PendingCollisionContext pendingCollisionContext = null;
		for (int i = 0; i < _pendingCollisionContexts.Count; i++)
		{
			PendingCollisionContext pendingCollisionContext2 = _pendingCollisionContexts[i];
			if (pendingCollisionContext2 != null && pendingCollisionContext2.Attacker == attacker)
			{
				if (attachedAgent != null && pendingCollisionContext2.Victim == attachedAgent)
				{
					return pendingCollisionContext2;
				}
				if (pendingCollisionContext == null)
				{
					pendingCollisionContext = pendingCollisionContext2;
				}
			}
		}
		return pendingCollisionContext;
	}

	private void QueueEarlyCollisionReaction(Mission.MissileCollisionReaction reaction, Agent attacker, Agent attachedAgent)
	{
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		while (_earlyCollisionReactions.Count >= 32)
		{
			_earlyCollisionReactions.RemoveAt(0);
		}
		_earlyCollisionReactions.Add(new EarlyCollisionReaction
		{
			Reaction = reaction,
			Attacker = attacker,
			AttachedAgent = attachedAgent,
			CreatedTimestamp = Stopwatch.GetTimestamp()
		});
	}

	private void TryConsumeEarlyCollisionReaction(int missileIndex)
	{
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		PendingCollisionContext pendingCollisionContext = null;
		for (int i = 0; i < _pendingCollisionContexts.Count; i++)
		{
			if (_pendingCollisionContexts[i] != null && _pendingCollisionContexts[i].MissileIndex == missileIndex)
			{
				pendingCollisionContext = _pendingCollisionContexts[i];
				break;
			}
		}
		if (pendingCollisionContext == null)
		{
			return;
		}
		for (int j = 0; j < _earlyCollisionReactions.Count; j++)
		{
			EarlyCollisionReaction earlyCollisionReaction = _earlyCollisionReactions[j];
			if (earlyCollisionReaction != null && earlyCollisionReaction.Attacker == pendingCollisionContext.Attacker && (earlyCollisionReaction.AttachedAgent == null || pendingCollisionContext.Victim == null || earlyCollisionReaction.AttachedAgent == pendingCollisionContext.Victim))
			{
				Mission.MissileCollisionReaction reaction = earlyCollisionReaction.Reaction;
				_earlyCollisionReactions.RemoveAt(j);
				ResolveCollisionReaction(missileIndex, reaction, "EarlyNativeCollisionReaction");
				break;
			}
		}
	}

	private unsafe void ResolveCollisionReaction(int missileIndex, Mission.MissileCollisionReaction reaction, string source)
	{
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Invalid comparison between Unknown and I4
		PendingCollisionContext pendingCollisionContext = null;
		for (int i = 0; i < _pendingCollisionContexts.Count; i++)
		{
			PendingCollisionContext pendingCollisionContext2 = _pendingCollisionContexts[i];
			if (pendingCollisionContext2 != null && pendingCollisionContext2.MissileIndex == missileIndex)
			{
				pendingCollisionContext = pendingCollisionContext2;
				break;
			}
		}
		Agent val = pendingCollisionContext?.Victim;
		RemovePendingCollisionContext(missileIndex);
		TrackedMissile trackedMissile = FindTrackedMissile(missileIndex);
		if (trackedMissile == null)
		{
			return;
		}
		trackedMissile.AwaitingCollisionReaction = false;
		bool num = val != null;
		bool flag = num && IsAgentPenetrationOverrideEnabled();
		bool flag2 = (int)reaction == 1;
		if (num && IsAutoguidanceRuntimeActive())
		{
			HandleAutoguidanceAfterAgentImpact(trackedMissile, val);
		}
		if (flag2)
		{
			trackedMissile.LastCommandedVelocityValid = false;
			if (flag && !HasRemainingAgentPenetration(trackedMissile))
			{
				QueueNativeMissileRemoval(trackedMissile);
				bool num2 = trackedMissile.Index == _cameraMissileIndex;
				AbandonNativePresentationHandlesAfterImpact(trackedMissile);
				RemoveTrackedMissile(trackedMissile, removeVisual: true);
				if (num2)
				{
					TryPromoteCameraOwnerWithinSwarm(missileIndex);
				}
				Log("Queued missile #" + missileIndex + " for safe next-tick removal after the configured penetration budget was exhausted.");
				return;
			}
			trackedMissile.PenetrationsUsed++;
			if (!ResumeProjectileCameraAfterPassThrough(trackedMissile) || !IsTrackedMissileIdentityValid(trackedMissile))
			{
				bool num3 = trackedMissile.Index == _cameraMissileIndex;
				RemoveTrackedMissile(trackedMissile, removeVisual: true);
				if (num3)
				{
					TryPromoteCameraOwnerWithinSwarm(missileIndex);
				}
				Log("Rejected PassThrough continuation for missile #" + missileIndex + " because the exact original live missile could not be reacquired.");
				if (_state == State.Guiding && _trackedMissiles.Count == 0)
				{
					HandleGuidedSwarmTerminal("AllPassThroughIdentityLost");
				}
			}
			else
			{
				Log("Mission.Missile #" + missileIndex + " received native PassThrough; exact live guidance continues.");
			}
		}
		else if (flag && HasRemainingAgentPenetration(trackedMissile))
		{
			QueuePenetrationContinuation(trackedMissile, pendingCollisionContext);
			Log("Queued controlled penetration continuation after native terminal agent collision " + ((object)(*(Mission.MissileCollisionReaction*)(&reaction))/*cast due to constrained. prefix*/).ToString() + "; native missile creation is deferred until the next mission tick.");
		}
		else
		{
			bool num4 = trackedMissile.Index == _cameraMissileIndex;
			AbandonNativePresentationHandlesAfterImpact(trackedMissile);
			RemoveTrackedMissile(trackedMissile, removeVisual: true);
			if (num4)
			{
				TryPromoteCameraOwnerWithinSwarm(missileIndex);
			}
			Log("Mission.Missile #" + missileIndex + " terminated by native collision reaction " + ((object)(*(Mission.MissileCollisionReaction*)(&reaction))/*cast due to constrained. prefix*/).ToString() + " via " + source + ".");
			if (_state == State.Guiding && _trackedMissiles.Count == 0)
			{
				HandleGuidedSwarmTerminal("AllMissilesCollisionTerminal/" + ((object)(*(Mission.MissileCollisionReaction*)(&reaction))/*cast due to constrained. prefix*/).ToString());
			}
		}
	}

	private static bool IsAgentPenetrationOverrideEnabled()
	{
		return GlobalSettings<Settings>.Instance?.EnablePenetrationOverride ?? false;
	}

	private static bool HasRemainingAgentPenetration(TrackedMissile tracked)
	{
		if (tracked == null || !IsAgentPenetrationOverrideEnabled())
		{
			return false;
		}
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance != null && instance.InfiniteAgentPenetrations)
		{
			return true;
		}
		int num = Math.Max(0, Math.Min(100, GlobalSettings<Settings>.Instance?.MaximumAgentPenetrations ?? 0));
		return tracked.PenetrationsUsed < num;
	}

	private void QueuePenetrationContinuation(TrackedMissile tracked, PendingCollisionContext collisionContext)
	{
		if (tracked != null && collisionContext != null)
		{
			_pendingContinuationSpawns.Add(new PendingContinuationSpawn
			{
				Source = tracked,
				Collision = collisionContext,
				WasCameraOwner = (tracked.Index == _cameraMissileIndex),
				WasFormationLeader = (tracked.Index == _missileIndex),
				PenetrationsUsed = tracked.PenetrationsUsed + 1
			});
			AbandonNativePresentationHandlesAfterImpact(tracked);
			RemoveTrackedMissile(tracked, removeVisual: true);
		}
	}

	private void QueueNativeMissileRemoval(TrackedMissile tracked)
	{
		if (tracked != null)
		{
			_pendingNativeMissileRemovals.Add(new PendingNativeMissileRemoval
			{
				Index = tracked.Index,
				IdentityEntity = tracked.IdentityEntity,
				Shooter = tracked.OriginalShooter,
				ShotGeneration = tracked.ShotGeneration
			});
		}
	}

	private void ProcessDeferredNativeMissileWork()
	{
		if (((MissionBehavior)this).Mission == null)
		{
			_pendingContinuationSpawns.Clear();
			_pendingNativeMissileRemovals.Clear();
			return;
		}
		for (int num = _pendingNativeMissileRemovals.Count - 1; num >= 0; num--)
		{
			PendingNativeMissileRemoval pendingNativeMissileRemoval = _pendingNativeMissileRemovals[num];
			_pendingNativeMissileRemovals.RemoveAt(num);
			if (pendingNativeMissileRemoval != null && pendingNativeMissileRemoval.ShotGeneration == _activeShotGeneration && pendingNativeMissileRemoval.Shooter == _activeShotShooter)
			{
				Mission.Missile val = FindExactLiveMissileForDeferredRemoval(pendingNativeMissileRemoval);
				if (val != null)
				{
					try
					{
						((MissionBehavior)this).Mission.RemoveMissileAsClient(((MBMissile)val).Index);
					}
					catch (Exception ex)
					{
						Log("Deferred native missile removal failed: " + ex.GetType().Name + ".");
					}
				}
			}
		}
		int num2 = 0;
		while (num2 < _pendingContinuationSpawns.Count)
		{
			PendingContinuationSpawn pendingContinuationSpawn = _pendingContinuationSpawns[num2];
			_pendingContinuationSpawns.RemoveAt(num2);
			if (pendingContinuationSpawn?.Source == null || pendingContinuationSpawn.Collision == null || pendingContinuationSpawn.Source.ShotGeneration != _activeShotGeneration || pendingContinuationSpawn.Source.OriginalShooter != _activeShotShooter)
			{
				continue;
			}
			if (!TrySpawnPenetrationContinuation(pendingContinuationSpawn.Source, pendingContinuationSpawn.Collision, out var continuation))
			{
				Log("Deferred penetration continuation could not be created.");
				continue;
			}
			continuation.PenetrationsUsed = pendingContinuationSpawn.PenetrationsUsed;
			_trackedMissiles.Add(continuation);
			if (pendingContinuationSpawn.WasFormationLeader || _missile == null)
			{
				_missile = continuation.Missile;
				_missileIndex = continuation.Index;
				continuation.FormationSlot = 0;
			}
			if (pendingContinuationSpawn.WasCameraOwner || _cameraMissileIndex < 0)
			{
				_cameraMissileIndex = continuation.Index;
			}
			ApplyFullDetailVisual(continuation);
			ApplyGuidedProjectileParticlePolicy(continuation);
			continuation.AwaitingCollisionReaction = false;
			Log("Created deferred controlled penetration continuation #" + continuation.Index + ".");
		}
		if (_trackedMissiles.Count > 0)
		{
			TrackedMissile leader = FindTrackedMissile(_missileIndex) ?? _trackedMissiles[0];
			PromoteFormationLeader(leader);
		}
	}

	private Mission.Missile FindExactLiveMissileForDeferredRemoval(PendingNativeMissileRemoval pending)
	{
		if (pending == null || ((MissionBehavior)this).Mission == null)
		{
			return null;
		}
		try
		{
			foreach (Mission.Missile item in (List<Mission.Missile>)(object)((MissionBehavior)this).Mission.MissilesList)
			{
				if (item != null && ((MBMissile)item).Index == pending.Index && item.ShooterAgent == pending.Shooter)
				{
					GameEntity val = null;
					try
					{
						val = item.Entity;
					}
					catch
					{
					}
					if (pending.IdentityEntity == (GameEntity)null || val == pending.IdentityEntity)
					{
						return item;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private void HandleAutoguidanceAfterAgentImpact(TrackedMissile tracked, Agent impactedVictim)
	{
		if (tracked != null && impactedVictim != null)
		{
			MarkAutoguidanceTargetConsumed(tracked, impactedVictim);
			if (tracked.GuidanceTarget == impactedVictim && !TryAdvanceAutoguidanceRoute(tracked, impactedVictim, impactConfirmed: true) && (!CollectAutoguidanceCandidates() || (!PlanAndAssignAutoguidanceRoute(tracked, requireUnused: true, null) && !PlanAndAssignAutoguidanceRoute(tracked, requireUnused: false, null))))
			{
				tracked.GuidanceTarget = null;
				tracked.GuidanceHeadBoneIndex = -1;
				tracked.GuidanceRouteTargets.Clear();
				tracked.GuidanceRouteReplanRequested = true;
				tracked.GuidanceNoProgressElapsed = 0f;
				_autoguidanceReacquireCountdown = 0f;
			}
		}
	}

	private bool TrySpawnPenetrationContinuation(TrackedMissile source, PendingCollisionContext collisionContext, out TrackedMissile continuation)
	{
		continuation = null;
		if (source == null || collisionContext == null || collisionContext.Victim == null || ((MissionBehavior)this).Mission == null || _activeShotShooter == null || !source.SpawnWeaponValid || source.ResolvedLaunchData == null)
		{
			return false;
		}
		Vec3 impactVelocity = collisionContext.ImpactVelocity;
		float speed = impactVelocity.Length;
		Vec3 direction = ((IsFinite(impactVelocity) && IsFinite(speed) && speed > 1E-06f) ? (impactVelocity / speed) : NormalizeSafe(collisionContext.ImpactDirection, source.GuidanceFallbackDirection));
		if (!IsFinite(direction) || direction.LengthSquared <= 0.0001f)
		{
			direction = NormalizeSafe(source.GuidanceFallbackDirection, _lastMissileDirection);
		}
		if (!IsFinite(speed) || speed <= 1E-06f)
		{
			float launchSpeed = source.GuidanceLaunchVelocity.Length;
			speed = ((IsFinite(launchSpeed) && launchSpeed > 1E-06f) ? launchSpeed : source.SpawnBaseSpeed);
		}
		if (!IsFinite(speed) || speed <= 1E-06f)
		{
			return false;
		}

		float exitDistance = 1.25f;
		try
		{
			Vec3 victimPosition = GetVisualPosition(collisionContext.Victim);
			float centreDepth = Dot(victimPosition - collisionContext.ImpactPosition, direction);
			if (IsFinite(centreDepth))
			{
				exitDistance = Clamp(centreDepth + 0.95f, 1f, 2.5f);
			}
		}
		catch
		{
		}

		Vec3 spawnPosition = collisionContext.ImpactPosition + direction * exitDistance;
		if (!IsFinite(spawnPosition))
		{
			return false;
		}
		Mat3 orientation = (source.SpawnOrientationValid ? source.SpawnOrientation : Mat3.Identity);
		float baseSpeed = ((source.SpawnBaseSpeed > 1E-06f) ? source.SpawnBaseSpeed : speed);
		Mission.Missile missile;
		try
		{
			using IDisposable damageOverride = MissileDamageBridge.OverrideNextSyntheticMissile(((MissionBehavior)this).Mission, _activeShotShooter, source.ResolvedLaunchData);
			if (damageOverride == null)
			{
				return false;
			}
			missile = ((MissionBehavior)this).Mission.AddCustomMissile(_activeShotShooter, source.SpawnWeapon, spawnPosition, direction, orientation, baseSpeed, speed, source.SpawnHasRigidBody, (MissionObject)null, -1);
		}
		catch (Exception ex)
		{
			Log("Penetration continuation spawn failed: " + ex.GetType().Name + ".");
			return false;
		}
		if (!ValidateSyntheticMissileEntity(missile, "penetration continuation"))
		{
			return false;
		}

		Vec3 continuationVelocity = direction * speed;
		try
		{
			MBAgentVisuals victimVisuals = collisionContext.Victim.AgentVisuals;
			GameEntity victimEntity = ((NativeObject)(object)victimVisuals != (NativeObject)null) ? victimVisuals.GetEntity() : null;
			if (victimEntity != (GameEntity)null)
			{
				missile.PassThroughEntity(victimEntity);
			}
			continuationVelocity = ((MBMissile)missile).GetVelocity();
		}
		catch
		{
		}

		continuation = CreateTrackedMissileFromSpawn(missile, source, spawnPosition, continuationVelocity, continuation: true);
		return continuation != null;
	}

	private void ReplaceTrackedMissileAfterContinuation(TrackedMissile source, TrackedMissile continuation)
	{
		if (source != null && continuation != null)
		{
			int num = _trackedMissiles.IndexOf(source);
			bool num2 = source.Index == _cameraMissileIndex;
			bool flag = source.Index == _missileIndex;
			RemovePendingCollisionContext(source.Index);
			RemoveFullDetailVisual(source);
			if (num >= 0)
			{
				_trackedMissiles[num] = continuation;
			}
			else
			{
				_trackedMissiles.Add(continuation);
			}
			if (num2)
			{
				_cameraMissileIndex = continuation.Index;
			}
			if (flag)
			{
				_missile = continuation.Missile;
				_missileIndex = continuation.Index;
			}
			ApplyFullDetailVisual(continuation);
			ApplyGuidedProjectileParticlePolicy(continuation);
			continuation.AwaitingCollisionReaction = false;
		}
	}

	private void RemoveNativeMissileSafely(int missileIndex)
	{
		try
		{
			Mission mission = ((MissionBehavior)this).Mission;
			if (mission != null)
			{
				mission.RemoveMissileAsClient(missileIndex);
			}
		}
		catch
		{
		}
	}

	private void ExpirePendingCollisionReactions()
	{
		if (_pendingCollisionContexts.Count == 0 && _earlyCollisionReactions.Count == 0)
		{
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		double num = Stopwatch.Frequency;
		for (int num2 = _pendingCollisionContexts.Count - 1; num2 >= 0; num2--)
		{
			PendingCollisionContext pendingCollisionContext = _pendingCollisionContexts[num2];
			if (pendingCollisionContext == null)
			{
				_pendingCollisionContexts.RemoveAt(num2);
			}
			else
			{
				double num3 = ((pendingCollisionContext.CreatedTimestamp > 0) ? ((double)(timestamp - pendingCollisionContext.CreatedTimestamp) / num) : double.MaxValue);
				if (double.IsNaN(num3) || double.IsInfinity(num3) || !(num3 >= 0.0) || !(num3 < 0.05000000074505806))
				{
					int missileIndex = pendingCollisionContext.MissileIndex;
					_pendingCollisionContexts.RemoveAt(num2);
					ResolveCollisionReaction(missileIndex, (Mission.MissileCollisionReaction)(-1), "CollisionReactionTimeout");
					if (_state != State.Guiding)
					{
						return;
					}
				}
			}
		}
		for (int num4 = _earlyCollisionReactions.Count - 1; num4 >= 0; num4--)
		{
			EarlyCollisionReaction earlyCollisionReaction = _earlyCollisionReactions[num4];
			double num5 = ((earlyCollisionReaction != null && earlyCollisionReaction.CreatedTimestamp > 0) ? ((double)(timestamp - earlyCollisionReaction.CreatedTimestamp) / num) : double.MaxValue);
			if (double.IsNaN(num5) || double.IsInfinity(num5) || num5 < 0.0 || num5 >= 0.05000000074505806)
			{
				_earlyCollisionReactions.RemoveAt(num4);
			}
		}
	}

	private void HandleGuidedSwarmTerminal(string source)
	{
		if ((_state == State.Guiding || _state == State.ImpactPending) && _pendingContinuationSpawns.Count <= 0 && _pendingNativeMissileRemovals.Count <= 0)
		{
			CloseSplitSiblingAcquisition("GuidedSwarmTerminal");
			_pendingShotSeeds.Clear();
			_pendingCollisionContexts.Clear();
			_earlyCollisionReactions.Clear();
			HideCrosshair();
			if (_deferredCinematicVictim != null)
			{
				CleanupTrackedMissiles(removeVisuals: true);
				BeginDeferredKillCinematic(source + "/KnownConfirmedKill");
			}
			else if (_pendingHitVictims.Count > 0)
			{
				CleanupTrackedMissiles(removeVisuals: true);
				_impactConfirmElapsed = 0f;
				_impactConfirmLastTimestamp = Stopwatch.GetTimestamp();
				_state = State.ImpactPending;
				ResetGuidanceTimeControl();
				RemoveOwnTimeRequest();
				_cameraFrameValid = false;
				_releaseCameraAfterOverride = false;
				ReleaseCustomCameraOwnership("ImpactPendingBookkeepingOnly");
				RestoreNativeCameraAfterGuidance();
				Log(source + "; awaiting terminal kill confirmation with no guided missile and native camera restored.");
			}
			else
			{
				BeginReturn(source + "WithoutKill", fastReturn: true);
			}
		}
	}

	private TrackedMissile FindTrackedMissile(int index)
	{
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			if (_trackedMissiles[i].Index == index)
			{
				return _trackedMissiles[i];
			}
		}
		return null;
	}

	private void RemoveTrackedMissile(TrackedMissile tracked, bool removeVisual)
	{
		if (tracked != null)
		{
			if (tracked.ParticleDiscoveryLockedAfterImpact)
			{
				tracked.ScaledParticleEntities.Clear();
			}
			else
			{
				RestoreGuidedProjectileParticleScale(tracked);
			}
			RemovePendingCollisionContext(tracked.Index);
			MissileDamageBridge.Forget(((MissionBehavior)this).Mission, tracked.Index);
			if (removeVisual)
			{
				RemoveFullDetailVisual(tracked);
			}
			_trackedMissiles.Remove(tracked);
			if (_trackedMissiles.Count > 0)
			{
				TrackedMissile trackedMissile = ((tracked.Index == _missileIndex) ? _trackedMissiles[0] : FindTrackedMissile(_missileIndex));
				PromoteFormationLeader(trackedMissile ?? _trackedMissiles[0]);
			}
			else
			{
				_missile = null;
				_missileIndex = -1;
			}
		}
	}

	private void CleanupTrackedMissiles(bool removeVisuals)
	{
		if (removeVisuals)
		{
			for (int i = 0; i < _trackedMissiles.Count; i++)
			{
				RemoveFullDetailVisual(_trackedMissiles[i]);
			}
		}
		for (int j = 0; j < _trackedMissiles.Count; j++)
		{
			MissileDamageBridge.Forget(((MissionBehavior)this).Mission, _trackedMissiles[j].Index);
		}
		_trackedMissiles.Clear();
		_pendingCollisionContexts.Clear();
		_earlyCollisionReactions.Clear();
		_pendingContinuationSpawns.Clear();
		_pendingNativeMissileRemovals.Clear();
		_missile = null;
		_missileIndex = -1;
		_cameraMissileIndex = -1;
	}

	private bool TryGetSwarmCameraAnchor(out Vec3 position, out Vec3 forward, out float movingCount)
	{
		int movingCount2;
		float lateralRadius;
		float depthExtent;
		bool result = TryGetSwarmCameraData(out position, out forward, out movingCount2, out lateralRadius, out depthExtent);
		movingCount = movingCount2;
		return result;
	}

	private static void AbandonNativePresentationHandlesAfterImpact(TrackedMissile tracked)
	{
		if (tracked != null)
		{
			tracked.ScaledParticleEntities.Clear();
			tracked.HiddenNativeEntities.Clear();
			tracked.VisualSourceEntity = null;
			tracked.NativeEntity = null;
		}
	}

	private void ApplyGuidedProjectileParticlePolicy(TrackedMissile tracked)
	{
		if (tracked == null)
		{
			return;
		}
		tracked.ProjectileParticlePolicyCountdown -= 0.016f;
		if (tracked.ProjectileParticlePolicyCountdown > 0f)
		{
			return;
		}
		tracked.ProjectileParticlePolicyCountdown = 0.2f;
		int num = GlobalSettings<Settings>.Instance?.GuidedProjectileParticleEffectMode ?? 1;
		bool flag = GlobalSettings<Settings>.Instance?.FrameAllControlledSplitProjectiles ?? true;
		bool flag2 = num >= 2 || (num == 1 && (tracked.Index == _cameraMissileIndex || flag));
		float num2 = (flag2 ? Clamp(GlobalSettings<Settings>.Instance?.GuidedProjectileFlightEffectScale ?? 0.25f, 0f, 1f) : 1f);
		GameEntity val = null;
		try
		{
			Mission.Missile missile = tracked.Missile;
			val = ((missile != null) ? missile.Entity : null);
		}
		catch
		{
			val = null;
		}
		if (val == (GameEntity)null)
		{
			return;
		}
		if (!flag2 || num2 >= 0.999f)
		{
			RestoreGuidedProjectileParticleScale(tracked);
			tracked.AppliedProjectileParticleScale = 1f;
			return;
		}
		if (!tracked.ParticleDiscoveryLockedAfterImpact)
		{
			ScaleFlightParticleEntitiesRecursive(tracked, val, val, num2);
		}
		tracked.AppliedProjectileParticleScale = num2;
	}

	private static ParticleScaleState FindParticleScaleState(TrackedMissile tracked, GameEntity entity)
	{
		if (tracked == null || entity == (GameEntity)null)
		{
			return null;
		}
		for (int i = 0; i < tracked.ScaledParticleEntities.Count; i++)
		{
			ParticleScaleState particleScaleState = tracked.ScaledParticleEntities[i];
			if (particleScaleState != null && particleScaleState.Entity == entity)
			{
				return particleScaleState;
			}
		}
		return null;
	}

	private void ScaleFlightParticleEntitiesRecursive(TrackedMissile tracked, GameEntity root, GameEntity entity, float scale)
	{
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		if (tracked == null || entity == (GameEntity)null)
		{
			return;
		}
		if (HasParticleSystemComponent(entity))
		{
			ParticleScaleState particleScaleState = FindParticleScaleState(tracked, entity);
			if (particleScaleState == null)
			{
				particleScaleState = new ParticleScaleState
				{
					Entity = entity
				};
				try
				{
					particleScaleState.OriginalLocalFrame = entity.GetLocalFrame();
					particleScaleState.OriginalFrameValid = true;
				}
				catch
				{
					particleScaleState.OriginalFrameValid = false;
				}
				tracked.ScaledParticleEntities.Add(particleScaleState);
			}
			if (scale <= 0.001f)
			{
				try
				{
					entity.PauseParticleSystem(true);
					particleScaleState.PausedByGuidedArrow = true;
				}
				catch
				{
				}
			}
			else
			{
				if (particleScaleState.PausedByGuidedArrow)
				{
					try
					{
						entity.ResumeParticleSystem(true);
					}
					catch
					{
					}
					particleScaleState.PausedByGuidedArrow = false;
				}
				if (entity != root && particleScaleState.OriginalFrameValid)
				{
					try
					{
						MatrixFrame originalLocalFrame = particleScaleState.OriginalLocalFrame;
						ref Vec3 s = ref originalLocalFrame.rotation.s;
						s *= scale;
						ref Vec3 f = ref originalLocalFrame.rotation.f;
						f *= scale;
						ref Vec3 u = ref originalLocalFrame.rotation.u;
						u *= scale;
						entity.SetLocalFrame(ref originalLocalFrame, true);
					}
					catch
					{
					}
				}
			}
		}
		int num = 0;
		try
		{
			num = entity.ChildCount;
		}
		catch
		{
			num = 0;
		}
		for (int i = 0; i < num; i++)
		{
			try
			{
				ScaleFlightParticleEntitiesRecursive(tracked, root, entity.GetChild(i), scale);
			}
			catch
			{
			}
		}
	}

	private static bool HasParticleSystemComponent(GameEntity entity)
	{
		if (entity == (GameEntity)null)
		{
			return false;
		}
		try
		{
			MethodInfo method = ((object)entity).GetType().GetMethod("GetComponentCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (method == null)
			{
				return false;
			}
			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum)
			{
				return false;
			}
			object obj = Enum.Parse(parameters[0].ParameterType, "ParticleSystem", ignoreCase: true);
			object obj2 = method.Invoke(entity, new object[1] { obj });
			return obj2 is int && (int)obj2 > 0;
		}
		catch
		{
			return false;
		}
	}

	private static void RestoreGuidedProjectileParticleScale(TrackedMissile tracked)
	{
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		if (tracked == null)
		{
			return;
		}
		for (int num = tracked.ScaledParticleEntities.Count - 1; num >= 0; num--)
		{
			ParticleScaleState particleScaleState = tracked.ScaledParticleEntities[num];
			if (particleScaleState?.Entity == (GameEntity)null)
			{
				tracked.ScaledParticleEntities.RemoveAt(num);
			}
			else
			{
				try
				{
					if (particleScaleState.PausedByGuidedArrow)
					{
						particleScaleState.Entity.ResumeParticleSystem(true);
					}
					if (particleScaleState.OriginalFrameValid)
					{
						MatrixFrame originalLocalFrame = particleScaleState.OriginalLocalFrame;
						particleScaleState.Entity.SetLocalFrame(ref originalLocalFrame, true);
					}
				}
				catch
				{
				}
				particleScaleState.PausedByGuidedArrow = false;
			}
		}
	}

	private void ApplyFullDetailVisual(TrackedMissile tracked)
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		if (tracked == null)
		{
			return;
		}
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance != null && !instance.UseFullDetailProjectileModels)
		{
			return;
		}
		Mission mission = ((MissionBehavior)this).Mission;
		if ((NativeObject)(object)((mission != null) ? mission.Scene : null) == (NativeObject)null)
		{
			return;
		}
		try
		{
			Mission.Missile missile = tracked.Missile;
			GameEntity val = ((missile != null) ? missile.Entity : null);
			Mission.Missile missile2 = tracked.Missile;
			object obj;
			if (missile2 == null)
			{
				obj = null;
			}
			else
			{
				MissionWeapon weapon = missile2.Weapon;
				obj = weapon.Item;
			}
			string text = ((obj != null) ? ((ItemObject)obj).MultiMeshName : null);
			if (val == (GameEntity)null || string.IsNullOrEmpty(text))
			{
				return;
			}
			GameEntity val2 = FindFirstNativeMeshEntity(val);
			if (val2 == (GameEntity)null)
			{
				Log("Full-detail visual skipped because no native missile mesh entity was found for #" + tracked.Index + ".");
				return;
			}
			MetaMesh copy = MetaMesh.GetCopy(text, false, true);
			if (!((NativeObject)(object)copy == (NativeObject)null) && copy.IsValid)
			{
				GameEntity val3 = GameEntity.CreateEmpty(((MissionBehavior)this).Mission.Scene, true, true, true);
				if (!(val3 == (GameEntity)null))
				{
					ConfigureFullDetailPresentationEntity(val3);
					val3.AddMultiMesh(copy, true);
					tracked.NativeEntity = val;
					tracked.VisualSourceEntity = val2;
					tracked.VisualEntity = val3;
					tracked.FullDetailMesh = copy;
					UpdateFullDetailVisual(tracked);
					CaptureAndHideNativeMeshEntities(val, tracked.HiddenNativeEntities);
					Log("Safely overlaid full-detail mesh '" + text + "' and hid " + tracked.HiddenNativeEntities.Count + " native render entity/entities for #" + tracked.Index + ".");
				}
			}
		}
		catch (Exception ex)
		{
			Log("Full-detail projectile presentation unavailable: " + ex.GetType().Name);
			RemoveFullDetailVisual(tracked);
		}
	}

	private static void ConfigureFullDetailPresentationEntity(GameEntity visualEntity)
	{
		if (visualEntity == (GameEntity)null)
		{
			return;
		}
		try
		{
			Type type = ((object)visualEntity).GetType();
			MethodInfo method = type.GetMethod("SetAsNotEffectedBySeason", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
			if (method != null)
			{
				method.Invoke(visualEntity, null);
			}
			MethodInfo method2 = type.GetMethod("SetForceNotAffectedBySeason", BindingFlags.Instance | BindingFlags.Public, null, new Type[1] { typeof(bool) }, null);
			if (method2 != null)
			{
				method2.Invoke(visualEntity, new object[1] { true });
			}
		}
		catch
		{
		}
	}

	private static GameEntity FindFirstNativeMeshEntity(GameEntity entity)
	{
		if (entity == (GameEntity)null)
		{
			return null;
		}
		try
		{
			if (entity.MultiMeshComponentCount > 0)
			{
				return entity;
			}
		}
		catch
		{
		}
		int num = 0;
		try
		{
			num = entity.ChildCount;
		}
		catch
		{
			num = 0;
		}
		for (int i = 0; i < num; i++)
		{
			try
			{
				GameEntity val = FindFirstNativeMeshEntity(entity.GetChild(i));
				if (val != (GameEntity)null)
				{
					return val;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static void CaptureAndHideNativeMeshEntities(GameEntity entity, List<NativeVisibilityState> states)
	{
		if (entity == (GameEntity)null || states == null)
		{
			return;
		}
		bool wasVisible = true;
		try
		{
			wasVisible = entity.GetVisibilityExcludeParents();
		}
		catch
		{
		}
		states.Add(new NativeVisibilityState
		{
			Entity = entity,
			WasVisible = wasVisible
		});
		ForceEntityRenderHidden(entity);
		int num = 0;
		try
		{
			num = entity.ChildCount;
		}
		catch
		{
			num = 0;
		}
		for (int i = 0; i < num; i++)
		{
			try
			{
				CaptureAndHideNativeMeshEntities(entity.GetChild(i), states);
			}
			catch
			{
			}
		}
	}

	private static void ForceEntityRenderHidden(GameEntity entity)
	{
		if (entity == (GameEntity)null)
		{
			return;
		}
		try
		{
			entity.SetVisibilityExcludeParents(false);
		}
		catch
		{
		}
	}

	private static void KeepNativeProjectileHidden(TrackedMissile tracked)
	{
		if (tracked == null)
		{
			return;
		}
		for (int i = 0; i < tracked.HiddenNativeEntities.Count; i++)
		{
			NativeVisibilityState nativeVisibilityState = tracked.HiddenNativeEntities[i];
			if (nativeVisibilityState?.Entity != (GameEntity)null)
			{
				ForceEntityRenderHidden(nativeVisibilityState.Entity);
			}
		}
	}

	private void UpdateFullDetailVisuals()
	{
		for (int num = _trackedMissiles.Count - 1; num >= 0; num--)
		{
			TrackedMissile tracked = _trackedMissiles[num];
			ApplyGuidedProjectileParticlePolicy(tracked);
			UpdateFullDetailVisual(tracked);
		}
	}

	private void UpdateFullDetailVisual(TrackedMissile tracked)
	{
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		if (tracked?.VisualEntity == (GameEntity)null || tracked.VisualSourceEntity == (GameEntity)null)
		{
			return;
		}
		KeepNativeProjectileHidden(tracked);
		bool flag = tracked.Index == _cameraMissileIndex && (GlobalSettings<Settings>.Instance?.ProjectileCameraMode ?? 1) == 0;
		try
		{
			tracked.VisualEntity.SetVisibilityExcludeParents(!flag);
		}
		catch
		{
		}
		if (flag)
		{
			return;
		}
		try
		{
			MatrixFrame globalFrame = tracked.VisualSourceEntity.GetGlobalFrame();
			if (IsFinite(globalFrame.origin))
			{
				Mission.Missile missile = tracked.Missile;
				Vec3 flightForward = NormalizeSafe((missile != null) ? ((MBMissile)missile).GetVelocity() : Vec3.Zero, _lastMissileDirection);
				MatrixFrame val = BuildDetailedProjectileFrame(globalFrame, flightForward);
				tracked.VisualEntity.SetGlobalFrame(ref val, true);
			}
		}
		catch
		{
			try
			{
				if (tracked.NativeEntity != (GameEntity)null)
				{
					MatrixFrame globalFrame2 = tracked.NativeEntity.GetGlobalFrame();
					Mission.Missile missile2 = tracked.Missile;
					Vec3 value = ((missile2 != null) ? ((MBMissile)missile2).GetVelocity() : Vec3.Zero);
					GameEntity visualEntity = tracked.VisualEntity;
					MatrixFrame val2 = BuildDetailedProjectileFrame(globalFrame2, NormalizeSafe(value, _lastMissileDirection));
					visualEntity.SetGlobalFrame(ref val2, true);
				}
			}
			catch
			{
			}
		}
	}

	private static MatrixFrame BuildDetailedProjectileFrame(MatrixFrame nativeFrame, Vec3 flightForward)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0111: Unknown result type (might be due to invalid IL or missing references)
		//IL_0112: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_011a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		flightForward = NormalizeSafe(flightForward, new Vec3(0f, 1f, 0f, -1f));
		Vec3 val = flightForward;
		Vec3 val2 = nativeFrame.rotation.u - val * Dot(nativeFrame.rotation.u, val);
		if (!IsFinite(val2) || val2.LengthSquared <= 1E-06f)
		{
			val2 = WorldUp - val * Dot(WorldUp, val);
		}
		if (!IsFinite(val2) || val2.LengthSquared <= 1E-06f)
		{
			val2 = nativeFrame.rotation.f - val * Dot(nativeFrame.rotation.f, val);
		}
		val2 = NormalizeSafe(val2, new Vec3(0f, 0f, 1f, -1f));
		Vec3 val3 = NormalizeSafe(Cross(val2, val), nativeFrame.rotation.s);
		val2 = NormalizeSafe(Cross(val, val3), val2);
		Mat3 identity = Mat3.Identity;
		identity.s = val3;
		identity.f = val2;
		identity.u = val;
		return new MatrixFrame(ref identity, ref nativeFrame.origin);
	}

	private void RemoveFullDetailVisual(TrackedMissile tracked)
	{
		if (tracked == null)
		{
			return;
		}
		for (int i = 0; i < tracked.HiddenNativeEntities.Count; i++)
		{
			NativeVisibilityState nativeVisibilityState = tracked.HiddenNativeEntities[i];
			if (!(nativeVisibilityState?.Entity == (GameEntity)null))
			{
				try
				{
					nativeVisibilityState.Entity.SetVisibilityExcludeParents(nativeVisibilityState.WasVisible);
				}
				catch
				{
				}
			}
		}
		tracked.HiddenNativeEntities.Clear();
		try
		{
			if (tracked.VisualEntity != (GameEntity)null)
			{
				tracked.VisualEntity.Remove(0);
			}
		}
		catch
		{
			try
			{
				if (tracked.VisualEntity != (GameEntity)null && (NativeObject)(object)tracked.FullDetailMesh != (NativeObject)null)
				{
					tracked.VisualEntity.RemoveMultiMesh(tracked.FullDetailMesh);
				}
			}
			catch
			{
			}
		}
		tracked.FullDetailMesh = null;
		tracked.VisualEntity = null;
		tracked.VisualSourceEntity = null;
		tracked.NativeEntity = null;
	}

	private Vec3 GetCinematicFocus(Agent victim)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		Vec3 ragdollVisualPosition = GetRagdollVisualPosition(victim);
		if (TryGetBoneWorldPosition(victim, _cinematicCollisionBoneIndex, out var position))
		{
			return Lerp(ragdollVisualPosition, position, 0.72f);
		}
		GameEntity cinematicArrowEntity = _cinematicArrowEntity;
		if (cinematicArrowEntity != (GameEntity)null)
		{
			try
			{
				Vec3 globalPosition = cinematicArrowEntity.GlobalPosition;
				if (IsFinite(globalPosition))
				{
					Vec3 result = globalPosition - ragdollVisualPosition;
					if (result.LengthSquared <= 25f)
					{
						result = Lerp(ragdollVisualPosition, globalPosition, 0.6f);
						return result;
					}
				}
			}
			catch
			{
			}
		}
		return ragdollVisualPosition;
	}

	private void ResetCinematicBoneAnchor()
	{
		_cinematicCollisionBoneIndex = -1;
	}

	private static Vec3 GetRagdollVisualPosition(Agent victim)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		if (victim == null)
		{
			return Vec3.Zero;
		}
		int monsterBoneIndex = GetMonsterBoneIndex(victim, "SpineUpperBoneIndex");
		int monsterBoneIndex2 = GetMonsterBoneIndex(victim, "PelvisBoneIndex");
		Vec3 position;
		bool flag = TryGetBoneWorldPosition(victim, monsterBoneIndex, out position);
		Vec3 position2;
		bool flag2 = TryGetBoneWorldPosition(victim, monsterBoneIndex2, out position2);
		if (flag && flag2)
		{
			return Lerp(position2, position, 0.58f);
		}
		if (flag)
		{
			return position;
		}
		if (flag2)
		{
			return position2;
		}
		return GetVisualPosition(victim) + WorldUp * 0.95f;
	}

	private static int GetMonsterBoneIndex(Agent victim, string propertyName)
	{
		try
		{
			object obj = ((victim != null) ? victim.Monster : null);
			object obj2 = (obj?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public))?.GetValue(obj, null);
			if (obj2 != null)
			{
				return Convert.ToInt32(obj2);
			}
		}
		catch
		{
		}
		return -1;
	}

	private static bool TryGetBoneEntitialFrame(Skeleton skeleton, int boneIndex, out MatrixFrame frame)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		frame = default(MatrixFrame);
		if ((NativeObject)(object)skeleton == (NativeObject)null || boneIndex < 0)
		{
			return false;
		}
		try
		{
			Type type = ((object)skeleton).GetType();
			MethodInfo methodInfo = _cachedBoneFrameMethod;
			if (methodInfo == null || _cachedBoneFrameSkeletonType != type)
			{
				methodInfo = null;
				MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public);
				foreach (MethodInfo methodInfo2 in methods)
				{
					if ((!(methodInfo2.Name != "GetBoneEntitialFrame") || !(methodInfo2.Name != "GetBoneEntitialFrameWithIndex")) && methodInfo2.GetParameters().Length == 1)
					{
						methodInfo = methodInfo2;
						break;
					}
				}
				_cachedBoneFrameSkeletonType = type;
				_cachedBoneFrameMethod = methodInfo;
			}
			if (methodInfo == null)
			{
				return false;
			}
			ParameterInfo[] parameters = methodInfo.GetParameters();
			object obj = Convert.ChangeType(boneIndex, parameters[0].ParameterType);
			if (!(methodInfo.Invoke(skeleton, new object[1] { obj }) is MatrixFrame val))
			{
				return false;
			}
			frame = val;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetBoneWorldPosition(Agent victim, int boneIndex, out Vec3 position)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0058: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		position = Vec3.Zero;
		if (victim == null || boneIndex < 0)
		{
			return false;
		}
		try
		{
			MBAgentVisuals agentVisuals = victim.AgentVisuals;
			Skeleton val = ((agentVisuals != null) ? agentVisuals.GetSkeleton() : null);
			if ((NativeObject)(object)agentVisuals == (NativeObject)null || (NativeObject)(object)val == (NativeObject)null)
			{
				return false;
			}
			if (!TryGetBoneEntitialFrame(val, boneIndex, out var frame))
			{
				return false;
			}
			MatrixFrame globalFrame = agentVisuals.GetGlobalFrame();
			position = TransformEntitialPointToWorld(globalFrame, frame.origin);
			return IsFinite(position);
		}
		catch
		{
		}
		return false;
	}

	private CinematicSubjectRecord FindCinematicSubject(Agent agent)
	{
		if (agent == null)
		{
			return null;
		}
		for (int i = 0; i < _cinematicSubjects.Count; i++)
		{
			CinematicSubjectRecord cinematicSubjectRecord = _cinematicSubjects[i];
			if (cinematicSubjectRecord != null && cinematicSubjectRecord.Agent == agent)
			{
				return cinematicSubjectRecord;
			}
		}
		return null;
	}

	private void TrackCinematicSubject(Agent agent, Vec3 impactPosition)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		if (agent == null)
		{
			return;
		}
		CinematicSubjectRecord cinematicSubjectRecord = FindCinematicSubject(agent);
		Vec3 val = Vec3.Zero;
		bool flag = false;
		try
		{
			Vec3 ragdollVisualPosition = GetRagdollVisualPosition(agent);
			if (IsFinite(ragdollVisualPosition))
			{
				val = ragdollVisualPosition;
				flag = true;
			}
		}
		catch
		{
		}
		if (!flag && IsFinite(impactPosition))
		{
			val = impactPosition;
			flag = true;
		}
		if (cinematicSubjectRecord != null)
		{
			if (flag)
			{
				cinematicSubjectRecord.LastKnownPosition = val;
				cinematicSubjectRecord.HasLastKnownPosition = true;
			}
		}
		else
		{
			_cinematicSubjects.Add(new CinematicSubjectRecord
			{
				Agent = agent,
				LastKnownPosition = (flag ? val : Vec3.Zero),
				HasLastKnownPosition = flag,
				LastSamplePosition = Vec3.Zero,
				LastSampleValid = false,
				ConfirmedKill = false
			});
		}
	}

	private bool MarkCinematicSubjectKilled(Agent agent)
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		if (agent == null)
		{
			return false;
		}
		CinematicSubjectRecord cinematicSubjectRecord = FindCinematicSubject(agent);
		if (cinematicSubjectRecord == null)
		{
			TrackCinematicSubject(agent, _impactPosition);
			cinematicSubjectRecord = FindCinematicSubject(agent);
		}
		if (cinematicSubjectRecord == null || cinematicSubjectRecord.ConfirmedKill)
		{
			return false;
		}
		cinematicSubjectRecord.ConfirmedKill = true;
		_confirmedCinematicKillCount++;
		SnapshotCinematicSubject(agent);
		return true;
	}

	private void SnapshotCinematicSubject(Agent agent)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		CinematicSubjectRecord cinematicSubjectRecord = FindCinematicSubject(agent);
		if (cinematicSubjectRecord == null)
		{
			return;
		}
		try
		{
			Vec3 ragdollVisualPosition = GetRagdollVisualPosition(agent);
			if (IsFinite(ragdollVisualPosition))
			{
				cinematicSubjectRecord.LastKnownPosition = ragdollVisualPosition;
				cinematicSubjectRecord.HasLastKnownPosition = true;
			}
		}
		catch
		{
		}
	}

	private bool TryGetCinematicSubjectPosition(CinematicSubjectRecord record, out Vec3 position)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		position = Vec3.Zero;
		if (record == null)
		{
			return false;
		}
		Agent agent = record.Agent;
		if (agent != null)
		{
			try
			{
				Vec3 ragdollVisualPosition = GetRagdollVisualPosition(agent);
				if (IsFinite(ragdollVisualPosition))
				{
					record.LastKnownPosition = ragdollVisualPosition;
					record.HasLastKnownPosition = true;
					position = ragdollVisualPosition;
					return true;
				}
			}
			catch
			{
			}
		}
		if (record.HasLastKnownPosition && IsFinite(record.LastKnownPosition))
		{
			position = record.LastKnownPosition;
			return true;
		}
		return false;
	}

	private Agent FindReplacementCinematicVictim(Agent excluded)
	{
		for (int num = _cinematicSubjects.Count - 1; num >= 0; num--)
		{
			CinematicSubjectRecord cinematicSubjectRecord = _cinematicSubjects[num];
			if (cinematicSubjectRecord != null && cinematicSubjectRecord.ConfirmedKill && cinematicSubjectRecord.Agent != null && cinematicSubjectRecord.Agent != excluded)
			{
				return cinematicSubjectRecord.Agent;
			}
		}
		return null;
	}

	private void ResetCinematicSubjects()
	{
		_cinematicSubjects.Clear();
		_confirmedCinematicKillCount = 0;
	}

	private PendingHitRecord FindPendingHitVictim(Agent victim)
	{
		if (victim == null)
		{
			return null;
		}
		for (int i = 0; i < _pendingHitVictims.Count; i++)
		{
			PendingHitRecord pendingHitRecord = _pendingHitVictims[i];
			if (pendingHitRecord != null && pendingHitRecord.Victim == victim)
			{
				return pendingHitRecord;
			}
		}
		return null;
	}

	private void TrackHitVictim(Agent victim, int missileIndex, int collisionBoneIndex, GameEntity arrowEntity, Vec3 impactDirection, Vec3 impactPosition)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Expected O, but got Unknown
		if (victim == null)
		{
			return;
		}
		TrackCinematicSubject(victim, impactPosition);
		_hitVictim = victim;
		PendingHitRecord pendingHitRecord = FindPendingHitVictim(victim);
		if (pendingHitRecord != null)
		{
			pendingHitRecord.MissileIndex = missileIndex;
			pendingHitRecord.CollisionBoneIndex = collisionBoneIndex;
			pendingHitRecord.ArrowEntity = arrowEntity;
			pendingHitRecord.ImpactDirection = impactDirection;
			pendingHitRecord.ImpactPosition = impactPosition;
			return;
		}
		PendingHitRecord item = new PendingHitRecord
		{
			Victim = victim,
			MissileIndex = missileIndex,
			CollisionBoneIndex = collisionBoneIndex,
			ArrowEntity = arrowEntity,
			ImpactDirection = impactDirection,
			ImpactPosition = impactPosition
		};
		try
		{
			victim.OnAgentHealthChanged += new Agent.OnAgentHealthChangedDelegate(OnHitVictimHealthChanged);
			_pendingHitVictims.Add(item);
		}
		catch
		{
		}
	}

	private void RemoveTrackedVictim(Agent victim)
	{
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Expected O, but got Unknown
		if (victim == null)
		{
			return;
		}
		for (int num = _pendingHitVictims.Count - 1; num >= 0; num--)
		{
			PendingHitRecord pendingHitRecord = _pendingHitVictims[num];
			if (pendingHitRecord != null && pendingHitRecord.Victim == victim)
			{
				_pendingHitVictims.RemoveAt(num);
				try
				{
					victim.OnAgentHealthChanged -= new Agent.OnAgentHealthChangedDelegate(OnHitVictimHealthChanged);
				}
				catch
				{
				}
				break;
			}
		}
		if (_hitVictim == victim)
		{
			_hitVictim = ((_pendingHitVictims.Count <= 0) ? null : _pendingHitVictims[_pendingHitVictims.Count - 1]?.Victim);
		}
	}

	private void ResetTrackedVictim()
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Expected O, but got Unknown
		for (int i = 0; i < _pendingHitVictims.Count; i++)
		{
			Agent val = _pendingHitVictims[i]?.Victim;
			try
			{
				if (val != null)
				{
					val.OnAgentHealthChanged -= new Agent.OnAgentHealthChangedDelegate(OnHitVictimHealthChanged);
				}
			}
			catch
			{
			}
		}
		_pendingHitVictims.Clear();
		_hitVictim = null;
	}

	private void OnHitVictimHealthChanged(Agent agent, float oldHealth, float newHealth)
	{
		if ((_state == State.Guiding || _state == State.ImpactPending) && agent != null && FindPendingHitVictim(agent) != null && IsFinite(oldHealth) && IsFinite(newHealth) && !(oldHealth <= 0f) && newHealth <= 0f)
		{
			HandleConfirmedKill(agent, "OnAgentHealthChanged");
		}
	}

	private void ConfirmRemovalKillFallback(Agent affectedAgent, Agent affectorAgent, AgentState state, string source)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Invalid comparison between Unknown and I4
		if ((_state == State.Guiding || _state == State.ImpactPending) && affectedAgent != null && FindPendingHitVictim(affectedAgent) != null && (int)state == 4 && (affectorAgent == null || affectorAgent == _activeShotShooter))
		{
			HandleConfirmedKill(affectedAgent, source);
		}
	}

	private void HandleConfirmedKill(Agent victim, string source)
	{
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0157: Unknown result type (might be due to invalid IL or missing references)
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Unknown result type (might be due to invalid IL or missing references)
		if ((_state != State.Guiding && _state != State.ImpactPending && _state != State.Cinematic) || victim == null)
		{
			return;
		}
		bool flag = false;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			TrackedMissile trackedMissile = _trackedMissiles[i];
			if (trackedMissile != null && trackedMissile.GuidanceTarget == victim)
			{
				ClearAutoguidanceTarget(trackedMissile);
				flag = true;
			}
		}
		if (flag)
		{
			_autoguidanceReacquireCountdown = 0f;
		}
		PendingHitRecord pendingHitRecord = FindPendingHitVictim(victim);
		if (pendingHitRecord != null)
		{
			_cinematicCollisionBoneIndex = pendingHitRecord.CollisionBoneIndex;
			_cinematicArrowEntity = pendingHitRecord.ArrowEntity;
			_impactDirection = pendingHitRecord.ImpactDirection;
			_impactPosition = pendingHitRecord.ImpactPosition;
		}
		if (!MarkCinematicSubjectKilled(victim))
		{
			RemoveTrackedVictim(victim);
			Log("Duplicate confirmed-kill callback ignored via " + source + ".");
			return;
		}
		RemoveTrackedVictim(victim);
		if (_state == State.Cinematic)
		{
			_cinematicVictim = victim;
			_cinematicElapsed = Math.Max(_cinematicElapsed, 0f);
			Log("Active cinematic updated with an additional confirmed kill via " + source + ".");
			return;
		}
		if ((GlobalSettings<Settings>.Instance?.CinematicTriggerMode ?? 0) <= 0)
		{
			BeginKillCinematic(victim, source + "/FirstKill");
			return;
		}
		_deferredCinematicVictim = victim;
		_deferredCinematicArrowEntity = _cinematicArrowEntity;
		_deferredCinematicCollisionBoneIndex = _cinematicCollisionBoneIndex;
		_deferredImpactDirection = _impactDirection;
		_deferredImpactPosition = _impactPosition;
		if (_state == State.ImpactPending)
		{
			BeginDeferredKillCinematic(source + "/TerminalConfirmed");
		}
		else
		{
			Log("Confirmed penetration kill deferred only while the exact native projectile continues via " + source + ".");
		}
	}

	private void BeginDeferredKillCinematic(string source)
	{
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		Agent deferredCinematicVictim = _deferredCinematicVictim;
		if (deferredCinematicVictim != null)
		{
			_cinematicArrowEntity = _deferredCinematicArrowEntity;
			_cinematicCollisionBoneIndex = _deferredCinematicCollisionBoneIndex;
			_impactDirection = _deferredImpactDirection;
			_impactPosition = _deferredImpactPosition;
			ClearDeferredCinematicKill();
			BeginKillCinematic(deferredCinematicVictim, source + "/DeferredKill");
		}
	}

	private void ClearDeferredCinematicKill()
	{
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		_deferredCinematicVictim = null;
		_deferredCinematicArrowEntity = null;
		_deferredCinematicCollisionBoneIndex = -1;
		_deferredImpactDirection = Vec3.Zero;
		_deferredImpactPosition = Vec3.Zero;
	}

	private void BeginKillCinematic(Agent victim, string source)
	{
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Unknown result type (might be due to invalid IL or missing references)
		if (victim == null)
		{
			return;
		}
		if (_state == State.Cinematic)
		{
			_cinematicVictim = victim;
			Log("Existing kill cinematic retained; primary victim updated via " + source + ".");
		}
		else if (_state == State.Guiding || _state == State.ImpactPending)
		{
			_state = State.Cinematic;
			CloseSplitSiblingAcquisition("KillCinematicStarted");
			_pendingShotSeeds.Clear();
			_pendingCollisionContexts.Clear();
			_earlyCollisionReactions.Clear();
			ResetTrackedVictim();
			ClearDeferredCinematicKill();
			HideCrosshair();
			_cinematicVictim = victim;
			CleanupTrackedMissiles(removeVisuals: true);
			_cinematicElapsed = 0f;
			_cinematicLastTimestamp = Stopwatch.GetTimestamp();
			_impactConfirmElapsed = 0f;
			_restPollCountdown = 0f;
			_lastRestPositionValid = false;
			_settledElapsed = 0f;
			_cinematicSawActiveRagdoll = false;
			_returnDurationOverride = 0f;
			if (!IsFinite(_impactDirection) || _impactDirection.LengthSquared <= 1E-06f)
			{
				_impactDirection = _lastMissileDirection;
			}
			ResetGuidanceTimeControl();
			RemoveOwnTimeRequest();
			SetCinematicTimeSpeed(GlobalSettings<Settings>.Instance?.CinematicTimeSpeed ?? 0.1f);
			InitializeCinematicCamera(victim);
			Log("Kill cinematic started via " + source + ".");
		}
	}

	private void InitializeCinematicCamera(Agent victim)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		if (victim == null)
		{
			return;
		}
		try
		{
			Vec3 cinematicFocus = GetCinematicFocus(victim);
			Vec3 val = NormalizeSafe(_impactDirection, _lastMissileDirection);
			Vec3 val2 = NormalizeSafe(val - WorldUp * Dot(val, WorldUp), new Vec3(0f, 1f, 0f, -1f));
			float num = Clamp(GlobalSettings<Settings>.Instance?.CinematicCameraDistance ?? 4f, 1.5f, 12f);
			float num2 = Clamp(GlobalSettings<Settings>.Instance?.CinematicElevationAngle ?? 18f, -20f, 60f) * ((float)Math.PI / 180f);
			Vec3 val3 = -val2;
			Vec3 val4 = cinematicFocus + val3 * (num * (float)Math.Cos(num2)) + WorldUp * (num * (float)Math.Sin(num2));
			Vec3 viewForward = NormalizeSafe(cinematicFocus - val4, val);
			_cameraFrame = MakeCameraFrame(val4, viewForward, WorldUp);
			_cameraFrameValid = true;
			SetMissionCamera(_cameraFrame);
		}
		catch
		{
		}
	}

	private void GetAdaptiveCinematicFraming(Agent primaryVictim, out Vec3 center, out float cameraDistance)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0165: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_0167: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_0176: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_018a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fa: Unknown result type (might be due to invalid IL or missing references)
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0126: Unknown result type (might be due to invalid IL or missing references)
		//IL_012c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d3: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01db: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e2: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0227: Unknown result type (might be due to invalid IL or missing references)
		//IL_0237: Unknown result type (might be due to invalid IL or missing references)
		float num = Clamp(GlobalSettings<Settings>.Instance?.CinematicCameraDistance ?? 4f, 1.5f, 12f);
		center = GetCinematicFocus(primaryVictim);
		cameraDistance = num;
		if (_confirmedCinematicKillCount <= 1 || _cinematicSubjects.Count <= 1)
		{
			return;
		}
		bool flag = false;
		Vec3 val = Vec3.Zero;
		Vec3 val2 = Vec3.Zero;
		int num2 = 0;
		for (int i = 0; i < _cinematicSubjects.Count; i++)
		{
			CinematicSubjectRecord cinematicSubjectRecord = _cinematicSubjects[i];
			if (cinematicSubjectRecord != null && cinematicSubjectRecord.ConfirmedKill && TryGetCinematicSubjectPosition(cinematicSubjectRecord, out var position))
			{
				if (!flag)
				{
					val = position;
					val2 = position;
					flag = true;
				}
				else
				{
					val.x = Math.Min(val.x, position.x);
					val.y = Math.Min(val.y, position.y);
					val.z = Math.Min(val.z, position.z);
					val2.x = Math.Max(val2.x, position.x);
					val2.y = Math.Max(val2.y, position.y);
					val2.z = Math.Max(val2.z, position.z);
				}
				num2++;
			}
		}
		if (!flag || num2 <= 1)
		{
			return;
		}
		center = (val + val2) * 0.5f + WorldUp * 0.25f;
		float num3 = 0f;
		float num4 = 0f;
		for (int j = 0; j < _cinematicSubjects.Count; j++)
		{
			CinematicSubjectRecord cinematicSubjectRecord2 = _cinematicSubjects[j];
			if (cinematicSubjectRecord2 != null && cinematicSubjectRecord2.ConfirmedKill && TryGetCinematicSubjectPosition(cinematicSubjectRecord2, out var position2))
			{
				Vec3 val3 = position2 - center;
				float num5 = val3.x * val3.x + val3.y * val3.y;
				if (IsFinite(num5) && num5 > 1E-06f)
				{
					num3 = Math.Max(num3, (float)Math.Sqrt(num5));
				}
				if (IsFinite(val3.z))
				{
					num4 = Math.Max(num4, Math.Abs(val3.z));
				}
			}
		}
		float num6 = Clamp(GlobalSettings<Settings>.Instance?.CinematicMultiKillFramingPadding ?? 0.85f, 0.25f, 3f);
		float val4 = (num3 + num6) * 1.35f;
		float val5 = (num4 + num6) * 1.75f;
		float val6 = Math.Max(val4, val5);
		float val7 = Clamp(GlobalSettings<Settings>.Instance?.CinematicMultiKillMaximumDistance ?? 30f, 2f, 60f);
		float max = Math.Max(num, val7);
		cameraDistance = Clamp(Math.Max(num, val6), num, max);
	}

	private void TickCinematicDisplay(float dt)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Unknown result type (might be due to invalid IL or missing references)
		//IL_011d: Unknown result type (might be due to invalid IL or missing references)
		//IL_011e: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_0145: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0151: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Unknown result type (might be due to invalid IL or missing references)
		//IL_0154: Unknown result type (might be due to invalid IL or missing references)
		//IL_0159: Unknown result type (might be due to invalid IL or missing references)
		//IL_015b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_0162: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0166: Unknown result type (might be due to invalid IL or missing references)
		//IL_016b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0170: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a1: Unknown result type (might be due to invalid IL or missing references)
		EnsureCinematicTimeSpeed();
		Agent cinematicVictim = _cinematicVictim;
		if (cinematicVictim == null)
		{
			BeginReturn("NoCinematicVictim");
			return;
		}
		float cinematicRealDelta = GetCinematicRealDelta(dt);
		_cinematicElapsed += cinematicRealDelta;
		Vec3 center;
		float cameraDistance;
		try
		{
			GetAdaptiveCinematicFraming(cinematicVictim, out center, out cameraDistance);
		}
		catch
		{
			BeginReturn("VictimPositionUnavailable");
			return;
		}
		Vec3 val = NormalizeSafe(_impactDirection, _lastMissileDirection);
		Vec3 val2 = NormalizeSafe(val - WorldUp * Dot(val, WorldUp), new Vec3(0f, 1f, 0f, -1f));
		float num = Clamp(GlobalSettings<Settings>.Instance?.CinematicOrbitSpeed ?? 32f, -120f, 120f) * ((float)Math.PI / 180f);
		float num2 = Clamp(GlobalSettings<Settings>.Instance?.CinematicElevationAngle ?? 18f, -20f, 60f) * ((float)Math.PI / 180f);
		Vec3 val3 = -val2;
		Vec3 val4 = NormalizeSafe(RotateAroundAxis(val3, WorldUp, num * _cinematicElapsed), val3);
		Vec3 val5 = center + val4 * (cameraDistance * (float)Math.Cos(num2)) + WorldUp * (cameraDistance * (float)Math.Sin(num2));
		Vec3 viewForward = NormalizeSafe(center - val5, val);
		MatrixFrame desired = MakeCameraFrame(val5, viewForward, WorldUp);
		float positionRate = ((_confirmedCinematicKillCount > 1) ? 6f : 14f);
		float rotationRate = ((_confirmedCinematicKillCount > 1) ? 10f : 16f);
		ApplySmoothedCamera(desired, cinematicRealDelta, positionRate, rotationRate);
		int num3 = GlobalSettings<Settings>.Instance?.CinematicMode ?? 2;
		if (num3 <= 0)
		{
			float num4 = Clamp(GlobalSettings<Settings>.Instance?.FixedCinematicDuration ?? 1.5f, 0.1f, 10f);
			if (_cinematicElapsed >= num4)
			{
				BeginReturn("FixedCinematicComplete");
			}
			return;
		}
		float adaptiveCinematicMaximumDuration = GetAdaptiveCinematicMaximumDuration();
		if (_cinematicElapsed >= adaptiveCinematicMaximumDuration)
		{
			BeginReturn("AdaptiveCinematicMaximumDuration");
		}
		else if (num3 == 1)
		{
			TickSettledMode(cinematicVictim, cinematicRealDelta);
		}
		else
		{
			TickFullFinalizationMode(cinematicVictim, cinematicRealDelta);
		}
	}

	private float GetCinematicRealDelta(float fallback)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long cinematicLastTimestamp = _cinematicLastTimestamp;
		_cinematicLastTimestamp = timestamp;
		if (cinematicLastTimestamp <= 0 || timestamp <= cinematicLastTimestamp)
		{
			return Clamp(fallback, 0f, 0.1f);
		}
		double num = (double)(timestamp - cinematicLastTimestamp) / (double)Stopwatch.Frequency;
		if (double.IsNaN(num) || double.IsInfinity(num))
		{
			return Clamp(fallback, 0f, 0.1f);
		}
		return Clamp((float)num, 0f, 0.1f);
	}

	private float GetAdaptiveCinematicMinimumDuration()
	{
		return Clamp(GlobalSettings<Settings>.Instance?.AdaptiveCinematicMinimumDuration ?? 1f, 0.25f, 3f);
	}

	private float GetAdaptiveCinematicMaximumDuration()
	{
		float adaptiveCinematicMinimumDuration = GetAdaptiveCinematicMinimumDuration();
		float val = Clamp(GlobalSettings<Settings>.Instance?.AdaptiveCinematicMaximumDuration ?? 2.5f, 1f, 8f);
		return Math.Max(adaptiveCinematicMinimumDuration, val);
	}

	private void TickSettledMode(Agent victim, float dt)
	{
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		//IL_014a: Unknown result type (might be due to invalid IL or missing references)
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0154: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		_restPollCountdown -= dt;
		if (_restPollCountdown > 0f)
		{
			return;
		}
		_restPollCountdown = 0.1f;
		float num = Clamp(GlobalSettings<Settings>.Instance?.SettledMotionThreshold ?? 0.025f, 0.002f, 0.2f);
		Vec3 val;
		if (_confirmedCinematicKillCount > 1)
		{
			bool flag = false;
			bool flag2 = true;
			for (int i = 0; i < _cinematicSubjects.Count; i++)
			{
				CinematicSubjectRecord cinematicSubjectRecord = _cinematicSubjects[i];
				if (cinematicSubjectRecord == null || !cinematicSubjectRecord.ConfirmedKill || !TryGetCinematicSubjectPosition(cinematicSubjectRecord, out var position))
				{
					continue;
				}
				flag = true;
				if (cinematicSubjectRecord.LastSampleValid)
				{
					val = position - cinematicSubjectRecord.LastSamplePosition;
					float length = val.Length;
					if (!IsFinite(length) || length > num)
					{
						flag2 = false;
					}
				}
				else
				{
					flag2 = false;
				}
				cinematicSubjectRecord.LastSamplePosition = position;
				cinematicSubjectRecord.LastSampleValid = true;
			}
			if (!flag)
			{
				BeginReturn("SettledVictimsUnavailable");
				return;
			}
			if (flag2)
			{
				_settledElapsed += 0.1f;
			}
			else
			{
				_settledElapsed = 0f;
			}
		}
		else
		{
			Vec3 ragdollVisualPosition;
			try
			{
				ragdollVisualPosition = GetRagdollVisualPosition(victim);
			}
			catch
			{
				BeginReturn("SettledVictimUnavailable");
				return;
			}
			if (_lastRestPositionValid)
			{
				val = ragdollVisualPosition - _lastRestPosition;
				if (val.Length <= num)
				{
					_settledElapsed += 0.1f;
				}
				else
				{
					_settledElapsed = 0f;
				}
			}
			_lastRestPosition = ragdollVisualPosition;
			_lastRestPositionValid = true;
		}
		float num2 = Clamp(GlobalSettings<Settings>.Instance?.SettledHoldTime ?? 0.5f, 0.1f, 3f);
		float adaptiveCinematicMinimumDuration = GetAdaptiveCinematicMinimumDuration();
		if (_cinematicElapsed >= adaptiveCinematicMinimumDuration && _settledElapsed >= num2)
		{
			BeginReturn("CorpseSettled");
		}
	}

	private void TickFullFinalizationMode(Agent victim, float dt)
	{
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Invalid comparison between Unknown and I4
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Invalid comparison between Unknown and I4
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Invalid comparison between Unknown and I4
		//IL_00b8: Unknown result type (might be due to invalid IL or missing references)
		float num = Clamp(GlobalSettings<Settings>.Instance?.FullCinematicTimeout ?? 30f, 1f, 60f);
		if (_cinematicElapsed >= num)
		{
			BeginReturn("FullModeFailsafe");
			return;
		}
		_restPollCountdown -= dt;
		if (_restPollCountdown > 0f)
		{
			return;
		}
		_restPollCountdown = 0.1f;
		try
		{
			MBAgentVisuals agentVisuals = victim.AgentVisuals;
			Skeleton val = ((agentVisuals != null) ? agentVisuals.GetSkeleton() : null);
			if (!((NativeObject)(object)val == (NativeObject)null))
			{
				RagdollState currentRagdollState = val.GetCurrentRagdollState();
				if ((int)currentRagdollState == 2 || (int)currentRagdollState == 3)
				{
					_cinematicSawActiveRagdoll = true;
				}
				else if ((int)currentRagdollState == 4 && _cinematicElapsed >= GetAdaptiveCinematicMinimumDuration())
				{
					BeginReturn("NativeCorpseFinalizationBoundary");
				}
				else if ((int)currentRagdollState == 0 && _cinematicElapsed >= GetAdaptiveCinematicMinimumDuration() && (_cinematicSawActiveRagdoll || _cinematicElapsed >= 0.75f))
				{
					BeginReturn("NativeCorpseFinalizedBetweenSamples");
				}
			}
		}
		catch
		{
		}
	}

	private void BeginReturn(string reason, bool fastReturn = false)
	{
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		if (_state != State.Idle && _state != State.Returning)
		{
			ResetTrackedVictim();
			_pendingShotSeeds.Clear();
			_pendingCollisionContexts.Clear();
			_earlyCollisionReactions.Clear();
			HideCrosshair();
			CleanupTrackedMissiles(removeVisuals: true);
			ClearDeferredCinematicKill();
			_cinematicVictim = null;
			_cinematicArrowEntity = null;
			ResetCinematicBoneAnchor();
			RemoveOwnTimeRequest();
			ResetGuidanceTimeControl();
			_returnDurationOverride = (fastReturn ? Clamp(GlobalSettings<Settings>.Instance?.MissReturnDuration ?? 0.1f, 0.02f, 0.5f) : 0f);
			if (_cameraFrameValid)
			{
				_returnStartFrame = _cameraFrame;
				_returnElapsed = 0f;
				_releaseCameraAfterOverride = false;
				_state = State.Returning;
				Log("Return flight started: " + reason + ".");
			}
			else
			{
				_state = State.Idle;
				ResumeNativeCrosshairViews();
				Log("Returned without custom camera: " + reason + ".");
			}
		}
	}

	private void TickReturnDisplay(float dt)
	{
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		Mission mission = ((MissionBehavior)this).Mission;
		Agent val = ((mission != null) ? mission.MainAgent : null);
		if (val == null)
		{
			ResetAll(behaviorRemoving: false);
			return;
		}
		float num = ((_returnDurationOverride > 0f) ? _returnDurationOverride : Clamp(GlobalSettings<Settings>.Instance?.ReturnDuration ?? 0.32f, 0.08f, 2f));
		_returnElapsed += dt;
		float num2 = Clamp(_returnElapsed / num, 0f, 1f);
		MatrixFrame missionCamera = BlendFrames(t: SmoothStep(num2), b: BuildReturnTargetFrame(val), a: _returnStartFrame);
		SetMissionCamera(missionCamera);
		if (num2 >= 1f)
		{
			_releaseCustomCameraNextDisplay = true;
		}
	}

	private void CaptureReturnPose(Agent player)
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_0091: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_0105: Unknown result type (might be due to invalid IL or missing references)
		//IL_010a: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_0116: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_011c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Unknown result type (might be due to invalid IL or missing references)
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		//IL_0128: Unknown result type (might be due to invalid IL or missing references)
		//IL_012d: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		if (((MissionBehavior)this).Mission != null && player != null)
		{
			MatrixFrame val2;
			try
			{
				MissionScreen val = ResolveMissionScreen();
				val2 = (((NativeObject)(object)((val != null) ? val.CombatCamera : null) != (NativeObject)null) ? val.CombatCamera.Frame : ((MissionBehavior)this).Mission.GetCameraFrame());
			}
			catch
			{
				return;
			}
			Vec3 val3 = NormalizeSafe(player.LookDirection, new Vec3(0f, 1f, 0f, -1f));
			Vec3 val4 = NormalizeSafe(Cross(val3, WorldUp), new Vec3(1f, 0f, 0f, -1f));
			Vec3 val5 = NormalizeSafe(Cross(val4, val3), WorldUp);
			Vec3 val6 = player.Position + WorldUp * 1.45f;
			Vec3 a = val2.origin - val6;
			_returnLocalRight = Dot(a, val4);
			_returnLocalForward = Dot(a, val3);
			_returnLocalUp = Dot(a, val5);
			_returnViewForwardLocal = ToLocal(-val2.rotation.u, val4, val3, val5);
			_returnViewUpLocal = ToLocal(val2.rotation.f, val4, val3, val5);
		}
	}

	private MatrixFrame BuildReturnTargetFrame(Agent player)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		Vec3 val = NormalizeSafe(player.LookDirection, new Vec3(0f, 1f, 0f, -1f));
		Vec3 val2 = NormalizeSafe(Cross(val, WorldUp), new Vec3(1f, 0f, 0f, -1f));
		Vec3 val3 = NormalizeSafe(Cross(val2, val), WorldUp);
		Vec3 position = player.Position + WorldUp * 1.45f + val2 * _returnLocalRight + val * _returnLocalForward + val3 * _returnLocalUp;
		Vec3 viewForward = FromLocal(_returnViewForwardLocal, val2, val, val3);
		Vec3 upHint = FromLocal(_returnViewUpLocal, val2, val, val3);
		return MakeCameraFrame(position, viewForward, upHint);
	}

	private void ApplySmoothedCamera(MatrixFrame desired, float dt, float positionRate, float rotationRate)
	{
		//IL_0062: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ed: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		if (!_cameraFrameValid)
		{
			_cameraFrame = desired;
			_cameraFrameValid = true;
			SetMissionCamera(_cameraFrame);
			return;
		}
		float t = 1f - (float)Math.Exp((0f - positionRate) * Math.Max(0f, dt));
		float t2 = 1f - (float)Math.Exp((0f - rotationRate) * Math.Max(0f, dt));
		Vec3 position = Lerp(_cameraFrame.origin, desired.origin, t);
		Vec3 viewForward = NormalizeSafe(Lerp(-_cameraFrame.rotation.u, -desired.rotation.u, t2), -desired.rotation.u);
		Vec3 upHint = NormalizeSafe(Lerp(_cameraFrame.rotation.f, desired.rotation.f, t2), desired.rotation.f);
		_cameraFrame = MakeCameraFrame(position, viewForward, upHint);
		SetMissionCamera(_cameraFrame);
	}

	private void SetMissionCamera(MatrixFrame frame)
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		if (((MissionBehavior)this).Mission == null)
		{
			return;
		}
		_cameraFrame = frame;
		_cameraFrameValid = true;
		try
		{
			MissionScreen val = ResolveMissionScreen();
			if (val != null)
			{
				EnsureCustomCameraOwnership(val);
				if ((NativeObject)(object)_ownedCustomCamera != (NativeObject)null)
				{
					_ownedCustomCamera.Frame = frame;
				}
			}
			((MissionBehavior)this).Mission.SetCameraFrame(ref frame, 1f);
		}
		catch (Exception ex)
		{
			Log("Camera frame submission failed: " + ex.GetType().Name);
		}
	}

	private MissionScreen ResolveMissionScreen()
	{
		try
		{
			if (((MissionView)this).MissionScreen != null)
			{
				return ((MissionView)this).MissionScreen;
			}
			ScreenBase topScreen = ScreenManager.TopScreen;
			return (MissionScreen)(object)((topScreen is MissionScreen) ? topScreen : null);
		}
		catch
		{
			return null;
		}
	}

	private void AcquireCustomCameraOwnership()
	{
		MissionScreen val = ResolveMissionScreen();
		if (val == null)
		{
			if (!_cameraOwnershipFailureLogged)
			{
				_cameraOwnershipFailureLogged = true;
				Log("Camera ownership unavailable: active MissionScreen not found.");
			}
		}
		else
		{
			EnsureCustomCameraOwnership(val);
		}
	}

	private void EnsureCustomCameraOwnership(MissionScreen screen)
	{
		if (screen == null)
		{
			return;
		}
		if (!_ownsCustomCamera)
		{
			_previousCustomCamera = screen.CustomCamera;
			if ((NativeObject)(object)_ownedCustomCamera == (NativeObject)null)
			{
				_ownedCustomCamera = Camera.CreateCamera();
			}
			Camera val = _previousCustomCamera ?? screen.CombatCamera;
			if ((NativeObject)(object)val != (NativeObject)null)
			{
				_ownedCustomCamera.FillParametersFrom(val);
			}
			_ownsCustomCamera = true;
			_cameraOwnershipFailureLogged = false;
			Log("Camera ownership acquired through MissionScreen.CustomCamera.");
		}
		if (screen.CustomCamera != _ownedCustomCamera)
		{
			screen.CustomCamera = _ownedCustomCamera;
		}
	}

	private void ReleaseCustomCameraOwnership(string reason)
	{
		if (!_ownsCustomCamera)
		{
			return;
		}
		try
		{
			MissionScreen val = ResolveMissionScreen();
			if (val != null && val.CustomCamera == _ownedCustomCamera)
			{
				val.CustomCamera = _previousCustomCamera;
			}
		}
		catch
		{
		}
		_ownsCustomCamera = false;
		_previousCustomCamera = null;
		Log("Camera ownership released: " + reason + ".");
	}

	private void RestoreNativeCameraAfterGuidance()
	{
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			Mission mission = ((MissionBehavior)this).Mission;
			Agent val = ((mission != null) ? mission.MainAgent : null);
			MissionScreen val2 = ResolveMissionScreen();
			if (val != null && val2 != null)
			{
				MatrixFrame frame = BuildReturnTargetFrame(val);
				if ((NativeObject)(object)val2.CombatCamera != (NativeObject)null)
				{
					val2.CombatCamera.Frame = frame;
				}
				((MissionBehavior)this).Mission.SetCameraFrame(ref frame, 1f);
				_cameraFrameValid = false;
				Log("Native combat camera explicitly restored after guidance release.");
			}
		}
		catch (Exception ex)
		{
			Log("Native camera restore failed: " + ex.GetType().Name);
		}
	}

	private void InitializeGuidanceTimeControl()
	{
		ResetGuidanceTimeControl();
		_guidanceLastTimestamp = Stopwatch.GetTimestamp();
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance == null || instance.EnableProximityTimeDilation)
		{
			float speed = (_proximityTargetSpeed = (_proximityCurrentSpeed = Clamp(instance?.ProximityFarTimeSpeed ?? 1f, SpeedSteps[0], 1f)));
			_proximityScanCountdown = 0f;
			ApplyAutomaticTimeSpeed(speed);
			Log("Proximity Time Dilation started at " + speed.ToString("0.00") + "x.");
		}
		else
		{
			float ownTimeSpeed = (_proximityTargetSpeed = (_proximityCurrentSpeed = Clamp(instance?.InitialGuidanceTimeSpeed ?? 0.15f, SpeedSteps[0], 1f)));
			SetOwnTimeSpeed(ownTimeSpeed);
			Log("Legacy fixed guidance time speed started at " + ownTimeSpeed.ToString("0.00") + "x.");
		}
	}

	private void ResetGuidanceTimeControl()
	{
		((List<Agent>)(object)_proximityCandidates).Clear();
		_proximityScanCountdown = 0f;
		_proximityTargetSpeed = 1f;
		_proximityCurrentSpeed = 1f;
		_closestProximityDistance = float.PositiveInfinity;
		_closestProximityEnemy = null;
		_proximityLiveMissileSpeed = 0f;
		_manualTimeOverride = false;
		_guidanceLastTimestamp = 0L;
	}

	private float GetGuidanceRealDelta(float fallback)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long guidanceLastTimestamp = _guidanceLastTimestamp;
		_guidanceLastTimestamp = timestamp;
		if (guidanceLastTimestamp <= 0 || timestamp <= guidanceLastTimestamp)
		{
			return Clamp(fallback, 0f, 0.1f);
		}
		double num = (double)(timestamp - guidanceLastTimestamp) / (double)Stopwatch.Frequency;
		if (double.IsNaN(num) || double.IsInfinity(num))
		{
			return Clamp(fallback, 0f, 0.1f);
		}
		return Clamp((float)num, 0f, 0.1f);
	}

	private void UpdateGuidanceTimeControl(Vec3 swarmAnchor, float realDt)
	{
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		if (_manualTimeOverride)
		{
			return;
		}
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance == null || instance.EnableProximityTimeDilation)
		{
			_proximityScanCountdown -= Math.Max(0f, realDt);
			if (_proximityScanCountdown <= 0f)
			{
				float proximityScanCountdown = Clamp(GlobalSettings<Settings>.Instance?.ProximityScanInterval ?? 0.05f, 0.02f, 0.25f);
				_proximityScanCountdown = proximityScanCountdown;
				RefreshClosestProximityEnemy(swarmAnchor);
				_proximityLiveMissileSpeed = GetLiveSwarmMissileSpeed();
				_proximityTargetSpeed = ComputeProximityTargetSpeed(_closestProximityDistance, _proximityLiveMissileSpeed);
			}
			float num = Clamp(GlobalSettings<Settings>.Instance?.ProximityReferenceMissileSpeed ?? 70f, 10f, 250f);
			float num2 = ((_proximityLiveMissileSpeed > num) ? Clamp(_proximityLiveMissileSpeed / num, 1f, 3f) : 1f);
			float num3 = Clamp(GlobalSettings<Settings>.Instance?.ProximityTimeResponseRate ?? 12f, 1f, 30f) * num2;
			float num4 = 1f - (float)Math.Exp((0f - num3) * Math.Max(0f, realDt));
			_proximityCurrentSpeed += (_proximityTargetSpeed - _proximityCurrentSpeed) * num4;
			_proximityCurrentSpeed = Clamp(_proximityCurrentSpeed, 0.01f, 1f);
			ApplyAutomaticTimeSpeed(_proximityCurrentSpeed);
		}
	}

	private void RefreshClosestProximityEnemy(Vec3 swarmAnchor)
	{
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c1: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_01c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_018f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0194: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
		_closestProximityDistance = float.PositiveInfinity;
		_closestProximityEnemy = null;
		((List<Agent>)(object)_proximityCandidates).Clear();
		Mission mission = ((MissionBehavior)this).Mission;
		Agent val = ((mission != null) ? mission.MainAgent : null);
		Team val2 = ((val != null) ? val.Team : null);
		if (mission == null || val == null || val2 == null || _trackedMissiles.Count == 0)
		{
			return;
		}
		float num = Clamp(GlobalSettings<Settings>.Instance?.ProximitySlowdownStartDistance ?? 45f, 5f, 150f);
		float num2 = 0f;
		Vec3 val3;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			try
			{
				Vec3 position = ((MBMissile)_trackedMissiles[i].Missile).GetPosition();
				if (IsFinite(position))
				{
					val3 = position - swarmAnchor;
					float length = val3.Length;
					if (IsFinite(length) && length > num2)
					{
						num2 = length;
					}
				}
			}
			catch
			{
			}
		}
		float num3 = num + num2 + 3f;
		try
		{
			mission.GetNearbyEnemyAgents(new Vec2(swarmAnchor.x, swarmAnchor.y), num3, val2, _proximityCandidates);
		}
		catch
		{
			((List<Agent>)(object)_proximityCandidates).Clear();
			return;
		}
		float num4 = float.PositiveInfinity;
		for (int j = 0; j < ((List<Agent>)(object)_proximityCandidates).Count; j++)
		{
			Agent val4 = ((List<Agent>)(object)_proximityCandidates)[j];
			if (val4 == null || val4 == val)
			{
				continue;
			}
			try
			{
				if (!val4.IsActive() || val4.Health <= 0f)
				{
					continue;
				}
				goto IL_017e;
			}
			catch
			{
			}
			continue;
			IL_017e:
			Vec3 val5;
			try
			{
				val5 = GetVisualPosition(val4) + WorldUp * 0.85f;
			}
			catch
			{
				continue;
			}
			if (!IsFinite(val5))
			{
				continue;
			}
			for (int k = 0; k < _trackedMissiles.Count; k++)
			{
				try
				{
					val3 = ((MBMissile)_trackedMissiles[k].Missile).GetPosition() - val5;
					float lengthSquared = val3.LengthSquared;
					if (IsFinite(lengthSquared) && !(lengthSquared >= num4))
					{
						num4 = lengthSquared;
						_closestProximityEnemy = val4;
					}
				}
				catch
				{
				}
			}
		}
		if (_closestProximityEnemy != null && IsFinite(num4) && num4 >= 0f)
		{
			_closestProximityDistance = (float)Math.Sqrt(num4);
		}
	}

	private float GetLiveSwarmMissileSpeed()
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		float num = 0f;
		for (int i = 0; i < _trackedMissiles.Count; i++)
		{
			try
			{
				Vec3 velocity = ((MBMissile)_trackedMissiles[i].Missile).GetVelocity();
				float lengthSquared = velocity.LengthSquared;
				if (IsFinite(velocity) && IsFinite(lengthSquared) && !(lengthSquared <= 1E-06f))
				{
					float num2 = (float)Math.Sqrt(lengthSquared);
					if (IsFinite(num2) && num2 > num)
					{
						num = num2;
					}
				}
			}
			catch
			{
			}
		}
		return num;
	}

	private float ComputeProximityTargetSpeed(float distance, float missileSpeed)
	{
		float num = Clamp(GlobalSettings<Settings>.Instance?.ProximityFarTimeSpeed ?? 1f, 0.01f, 1f);
		float num2 = Clamp(GlobalSettings<Settings>.Instance?.ProximityNearTimeSpeed ?? 0.1f, 0.01f, num);
		float num3 = Clamp(GlobalSettings<Settings>.Instance?.ProximitySlowdownStartDistance ?? 45f, 5f, 150f);
		float num4 = Clamp(GlobalSettings<Settings>.Instance?.ProximityFullSlowdownDistance ?? 6f, 0.5f, num3 - 0.5f);
		if (!IsFinite(distance) || distance >= num3)
		{
			return num;
		}
		float num5;
		float num6;
		if (distance <= num4)
		{
			num5 = 1f;
			num6 = num2;
		}
		else
		{
			float num7 = Clamp((distance - num4) / Math.Max(0.5f, num3 - num4), 0f, 1f);
			float num8 = num7 * num7 * num7 * (num7 * (num7 * 6f - 15f) + 10f);
			num5 = 1f - num8;
			num6 = num2 + (num - num2) * num8;
		}
		float num9 = Clamp(GlobalSettings<Settings>.Instance?.ProximityReferenceMissileSpeed ?? 70f, 10f, 250f);
		float num10 = Clamp(GlobalSettings<Settings>.Instance?.ProximitySpeedCompensationStrength ?? 1f, 0f, 2f);
		if (!IsFinite(missileSpeed) || missileSpeed <= num9 || num10 <= 1E-06f)
		{
			return num6;
		}
		float num11 = Clamp(missileSpeed / num9, 1f, 12f);
		float num12 = Clamp((num11 - 1f) / (num11 + 1f) * num10, 0f, 1f) * num5;
		float num13 = Clamp(GlobalSettings<Settings>.Instance?.ProximityMaximumExtraSlowdown ?? 0.45f, 0f, 0.75f);
		float num14 = 1f - num13 * num12;
		return Clamp(num6 * num14, 0.01f, num);
	}

	private void ApplyAutomaticTimeSpeed(float speed)
	{
		float num = (float)Math.Round(Clamp(speed, 0.01f, 1f) * 100f) / 100f;
		if (num >= 0.995f)
		{
			if (_timeRequestActive)
			{
				RemoveGuidanceTimeRequest();
			}
		}
		else if (!_timeRequestActive || Math.Abs(_requestedSpeed - num) >= 0.005f)
		{
			SetOwnTimeSpeed(num);
		}
	}

	private void SetOwnTimeSpeed(float requested)
	{
		//IL_0053: Unknown result type (might be due to invalid IL or missing references)
		if (((MissionBehavior)this).Mission == null)
		{
			return;
		}
		float num = Clamp(requested, 0.01f, SpeedSteps[SpeedSteps.Length - 1]);
		if (_timeRequestActive && Math.Abs(num - _requestedSpeed) < 0.0001f)
		{
			return;
		}
		RemoveGuidanceTimeRequest();
		try
		{
			((MissionBehavior)this).Mission.AddTimeSpeedRequest(new Mission.TimeSpeedRequest(num, 1195463255));
			_timeRequestActive = true;
			_requestedSpeed = num;
		}
		catch
		{
			_timeRequestActive = false;
			_requestedSpeed = 1f;
		}
	}

	private void SetCinematicTimeSpeed(float requested)
	{
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		if (((MissionBehavior)this).Mission == null)
		{
			return;
		}
		float num = Clamp(requested, 0.01f, 1f);
		if (_cinematicTimeRequestActive && Math.Abs(_cinematicRequestedSpeed - num) < 0.0001f && !_timeRequestActive)
		{
			return;
		}
		RemoveGuidanceTimeRequest();
		RemoveCinematicTimeRequest();
		try
		{
			((MissionBehavior)this).Mission.AddTimeSpeedRequest(new Mission.TimeSpeedRequest(num, 1195459401));
			_cinematicTimeRequestActive = true;
			_cinematicRequestedSpeed = num;
		}
		catch
		{
			_cinematicTimeRequestActive = false;
			_cinematicRequestedSpeed = 1f;
		}
	}

	private void EnsureCinematicTimeSpeed()
	{
		if (_state == State.Cinematic && ((MissionBehavior)this).Mission != null)
		{
			float num = Clamp(GlobalSettings<Settings>.Instance?.CinematicTimeSpeed ?? 0.1f, 0.01f, 1f);
			if (!_cinematicTimeRequestActive || Math.Abs(_cinematicRequestedSpeed - num) >= 0.0001f)
			{
				SetCinematicTimeSpeed(num);
			}
		}
	}

	private void RemoveGuidanceTimeRequest()
	{
		if (((MissionBehavior)this).Mission != null)
		{
			try
			{
				((MissionBehavior)this).Mission.RemoveTimeSpeedRequest(1195463255);
			}
			catch
			{
			}
		}
		_timeRequestActive = false;
		_requestedSpeed = 1f;
	}

	private void RemoveCinematicTimeRequest()
	{
		if (((MissionBehavior)this).Mission != null)
		{
			try
			{
				((MissionBehavior)this).Mission.RemoveTimeSpeedRequest(1195459401);
			}
			catch
			{
			}
		}
		_cinematicTimeRequestActive = false;
		_cinematicRequestedSpeed = 1f;
	}

	private void RemoveOwnTimeRequest()
	{
		RemoveGuidanceTimeRequest();
		RemoveCinematicTimeRequest();
	}

	private void StepTimeSpeed(int direction)
	{
		if (!_manualTimeOverride)
		{
			_manualTimeOverride = true;
			Log("Q/E manual time-speed override activated for the current guided shot.");
		}
		float num = (_timeRequestActive ? _requestedSpeed : _proximityCurrentSpeed);
		int num2 = 0;
		float num3 = float.MaxValue;
		for (int i = 0; i < SpeedSteps.Length; i++)
		{
			float num4 = Math.Abs(SpeedSteps[i] - num);
			if (num4 < num3)
			{
				num3 = num4;
				num2 = i;
			}
		}
		int num5 = num2 + direction;
		if (num5 < 0)
		{
			num5 = 0;
		}
		if (num5 >= SpeedSteps.Length)
		{
			num5 = SpeedSteps.Length - 1;
		}
		SetOwnTimeSpeed(SpeedSteps[num5]);
	}

	private void ResetAll(bool behaviorRemoving)
	{
		//IL_00ca: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		ResetTrackedVictim();
		ResetCinematicSubjects();
		RemoveOwnTimeRequest();
		ResetGuidanceTimeControl();
		ResetAutoguidanceState(notify: false);
		HideCrosshair();
		CleanupTrackedMissiles(removeVisuals: true);
		_pendingShotSeeds.Clear();
		_pendingCollisionContexts.Clear();
		_earlyCollisionReactions.Clear();
		_queuedAlliedShots.Clear();
		_activeShotShooter = null;
		_activeShotGeneration = 0;
		_alliedTakeoverChainArmed = false;
		ClearDeferredCinematicKill();
		_cinematicVictim = null;
		_cinematicArrowEntity = null;
		ResetCinematicBoneAnchor();
		_state = State.Idle;
		_cameraFrameValid = false;
		_releaseCameraAfterOverride = false;
		_releaseCustomCameraNextDisplay = false;
		ReleaseCustomCameraOwnership(behaviorRemoving ? "BehaviorRemoved" : "Reset");
		ResumeNativeCrosshairViews();
		_pendingAcquireElapsed = 0f;
		_pendingShotOrientation = Mat3.Identity;
		_pendingShotHasRigidBody = false;
		_standaloneSplitSpawned = false;
		_standaloneSplitDamagePacketWaitLogged = false;
		_nativeSplitBatchDetected = false;
		_pendingContinuationSpawns.Clear();
		_pendingNativeMissileRemovals.Clear();
		_splitSiblingAcquireStartTimestamp = 0L;
		_splitSiblingAcquisitionClosed = true;
		_guidanceRealElapsed = 0f;
		_impactConfirmElapsed = 0f;
		_impactConfirmLastTimestamp = 0L;
		_pendingYawInput = 0f;
		_pendingPitchInput = 0f;
		_formationElapsed = 0f;
		_cameraMissileIndex = -1;
		_cinematicElapsed = 0f;
		_cinematicLastTimestamp = 0L;
		_returnElapsed = 0f;
		_returnDurationOverride = 0f;
		_restPollCountdown = 0f;
		_lastRestPositionValid = false;
		_settledElapsed = 0f;
		_cinematicSawActiveRagdoll = false;
		if (behaviorRemoving)
		{
			_requestedSpeed = 1f;
		}
	}

	private static Vec3 GetVisualPosition(Agent agent)
	{
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_002f: Unknown result type (might be due to invalid IL or missing references)
		if (agent == null)
		{
			return Vec3.Zero;
		}
		try
		{
			MBAgentVisuals agentVisuals = agent.AgentVisuals;
			if ((NativeObject)(object)agentVisuals != (NativeObject)null)
			{
				Vec3 origin = agentVisuals.GetGlobalFrame().origin;
				if (IsFinite(origin))
				{
					return origin;
				}
			}
		}
		catch
		{
		}
		try
		{
			return agent.Position;
		}
		catch
		{
			return Vec3.Zero;
		}
	}

	private static bool IsArrowOrBolt(Mission.Missile missile)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		if (missile == null)
		{
			return false;
		}
		try
		{
			string text = ExtractWeaponClassName(missile.Weapon);
			if (!string.IsNullOrEmpty(text))
			{
				return text.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Bolt", StringComparison.OrdinalIgnoreCase) >= 0;
			}
		}
		catch
		{
		}
		return false;
	}

	private static string ExtractWeaponClassName(object weapon)
	{
		if (weapon == null)
		{
			return null;
		}
		Type type = weapon.GetType();
		string text = TryReadWeaponClass(TryReadProperty(type, weapon, "CurrentUsageItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		object obj = TryReadProperty(type, weapon, "Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (obj != null)
		{
			text = TryReadWeaponClass(TryReadProperty(obj.GetType(), obj, "PrimaryWeapon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (!string.IsNullOrEmpty(text))
			{
				return text;
			}
			object obj2 = TryReadProperty(obj.GetType(), obj, "ItemType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (obj2 != null)
			{
				return obj2.ToString();
			}
		}
		return weapon.ToString();
	}

	private static string TryReadWeaponClass(object value, BindingFlags flags)
	{
		if (value == null)
		{
			return null;
		}
		return TryReadProperty(value.GetType(), value, "WeaponClass", flags)?.ToString();
	}

	private static object TryReadProperty(Type type, object instance, string name, BindingFlags flags)
	{
		try
		{
			return type.GetProperty(name, flags)?.GetValue(instance, null);
		}
		catch
		{
			return null;
		}
	}

	private static MatrixFrame BlendFrames(MatrixFrame a, MatrixFrame b, float t)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		Vec3 position = Lerp(a.origin, b.origin, t);
		Vec3 viewForward = NormalizeSafe(Lerp(-a.rotation.u, -b.rotation.u, t), -b.rotation.u);
		Vec3 upHint = NormalizeSafe(Lerp(a.rotation.f, b.rotation.f, t), b.rotation.f);
		return MakeCameraFrame(position, viewForward, upHint);
	}

	private static MatrixFrame MakeCameraFrame(Vec3 position, Vec3 viewForward, Vec3 upHint)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0092: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		viewForward = NormalizeSafe(viewForward, new Vec3(0f, 1f, 0f, -1f));
		Vec3 val = Cross(viewForward, upHint);
		if (!IsFinite(val) || val.LengthSquared <= 1E-06f)
		{
			val = Cross(viewForward, (Vec3)((Math.Abs(viewForward.z) > 0.9f) ? new Vec3(0f, 1f, 0f, -1f) : WorldUp));
		}
		val = NormalizeSafe(val, new Vec3(1f, 0f, 0f, -1f));
		Vec3 f = NormalizeSafe(Cross(val, viewForward), WorldUp);
		Mat3 identity = Mat3.Identity;
		identity.s = val;
		identity.f = f;
		identity.u = -viewForward;
		return new MatrixFrame(ref identity, ref position);
	}

	private static Vec3 RotateTowards(Vec3 current, Vec3 target, float maxAngle)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Unknown result type (might be due to invalid IL or missing references)
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_008b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_0096: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		current = NormalizeSafe(current, target);
		target = NormalizeSafe(target, current);
		float num = (float)Math.Acos(Clamp(Dot(current, target), -1f, 1f));
		if (num <= maxAngle || num <= 1E-05f)
		{
			return target;
		}
		Vec3 value = Cross(current, target);
		if (value.LengthSquared <= 1E-06f)
		{
			value = Cross(current, WorldUp);
			if (value.LengthSquared <= 1E-06f)
			{
				value = new Vec3(1f, 0f, 0f, -1f);
			}
		}
		return NormalizeSafe(RotateAroundAxis(current, NormalizeSafe(value, WorldUp), maxAngle), target);
	}

	private static Vec3 RotateAroundAxis(Vec3 vector, Vec3 axis, float angle)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_003a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		axis = NormalizeSafe(axis, WorldUp);
		float num = (float)Math.Cos(angle);
		float num2 = (float)Math.Sin(angle);
		return vector * num + Cross(axis, vector) * num2 + axis * (Dot(axis, vector) * (1f - num));
	}

	private static Vec3 Cross(Vec3 a, Vec3 b)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		return new Vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x, -1f);
	}

	private static float Dot(Vec3 a, Vec3 b)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		return a.x * b.x + a.y * b.y + a.z * b.z;
	}

	private static Vec3 ToLocal(Vec3 value, Vec3 right, Vec3 forward, Vec3 up)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		return new Vec3(Dot(value, right), Dot(value, forward), Dot(value, up), -1f);
	}

	private static Vec3 FromLocal(Vec3 local, Vec3 right, Vec3 forward, Vec3 up)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		return right * local.x + forward * local.y + up * local.z;
	}

	private static Vec3 NormalizeSafe(Vec3 value, Vec3 fallback)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		if (!IsFinite(value) || value.LengthSquared <= 1E-06f)
		{
			value = fallback;
		}
		float lengthSquared = value.LengthSquared;
		if (!IsFinite(lengthSquared) || lengthSquared <= 1E-06f)
		{
			return new Vec3(0f, 1f, 0f, -1f);
		}
		return value / (float)Math.Sqrt(lengthSquared);
	}

	private static Vec3 Lerp(Vec3 a, Vec3 b, float t)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		return a + (b - a) * t;
	}

	private static float SmoothStep(float t)
	{
		return t * t * (3f - 2f * t);
	}

	private static float Clamp(float v, float min, float max)
	{
		if (!(v < min))
		{
			if (!(v > max))
			{
				return v;
			}
			return max;
		}
		return min;
	}

	private static bool IsFinite(float v)
	{
		if (!float.IsNaN(v))
		{
			return !float.IsInfinity(v);
		}
		return false;
	}

	private static bool IsFinite(Vec3 v)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		if (IsFinite(v.x) && IsFinite(v.y))
		{
			return IsFinite(v.z);
		}
		return false;
	}

	private static void Log(string message)
	{
		Settings instance = GlobalSettings<Settings>.Instance;
		if (instance == null || !instance.DebugLogging)
		{
			return;
		}
		try
		{
			string logPath = LogPath;
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));
			File.AppendAllText(logPath, DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
		}
		catch
		{
		}
	}
}
