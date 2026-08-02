# GuidedArrow core recovered reference source

This directory contains a complete ILSpy reconstruction of the verified binary gameplay core shipped as `GuidedArrow.dll`.

- Source binary SHA-256: `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`
- Decompiled with: ILSpy command-line tool 10.1.1.8388
- Source binary lineage: Guided Arrow v1.1.17 core retained by releases v1.2.1 through v1.3.6
- Production status: reference-only; excluded from `GuidedArrow.sln` and `build.ps1`

This is not the unavailable original authoring project. Decompilation cannot restore original comments, formatting, local-variable intent, exact project metadata, or guarantee a byte-identical rebuild. A previous rebuilt DLL from recovered source crashed when entering a battle, so releases continue to package the verified binary and apply maintained fixes through `GuidedArrow.Progression`.

The files are included to make the core implementation readable, auditable, and maintainable. Do not replace the shipped core DLL with a rebuild without separate in-game validation.
