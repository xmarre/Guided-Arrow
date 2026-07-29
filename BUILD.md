# Building Guided Arrow

## Requirements

- Windows
- .NET 8 SDK
- PowerShell 7 or Windows PowerShell 5.1
- Internet access for NuGet restore

## Build

```powershell
./build.ps1
```

The project targets .NET Framework 4.7.2 through Bannerlord 1.3.15 reference assemblies and produces a universal Bannerlord 1.3.15–1.4.7 module.

## Integrity boundary

The gameplay core is the verified v1.1.17 `GuidedArrow.dll`:

```text
0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0
```

`build.ps1` fails before compilation when this hash does not match. The core DLL is not rebuilt.

The maintained `GuidedArrow.Progression` project contains:

- mastery progression and save migration;
- the centre-outward mastery UI;
- MCM integration;
- character-screen navigation;
- narrow runtime patches around the verified core;
- split-penetration stability and additive native-volley support;
- exact live-registry validation for stable-core missile wrappers;
- removal-safe Autoguidance target transitions;
- terminal continuation and final-swarm lifecycle guards.

## v1.3.2 runtime model

The normal Guided Arrow MCM settings are never Harmony-patched at getter level. The sidecar applies the effective mastery limits once before Guided Arrow evaluates a shot, keeps that snapshot active through the complete callback burst, and restores the original values only after terminal work reaches a stable display tick.

Guided Release's four-second starting cap is enforced directly against the core's real-time guidance counter because the verified core internally clamps its own setting to at least five seconds.

Before stable-core guidance and projectile-camera entry points execute, the sidecar compares every tracked missile index and wrapper reference against Bannerlord's managed mission missile dictionary. A legitimate wrapper replacement is first passed through the core's existing shooter/entity/item identity refresh. Registry-missing, identity-mismatched or recycled entries are removed through the core's cleanup path without calling their native missile methods. Leader and camera ownership are repaired only from the remaining exact live entries, while intentional camera index `-1` states remain suspended.

A terminal collision sourced from `OnMissileHitAlreadyDead` cannot create another synthetic penetration continuation. Legitimate continuation into a different live victim remains unchanged.

When the final tracked projectile disappears through the penetration-budget path, the sidecar records a generation-scoped terminal request instead of invoking the cinematic transition inside the collision callback. The request remains blocked until the tracked set and all collision, early-reaction, continuation-spawn and native-removal queues are empty for two consecutive display ticks. It is cancelled when the core transitions normally or guidance resumes.

Mastery XP is captured from the completed shot summary and recorded on the display tick. Teamless, friendly and allied impacts are rejected because a hostile victim must be confirmed before the summary is queued.

## Outputs

- `dist/GuidedArrow-v1.3.2-Bannerlord-1.3.15-to-1.4.7-Universal.zip`
- `dist/GuidedArrow-v1.3.2-SOURCE.zip`
- `checksums/SHA256SUMS.txt`

The source archive excludes build intermediates and includes the current source, module tree, solution, documentation and build script.
