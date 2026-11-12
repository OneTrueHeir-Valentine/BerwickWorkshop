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
- `0x002274DC` to `0x002274EC`: shield block roll: a random 0 to 99 compared against the unit's shield rate (stored at offset 0xA0 of the battle unit).
- `0x0022CE0C`: a rate formula that subtracts 50, then subtracts 50 or 75 and halves. Not identified yet.
Live confirmation (PCSX2 2.9.78, English v2.3, first battle, memory read and written over PINE):
- All seven locations above hold exactly the executable's code while the game runs (8 words each), and 395 of 400 random words across the program match the file (the rest are data that changes during play). The code is not relocated, so changes written into the executable on disc land where expected.
- Shield block roll confirmed: changing the branch at `0x002274EC` so the failed-roll path is never taken made shield units parry every attack.
- Experience gain confirmed: replacing the instruction at `0x00224580` so the gained amount is 100 made a unit level up after one fight.
- Both changes were temporary (emulator memory only) and were reverted.

### Stat caps (static map plus one live test, 2026-11-12)
- The attribute add routine at `0x00174A90` uses a 15-entry jump table at `0x004A4060` (same in both editions). The unit record in memory has the disc starting-record layout without its 4-byte tag, and the cases map to: HP bonus (cap 50), Strength, Agility, Defense and Mind bonuses (cap 15 each), Luck (cap 20), level (cap 30), and experience (cap 199, 100 at level 29, 0 at level 30). Five indexes are not identified.
- Live units in memory (English v2.3, first chapter): a starting copy from `0x01E13C8C` (stride 0x234) and the current army from `0x01E17F9C`; all decoded fields match the game.
- Live test 1: with all six stat caps in this routine set to 0, a level up still raised Strength.
- The add-and-store routine `0x00174550` (table `0x004A4020`, writers in `0x004A3FE0`) holds a second copy of the same limits and is the one level ups call. Live test 2: with both copies at 0, a level up raised nothing. Both copies together are the stat limits; offered as rules in v0.11.0.
- Next leads: two sibling 15-entry tables at `0x004A3FE0` and `0x004A4020` with a routine at `0x00174550` that writes the stat bytes (likely get and set attribute), and the level-up code after `0x002245A0`.

### Movement (2026-11-12)
- Movement type is class record byte 6, bits 1 to 4: 0 cavalry, 1 horseman (light cavalry), 2 flying, 3 knight, 4 armor, 5 infantry, 6 thief, 7 civilian, 8 special (ballistician), 9 snow, 10 war priest, 11 fighter (light infantry), 12 priest. Matches the EmblemWiki class groups for 37 of 40 classes; the 3 misses are likely name pairing in the comparison.
- Mount state is class record byte 3, bits 4 to 5: 0 on foot, 1 mounted, 2 flying. It agrees with the movement type for every class on both discs.
- Terrain costs per movement type and terrain avoid are published at gamekouryaku.com (terrain list). Searches for those rows as byte, halfword and word tables, in game order and in the site's column order, with wildcards for impassable, found nothing in the program, a battle RAM dump or the first 256 MB of DATA3. The data may be packed differently or loaded per map.
- In RAM the class table sits at `0x006DA3A0` (copy at `0x018FA58C`); a pointer to it is stored at `0x004B4768`.
- Old cheat code pages (atwiki, archived) list program patches for Canto on any unit and full move after moving; leads for the speed prototypes.

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
