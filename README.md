# Guided Arrow

Guided Arrow is a Mount & Blade II: Bannerlord single-player mod that adds manually guided projectiles, autonomous guidance, split-arrow behaviour, formations, cinematic camera features and an optional mastery progression tree.

## Current repository snapshot

- Mod version: **1.2.2 test branch**
- Bannerlord support: **1.3.15 through 1.4.7**
- Build target: **.NET Framework 4.7.2**
- Stable core runtime: verified v1.1.17 `GuidedArrow.dll`
- Progression/MCM and stable-core sidecar patches: buildable under `src/GuidedArrow.Progression`
- Runtime module: ready to install under `module/GuidedArrow`

The supplied v1.1.17 clean archive did not include the original core source. A recovered-core experiment compiled successfully but caused an immediate native crash when missions started. The recovered implementation and patch scripts have therefore been removed rather than retained as misleading or unsafe source. Release builds preserve the exact known-working v1.1.17 core binary and fail if its SHA-256 changes.

Core corrections are introduced only as narrowly scoped Harmony patches in the maintained progression sidecar. The penetration-continuation safety layer adjusts the stable core's existing synthetic continuation at runtime, validates the reflected result before the core dereferences it, and serialises large split-arrow continuation batches instead of creating the full batch in one native mission tick.

When standalone splitting is enabled with a total greater than one, that configured total is authoritative for native/TOR player volleys as well. For abilities such as Waywatcher Lethal Shot, one native ability arrow remains the weapon and resolved-damage source; the extra native siblings are removed before the core mission tick and Guided Arrow creates the configured total through its existing standalone splitter. With standalone splitting disabled or set to one, the native/TOR volley is left unchanged.

## Repository layout

```text
src/GuidedArrow.Core/            provenance note for the binary-only stable core
src/GuidedArrow.Progression/     mastery, MCM, UI and narrow stable-core patches
module/GuidedArrow/              installable Bannerlord module tree
dist/                            clean compiled and source archives
checksums/                       SHA-256 manifests
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

This test branch contains the character-screen navigation fixes, progression UI changes, penetration hardening and native-volley replacement around the unchanged v1.1.17 core. Synthetic continuations are moved beyond the impacted agent, the impacted entity is marked pass-through, incomplete continuation objects are rejected, and large split-arrow continuation batches are processed one per mission tick with targeted queue/state recovery.

The native-volley replacement is generic and contains no static `TOR_Core` dependency. It acts only when the standalone split override is enabled with a requested total above one; otherwise native ability volleys retain their original count and behaviour.
