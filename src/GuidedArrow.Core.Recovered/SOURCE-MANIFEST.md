# Recovered core source manifest

The reference project was generated from the verified `GuidedArrow.dll` with ILSpy 10.1.1.8388.

## Recovered implementation

- `GuidedArrow/GuidedArrowBehavior.cs` — mission behavior, guidance, Autoguidance, projectile camera, splitting, penetration and collision lifecycle.
- `GuidedArrow/MissileDamageBridge.cs` — native missile launch-data and damage bridge.
- `GuidedArrow/Settings.cs` — original core MCM settings model.
- `GuidedArrow/SubModule.cs` — Bannerlord module entry point.
- `Properties/AssemblyInfo.cs` — recovered assembly metadata for core version 1.1.17.
- `GuidedArrow.Core.Recovered.csproj` — ILSpy-generated reference project.

## Provenance files

- `README.md` — limitations and production-build policy.
- `BINARY-SHA256.txt` — exact SHA-256 identity of the source binary.

This manifest covers every non-compiler-generated top-level type present in the recovered core project. Compiler-generated state-machine and closure types are reconstructed inside their owning C# types by ILSpy.
