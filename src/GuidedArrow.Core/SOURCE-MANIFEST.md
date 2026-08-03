# Core source manifest

The project was reconstructed from the verified `GuidedArrow.dll` with ILSpy 10.1.1.8388 and is now compiled as `src/GuidedArrow.Core/GuidedArrow.Core.csproj`.

## Core implementation

- `GuidedArrow/GuidedArrowBehavior.cs` — mission behavior, guidance, Autoguidance, projectile camera, splitting, penetration and collision lifecycle.
- `GuidedArrow/MissileDamageBridge.cs` — native missile launch-data and damage bridge.
- `GuidedArrow/Settings.cs` — core MCM settings model.
- `GuidedArrow/SubModule.cs` — Bannerlord module entry point.
- `Properties/AssemblyInfo.cs` — retained assembly metadata for core version 1.1.17.
- `GuidedArrow.Core.csproj` — maintained SDK-style build project.

## Provenance and migration

- `README.md` — current build and packaging status.
- `NOTICE.md` — recovered-source disclosure.
- `BINARY-SHA256.txt` — identity of the verified source binary.
- `../../CORE_SOURCE_MIGRATION.md` — acceptance gate for promoting the source-built DLL into stable packages.

Compiler-generated state-machine and closure types were reconstructed inside their owning C# types by ILSpy.
