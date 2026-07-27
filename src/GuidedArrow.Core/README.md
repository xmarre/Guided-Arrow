# GuidedArrow.Core provenance

The supplied clean v1.1.17 archive did not contain the original source for `GuidedArrow.dll`.

The repository therefore preserves the verified compiled core runtime rather than presenting decompiled output as authoritative source.

- Stable core version: v1.1.17
- Stable core SHA-256: `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`
- Runtime target: Mount & Blade II: Bannerlord 1.3.15–1.4.7

A recovered-source experiment compiled successfully but caused an immediate native crash when entering a battle. Its implementation and recovery scripts were removed from the repository. `build.ps1` compiles only the progression/UI sidecar and fails immediately if the preserved core DLL is missing or its hash changes.

Future core changes require authentic source or narrowly scoped patches around the verified binary with separate in-game validation.
