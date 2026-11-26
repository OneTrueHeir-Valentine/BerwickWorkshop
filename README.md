# Berwick Workshop

Berwick Workshop is a Windows editor for *Berwick Saga: Lazberia Chronicle, Chapter 174* on PlayStation 2. It reads the Japanese retail game and the English v2.3 translation, using the verified data locations for each edition. Changes are kept in a project until you export a new disc image.

[Download the latest Windows release](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/releases/latest). Berwick Workshop needs the [Microsoft .NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). ISO patches also need the [Microsoft Visual C++ Redistributable (x64)](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist), which many PCs already have. Install them first if you do not have them, then extract the archive and run `Berwick Workshop.exe`.

## What you can edit

The Items page covers weapon and equipment stats, durability, resistances, bonuses while equipped and known special effects. Shop inventories, the Stable, Atelier recipes and furniture prices have their own editors. New items can be made using the game's spare item records.

Characters have editable levels, classes, stat bonuses, growths, weapon skills, horse HP and starting equipment. The page also covers bags, partners, bonds and mercenary fees, with expected stats shown for each level. The Armies page gives similar access to enemies, bosses, guests and story units, including their items, drops and horse HP. Class base stats, movement types, weapon caps, shield permissions and class skills are edited separately, so changes to a class can affect more than one unit.

Movement costs can be changed for each terrain type, along with terrain avoid, recovery and the places where flying units may stop. The Skills page documents the game's 90 skills and the parts of their behavior that can be edited. The Rules and Mechanics pages cover combat calculations and settings such as Parry, shields, durability, experience and stat limits. Settings that have not been verified sufficiently in the running game are not offered as editable rules.

The Promotions page lists the 16 promotions read from the original events, with the quest, level and weapon level each one checks and the class the character becomes. Required levels and weapon levels can be changed, and so can the destination class for characters whose events have been checked for it. Promotion changes have not been tested through a full playthrough, so try them on a separate disc image and save.

## Randomizer

The Randomizer page changes starting items, character stat bonuses and growth rates, or starting weapon levels and weapon growth. Each option has its own settings and a seed, so the same settings give the same result. A preview lists every change by character before anything is applied, and applying the changes is a single step you can undo. Horses are never handed out as ordinary items, and items that the game's events give to particular characters are left alone. Settings can be saved as a profile and loaded again later.

## Maps and events

Maps displays the painted battle maps, terrain, deployment hexes, starting units, later arrivals and event spots. Existing unit positions, deployment positions and limits, and some reinforcement locations and turns can be changed there. A starting unit removed in a project can be put back on its original hex; units that have no starting position on the original disc cannot be added.

The Events page opens the game's scripts as readable commands. You can edit numbers, individual commands and complete scripts, including the conditions that determine when units arrive. Some event structures still require direct work in the Events editor rather than a dedicated Maps control.

## Graphics

Battle Animations plays the sets and palettes assigned to classes and characters, including sets that no class currently uses. Assignments can be changed, and the decoded animation frames can be exported as transparent PNGs or sprite sheets. JSON files record frame order, timing and alignment. Effect artwork has a separate export with its palette and transform information. The exports contain the decoded source artwork, not necessarily the complete image the game produces after runtime effects.

The Maps page already draws its background from the opened disc. Decoding the smaller unit sprites used on the map is separate work. The application does not ship with extracted game graphics.

## ISO patches

ISO patches (Ctrl+Shift+P) makes a small patch from two disc images or from the open project, and applies one patch or several in order, so changes can be shared without sharing a disc image. **Compatible**, the default, adds no extra compression. **Compact** adds LZMA compression; the program that applies it must support LZMA, and the patch is not always smaller.

Each patch Workshop makes has a manifest next to it (`<patch>.json`) that records the original image, the patch and the expected result by SHA-256. A patch from elsewhere can be applied on its own without one, but applying several in order needs a manifest for each. A manifest shows that a file is the one described; it does not show who made a patch. Original images and existing files are never changed or replaced.

Patches are made and applied by the official xdelta3 3.2.2, which comes in the package's `tools` folder and is checked before every use. It needs the Microsoft Visual C++ Redistributable (x64). The exact program, its source and its licences are listed in [third-party/xdelta3](third-party/xdelta3/README.txt).

## Projects and PCSX2

A `.bwproj` file holds the edits, leaving the original ISO untouched. The change review shows what differs from the disc, including changes to scripts and text, and can export a JSON manifest. Export creates a new ISO that can be opened in PCSX2.

Compare lets you inspect item, character and class differences between two discs. When PCSX2 is running on the same computer, the Live Game page can modify units in memory, try rule changes and select a different next sortie. These live changes do not rewrite the disc, saves or memory cards.

Source changes and outstanding bugs are recorded in the [GitHub issues](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/issues).

## Building

You need the .NET 10 SDK. From the repository root:

```powershell
dotnet build Tests -c Release
dotnet Tests\bin\Release\net10.0\Berwick.Tests.dll "<Japanese ISO>" "<English ISO>"
dotnet publish App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "..\Berwick Workshop"
```

The ISO arguments are optional. Without them, the tests use their offline fixtures. A third path can check a Redux modified disc. Game image tests make temporary exports and remove them afterward, so allow at least 9 GB of free space. Original images are opened read only.

No game files are needed to build the application. The `Core` project handles formats, editing and export; `App` contains the Windows interface; `Tests` holds the checks. The `reference` folder contains credited source data. Its licensing and attribution details are in [LICENSING.md](LICENSING.md).

## License and contributions

Berwick Workshop's original source code is licensed under the [GNU GPL, version 3 or later](LICENSE). The [licensing notes](LICENSING.md) explain the separate rights for the game's content and credited reference material. To contribute code or corrections, see [Contributing](CONTRIBUTING.md). Contributors retain copyright in their work.
