# GuidedArrow.Core provenance

The supplied clean v1.1.17 archive did not contain the original authoring source for `GuidedArrow.dll`.

The repository preserves the verified compiled core runtime as the production implementation and now also provides a complete ILSpy reconstruction under [`src/GuidedArrow.Core.Recovered`](../GuidedArrow.Core.Recovered/) for reading, auditing, and future recovery work.

- Stable core version: v1.1.17
- Stable core SHA-256: `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`
- Runtime target: Mount & Blade II: Bannerlord 1.3.15–1.4.7
- Recovered source status: reference-only, not authoritative original source

Decompilation cannot restore comments, exact original project metadata, or guarantee a byte-identical rebuild. A previous rebuilt DLL from recovered source compiled successfully but caused an immediate native crash when entering a battle. `build.ps1` therefore compiles only the progression/UI sidecar and fails immediately if the preserved core DLL is missing or its hash changes.

Production core changes still require authentic original source or narrowly scoped patches around the verified binary with separate in-game validation.
