# Berwick Workshop roadmap

Goal: one offline, single-player Windows tool for Berwick Saga (PS2) that covers everything useful from **FEBuilderGBA** (GBA Fire Emblem ROM suite) and **ReWeaver** (Fire Emblem: Fortune's Weave mod and save editor), adapted to this game. It must support both the Japanese retail disc and the English v2.3 translation, each with its own verified offsets.

Status keys: `done`, `next`, `planned`, `research` (format or address not yet proven; see the Redux research docs).

## Feature parity map

| Source tool feature | Berwick Workshop equivalent | Milestone | Status |
|---|---|---|---|
| ReWeaver: verify game dump, detect version | Exact SHA-256 + ELF CRC profiles for JP and EN | M0 | done |
| FEBuilder: item editor | Item browser (read) then item editing | M0 / M2 | done: edit in project (v0.4.0), export to disc (v0.5.0) |
| ReWeaver: diff between game versions | JP vs EN item comparison by stable ID | M0 | done |
| FEBuilder: unit editor | Character bases, growths, skills, inventory, supports | M1 / M4 | read (v0.2.0); edit level, bonuses, growths, weapon levels and growth (v0.6.0); skills, starting items, bracket (v0.7.0); supports not yet |
| FEBuilder: game rule patches | Formulas, level-up rules, brackets as named rules written into the program | M6 | research plan written |
| FEBuilder: table expansion | New items, classes and units, first by reusing free slots, later by growing tables | M7 | new items in free slots done (v0.8.0); classes and units not yet |
| FEBuilder: class editor | Class bases, growths, caps, attributes, skills, weapon permissions | M1 / M4 | read (v0.3.0); edit bases, move, experience, caps (v0.6.0), class skills (v0.7.0); class growths and attributes still research |
| FEBuilder: skill tables | Learned skills per character | M1 | read done; ID numbering verified (see below) |
| ReWeaver: .rwmod changes-only project | `.bwproj` JSON storing only changed fields, keyed by ID and edition | M2 | done (v0.4.0) |
| ReWeaver: Save mod / Export mod split | Save writes project only; Export is a separate, confirmed step | M2 / M3 | done (v0.5.0) |
| ReWeaver: diff between versions (modded vs vanilla) | Open a modified disc (exports, Redux) and compare it with the original | M3 | done (v0.5.0) |
| FEBuilder: undo history | Undo/redo per edit, change list panel | M2 | undo/redo done; change list panel not yet |
| FEBuilder: lint / error checker | Validation rules plus hazard list from `editor_hazards.json` | M2 | planned |
| FEBuilder: UPS export | Export patched ISO copy and an xdelta patch against the clean disc | M3 | ISO export done (v0.5.0); xdelta patch not yet |
| ReWeaver: export to emulator folder, backups | Export into a chosen folder, keep previous exports in a backups folder | M3 | planned |
| ReWeaver: first-time setup | Setup: locate ISOs, PCSX2 folder, memory cards, export folder | M3 | planned |
| FEBuilder: text editor | Dialogue and menu text (TARC archives in DATA*.DAT) | M5 | research (`extract_berwick_tarc_file_v2.py`) |
| FEBuilder: graphics import/export | Portraits, sprites, icons viewer then import | M8+ | research |
| FEBuilder: map editor, event editor | Chapter maps, unit placement, events | M8+ | research |
| FEBuilder: emulator RAM link | PCSX2 PINE live viewer (read-only first), launch PCSX2 with export | M8+ | research (`pine_*.py`) |
| FEBuilder: patch manager | PNACH patch manager with per-CRC files, community data packs (Redux safe core) | M6 / M8+ | planned |
| ReWeaver: save data editor with auto backup | Memory card (.ps2) browser, then save editing with automatic backups | M8+ | research (no saves yet) |
| FEBuilder: song insertion | Audio browse/replace | M8+ | research |
| FEBuilder: report/log tool | Log folder and "copy diagnostics" button | M3 | planned |
| Berwick specific | Food table, Pavilion diner selection, hidden values (happiness, recruit progress) | M8+ | research |

Excluded on purpose: online features, machine translation, telemetry, anything that changes the user's original ISOs.

## Milestones

### M0 Read-only inspector (done, v0.1.0)
Disc identity, 646 items, search, filter, JP/EN comparison. Shipped to `../Berwick Workshop`.

### M1 Read-only data browsers (characters in v0.2.0, classes in v0.3.0; supports remain)
1. Port character table reader from `reference/BWSTableEditors.py` (`set_base`, `set_growth`, `set_skill`, `set_item`, `set_support`) into `Core/Units.cs`, read side only.
2. Port class reader (`set_class_base`, `set_class_growth`, `set_class_caps`, `set_class_attribute`, `set_class_skill`) into `Core/Classes.cs`.
3. Validate against known values (EmblemWiki class and character tables) on both ISOs before showing any field. Unproven fields are shown as raw numbers with an "unconfirmed" label, never guessed.
4. Learned skills: show raw six level and six skill bytes only. See `berwick-redux2-r6/docs/OPEN_REVERSE_ENGINEERING.md` validation set.
5. App: add Characters and Classes pages to the sidebar.

Verified while building the Characters page (both discs, checked against EmblemWiki tables):
- Unit and growth tables: both copies identical on both discs; all 35 characters identical between editions.
- Level, weapon levels (stored x10), HP/Str/Def/Agi growths, weapon growths, personal skill bits, learned skills: match published tables.
- Stat bonuses over class base: confirmed by the Adel and Leon comparison (same class and level), and again by class base plus bonus matching published base stats for 12 characters in 11 classes (Mind included).

Verified while building the Classes page (v0.3.0):
- Class ID of each starting character is byte 19 of the unit record (the only byte that matched all 12 known classes; unit records begin with the ASCII tag `UNIT`).
- Class records are 100 bytes at `(id - 1) * 100`. Both copies identical on both discs; all 217 classes identical between editions.
- Class base stats are five packed 5-bit fields, in disc order: Strength (byte 0 bit 0), Agility (byte 0 bit 5), Defense (byte 1 bit 2), Mind (byte 2 bit 0), HP (byte 2 bit 5). The upstream editor labels these HP, Strength, Speed, Defense, Mind: every label is shifted by one, which explains community reports that class HP edits change a different stat. Any writer must use the corrected order.
- Move (byte 8 low nibble) and experience value (byte 4, 7 bits) match the published class table for Lord, Spear Knight, Horseman and Thief, both mounted and dismounted forms.
- Class skill bits at byte 12 use the same order as personal skill bits; mounted forms carry Canto, Thief carries Locktouch and Hide.
- Weapon level caps (6-bit fields from byte 28): no starting character's weapon level exceeds its class cap (66 levels checked). No published source to compare values against.
- Not shown, unverified: class growth rates (bytes 24 to 28). Upstream labels give Thieves 50% Defense and 10% Agility, which looks wrong; reading them in the personal growth order (HP, Str, Def, Agi, Mind) gives Thieves 50% Agility and mages 30% Agility and Mind, which looks right, but needs a source. Upstream mount and type fields overlap the experience field and are also unverified.
- Learned skill IDs use the skill list without the four unused slots (`Skills2` upstream). 13 of 13 level-learned skills match. The upstream writer already uses this list, so the community Overwatch to Silence report is not explained by ID numbering.
- Mind growth is stored at twice the published value for every mage checked. Shown as stored.
- Item range uses the game's own counting: 0 means adjacent only (matches the wiki).

### M2 Projects and item editing (done in v0.4.0, except the changes panel)
1. `.bwproj` format: JSON with `format`, `formatVersion` (1), `edition`, `editionSha256`, `appVersion`, and `changes` of `{table, id, name, field, original, value}`. Loading refuses a different edition, any change whose `original` no longer matches the disc, unknown fields, duplicates, and out-of-range values. Saves are atomic (temp file then rename).
2. Editable item fields (`Core/Editing.cs`, `ItemEncoder.Fields`): might, accuracy, weight, minimum and maximum range, critical, level, base price, durability grade, durability pool. Limits are the bit widths; minimum range may not exceed maximum range. Type and effects are not editable yet.
3. Writer tested as the exact inverse of the decoder: for every field at its minimum, maximum and middle value on random records, only that field's bits change. Edits on both real discs change only the edited bits.
4. Undo/redo (one step per Apply), unsaved marker in the title and project bar, prompts before closing or opening another disc or project. A change list panel is still to do.
5. Writing both table copies to a disc is part of M3; the session already produces the full edited 56-byte record per item (`EditSession.EditedBytes`).

### M3 Export (ISO export and modified discs done in v0.5.0)
1. Done. `Core/Export.cs`: one streaming pass copies the source, hashes it (must equal the hash taken at open, else abort), applies byte patches in memory as buffers pass, and hashes the output. Writes to `<name>.partial`, reads every patched range back, then renames. Partial file is deleted on cancel or error. Refuses the source path, overlapping or out-of-range patches, and too little free space. Each edited item is written to both table copies (56 bytes each).
2. Done. Modified-disc recognition: if the full hash is unknown but the executable SHA-256 and the DATA3.DAT length match a profile, the disc loads with that profile and `IsModified`. Exports and Redux v1.4.1 open this way. Redux v1.4.1 differs from English v2.3 in 103 items, 29 characters and 5 classes; its item table copies agree.
3. Verified: a real export of an edited English disc (4,321,804,288 bytes, 12 bytes changed) reopens as Modified English v2.3 with exactly the project's item values and unchanged characters and classes; the in-app export, compared against the live session, matches on all 646 items. Not yet tested by booting an export in PCSX2.
4. Not done: xdelta patch output (DeltaPatcher compatible). The user's `bws-rc-v2.3.xdelta` could validate a VCDIFF decoder before writing an encoder. Also not done: setup screen, export folder preference, logs.

### M4 Character and class editing (done in v0.6.0 for proven fields)
- `EditSession` now covers four tables: `items`, `characters` (unit record, 0x100 bytes at the roster offset), `growths` (32 bytes per character ID) and `classes` (100 bytes per class ID). One Apply can span tables and is one undo step. `.bwproj` format version 1 is unchanged (it already stored `table`).
- Field definitions: `CharacterFields.Record`, `CharacterFields.Growth`, `ClassFields.Fields` in `Core/Editing.cs`. `TableField.Signed` handles the 5-bit stat bonuses; `TableField.Display` handles weapon levels (stored x10, shown with one decimal) and weapon growth (stored /10, shown in steps of 10).
- Export writes only the bytes that differ, as runs, to both copies of every changed table (`Exporter.RecordLocations`).
- Verified: bit-isolation inverse tests for every field; field reads equal decoder output on random records; real export with item, character, growth and class edits reopens with exactly those values, everything else unchanged and both table copies still equal.
- Not editable yet: starting items (needs an item picker), personal and class skills (bit sets, decoded and verified, so this is a good next step), learned skills (cause of community corruption reports still unknown), class growths and attributes (unverified), character class (risky: mounted forms, sprites).

### M4b Skills, starting items, growth bracket (done in v0.7.0)
- Personal skills (unit record byte 56) and class skills (class record byte 12) are editable as one flag per skill, using the same bit order the decoders verified against the wiki.
- Starting items: slots 1 to 5 (unit record 0xBC + 8 per slot: item ID 16-bit, durability or uses 8-bit at +2 bit 4). The record has room for 8 slots, but only 5 are proven in use (Owen fills all 5); slots 6 to 8 stay locked until the game's inventory size is confirmed. Item IDs must exist in the item table.
- Growth bracket: growth record byte 2 bits 5 to 6, values 1 to 3. A player write-up explains the system (stats stay within a band around start + growth x levels) and names Daoud and Lynette as the only uncorrected characters, which are exactly the two with value 1; Reese (value 2) has a weak ±3 correction. Labels: None, Loose (±3), Tight. See `RESEARCH.md`.

### M5 Text (names and descriptions)
Needed before data expansion is useful: a new item or class has no name in game until its text entry can be written. Steps: locate the item, class, character and skill name tables and descriptions in the DATA files (TARC archives; see `..\berwick-redux2-r6\tools\extract_berwick_tarc_file_v2.py`) and the font encoding for each edition; read-only text browser; then editing with length limits; then export. Japanese and English text are different formats and must be handled separately.

### M6 Game rules (formulas, level ups, brackets)
Best lead (see `RESEARCH.md`): working 2005 CodeBreaker codes for the Japanese release change experience gain, guarantee level ups, guarantee shield blocks and raise class skill activation. They are encrypted; decrypting them with a CodeBreaker v7 converter gives exact program addresses for that logic in the Japanese executable, which can then be matched to the English executable by surrounding instruction patterns.
The formulas live in the game program (`SLPS_254.97`), not in the data tables, so changing them means patching MIPS code. Published formulas to target first (EmblemWiki "Berwick Saga Calculations"):
- Avoid = Attack Speed x 2 (+ terrain, x 2 terrain at range 2+); Attack Speed = Speed minus (weapon weight + shield weight minus Strength / 2).
- Accuracy = weapon precision x 10 + weapon skill + bonuses.
- Critical rate = weapon critical + (weapon skill minus 50) + bonuses; critical damage = damage + random 10 to 20.
- Equipment failure = 5% per weapon rank above the unit's level (7 with Advanced).
- Injury base rates 9 (knives) and 3 (others); cripple thresholds 0.75, 0.50, 0.30.
- Orb experience ranges per level band; empty level up gives +1 Luck.
Method, per edition: find candidate code by searching the executable for the constants in context (for example `50` in a subtract near weapon skill, `x 2` shifts near speed), confirm each with a PCSX2 debugger breakpoint during a battle forecast, record the address and original instruction, then expose it as a named rule with safe limits. Export writes the changed instructions into the copied executable (same size, in place) and can also produce a PNACH file for testing without a new ISO. Growth brackets: measure with save states by levelling the same unit with each bracket value many times and comparing stat gains. Every rule needs an in-game check before it is offered.

### M7 Data expansion (new items, classes, units)
Stage A, reuse free slots (lowest risk):
- Items (done in v0.8.0): New item copies the whole 56-byte record of an existing item (as edited in the project) into one of the 156 placeholder slots, gives it a project name, and then edits like any item. Free slots exclude any item a character starts with. Projects with new items save as format version 2 (`copies` and `names` lists), so older versions refuse them. Item Type is now editable. Still open: shops, chests and drops are not mapped, so a slot could be referenced by chapter data; the in-game name stays the slot's own until M5.
- Classes: 27 class IDs inside the known range have no name in `classes.csv` and records after 0xF4 also hold data. They may be enemy or unused classes; confirm which are unused before offering them.
- Units: a new recruitable unit also needs a chapter event to add it to the army, so this depends on chapter script research (M8+, maps and events). Reusing an existing but unused unit record is the likely first route.
- Names for anything new depend on M5.
Stage B, grow tables beyond their fixed size: the bytes after the last item record belong to another table, so growing a table means moving it to free space and changing every place the program finds it. Research the program's table loading first; this is the highest risk feature and comes last.

### M8 and later
PCSX2 link and PNACH, saves, patch packs, audio, maps and events, and Berwick specific systems (food, Pavilion). Each starts with a read-only viewer and a round-trip test before any writes.

## Rules that apply to every milestone
- Never modify the original ISOs, memory cards, save states or PCSX2 settings in place.
- Offsets are per edition. Never reuse a Japanese address on English or the reverse.
- Item and unit identity is the numeric ID, never the name (two Pickaxes exist: 0x0B0 and 0x1FA).
- No placeholder controls for unfinished features.
- Interface and release text stays plain and finished: no placeholder or developer wording.
