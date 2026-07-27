# Guided Arrow

Guided Arrow is a Mount & Blade II: Bannerlord single-player mod that adds manually guided projectiles, autonomous guidance, split-arrow behaviour, formations, cinematic camera features and an optional mastery progression tree.

## Current repository snapshot

- Mod version: **1.2.2 test branch**
- Bannerlord support: **1.3.15 through 1.4.7**
- Build target: **.NET Framework 4.7.2**
- Stable core runtime: verified v1.1.17 `GuidedArrow.dll`
- Progression/MCM source: buildable under `src/GuidedArrow.Progression`
- Runtime module: ready to install under `module/GuidedArrow`

The supplied v1.1.17 clean archive did not include the original core source. A recovered core project remains under `src/GuidedArrow.Core` for audit and future analysis, but it is **not** part of the release solution or packaging process. A recovered-core test build compiled successfully but caused an immediate native crash when missions started, so release builds preserve the exact known-working v1.1.17 core binary instead.

## Repository layout

```text
src/GuidedArrow.Core/            recovered audit/reference source; not shipped
src/GuidedArrow.Progression/     mastery progression, MCM and UI source
module/GuidedArrow/              installable Bannerlord module tree
dist/                            clean compiled and source archives
checksums/                       SHA-256 manifests
tools/                           deterministic recovery/audit tooling
.github/workflows/build.yml      reproducible sidecar build and packaging workflow
build.ps1                        local Windows build/package script
```

## Installation

Copy `module/GuidedArrow` into Bannerlord's `Modules` folder, or use the compiled ZIP in `dist`.

Required modules:

- Harmony
- ButterLib
- UIExtenderEx
- Mod Configuration Menu v5

Delete an older `Modules/GuidedArrow` folder before installing a new build.

## Mastery progression

The optional skill tree can be opened from:

- the character-development screen using the bottom-right **Guided Arrow Mastery** button
- the campaign map with **Ctrl+U**

`Ctrl+U` is deliberately restricted to the campaign map. The character-screen button closes Bannerlord's native character-development game state, waits for the campaign map to stabilise, and then opens mastery.

Progression can be enabled in MCM under:

`Guided Arrow - Mastery Progression > Progression > Enable Mastery Progression`

It can also be toggled directly from the mastery screen.

## Building

On Windows with the .NET 8 SDK installed:

```powershell
./build.ps1
```

The build script:

1. verifies the preserved v1.1.17 core DLL against SHA-256 `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`;
2. compiles only `GuidedArrow.Progression.dll`;
3. refreshes the module tree;
4. creates compiled and source ZIPs;
5. writes SHA-256 checksums.

The build fails immediately if the stable core DLL is missing or has changed.

## Current runtime scope

This stable-core recovery build contains the character-screen navigation fixes and progression UI changes. The proposed penetration and TOR native-volley changes from the recovered-core experiment are not shipped. They must be implemented as targeted patches around the stable core and verified separately in game.
