# Guided Arrow

Guided Arrow is a Mount & Blade II: Bannerlord single-player mod that adds manually guided projectiles, autonomous guidance, split-arrow behaviour, formations, cinematic camera features and an optional mastery progression tree.

## Current repository snapshot

- Mod version: **1.2.2**
- Bannerlord support: **1.3.15 through 1.4.7**
- Build target: **.NET Framework 4.7.2**
- Core runtime source: buildable under `src/GuidedArrow.Core`
- Progression/MCM source: buildable under `src/GuidedArrow.Progression`
- Runtime module: ready to install under `module/GuidedArrow`

The supplied v1.1.17 clean archive did not include the original core source. The core project in this repository was deterministically recovered from the verified v1.1.17 binary and then updated for v1.2.2. Its provenance and original binary SHA-256 are documented in `src/GuidedArrow.Core/README.md`; it is not presented as the author's original formatting or symbol naming.

## Repository layout

```text
src/GuidedArrow.Core/            buildable core runtime source and provenance
src/GuidedArrow.Progression/     mastery progression, MCM and UI source
module/GuidedArrow/              installable Bannerlord module tree
dist/                            clean compiled and source archives
checksums/                       SHA-256 manifests
tools/                           deterministic core-recovery patch tooling
.github/workflows/build.yml      reproducible build and packaging workflow
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

`Ctrl+U` is deliberately restricted to the campaign map. Use the button when the character-development screen is open.

Progression can be enabled in MCM under:

`Guided Arrow - Mastery Progression > Progression > Enable Mastery Progression`

It can also be toggled directly from the mastery screen.

## v1.2.2 runtime fixes

- Native multi-projectile callbacks are captured as one native batch, so TOR Lethal Shot and similar abilities do not fall back to Guided Arrow standalone splitting.
- Standalone splitting remains independent and is only created when no native burst exists.
- Synthetic penetration continuations spawn beyond the impacted agent, explicitly ignore that agent's entity and retain the original resolved damage packet.
- No TOR assembly is referenced by the core project; vanilla and TOR penetration share the same generic Bannerlord continuation path.

## Building

On Windows with the .NET 8 SDK installed:

```powershell
./build.ps1
```

Both projects target `net472` and restore Bannerlord 1.3.15 reference assemblies from NuGet. The build script compiles the core and progression DLLs, refreshes the module tree, creates compiled/source ZIPs and writes SHA-256 checksums.

## Integrity

The repository build workflow compiles both assemblies from committed source and publishes compiled and source artifacts. Release hashes are stored in `checksums/SHA256SUMS.txt`.
