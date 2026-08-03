# GuidedArrow core source

This is the maintained C# source project for the `GuidedArrow.dll` gameplay core.

The original authoring project for the v1.1.17 binary was not available. The implementation was reconstructed from the exact verified production DLL with ILSpy and then placed into a normal SDK-style project so it can be compiled and migrated back into production.

- Source binary SHA-256: `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`
- Reconstructed with ILSpy 10.1.1.8388
- Assembly identity retained: `GuidedArrow`, version `1.1.17.0`
- Build status: compiled on every normal build and in CI
- Package status: emitted as a separate source-core candidate until runtime acceptance is complete

The normal release package still uses the verified binary core because an earlier reconstructed build crashed at mission start. That is a temporary migration gate, not the intended permanent architecture. The source-built candidate must pass mission start, battle audio, manual guidance, Autoguidance, split volleys, finite and infinite penetration, camera handoff, siege targeting, mission exit and save compatibility before it replaces the binary in the stable package.

See [`CORE_SOURCE_MIGRATION.md`](../../CORE_SOURCE_MIGRATION.md) for the exact promotion process.
