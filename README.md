# Berwick Workshop

Berwick Workshop is a Windows editor for *Berwick Saga: Lazberia Chronicle, Chapter 174* on PlayStation 2. It supports the Japanese retail disc and the English v2.3 translation, with separate verified addresses for each edition.

[Download the latest Windows release](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/releases/latest), extract it and run `Berwick Workshop.exe`. The release includes the required runtime.

## Editing game data

**Items and economy.** Edit item stats, durability, equipped bonuses, resistances and known special effects. The editor also covers shop inventories, horse prices, the Atelier, furniture prices and new items using the game's available item records.

**Characters and armies.** Edit classes, levels, stat bonuses, Luck, growth rates and growth bands, weapon experience, skills, starting equipment, bags, partners, bonds and mercenary fees. The Armies page covers enemies, bosses, guests and story units, including their equipment and drops. Expected stats by level are available for characters.

**Classes and terrain.** Edit class stats, movement, weapon caps, shield permissions and class skills. Terrain settings include movement costs, avoid, recovery and restrictions on where flying units may stop.

**Maps and events.** View the painted maps, terrain, deployment positions, starting units, reinforcements and event spots. The Maps page supports editing existing positions and deployment hexes. The Events page reads the game's scripts as commands and can change values, individual lines or complete scripts, including reinforcement timing.

**Rules and mechanics.** Change supported battle rules for Parry, shield use, durability, experience, stat limits and skill behavior. The Mechanics page explains the formulas and their verification status. Rules without sufficient in-game verification are not offered for editing.

## Maps, graphics and animations

The Maps page displays map artwork decoded from the opened game image, with optional terrain and unit overlays.

The Battle Animations page shows the animation sets and palettes assigned to classes and characters. It also lists unused sets and allows existing animation and map-sprite assignments to be changed. Animation frames and sprite sheets can be exported as transparent PNGs, with timing and alignment information in JSON. Separate effect layers are available with their source metadata. These exports reproduce decoded artwork, not every transform or blend applied by the game at runtime.

Game artwork is read from the user's disc image. The repository and application downloads do not include extracted game art.

## Projects, comparison and live testing

Edits are saved in `.bwproj` projects. Export creates a new ISO for use in PCSX2; the original disc image is not overwritten. The change review shows original and current values, affected files and known dependency conflicts, and can export a JSON manifest.

The Compare page checks items, characters and classes against a second disc. With PCSX2 running locally, the Live Game page can inspect and edit units in memory, test rules and select the next sortie. These live operations do not write to the disc image, save files or memory cards.

The [roadmap](docs/ROADMAP.md) lists implemented features and remaining work. [Research notes](docs/RESEARCH.md) record the game-format findings and verification evidence.

## Building from source

The project requires the .NET 9 SDK. Build the tests, optionally run them against both supported game images, and publish the Windows application:

```powershell
dotnet build Tests -c Release
dotnet Tests\bin\Release\net9.0\Berwick.Tests.dll "<Japanese ISO>" "<English ISO>"
dotnet publish App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "..\Berwick Workshop"
```

The ISO arguments are optional. Without them, only offline tests run. A third argument can check a Redux-modified disc. Tests using game images create temporary exports and remove them afterward; allow at least 9 GB of free disk space. Original images are opened read-only.

No game files are needed to build the software.

## Source layout

| Directory | Contents |
| --- | --- |
| `Core` | Disc formats, game data, rules, projects and export |
| `App` | Windows desktop interface (WPF) |
| `Tests` | Offline and edition-specific checks |
| `reference` | Reference tables and tools, including credited MinN-11 editor material |
| `docs` | Development notes, research, release checks and roadmap |

See [Development and architecture](docs/DEVELOPMENT.md) for the source conventions and verification process.
