#!/usr/bin/env python3
"""Normalize the verified v1.1.17 core decompilation and apply v1.2.2 fixes.

The input tree must be produced from the repository's verified GuidedArrow.dll with
ILSpy's project export. This script is deterministic and intentionally contains no
TOR assembly references: native multi-projectile detection is callback-driven and
penetration continuation uses Bannerlord's own missile/entity APIs.
"""

from __future__ import annotations

import pathlib
import re
import sys


ON_AGENT_SHOOT_MISSILE = r'''	public override void OnAgentShootMissile(Agent shooterAgent, EquipmentIndex weaponIndex, Vec3 position, Vec3 velocity, Mat3 orientation, bool hasRigidBody, int forcedMissileIndex)
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
	}'''


IS_SPLIT_SIBLING_ACQUISITION_OPEN = r'''	private bool IsSplitSiblingAcquisitionOpen()
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
	}'''


TRY_SPAWN_PENETRATION_CONTINUATION = r'''	private bool TrySpawnPenetrationContinuation(TrackedMissile source, PendingCollisionContext collisionContext, out TrackedMissile continuation)
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
	}'''


def normalize_decompiler_output(text: str) -> str:
    text = re.sub(r"\(\([A-Za-z0-9_.<>]+\)\(ref ([^)]+)\)\)\.", r"\1.", text)
    text = re.sub(r"(?<![\w.])MissileCollisionReaction\b", "Mission.MissileCollisionReaction", text)
    text = re.sub(r"(?<![\w.])Missile\b", "Mission.Missile", text)
    replacements = {
        "Mission.Missile Mission.Missile;": "Mission.Missile Missile;",
        "Mission.Missile =": "Missile =",
        "((MBSubModuleBase)this).OnSubModuleLoad();": "base.OnSubModuleLoad();",
        "((MBSubModuleBase)this).OnMissionBehaviorInitialize(mission);": "base.OnMissionBehaviorInitialize(mission);",
        "Path.": "System.IO.Path.",
        "new OnAgentHealthChangedDelegate(": "new Agent.OnAgentHealthChangedDelegate(",
        "new TimeSpeedRequest(": "new Mission.TimeSpeedRequest(",
        "_003F fallback;": "Vec3 fallback;",
        "fallback = ((_003F?)obj) ?? new Vec3(0f, 1f, 0f, -1f);": "fallback = obj ?? new Vec3(0f, 1f, 0f, -1f);",
        "_shotDirection = NormalizeSafe(velocity, (Vec3)fallback);": "_shotDirection = NormalizeSafe(velocity, fallback);",
        "value._002Ector(1f, 0f, 0f, -1f);": "value = new Vec3(1f, 0f, 0f, -1f);",
    }
    for old, new in replacements.items():
        text = text.replace(old, new)
    return text


def method_span(text: str, name: str) -> tuple[int, int]:
    declaration = re.compile(
        rf"\n\t(?:public override|private|internal|public)\s+[^\n]*\b{re.escape(name)}\b[^\n]*\n\t\{{"
    )
    match = declaration.search(text)
    if match is None:
        raise RuntimeError(f"Could not locate method {name}")
    start = match.start() + 1
    brace = text.find("{", match.start())
    depth = 0
    i = brace
    state = "code"
    while i < len(text):
        char = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""
        if state == "code":
            if char == '"':
                state = "string"
            elif char == "'":
                state = "char"
            elif char == "/" and nxt == "/":
                state = "line_comment"
                i += 1
            elif char == "/" and nxt == "*":
                state = "block_comment"
                i += 1
            elif char == "{":
                depth += 1
            elif char == "}":
                depth -= 1
                if depth == 0:
                    return start, i + 1
        elif state == "string":
            if char == "\\":
                i += 1
            elif char == '"':
                state = "code"
        elif state == "char":
            if char == "\\":
                i += 1
            elif char == "'":
                state = "code"
        elif state == "line_comment":
            if char == "\n":
                state = "code"
        elif state == "block_comment":
            if char == "*" and nxt == "/":
                state = "code"
                i += 1
        i += 1
    raise RuntimeError(f"Unterminated method {name}")


def replace_method(text: str, name: str, replacement: str) -> str:
    start, end = method_span(text, name)
    return text[:start] + replacement + text[end:]


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: patch_recovered_core.py <recovered-core-directory>")
    root = pathlib.Path(sys.argv[1]).resolve()
    if not root.is_dir():
        raise SystemExit(f"not a directory: {root}")

    for source in root.rglob("*.cs"):
        source.write_text(normalize_decompiler_output(source.read_text(encoding="utf-8-sig")), encoding="utf-8")

    behavior = root / "GuidedArrow" / "GuidedArrowBehavior.cs"
    text = behavior.read_text(encoding="utf-8")
    text = replace_method(text, "OnAgentShootMissile", ON_AGENT_SHOOT_MISSILE)
    text = replace_method(text, "IsSplitSiblingAcquisitionOpen", IS_SPLIT_SIBLING_ACQUISITION_OPEN)
    text = replace_method(text, "TrySpawnPenetrationContinuation", TRY_SPAWN_PENETRATION_CONTINUATION)
    if "TOR_Core" in text or "TheOldRealms" in text:
        raise RuntimeError("Recovered core unexpectedly contains a TOR dependency")
    behavior.write_text(text, encoding="utf-8")

    assembly_info = root / "Properties" / "AssemblyInfo.cs"
    info = assembly_info.read_text(encoding="utf-8")
    info = info.replace('AssemblyFileVersion("1.1.17.0")', 'AssemblyFileVersion("1.2.2.0")')
    info = info.replace('AssemblyInformationalVersion("1.1.17")', 'AssemblyInformationalVersion("1.2.2")')
    info = info.replace('AssemblyVersion("1.1.17.0")', 'AssemblyVersion("1.2.2.0")')
    assembly_info.write_text(info, encoding="utf-8")

    print("Recovered core normalized and patched for v1.2.2")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
