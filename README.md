# Guided Arrow

Guided Arrow is a Mount & Blade II: Bannerlord single-player mod that adds manually guided projectiles, autonomous guidance, split-arrow behaviour, formations, cinematic camera features and an optional mastery progression tree.

## Current repository snapshot

- Mod version: **1.2.2**
- Bannerlord support: **1.3.15 through 1.4.7**
- Build target: **.NET Framework 4.7.2**
- Progression/MCM source: complete and buildable under `src/GuidedArrow.Progression`
- Runtime module: ready to install under `module/GuidedArrow`

The pre-existing `GuidedArrow.dll` runtime is the v1.1.17 core binary used by v1.2.2. Its original source was not present in the supplied clean v1.1.17 archive, so this repository does not pretend that decompiled output is original source. The complete source for the v1.2.2 progression/UI sidecar is included.

## Repository layout

```text
src/GuidedArrow.Progression/     v1.2.2 mastery progression, MCM and UI source
src/GuidedArrow.Core/            provenance note for the binary-only core snapshot
module/GuidedArrow/              installable Bannerlord module tree
dist/                            clean compiled and source archives
checksums/                       SHA-256 manifests
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

## Building

On Windows with the .NET 8 SDK installed:

```powershell
./build.ps1
```

The project targets `net472` and restores Bannerlord 1.3.15 reference assemblies from NuGet. The build script compiles the progression DLL, refreshes the module tree, creates compiled/source ZIPs and writes SHA-256 checksums.

## Integrity

The repository build workflow compiles from committed source and publishes both compiled and source artifacts. Release hashes are stored in `checksums/SHA256SUMS.txt`.
