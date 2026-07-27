# GuidedArrow.Core source provenance

This project was deterministically recovered from the verified v1.1.17 `GuidedArrow.dll` binary because the supplied clean archive did not contain the original core source.

- Original binary SHA-256: `0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0`
- Recovery tool: ILSpy project export
- Normalization and v1.2.2 fixes: `tools/patch_recovered_core.py`
- Build target: Bannerlord 1.3.15 / .NET Framework 4.7.2

This is recovered, buildable source—not a claim that it is the author's original formatting or symbol naming. The v1.2.2 changes remove behavioural coupling between TOR/native multi-shot batches and Guided Arrow standalone splitting, and make synthetic agent penetration continuations ignore the impacted entity safely.
