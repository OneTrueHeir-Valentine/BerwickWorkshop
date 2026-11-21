# Release history

These summaries describe the available public releases. Each version has its own GitHub release page with the Windows download. See the [latest releases](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/releases) for attached archives.

## Version 0.118.0

[Berwick Workshop 0.118.0](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/releases/tag/v0.118.0) adds a Review Changes window that shows original and edited values, text, scripts and the files affected by a project. It also reports known shared dependencies and patch conflicts. The change list can be saved as JSON.

Battle animations can be exported as transparent PNG frames or sprite sheets with JSON files describing their order, timing and alignment. Expanded canvases retain artwork outside the original preview area, while the earlier 288 by 288 export remains available. Effect artwork can be exported separately with its timing, palette and transform information. These exports contain the source images, not the game's fully composited runtime effects.

Editor panels can also be resized.

Download `Berwick.Workshop.0.118.0.zip` from the release page, extract it and run `Berwick Workshop.exe`. The archive includes the .NET runtime; no installation is required.

## Version 0.114.0

[Berwick Workshop 0.114.0](https://github.com/OneTrueHeir-Valentine/BerwickWorkshop/releases/tag/v0.114.0) edits data in the Japanese retail release of *Berwick Saga* and the English v2.3 translation. It opens an uncompressed game ISO and saves changes as a `.bwproj` project. Export writes a separate disc image without changing the original.

Items, characters, classes, enemy armies, terrain and movement values can be edited directly. The Maps page displays the battle maps and their starting positions, deployment areas and reinforcements. It can move existing units, change deployment positions and limits, and adjust existing arrival coordinates and turns. Other editors cover shops, the Stable, the Atelier, furniture, dishes, the Kingfisher Pavilion, game text and event scripts. Game rules include settings for experience, growth, shields, skills and durability.

The release also includes class animation and map sprite assignments, along with a Live Game connection for testing changes in a running PCSX2 session.

Download `Berwick.Workshop.0.114.0.zip` from its release page and run `Berwick Workshop.exe` after extraction. The included `README.txt` describes the pages and settings.

The GitHub release pages retain their original tags and attached files.
