# Guided Arrow

Guided Arrow is a Mount & Blade II: Bannerlord single-player mod that adds manually guided projectiles, autonomous guidance, split-arrow behaviour, formations, cinematic camera features and an optional mastery progression system.

## Current repository snapshot

- Mod version: **1.3.6**
- Bannerlord support: **1.3.15 through 1.4.7**
- Build target: **.NET Framework 4.7.2**
- Core source project: `src/GuidedArrow.Core`
- Progression/MCM/UI compatibility project: `src/GuidedArrow.Progression`
- Runtime module: `module/GuidedArrow`

The original authoring project for the v1.1.17 gameplay core was unavailable. Its implementation was reconstructed from the exact verified production DLL and is now a normal source project that compiles on every build and in CI.

The source-built core is the intended future production implementation. The normal stable package temporarily retains the verified binary core because an earlier reconstructed build crashed at mission start. Every build also emits a clearly named source-core candidate package for runtime validation. Once that candidate passes the acceptance gate in [`CORE_SOURCE_MIGRATION.md`](CORE_SOURCE_MIGRATION.md), the normal package will switch to the source-built core and the binary will remain only as a regression reference.

Core corrections are currently applied through narrowly scoped compatibility patches in `GuidedArrow.Progression`. After the source-built core becomes the stable package default, those patches can be folded into `GuidedArrow.Core` and the reflection/Harmony compatibility layer reduced.

v1.3.6 restores mission audio, fixes protected-memory crashes in repeated penetration chains, stabilises split-volley Autoguidance and camera ownership, improves siege targeting, and integrates the former Simple Controls entries into the appropriate sections of the main Guided Arrow MCM page while retaining existing saved values. Shields and world collisions remain terminal.

## Repository layout

```text
src/GuidedArrow.Core/            gameplay-core source and build project
src/GuidedArrow.Progression/     mastery, MCM, UI and compatibility patches
module/GuidedArrow/              installable Bannerlord module tree
dist/                            stable, source-core candidate and source archives
checksums/                       SHA-256 manifests
.github/workflows/build.yml      reproducible build and packaging workflow
CORE_SOURCE_MIGRATION.md         source-core promotion gate
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
- Existing v1 binary mastery unlocks migrate to level 1 of their corresponding skills.

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

The rank curve is quadratic through the main campaign and becomes moderately steeper after rank 50. Rank 99 requires roughly 68,000 mastery XP.

### MCM safety

Progression does not patch the normal Guided Arrow MCM property getters. Mastery limits are applied before Guided Arrow evaluates a shot, remain stable through its complete callback burst, and restore after terminal work reaches a clean display tick. The normal Guided Arrow MCM remains editable and displays the player's configured upper limits.

Guided Release rank 1 is enforced by a direct real-time timeout of **4.0 seconds**, bypassing the core's internal five-second minimum clamp. Higher Guided Release and Master of the Curve levels extend or eventually remove that cap.

## Native/TOR ability volleys

Native/TOR ability arrows are preserved rather than replaced. For example, Waywatcher Lethal Shot keeps all five original magic/explosive arrows. With the Guided Arrow split count set to 30, 30 Guided Arrow followers are added for 35 total projectiles.

Original native arrows remain on TOR/native collision and penetration handling. Added followers use Guided Arrow's hardened continuation path. Ordinary Guided Arrow split shots do not enter the native-volley augmentation branch.

## Building

On Windows with the .NET 8 SDK installed:

```powershell
./build.ps1
```

The build script:

1. verifies the archived production core binary against SHA-256 `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`;
2. compiles `GuidedArrow.Core.csproj` into a source-built `GuidedArrow.dll`;
3. compiles `GuidedArrow.Progression.dll`;
4. creates the normal stable package with the verified binary core;
5. creates a separate `SOURCE-CORE-CANDIDATE` package with the source-built core;
6. creates the complete source archive;
7. writes SHA-256 checksums for both core DLLs and all archives.

CI builds both source projects against Bannerlord 1.3.15 and verifies Bannerlord 1.4.7 API compatibility. The temporary dual-package arrangement is documented in [`CORE_SOURCE_MIGRATION.md`](CORE_SOURCE_MIGRATION.md).
