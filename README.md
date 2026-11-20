# Berwick Workshop

An editor for Berwick Saga: Lazberia Chronicle, Chapter 174 (PlayStation 2). Supports the Japanese retail disc and the English v2.3 translation, each with its own verified addresses.

Download [Berwick Workshop 0.118.0 for Windows](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/releases/tag/v0.118.0), unpack it, and run `Berwick Workshop.exe`. The release includes its runtime.

## What it edits

- **Items:** stats, durability, bonuses while equipped, resistances, special effects, stable prices for horses, Atelier recipes, and new items in the game's free slots.
- **Characters:** class, level, stat bonuses, Luck, growth rates and growth band, weapon levels and growth, personal skills, starting items, bag contents, partners and bonds, mercenary fees. Expected stats by level are shown alongside.
- **Armies:** every unit in the game's 275 armies (enemies, bosses, guests and story units): class, level, bonuses, weapon levels, skills, items and drops. Armies are named after the battles that place them where the events show it.
- **Classes:** base stats, move, movement type, experience value, weapon level caps, shield sizes and class skills.
- **Movement and terrain:** the cost of each of the 64 kinds of terrain for every movement type, and each terrain's avoid, recovery and whether flyers may stop there.
- **Shops and furniture:** what every shop sells and how, the stable, the Atelier and furniture prices.
- **Events:** the game's event scripts as readable commands. Numbers, lines and whole scripts can be edited, for example the turn a reinforcement arrives.
- **Game rules:** Parry, shield block and guard, durability and breaking, experience, stat limits, Luck, and many skill numbers and switches: activation chances, ranges, auras, command waits and strikes, the weapons a skill allows, Hurry, Deathmatch, Guard, Canto, Robbery, Provoke, Debility and more. Rules whose effect has not been confirmed in a running game stay hidden.

Current scope and remaining work are recorded in [Current status](docs/STATUS.md).

## Also included

- **Review changes:** searchable original and current values, scripts, texts, copied records, affected files and JSON manifest export. Shared dependencies and known conflicts are checked before disc export.

- **Skills page:** what each of the 90 skills does, whether it is a command, and various parts of different skills.
- **Maps page:** every battle's map with terrain, starting positions, and reinforcements.
- **Battle animations page:** the animation set each class plays and the colors each character wears, played back as in the game, plus the sets no class uses. Sets, colors and each class's map sprite can be changed. Export transparent PNG frames, sprite sheets and timing/alignment JSON for one motion or every motion in the selected set. Expanded canvases preserve larger artwork with origin coordinates in JSON; legacy 288 by 288 output remains available. Separate effect layers can be exported with original counters, record links, palettes and transform/blend metadata. These are source-art PNGs; runtime transforms and the final game composite are not simulated.
- **Mechanics page:** the game's formulas with how well each is verified and links to the values you can change.
- **Compare:** open a second disc to compare items, characters and classes.
- **Live game:** with PCSX2 running on the same computer (PINE), change units on the current map (including giving or changing a horse), try the project's rules, and send the next sortie to any battle. Nothing on the disc, memory cards or save states is changed.

Changes are saved as a `.bwproj` project and exported as a new ISO for PCSX2. The original disc image is never written to.

## Layout

- `Core` disc reading, edition detection, data tables, game rules, events, projects and export
- `App` Windows desktop interface (WPF)
- `Tests` core test runner
- `Tests.Windows` PNG and export-dialog checks
- `reference` table layouts and names from the MinN-11 Berwick Saga editor (MIT), and the game's own English names
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

Animation export checks on Windows:

```powershell
dotnet run --project Tests.Windows -c Release -- "<Japanese ISO>" "<English ISO>" "<new test output folder>"
```

This writes extracted test artwork to the chosen folder. Use a new folder for each run. The output is for local verification and is not part of the release.
