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
- deferred and removal-safe Autoguidance target transitions;
- pending-collision native-handle quarantine and victim-lifetime protection.

## v1.3.2 runtime model

The normal Guided Arrow MCM settings are never Harmony-patched at getter level. During a Guided Arrow mission callback, the sidecar temporarily applies the effective mastery limits to the settings backing fields and restores every original value in a Harmony finalizer. This keeps the MCM page stable and makes the configured values upper limits for progression.

Guided Release's four-second starting cap is enforced directly against the core's real-time guidance counter because the verified core internally clamps its own setting to at least five seconds.

Before stable-core guidance and projectile-camera entry points execute, the sidecar compares non-pending tracked missile indices and wrapper references against Bannerlord's managed mission missile dictionary. Registry validation is deferred while a tracked entry is awaiting its collision reaction because the verified core's refresh path queries mutable missile-native identity data. Once the reaction or timeout resolves, normal exact-wrapper validation resumes.

Binary inspection of the locked core shows that `OnMissionTick` reads `MBMissile.GetPosition` and `GetVelocity` before checking `TrackedMissile.AwaitingCollisionReaction`. v1.3.2 snapshots those pending wrappers at tick entry and replaces only the core's four position/velocity reads and four velocity writes with guarded sidecar calls. Pending wrappers return managed zero vectors and reject steering writes for that tick. A newly created continuation wrapper is not quarantined and enters the normal path immediately. Pending entries are also excluded from leader and projectile-camera ownership repair.

A penetration collision no longer performs full target collection, skeleton/head lookup or route assignment inside the native collision callback. The impacted target is recorded as consumed through managed references, the current target is cleared when necessary, and the existing fallback direction is retained. Planned-route advancement or fresh target selection then occurs through the normal display-tick path after the original projectile has survived native pass-through or a synthetic continuation has been created. The consumed-target list belongs to each tracked missile and only prevents that missile from reacquiring the same target.

Repeated hits on an already-tracked victim reuse the managed impact position instead of resampling native bones. Fatal-hit detection uses the collision packet's runtime `IsFatalDamage` getter rather than `Agent.Health`, and cinematic subjects are detached from their Agent reference when removal begins so later camera ticks use the stored position. The experimental collision-queue expansion was removed after equal 32-arrow and 48-arrow results ruled out the queue boundary.

Synthetic continuation creation uses only managed collision position and direction values with a fixed 1.25-metre exit distance. Mastery range accounting likewise uses the collision position and the core's cached shot origin rather than victim or shooter position pointers. Agent-removal callbacks purge that exact agent from active, planned, consumed and shared target collections before later Autoguidance scans.

## Outputs

- `dist/GuidedArrow-v1.3.2-Bannerlord-1.3.15-to-1.4.7-Universal.zip`
- `dist/GuidedArrow-v1.3.2-SOURCE.zip`
- `checksums/SHA256SUMS.txt`

The source archive excludes build intermediates and includes the current source, module tree, solution, documentation and build script.
