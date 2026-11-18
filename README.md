# Berwick Workshop

An offline Windows editor for Berwick Saga: Lazberia Chronicle, Chapter 174 (PlayStation 2). Supports the Japanese retail disc and the English v2.3 translation.

Edit items, character stats and growths, starting equipment, personal and class skills, class stats, movement types and terrain costs. Game rules cover Parry, shield block, shield guard, battle experience and stat limits. Save changes as a `.bwproj` project, compare discs, and export a separate patched ISO for PCSX2. A Live game page can also change units and try rules in a game running in PCSX2 on the same computer, through its PINE connection, when you choose Connect.

New items reuse existing slots; custom names currently appear only in the project. Learned skills, class growths and in-game text are not editable. Game rules affect enemies as well as player units. Shield guard applies only after a failed block roll; attacks that ignore Defense also ignore guard.

## Layout

- `Core` disc reading, edition detection, data tables, game rules, projects and export
- `App` Windows desktop interface (WPF)
- `Tests` test runner
- `reference` table layouts and names from the MinN-11 Berwick Saga editor (MIT)
- `docs/ROADMAP.md` planned features
- `docs/RESEARCH.md` notes on the game's data and program

## Building

Requires the .NET 9 SDK.

```powershell
dotnet build Tests -c Release
dotnet Tests\bin\Release\net9.0\Berwick.Tests.dll "<Japanese ISO>" "<English ISO>"
dotnet publish App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "..\Berwick Workshop"
```

The ISO paths are optional. Without them only the offline tests run. An optional third path adds the Redux modified-disc check. Real-disc tests create temporary exports, verify them, then remove them. Allow at least 9 GB of free space. The originals are read only.

No game files are included or needed to build.
