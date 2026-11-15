# Berwick Workshop roadmap

Goal: one offline, single-player Windows tool for Berwick Saga (PS2) that covers everything useful from **FEBuilderGBA** (GBA Fire Emblem ROM suite) and **ReWeaver** (Fire Emblem: Fortune's Weave mod and save editor), adapted to this game. It must support both the Japanese retail disc and the English v2.3 translation, each with its own verified offsets.

Status keys: `done`, `next`, `planned`, `research` (format or address not yet proven; see the Redux research docs).

## Feature parity map

| Source tool feature | Berwick Workshop equivalent | Milestone | Status |
|---|---|---|---|
| ReWeaver: verify game dump, detect version | Exact SHA-256 + ELF CRC profiles for JP and EN | M0 | done |
| FEBuilder: item editor | Item browser (read) then item editing | M0 / M2 | done: edit in project (v0.4.0), export to disc (v0.5.0) |
| ReWeaver: diff between game versions | JP vs EN item comparison by stable ID | M0 | done |
| FEBuilder: unit editor | Character bases, growths, skills, inventory, supports | M1 / M4 | read (v0.2.0); edit level, bonuses, growths, weapon levels and growth (v0.6.0); skills, starting items, bracket (v0.7.0); bag contents, partners (supports) and a starting item randomizer (v0.30.0); mercenary fees (v0.31.0) |
| FEBuilder: game rule patches | Formulas, level-up rules, brackets as named rules written into the program | M6 | Parry, battle experience, six stat limits, Shield block and Shield guard (v0.14.0); battle stat choices for attack, defense, magic defense, attack speed, weight and healing (v0.34.0); what hit and critical add and the critical threshold (v0.35.0); minimum damage (v0.36.0) |
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
| FEBuilder: text editor | Dialogue and menu text (TARC archives in DATA*.DAT); needed for the furniture shop list, whose prices are part of the menu text | M5 | research (`extract_berwick_tarc_file_v2.py`) |
| FEBuilder: graphics import/export | Portraits, sprites, icons viewer then import | M8+ | research |
| FEBuilder: map editor, event editor | Chapter maps, unit placement, events (armies and bosses editable since v0.32.0: class, level, bonuses, skills, items, drops) | M8+ | event script viewer done (v0.28.0, Events page: archives, decompression and the script format decoded); script compression and same-size archive writing done, first use stable prices (v0.29.0); map viewer done (v0.52.0, Maps page: painted map with its starting layers, hex grid, terrain, spots, armies; start positions of every unit and the deployment hexes in v0.53.0, reinforcements with their turns in v0.54.0); general script editing and map editing not yet |
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
2. Editable item fields (`Core/Editing.cs`, `ItemEncoder.Fields`): might, accuracy, weight, minimum and maximum range, critical, level, base price, durability grade, durability pool and type (since v0.8.0). Limits are the bit widths; minimum range may not exceed maximum range. Since v0.25.0 also bonuses while equipped (strength, agility, defense, mind, hit, avoid, critical evade), five resistances and 33 effect flags named from the items that carry them; the special effect (byte 27 = 100 + effect, byte 26 chance or amount) since v0.27.0; two unclear fields (byte 1 bits 3 to 6 on spears and lances, byte 17 bits 2 to 5 on a few swords) are not editable yet.
3. Writer tested as the exact inverse of the decoder: for every field at its minimum, maximum and middle value on random records, only that field's bits change. Edits on both real discs change only the edited bits.
4. Undo/redo (one step per Apply), unsaved marker in the title and project bar, prompts before closing or opening another disc or project. A change list panel is still to do.
5. Writing both table copies to a disc is part of M3; the session already produces the full edited 56-byte record per item (`EditSession.EditedBytes`).

### M3 Export (ISO export and modified discs done in v0.5.0)
1. Done. `Core/Export.cs`: one streaming pass copies the source, checks its hash against the opened disc, applies byte patches and hashes the output. A uniquely named temporary file is created exclusively, checked, then renamed without replacing an existing destination. Cancellation and errors remove only that export's temporary file. Export refuses overlapping or out-of-range patches, too little free space, a mismatched project/disc pair, and original-byte mismatches at edited locations. Each changed byte run is written to both table copies; differing bytes in either copy are never silently replaced.
2. Done. Modified-disc recognition: if the full hash is unknown but the executable SHA-256 and the DATA3.DAT length match a profile, the disc loads with that profile and `IsModified`. Exports and Redux v1.4.1 open this way. Redux v1.4.1 differs from English v2.3 in 103 items, 29 characters and 5 classes; its item table copies agree.
3. Verified: a real export of an edited English disc (4,321,804,288 bytes, 12 bytes changed) reopens as Modified English v2.3 with exactly the project's item values and unchanged characters and classes; the in-app export, compared against the live session, matches on all 646 items. Booted in PCSX2 in v0.9.0: an exported disc's item, rule and experience changes all showed in the first battle (see M6). Not yet tested over a full campaign.
4. Not done: xdelta patch output (DeltaPatcher compatible). The user's `bws-rc-v2.3.xdelta` could validate a VCDIFF decoder before writing an encoder. Also not done: setup screen, export folder preference, logs.

### M4 Character and class editing (done in v0.6.0 for proven fields)
- `EditSession` now covers four tables: `items`, `characters` (unit record, 0x100 bytes at the roster offset), `growths` (32 bytes per character ID) and `classes` (100 bytes per class ID). One Apply can span tables and is one undo step. `.bwproj` format version 1 is unchanged (it already stored `table`).
- Field definitions: `CharacterFields.Record`, `CharacterFields.Growth`, `ClassFields.Fields` in `Core/Editing.cs`. `TableField.Signed` handles the 5-bit stat bonuses; `TableField.Display` handles weapon levels (stored x10, shown with one decimal) and weapon growth (stored /10, shown in steps of 10).
- Export writes only the bytes that differ, as runs, to both copies of every changed table (`Exporter.RecordLocations`).
- Verified: bit-isolation inverse tests for every field; field reads equal decoder output on random records; real export with item, character, growth and class edits reopens with exactly those values, everything else unchanged and both table copies still equal.
- Starting items and personal/class skills became editable in M4b; class movement type became editable in v0.13.0. Still not editable: learned skills (cause of community corruption reports unknown), class growths and other unverified attributes, character class (mounted forms and sprites need research).

### M4b Skills, starting items, growth bracket (done in v0.7.0)
- Personal skills (unit record byte 56) and class skills (class record byte 12) are editable as one flag per skill, using the same bit order the decoders verified against the wiki.
- Starting items: slots 1 to 5 (unit record 0xBC + 8 per slot: item ID 16-bit, durability or uses 8-bit at +2 bit 4). The record has room for 8 slots, but only 5 are proven in use (Owen fills all 5); slots 6 to 8 stay locked until the game's inventory size is confirmed. Item IDs must exist in the item table.
- Growth bracket: growth record byte 2 bits 5 to 6, values 1 to 3. A player write-up explains the system (stats stay within a band around start + growth x levels) and names Daoud and Lynette as the only uncorrected characters, which are exactly the two with value 1; Reese (value 2) has a weak ±3 correction. Labels: None, Loose (±3), Tight. See `RESEARCH.md`.

### M5 Text (names and descriptions)
Needed before data expansion is useful: a new item or class has no name in game until its text entry can be written. Steps: locate the item, class, character and skill name tables and descriptions in the DATA files (TARC archives; see `..\berwick-redux2-r6\tools\extract_berwick_tarc_file_v2.py`) and the font encoding for each edition; read-only text browser; then editing with length limits; then export. Japanese and English text are different formats and must be handled separately.

### M6 Game rules (formulas, level ups, brackets)
Status: the 2005 Japanese codes are decrypted and the locations they patch are identical in both executables (see `RESEARCH.md`). Confirmed in a running game: Parry (`0x002274EC`), experience gain (`0x00224580`), six stat limits in both routines, and Shield block (`0x0022A638`). Shield guard rewrites 26 words starting at `0x0022A640`. The random range function, percent roll and an unidentified rate formula are located but not offered as rules.

The v0.9.0 rule at `0x002274EC` was mislabelled Shield block in v0.10.0 and corrected to Parry in v0.14.0. Its ID, `shieldParry` key and encodings are unchanged, so older projects retain their effects. Parry offers Normal, Always and Never; battle experience offers Normal or fixed 1 to 100. Rules are an editable table whose records hold all instruction words for a setting. Every word must match a supported encoding, including words shared by settings; mixed or unknown code is rejected. Export patches `SLPS_254.97` inside the copied ISO. Modified-disc recognition hashes the executable with recognised rules restored to the original instructions.
Confirmed in game: an exported English disc with Rapier might 7 to 20, parry Always and experience 50 was booted in PCSX2 2.9.78 by the user; all three changes showed in the first battle. This is also the first in-game confirmation of an exported data table edit.
Six stat limits are complete (v0.11.0). Open rule work includes level/experience caps and an experience multiplier, which needs free instruction words inside the executable; the 2005 multiplier used RAM below it, which an exported disc cannot use.
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

### Stat limits (done in v0.11.0)
- The limits exist twice: in the routine that measures a gain (`0x00174A90`, table `0x004A4060`) and in the routine that adds and stores it (`0x00174550`, table `0x004A4020`; the store itself goes through the field writers in table `0x004A3FE0`). Level ups call the add-and-store routine nine times and the measuring routine only for experience.
- Live test 1: zeroing the measuring copy did not stop a level-up Strength gain. Live test 2: zeroing both copies stopped every stat gain on Reese's level up (level 2 to 3, all stats unchanged). Confirmed.
- Rules on the Rules page since v0.11.0: HP bonus limit 0 to 127 (default 50); Strength, Agility, Defense and Mind bonus limits 0 to 15 (default 15, the field maximum); Luck limit 0 to 31 (default 20). Each changes both copies. Level limit (30) needs three linked constants and its own test.

### Mechanics reference (started in v0.10.0)
The Mechanics page lists each known formula with an explanation, its verification status (published, found in the program, confirmed in game) and links to what can be changed today. Every new rule or decoded field should update `Core/Mechanics.cs`. A formula only becomes editable once its code is located and confirmed; until then the page says so.

### Evaluation tools (priority from design review)
Design goal: evaluate a rule change before playing it. Results must say whether they rest on confirmed game behaviour or on a proposed formula.
1. Movement and range analysis (highest priority). Done in v0.13.0: class movement type and the terrain cost table (14 types by 64 terrain kinds, tenths allowed) are editable and confirmed in a running game. Next: reachable hexes after one and two activations, attackable hexes after moving (range and terrain), turns and actions needed to reach mission objectives, mounted against dismounted forms, and how a Move change shifts player and enemy threat ranges. Start with controlled test maps, then real chapter maps once the map format is known.
2. Historical comparison: clean vanilla, Redux 1.4.1, the current project and proposed rules, field by field, with the affected characters, classes and items. The comparison view already does disc against disc; extend it to project against disc and to three-way.
3. Combat simulator: expected and worst-case damage, survival chance, follow-ups and counters for chosen encounters under different rule sets, using only confirmed formulas or clearly labelled proposed ones.
4. Dependency checker: shared classes (player and enemy), unsupported or unverified fields, rule conflicts, invalid item references, equipment that a class cannot use, and changes that reach the enemy roster.

### Design studies (from review; need code located first)
- Hybrid shields (first version done in v0.14.0 as the Shield guard rule: quarter, half or full Might on a failed block, physical attacks only). Remaining ideas: a flat amount by shield size, or a bonus scaled by shield level, need more code space. Original notes: a passive defense bonus whenever a usable shield is equipped, with the existing block roll giving the full bonus. Options: a percentage of shield Might (keeps shield identity, favours heavy shields), a flat amount by shield size (clear small, medium, large roles), a bonus scaled by shield level (rewards training, may dominate), or a defensive stance (player control, may become mandatory). Starting preference: a modest share of shield Might, block chance from shield level. Prerequisite: locate where shield Might is added to defense in the damage routine. Open questions: whether the passive part costs durability, and how it interacts with large and special shields and very high Defense units.
- Shield training and Vantage (done in v0.17.0: Shield training with Vantage rule, one roll per battle, failed or successful block): units like Adel lose shield level gains when Vantage stops the attack first. Proposal: count qualifying defensive engagements for shield experience even when Vantage intercepts, with limits that stop repeatable training. Needs the shield experience routine located.
- Speed: compare three prototypes before changing any formula. S1 tactical mobility (selected agile classes or skills gain limited repositioning, without simply giving everyone Canto); S2 conditional offensive tempo (a limited extra attack for clearly faster attackers; dangerous with Pursuit, Adept, Deathmatch, critical hits and multi-hit weapons); S3 defensive reactions (predictable positioning or limited responses; technically hard). Preference: class or skill specific S1 first, S2 only as an experiment.

### Audit (from design review, 2026-11-12)
| Finding | Why it matters | Priority |
|---|---|---|
| Class edits also change enemies in that class | A player buff can strengthen many enemies | Critical: the editor warns, but should list enemy users once chapter data is mapped |
| Learned-skill writes have unresolved corruption reports | May create invalid character states | Critical: stays locked |
| Some items have more than one special effect | A single-effect editor would misrepresent them | Effect flags (v0.25.0) and the single special effect slot (v0.27.0) editable; effect 125 on gift items is unexplained and not offered |
| Terrain avoid bonuses and terrain kind names not located | Movement tool must use published avoid values | Medium |
| Class growth data unverified | Incomplete class progression editing | High |
| New item slots lack verified map references | A free slot might be used by chapter data | High |
| Item and class text editing unfinished | New content shows the slot's old name | High (M5) |
| Level-up limits confirmed in game (v0.11.0) | Stat limit rules now offered | Done |
| Only first-battle export testing | Full campaign behaviour untested | High: plan a longer playtest with a project of mixed edits |
| Independent code review completed; file-safety and project-validation fixes in v0.14.1 | Regression tests cover temporary files, original disc protection, mismatched edits and malformed projects | Done; full campaign testing remains open |

### Customization audit (v0.16.0)
Everything found so far and whether a user can change it. Editable means offered in the app; rules are offered only after a live check.

| System | What is known | Editable |
|---|---|---|
| Items | type, might, accuracy, weight, range, critical, level, price, durability grade and pool | yes; special effects not yet (effect fields only partly decoded) |
| New items | 156 free placeholder slots | yes (copy of an item, custom name in the project) |
| Characters | class, level, stat bonuses, weapon levels, personal skills, five starting items, growths, bracket, weapon growth | yes; learned skills and supports not yet |
| Classes | bases, Move, experience value, weapon caps, class skills, movement type, shield sizes | yes; class growths and mount state not yet (mount state follows the movement type) |
| Terrain | 14 movement types by 64 terrain kinds, costs in tenths; per terrain avoid, recovery while resting, flags | costs yes; avoid, recovery and flyer stop flag yes (v0.18.0); other flags not mapped |
| Damage | attack − defense − block reduction; critical bonus | critical bonus range (rule); the formula itself is fixed |
| Shields | block chance, block roll, Might reduction, guard on failure, training | Shield block, Shield guard, Shield training speed, Training from failed attempts |
| Parry | Parry skill roll, chance (weapon level + 10) / 2 | Parry rule, Parry chance bonus (v0.21.0) |
| Weapon training | step, cutoff at level 20, growth chance, roll ceiling 190 - 3 x level | Weapon training speed, Training from failed attempts, Training roll ceiling (v0.21.0) |
| Experience | defeat formula: class experience + level difference table, skill +20%, cap 100; staff formula | level difference table, cap and staff values editable (v0.18.0, v0.19.0) |
| Skills | 90 skill checks mapped (skillsites.py); activation formulas read for Iaido, Knock Away, banes, Robust+ area, Vengeful, Vulnerable, Pursuit, Slow Start II | Iaido, Knock Away, bane chances (v0.19.0), Pursuit lead, Shieldfaire bonus, Paragon bonus (v0.20.0); Vengeful, Vulnerable, Slow Start II read but meaning unclear |
| Stat limits | HP, Strength, Agility, Luck, Defense, Mind | six limit rules; level 30 cap not yet (three linked values) |
| Knives | ignore Defense and shields (program reading) | not yet (needs a knife user to test) |
| Hit, avoid, critical rate | final hit = hit - avoid, crit = crit - crit evade; avoid = attack speed x 2 + items + terrain; hit and crit are item, weapon level and skill sums | Attack speed in avoid (v0.22.0); terrain avoid on the Terrain page; range doubling read, not checked; situational bonuses are active skills: Sunder and Desperation editable (v0.23.0), Aim and Deadeye and the equipment window copy (v0.24.0); Focus Chant (magic orbs) and Throw need a unit that can use them |
| Tamper marker | unit check and program checksum set a saved marker | not needed; no effect seen |
| Vantage and shield training | no block roll, no training | Shield training with Vantage rule (v0.17.0): one training roll per battle |

### M8 and later
PCSX2 link and PNACH, saves, patch packs, audio, maps and events, and Berwick specific systems (food, Pavilion). Each starts with a read-only viewer and a round-trip test before any writes.

Stable-price research is complete at the static level: event 602 supplies both menu and purchase amounts, with 11 horse prices and three identical compressed copies per edition. See `RESEARCH.md`. Next implementation step is a safe compressed-event writer with all-copy verification, then a controlled in-game test. Changing item base prices does not change stable prices. Other open town data includes furniture prices, mercenary fees, supports and bag contents.

## Rules that apply to every milestone
- Never modify the original ISOs, memory cards, save states or PCSX2 settings in place.
- Offsets are per edition. Never reuse a Japanese address on English or the reverse.
- Item and unit identity is the numeric ID, never the name (two Pickaxes exist: 0x0B0 and 0x1FA).
- No placeholder controls for unfinished features.
- Interface and release text stays plain and finished: no placeholder or developer wording.
