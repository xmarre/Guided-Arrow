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
- exact live-registry validation for stable-core missile wrappers.

## v1.3.1 runtime model

The normal Guided Arrow MCM settings are never Harmony-patched at getter level. During a Guided Arrow mission callback, the sidecar temporarily applies the effective mastery limits to the settings backing fields and restores every original value in a Harmony finalizer. This keeps the MCM page stable and makes the configured values upper limits for progression.

Guided Release's four-second starting cap is enforced directly against the core's real-time guidance counter because the verified core internally clamps its own setting to at least five seconds.

Before stable-core guidance, deferred-work and projectile-camera tick paths execute, the sidecar compares every tracked missile index and wrapper reference against Bannerlord's managed mission missile dictionary. Registry-missing and wrapper-replaced entries are removed without calling native missile methods. Leader and camera ownership are then repaired only from the remaining exact live entries.

## Outputs

- `dist/GuidedArrow-v1.3.1-Bannerlord-1.3.15-to-1.4.7-Universal.zip`
- `dist/GuidedArrow-v1.3.1-SOURCE.zip`
- `checksums/SHA256SUMS.txt`

The source archive excludes build intermediates and includes the current source, module tree, solution, documentation and build script.
