# Berwick Workshop

Berwick Workshop is a Windows editor for *Berwick Saga: Lazberia Chronicle, Chapter 174* on PlayStation 2. It reads the Japanese retail game and the English v2.3 translation, using the verified data locations for each edition. Changes are kept in a project until you export a new disc image.

[Download the latest Windows release](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/releases/latest). Extract the archive and run `Berwick Workshop.exe`. The download includes the .NET runtime.

## What you can edit

The Items page covers weapon and equipment stats, durability, resistances, bonuses while equipped and known special effects. Shop inventories, the Stable, Atelier recipes and furniture prices have their own editors. New items can be made using the game's spare item records.

Characters have editable levels, classes, stat bonuses, growths, weapon skills and starting equipment. The page also covers bags, partners, bonds and mercenary fees, with expected stats shown for each level. The Armies page gives similar access to enemies, bosses, guests and story units, including their items and drops. Class base stats, movement types, weapon caps, shield permissions and class skills are edited separately, so changes to a class can affect more than one unit.

Movement costs can be changed for each terrain type, along with terrain avoid, recovery and the places where flying units may stop. The Skills page documents the game's 90 skills and the parts of their behavior that can be edited. The Rules and Mechanics pages cover combat calculations and settings such as Parry, shields, durability, experience and stat limits. Settings that have not been verified sufficiently in the running game are not offered as editable rules.

## Maps and events

Maps displays the painted battle maps, terrain, deployment hexes, starting units, later arrivals and event spots. Existing unit positions, deployment positions and limits, and some reinforcement locations and turns can be changed there.

The Events page opens the game's scripts as readable commands. You can edit numbers, individual commands and complete scripts, including the conditions that determine when units arrive. Some event structures still require direct work in the Events editor rather than a dedicated Maps control.

## Graphics

Battle Animations plays the sets and palettes assigned to classes and characters, including sets that no class currently uses. Assignments can be changed, and the decoded animation frames can be exported as transparent PNGs or sprite sheets. JSON files record frame order, timing and alignment. Effect artwork has a separate export with its palette and transform information. The exports contain the decoded source artwork, not necessarily the complete image the game produces after runtime effects.

The Maps page already draws its background from the opened disc. Decoding the smaller unit sprites used on the map is separate work. The application does not ship with extracted game graphics.

## Projects and PCSX2

A `.bwproj` file holds the edits, leaving the original ISO untouched. The change review shows what differs from the disc, including changes to scripts and text, and can export a JSON manifest. Export creates a new ISO that can be opened in PCSX2.

Compare lets you inspect item, character and class differences between two discs. When PCSX2 is running on the same computer, the Live Game page can modify units in memory, try rule changes and select a different next sortie. These live changes do not rewrite the disc, saves or memory cards.

For implementation status and unresolved game formats, see the [roadmap](docs/ROADMAP.md) and [research notes](docs/RESEARCH.md). Earlier versions are listed in the [release history](docs/RELEASE_HISTORY.md).

## Building

You need the .NET 9 SDK. From the repository root:

```powershell
dotnet build Tests -c Release
dotnet Tests\bin\Release\net9.0\Berwick.Tests.dll "<Japanese ISO>" "<English ISO>"
dotnet publish App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "..\Berwick Workshop"
```

The ISO arguments are optional. Without them, the tests use their offline fixtures. A third path can check a Redux modified disc. Game image tests make temporary exports and remove them afterward, so allow at least 9 GB of free space. Original images are opened read only.

No game files are needed to build the application. The `Core` project handles formats, editing and export; `App` contains the Windows interface; `Tests` holds the checks. The `reference` folder includes credited material from the MinN-11 editor and other data sources. Development and release procedures are in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and [docs/RELEASING.md](docs/RELEASING.md).
