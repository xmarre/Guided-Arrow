# GuidedArrow.Core recovered-source reference

This directory was deterministically recovered from the verified v1.1.17 `GuidedArrow.dll` because the supplied clean archive did not contain the original core source.

- Original binary SHA-256: `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`
- Recovery tool: ILSpy project export
- Normalisation tooling: `tools/patch_recovered_core.py`
- Reference target: Bannerlord 1.3.15 / .NET Framework 4.7.2

## Important

This recovered project is **audit/reference material only**. It is not included in `GuidedArrow.sln`, is not compiled by `build.ps1`, and must not replace the preserved core DLL in a release package.

A recovered-core v1.2.2 test assembly compiled without errors but caused an immediate native crash when entering a battle. Compilation therefore does not establish runtime equivalence for this assembly. Future core fixes must either be made from authentic source or introduced as narrowly scoped, independently tested patches around the verified stable runtime.
