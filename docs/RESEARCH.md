# Research notes

Prior work on Berwick Saga internals, collected 2026-11-12. Treat community claims as leads until checked against the discs.

## Queries used
- `Berwick Saga PS2 hacking executable addresses formula pcsx2 debugger`
- `ベルウィックサーガ 解析 アドレス 改造 コード 命中 回避 計算式`
- `"Berwick Saga" pnach OR "cheat codes" OR codebreaker OR "action replay" SLPS-25497`
- `"Berwick Saga" editor github OR randomizer OR "data3" OR decompilation`
- `ベルウィックサーガ 改造コード atwiki PS2 ショップ 成長率`
- Wayback CDX: `geocities.jp/kt_0t3wqu6irw*`
- universal-modder knowledge base: Berwick, TearRing, PS2, PCSX2, MIPS, ELF (no Berwick or PS2 MIPS notes exist).

## Findings

### Growth bracket (confirmed in part)
Source: [ベルウィックサーガのレベルアップ, 西へ東へ](https://west-east.blog.jp/archives/2718403.html) (player blog, older post).
- A stat grows toward start value + growth rate x levels gained, and a correction keeps the result within a band around that: strong ±1, medium ±2, weak ±3, none (no limit).
- The post says Daoud and Lynette are the only characters with no correction, and Reese's HP correction is weak.
- On the disc, growth record byte 2 bits 5 to 6 hold 1 for exactly Daoud and Lynette, and 2 for Reese. So 1 = none and 2 = weak (±3) match. 3 is a tighter band, ±1 or ±2 not yet known. The post says corrections are per stat, but the record has one value per character; the rest of byte 2 and byte 3 are other data (byte 3 repeats the character's class ID).
- Credibility: single player source, consistent with two independent disc facts.

### Formulas (published, not yet located in code)
Sources: [EmblemWiki, Berwick Saga Calculations](https://wiki.serenesforest.net/index.php/Berwick_Saga_Calculations); [銀の意思流攻略, 命中率と攻撃力](https://www.silver-intention.com/Game/BerwickSaga/System-5.php).
- Accuracy = weapon precision x 10 + weapon skill + bonuses. Avoid = attack speed x 2 + terrain (x 2 at range 2+). Attack speed = speed minus (weapon weight + shield weight minus strength / 2, not below 0).
- Critical rate = weapon critical + (weapon skill minus 50, not below 0) + bonuses; critical damage adds random 10 to 20.
- Equipment failure = 5% per weapon rank above level (7 levels of slack with Advanced). Injury base 9 for knives, 3 otherwise. Experience per kill = enemy class experience + modifier (class experience is the class field we already decode and edit).
- Credibility: two independent wiki and guide sources agree on accuracy.

### Code locations from cheat code communities (leads for M6)
- [2005 to 2006 cheat thread, 5ch gameurawaza 1116938870](https://kako.5ch.io/test/read.cgi/gameurawaza/1116938870): working codes for an experience multiplier (x2 to x32), guaranteed level ups, guaranteed shield block, higher class skill activation, and level and shop changes, all for the Japanese release. They are encrypted CodeBreaker codes, so their real addresses are not readable yet. Decrypting them (a CodeBreaker v7 converter) should reveal the exact program addresses of the experience, level up, shield and skill activation logic. This is the most promising route to the formula code.
- [改造コードまとめ @ wiki, ベルウィックサーガ](https://w.atwiki.jp/kaizoucodematome/pages/143.html) and [改造コード・入力済みデータ](http://www.ccn2.aitai.ne.jp/~keikun/gametitle/beruwhi.htm): more codes, including one described as removing the growth slowdown for weapon skill above 30. Not yet read in full.
- [note.com, PCSX2 1.6 code search, 2026-08-29](https://note.com/crisp_snail1748/n/nd1bd55d5c443): plain memory addresses found with PCSX2CT and Cheat Engine, region not stated (likely Japanese). Program-side values include stat elixir effects at `0x004525B0` and `0x004525C0`, a dish price and HP value near `0x0042A2B8` and `0x0042A300`, and a static shop list near `0x0052BB00`. Addresses below about `0x570000` fall inside the loaded executable, so these may be editable in `SLPS_254.97` directly. Unverified on either disc.
- [soulslip29 blog, PCSX2 PNACH for Berwick Saga (J)](https://soulslip29.blogspot.com/2024/07/ps2-pcsx2-pnach-j-berwick-saga.html): PNACH for CRC `2FCA8492` (Japanese) including guaranteed level ups and full stat gains; the file itself is on a drive link (not fetched). It notes stats wrap to 0 above about 20 with stat-boost cheats, which suggests a 5-bit stat field at run time.

### Program locations from the decrypted codes (static analysis, 2026-11-12)
The 2005 codes are Pro Action Replay 2 codes using the standard Action Replay 2 encryption (default seed `05100518`). Decrypted with a local port of Omniconvert's routine (GPL-2.0, not shipped). Decryption is confirmed by two money codes that decode to `0x01EC52E4` and `0x00DBEEF4`, the same town and battle gold addresses found independently in the note.com article.

Both executables have the same layout (one load segment; program address = file offset + `0xFFF00`) and only 12,258 bytes differ. Every location below is byte-identical in the Japanese and English executables.
- `0x0013A740`: random integer in a range (stream, minimum, maximum). Called from 206 places, so it marks every random roll in the game.
- `0x0013A7E0`: percent roll. 66 call sites; two are followed by 50% checks.
- `0x00174A90`: add to a unit attribute and clamp it to a hard-coded per-attribute cap (caps seen: 15, 20, 30, 50 or 100, 199, 999, 100000). This is where stat and experience caps live. Which index is which stat is not mapped yet.
- `0x00224570` to `0x002245A0`: experience gained after battle is added here (attribute 2). The old "always level up" code forced this amount to 100, and the experience multiplier code shifted it left.
- `0x002274DC` to `0x002274EC`: Parry skill roll (first labelled as the shield roll; corrected 2026-11-12): a random 0 to 99 compared against the parry rate at offset 0xA0 of the battle unit, which `0x00177EC0` builds from weapon level and needs the Parry skill. Success cancels the hit.
- `0x0022CE0C`: a rate formula that subtracts 50, then subtracts 50 or 75 and halves. Not identified yet.
Live confirmation (PCSX2 2.9.78, English v2.3, first battle, memory read and written over PINE):
- All seven locations above hold exactly the executable's code while the game runs (8 words each), and 395 of 400 random words across the program match the file (the rest are data that changes during play). The code is not relocated, so changes written into the executable on disc land where expected.
- Parry roll confirmed: changing the branch at `0x002274EC` so the failed-roll path is never taken made every unit parry every attack.
- Experience gain confirmed: replacing the instruction at `0x00224580` so the gained amount is 100 made a unit level up after one fight.
- Both changes were temporary (emulator memory only) and were reverted.

### Stat caps (static map plus two live tests, 2026-11-12)
- The attribute add routine at `0x00174A90` uses a 15-entry jump table at `0x004A4060` (same in both editions). The unit record in memory has the disc starting-record layout without its 4-byte tag, and the cases map to: HP bonus (cap 50), Strength, Agility, Defense and Mind bonuses (cap 15 each), Luck (cap 20), level (cap 30), and experience (cap 199, 100 at level 29, 0 at level 30). Five indexes are not identified.
- Live units in memory (English v2.3, first chapter): a starting copy from `0x01E13C8C` (stride 0x234) and the current army from `0x01E17F9C`; all decoded fields match the game.
- Live test 1: with all six stat caps in this routine set to 0, a level up still raised Strength.
- The add-and-store routine `0x00174550` (table `0x004A4020`, writers in `0x004A3FE0`) holds a second copy of the same limits and is the one level ups call. Live test 2: with both copies at 0, a level up raised nothing. Both copies together are the stat limits; offered as rules in v0.11.0.
- Next lead: the level-up code after `0x002245A0`; level and experience caps still need their own tests. The second limit routine and its tables are already mapped above.

### Movement (2026-11-12, confirmed in a running game)
- Movement type is an 8-bit field in the class record starting at byte 6 bit 1 (the program loads the halfword at offset 6 and keeps bits 1 to 8). Values: 0 cavalry, 1 horseman (light cavalry), 2 flying, 3 knight, 4 armor, 5 infantry, 6 thief, 7 civilian, 8 special (ballistician), 9 snow, 10 war priest, 11 fighter (light infantry), 12 priest, 13 unused by any class. Original classes use 0 to 12.
- Mount state is class record byte 3, bits 4 to 5: 0 on foot, 1 mounted, 2 flying. It agrees with the movement type for every class on both discs.
- Terrain cost table: in DATA3.DAT, two identical copies. English `0x00CCE128` and `0x08ECB3FC`, Japanese `0x00CD7928` and `0x08EB3BFC` (header start). Header: terrain count 64, type count 14 (two 32-bit values). Then 14 rows of 68 bytes: byte 0 mount state, byte 1 unknown (7 7 6 4 3 4 4 3 2 4 3 4 3 7), bytes 2 and 3 zero, bytes 4 to 67 the cost of terrain kinds 0 to 63 in tenths of a point; 255 means it cannot be entered.
- Earlier searches missed it because the costs are stored as tenths (forest for cavalry is 40, not 4). The rows match the gamekouryaku.com terrain list (snow forest is terrain 50: 7 6 1 4 4 4 3 2 3 3 2). Terrain names are not stored as plain text, so kinds are named by the published row they match.
- Program: the class table pointer is at gp minus 0x7B88 (`0x004B4768`, gp `0x004BC2F0`); class IDs count from 1 with 100-byte records. `0x0017CEB0` returns a unit's movement type; `0x0017CF20` returns a class's cost row from the table pointer at `0x004B4784`.
- Live tests (PCSX2, English v2.3, first battle): all enterable terrain set to 4 shrank every movement area; class 0x01 set to Flying let Reese move as a flier; open ground at 1.5 limited Move 4 units to 2 hexes. So the table, the class field and tenths all work.
- Old cheat code pages (atwiki, archived) list program patches for Canto on any unit and full move after moving; leads for the speed prototypes.

### Damage and shields (2026-11-12, program reading plus live tests)
- Battle setup `0x0022B940` fills one record per combatant: 0x54 attack, 0x6C physical defense, 0x70 magic defense base, 0xA0 parry rate, 0xA4 shield block chance (`0x00177C70`: shield accuracy + shield level, +18 with a skill), 0xA8 shield Might (item Might bits), 0xAC shield magic reduction, 0xC0 hit, 0xC4 critical. Shield values count only for a shield that is not broken and whose size the class can use (class byte 0x1B).
- Damage routine `0x0022A2D0`: damage = attack - defense - reduction (never below 0), plus a magic part for weapons that deal both; a critical hit adds a random 10 to 20. The reduction is the shield's Might on a successful block, a weapon guard skill, or a fixed 10 from another skill (only one applies).
- Shield block roll: branch at `0x0022A638`, tried only when the hit would do damage, the defender has a usable shield and no other guard applied. Knives (item type 0) and two weapon effects cancel the block afterwards. A reduction above 0 triggers the block animation and durability use, so a passive bonus must go into defense instead.
- Shield guard prototype: `0x0022A640` to `0x0022A6A4` rewritten in fewer instructions with the same success behaviour; a failed block (physical attacks) adds Might >> n to defense. The forecast uses the same routine with fixed rolls, so it shows the guard.
- Static review correction: guard applies after a failed block roll, not after a successful roll canceled later by a weapon effect. Knives follow the type-0 path to `0x0022A940`, which clears physical defense, including the guard bonus. Shield block Always does not override shield-bypassing attacks. The earlier promise of guard against knives was incorrect; v0.14.1 corrects the descriptions without changing the instructions.
- Live tests (PCSX2, the user's test export, with parry forced off in memory): Leon (attack 15) against a Deserter (defense 2, Verian small shield, Might 4). Prototype at full Might: an unblocked hit did 9. Original code: a blocked hit did 9; unblocked hits normally do 13.

### Weapon and shield training (2026-11-12)
- `0x0017A900` raises a weapon level (unit, type 0 to 13, amount), capped by the class cap; types 11 to 13 are the small, medium and large shield. Weapon levels sit three per 32-bit word with a 2-bit check value in the top bits, which the game rewrites on every change.
- `0x0022B2C0` grants battle training: mode 0 after an attack (step 1, `0x001716C4`), mode 1 for shields (step 4, `0x001716A4`), mode 2 (step 2) from another caller. Shield training is granted at `0x00228070` only when a block roll happened: block success, or a failed block on a hit.
- Amount (`0x001716D0`): nothing with 0 growth; at level 20 and above a failed attempt gives nothing (`0x00171760`); one or two rolls (two below level 10) plus one on success; each passes when random < growth × 10 and random < 190 − 3 × level, adding one step. Capped at the class cap.
- Vantage: a defender that kills its attacker first never gets a block roll, so it gains no shield training. The Vantage decision is made once per battle at `0x00224E50` (flag at battle +0x11A8, skipped in forecast mode +0x11D8). At `0x00224E68` to `0x00224EF4` an inlined battle-event push duplicates the routine at `0x0022E1E0`; calling that routine frees 20 words, enough to grant the defender one shield training event through `0x0022B2C0` (mode 1) when its shield Might (+0xA8) and cap (+0x10C) are above 0. Live test: with blocks forced to fail and the cutoff on, Ward's medium shield went 30 to 40 when Vantage triggered.
- Live tests: shield step 4 changed to 50 made a shield level jump after one block; weapon step 1 changed to 50 made a weapon level jump after one attack.
- Class record byte 0x1B bits 4 to 6 are the shield sizes a class can use (small, medium, large); they agree with the class's shield level caps for all 217 classes.
- Critical bonus: Random(1, 10, 20) at `0x0022AEE4` to `0x0022AEF0`; the range routine loops forever if the high value is below the low one. Live test: forced criticals with 30 to 30 added exactly 30.
- Failed-attempt cutoff: `slti at, a1, 2` at `0x00171760` (rank below 2, level below 20). Live test with the growth check forced: a failed block at shield level 25 trained once the cutoff was lifted.
- Experience: `0x00224230` handles the experience kinds. Battle experience is added only after a defeat: `0x00174050` takes the enemy class experience value (class byte 4) plus the level difference table at `0x00429F10` (16 words, enemy level minus own level from -8 to +7: -40 to +15), then `0x00173D50` adds 20% with skill flag 0x40000 and item percentages, clamped 0 to 100 (`0x0017410C`, `0x00174124`). Staff kind: staff level + 6 (`0x00224394`) + Random(0, 3) - level / 5. Live test: with the table all 50 a defeat gave exactly 50 (that enemy class has experience 0).
- Terrain effect table: DATA3 terrain cost table + 0x420, both copies, both editions. Count 64, then 24 bytes per terrain: +0 avoid (matches the published list; live test: plain set to 50 showed 50%), +0x10 recovery while resting in percent (poison swamp -20), +0x14 flags. Flag 0x40 keeps flying classes from stopping (`0x001AB610` and copies, enemy movement and mount change checks); set on buildings and a few other kinds. Live test: with it cleared, a flying class ended its move on a house.
- Tamper checks: a unit check (`0x00183540`, stats and weapon-level check bits) and a program checksum (`0x00125E60`) both set a marker (`0x00506E64`) that is copied into save data. Program rules trip the program check. No effect of the marker has been seen in play yet. Starting records on the disc carry no weapon check bits, so editing them is safe for the unit check.

### Editors
- [MinN-11 Berwick Saga editor and randomizer](https://github.com/MinN-11/berwick-saga-editor-randomizer) and its [FEUniverse thread](https://feuniverse.us/t/berwick-saga-berwick-saga-editor-randomizer-early-beta/11256): source of our table offsets and names (MIT). Corrections we found: Japanese second item table offset, class base stat labels rotated by one.
- KT Berwick Saga Data3 Editor v1.0.1 (2012): the author's site `geocities.jp/kt_0t3wqu6irw/` has Wayback snapshots from 2008 to 2015. Pages only, not downloads, should be read next to learn which tables KT mapped.
- [TRSModify for TearRing Saga (PS1)](https://feuniverse.us/t/trsmodify-now-virus-free-including-my-incomplete-notes/3415): a different game and engine generation; nothing reusable for Berwick found in the thread.

### Item slots (from the discs)
- 156 catalogue entries are unused placeholders, in runs inside each weapon category (for example 0x017 to 0x01F among knives). Their records hold template data; 19 are byte-identical to a real item. None are starting items. Shops, chests and drops have not been mapped, so a placeholder could still be referenced by chapter data.

## Gaps
- No public decompilation, symbol map or disassembly notes for `SLPS_254.97` were found.
- No English-edition addresses were published anywhere, but the code locations above are identical in the English executable.
- Nothing found on how the game finds its DATA3 tables (needed to grow tables).
