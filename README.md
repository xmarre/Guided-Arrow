# Guided Arrow

Guided Arrow is a Mount & Blade II: Bannerlord single-player mod that adds manually guided projectiles, autonomous guidance, split-arrow behaviour, formations, cinematic camera features and an optional mastery progression system.

## Current repository snapshot

- Mod version: **1.3.4**
- Bannerlord support: **1.3.15 through 1.4.7**
- Build target: **.NET Framework 4.7.2**
- Stable core runtime: verified v1.1.17 `GuidedArrow.dll`
- Progression/MCM/UI and stable-core sidecar patches: `src/GuidedArrow.Progression`
- Runtime module: `module/GuidedArrow`

The supplied v1.1.17 clean archive did not include the original core source. A recovered-core experiment compiled successfully but caused an immediate native mission-start failure. The recovered implementation was removed. Release builds preserve the exact known-working v1.1.17 core binary and fail if its SHA-256 changes.

Core corrections are introduced only as narrowly scoped Harmony patches in the maintained sidecar. Synthetic penetration continuations are validated, serialised and held behind a real native-frame boundary, while native/TOR ability projectiles retain their original effects and collision handling. When additive splitting is enabled, Guided Arrow followers are added on top of native volleys rather than replacing them.

v1.3.4 separates projectile-follow camera ownership from kill cinematics, keeps normal mission speed when proximity dilation is disabled, prevents synthetic continuations from spawning during the native collision tick that queued them, repairs first-time mastery activation, makes character-screen navigation tolerant of newer Bannerlord transition screens, and adds siege-only line-of-sight validation before Autoguidance commits to a target.

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

For practical presets and explanations of the main options, see [`CONFIGURATION.md`](CONFIGURATION.md).

## Mastery progression

The optional mastery screen can be opened from:

- the character-development screen using the bottom-right **Guided Arrow Mastery** button;
- the campaign map with **Ctrl+U**.

`Ctrl+U` is deliberately restricted to the campaign map. The character-screen button closes Bannerlord's native character-development state, waits for the map bar to stabilise, and then opens mastery.

Progression can be enabled in:

`MCM > Guided Arrow - Mastery Progression > Progression > Enable Mastery Progression`

It can also be toggled directly from the mastery screen. Enabling progression automatically invests the rank-1 starter point in **Guided Release**, the mandatory centre node. The separate progression MCM contains a **Mastery XP Multiplier** from 0.25 to 3.00.

### Level-99 structure

- Mastery starts at rank 1 and ends at rank 99.
- Every rank supplies one mastery point, for 99 total points.
- The tree contains 19 skills, mostly with 10 or 20 levels.
- Total possible investment is intentionally much larger than 99, forcing specialization rather than allowing one character to maximize everything.
- Existing v1 binary unlocks migrate to level 1 of their corresponding skills.

### Centre-outward tree

The tree grows from **Guided Release** in the centre:

- **Piercing Doctrine** grows north: controlled penetration.
- **Hand of the Archer** grows east: guidance duration, turn authority and time control.
- **Arrow Choir** grows west: native-volley awareness, generated splitting and formations.
- **Hunter's Mind** grows south: autonomous guidance, reacquisition, navigation and allied takeover.
- **Convergence** occupies the outer junctions where branches combine.

Each node displays its current level, maximum level, prerequisites, present effect and next-level effect.

### XP balance

Mastery XP is awarded only for unique enemy victims within one guided-shot generation:

- hit: 3 XP;
- kill: +6 XP;
- range: up to +4 XP;
- repeated kills in one shot: a bounded multi-kill bonus;
- maximum: 32 XP per shot before the MCM multiplier.

The rank curve is quadratic through the main campaign and becomes moderately steeper after rank 50. Rank 99 requires roughly 68,000 mastery XP: long enough to remain meaningful in a Bannerlord campaign, but split volleys and multi-kills prevent the system from requiring Diablo-style enemy density.

### MCM safety

Progression no longer patches the normal Guided Arrow MCM property getters. Mastery limits are applied once before Guided Arrow evaluates a shot, remain stable through its complete callback burst, and restore after terminal work reaches a clean display tick. The normal Guided Arrow MCM therefore remains editable and displays the player's configured upper limits.

Guided Release rank 1 is enforced by a direct real-time timeout of **4.0 seconds**, bypassing the stable core's internal five-second minimum clamp. Higher Guided Release and Master of the Curve levels extend or eventually release that cap.

## Native/TOR ability volleys

Native/TOR ability arrows are preserved rather than replaced. For example, Waywatcher Lethal Shot keeps all five original magic/explosive arrows. With the Guided Arrow split count set to 30, 30 Guided Arrow followers are added for 35 total projectiles.

Original native arrows remain on TOR/native collision and penetration handling. Added followers use Guided Arrow's hardened synthetic continuation path. Ordinary Guided Arrow split shots do not enter the native-volley augmentation branch.

## Building

On Windows with the .NET 8 SDK installed:

```powershell
./build.ps1
```

The build script:

1. verifies the preserved v1.1.17 core DLL against SHA-256 `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`;
2. compiles `GuidedArrow.Progression.dll`;
3. refreshes the module tree;
4. creates compiled and source ZIPs;
5. writes SHA-256 checksums.

The build fails immediately if the stable core DLL is missing or changed.
