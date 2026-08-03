# Core source migration

## Current build state

Every normal build compiles both source projects:

- `src/GuidedArrow.Core/GuidedArrow.Core.csproj` -> source-built `GuidedArrow.dll`
- `src/GuidedArrow.Progression/GuidedArrow.Progression.csproj` -> `GuidedArrow.Progression.dll`

The build produces two installable packages:

1. the normal stable package, which still contains the verified v1.1.17 core binary;
2. a clearly named `SOURCE-CORE-CANDIDATE` package, which contains the DLL compiled from `src/GuidedArrow.Core`.

This split is temporary. The source-built core is the intended production implementation, but it must not silently replace the proven binary after a previous reconstructed build crashed at mission start.

## Promotion gate

The source-built candidate replaces the verified binary in the normal package only after all of the following pass on Bannerlord 1.3.15:

- campaign load and new-game start;
- tournament, field battle and siege mission start;
- battle-command voices and general mission audio;
- manual guidance and cancellation;
- Autoguidance activation, target assignment and reacquisition;
- native/TOR volleys and additive Guided Arrow splitting;
- concentrated 48-projectile split volleys;
- finite penetration exhaustion;
- repeated Infinite Agent Penetration;
- `PassThrough`, `Stick` and `BecomeInvisible` continuation paths;
- shield and world collisions remaining terminal;
- projectile-camera ownership, kill cinematics and return transition;
- mission exit without stale camera, time-speed or crosshair state;
- existing MCM values and progression/save compatibility.

Bannerlord 1.4.7 API compatibility must also remain green in CI.

## Promotion change

After runtime acceptance, `build.ps1` will use the source-built core for the normal package and remove the candidate-only package. The verified binary will remain only as an archived regression reference. Later work can then fold stable sidecar patches into `GuidedArrow.Core` and reduce the reflection/Harmony compatibility layer.
