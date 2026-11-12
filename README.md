# Berwick Workshop

An offline Windows tool for browsing and, in later versions, editing data in Berwick Saga: Lazberia Chronicle, Chapter 174 (PlayStation 2). Supports the Japanese retail disc and the English v2.3 translation.

## Layout

- `Core` disc reading, edition detection, item tables
- `App` Windows desktop interface (WPF)
- `Tests` test runner
- `reference` table layouts and item names from the MinN-11 Berwick Saga editor (MIT)
- `docs/ROADMAP.md` planned features

## Building

Requires the .NET 9 SDK.

```powershell
dotnet build Tests -c Release
dotnet Tests\bin\Release\net9.0\Berwick.Tests.dll "<Japanese ISO>" "<English ISO>"
dotnet publish App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "..\Berwick Workshop"
```

The ISO paths are optional. Without them only the offline tests run.

No game files are included or needed to build.
