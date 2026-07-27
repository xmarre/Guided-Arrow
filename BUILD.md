# Build notes

## Requirements

- Windows
- .NET 8 SDK
- PowerShell 7 or Windows PowerShell 5.1
- Internet access for NuGet restore

## Progression project

`src/GuidedArrow.Progression/GuidedArrow.Progression.csproj` targets `net472` and references:

- Bannerlord reference assemblies 1.3.15.110062
- Harmony 2.4.1
- Bannerlord MCM 5.12.1

The resulting DLL is intended to remain compatible across Bannerlord 1.3.15-1.4.7 because it is compiled against the oldest supported API surface.

## Core runtime provenance

`module/GuidedArrow/bin/Win64_Shipping_Client/GuidedArrow.dll` is the byte-identical v1.1.17 universal runtime used as the base for v1.2.1.

SHA-256:

```text
0f84dcfe256b4c0235707a463e2fadb6ca6b05027d7bafb5e7313965d3d98af0
```

The original core source was not present in the supplied clean archive. Only the complete progression/UI source is built by this repository at present.
