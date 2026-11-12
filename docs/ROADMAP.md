# Berwick Workshop roadmap

Goal: one offline, single-player Windows tool for Berwick Saga (PS2) that covers everything useful from **FEBuilderGBA** (GBA Fire Emblem ROM suite) and **ReWeaver** (Fire Emblem: Fortune's Weave mod and save editor), adapted to this game. It must support both the Japanese retail disc and the English v2.3 translation, each with its own verified offsets.

Status keys: `done`, `next`, `planned`, `research` (format or address not yet proven; see the Redux research docs).

## Feature parity map

| Source tool feature | Berwick Workshop equivalent | Milestone | Status |
|---|---|---|---|
| ReWeaver: verify game dump, detect version | Exact SHA-256 + ELF CRC profiles for JP and EN | M0 | done |
| FEBuilder: item editor | Item browser (read) then item editing | M0 / M2 | read done |
| ReWeaver: diff between game versions | JP vs EN item comparison by stable ID | M0 | done |
| FEBuilder: unit editor | Character bases, growths, skills, inventory, supports | M1 / M4 | planned |
| FEBuilder: class editor | Class bases, growths, caps, attributes, skills, weapon permissions | M1 / M4 | research (stat bit mapping unproven) |
| FEBuilder: skill tables | Learned skills per character | M1 | research (encoding known to corrupt) |
| ReWeaver: .rwmod changes-only project | `.bwproj` JSON storing only changed fields, keyed by ID and edition | M2 | planned |
| ReWeaver: Save mod / Export mod split | Save writes project only; Export is a separate, confirmed step | M2 / M3 | planned |
| FEBuilder: undo history | Undo/redo per edit, change list panel | M2 | planned |
| FEBuilder: lint / error checker | Validation rules plus hazard list from `editor_hazards.json` | M2 | planned |
| FEBuilder: UPS export | Export patched ISO copy and an xdelta patch against the clean disc | M3 | planned |
| ReWeaver: export to emulator folder, backups | Export into a chosen folder, keep previous exports in a backups folder | M3 | planned |
| ReWeaver: first-time setup | Setup: locate ISOs, PCSX2 folder, memory cards, export folder | M3 | planned |
| FEBuilder: text editor | Dialogue and menu text (TARC archives in DATA*.DAT) | M5 | research (`extract_berwick_tarc_file_v2.py`) |
| FEBuilder: graphics import/export | Portraits, sprites, icons viewer then import | M6 | research |
| FEBuilder: map editor, event editor | Chapter maps, unit placement, events | M7 | research |
| FEBuilder: emulator RAM link | PCSX2 PINE live viewer (read-only first), launch PCSX2 with export | M8 | research (`pine_*.py`) |
| FEBuilder: patch manager | PNACH patch manager with per-CRC files, community data packs (Redux safe core) | M8 / M10 | planned |
| ReWeaver: save data editor with auto backup | Memory card (.ps2) browser, then save editing with automatic backups | M9 | research (no saves yet) |
| FEBuilder: song insertion | Audio browse/replace | M11 | research |
| FEBuilder: report/log tool | Log folder and "copy diagnostics" button | M3 | planned |
| Berwick specific | Food table, Pavilion diner selection, hidden values (happiness, recruit progress) | M11 | research |

Excluded on purpose: online features, machine translation, telemetry, anything that changes the user's original ISOs.

## Milestones

### M0 Read-only inspector (done, v0.1.0)
Disc identity, 646 items, search, filter, JP/EN comparison. Shipped to `../Berwick Workshop`.

### M1 Read-only data browsers (next)
1. Port character table reader from `reference/BWSTableEditors.py` (`set_base`, `set_growth`, `set_skill`, `set_item`, `set_support`) into `Core/Units.cs`, read side only.
2. Port class reader (`set_class_base`, `set_class_growth`, `set_class_caps`, `set_class_attribute`, `set_class_skill`) into `Core/Classes.cs`.
3. Validate against known values (EmblemWiki class and character tables) on both ISOs before showing any field. Unproven fields are shown as raw numbers with an "unconfirmed" label, never guessed.
4. Learned skills: show raw six level and six skill bytes only. See `berwick-redux2-r6/docs/OPEN_REVERSE_ENGINEERING.md` validation set.
5. App: add Characters and Classes pages to the sidebar.

### M2 Projects and item editing
1. `.bwproj` format: edition hash, list of `{table, id, field, old, new}`. Loads only on a matching edition.
2. Editable item fields with range checks from the bit widths in `Core/Items.cs`.
3. Write both table copies (primary and secondary) identically. Test that a decode of the written bytes returns the edit and that no other byte changed.
4. Undo/redo, unsaved changes marker, changes panel.

### M3 Export
1. Copy the clean ISO to a new file, patch DATA3 regions in place (tables do not change size), verify by re-reading.
2. Optional xdelta export (compatible with DeltaPatcher).
3. Setup screen, export folder, backups, logs.

### M4 Character and class editing
Only for fields proven in M1. Shared-class warning when a class is used by playable units.

### M5 to M11
Text, graphics, maps and events, PCSX2 link and PNACH, saves, patch packs, audio and Berwick specific systems. Each starts with a read-only viewer and a round-trip test before any writes.

## Rules that apply to every milestone
- Never modify the original ISOs, memory cards, save states or PCSX2 settings in place.
- Offsets are per edition. Never reuse a Japanese address on English or the reverse.
- Item and unit identity is the numeric ID, never the name (two Pickaxes exist: 0x0B0 and 0x1FA).
- No placeholder controls for unfinished features.
- Interface and release text stays plain and finished: no placeholder or developer wording.
