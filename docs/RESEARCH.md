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

### Editors
- [MinN-11 Berwick Saga editor and randomizer](https://github.com/MinN-11/berwick-saga-editor-randomizer) and its [FEUniverse thread](https://feuniverse.us/t/berwick-saga-berwick-saga-editor-randomizer-early-beta/11256): source of our table offsets and names (MIT). Corrections we found: Japanese second item table offset, class base stat labels rotated by one.
- KT Berwick Saga Data3 Editor v1.0.1 (2012): the author's site `geocities.jp/kt_0t3wqu6irw/` has Wayback snapshots from 2008 to 2015. Pages only, not downloads, should be read next to learn which tables KT mapped.
- [TRSModify for TearRing Saga (PS1)](https://feuniverse.us/t/trsmodify-now-virus-free-including-my-incomplete-notes/3415): a different game and engine generation; nothing reusable for Berwick found in the thread.

### Item slots (from the discs)
- 156 catalogue entries are unused placeholders, in runs inside each weapon category (for example 0x017 to 0x01F among knives). Their records hold template data; 19 are byte-identical to a real item. None are starting items. Shops, chests and drops have not been mapped, so a placeholder could still be referenced by chapter data.

## Gaps
- No public decompilation, symbol map or disassembly notes for `SLPS_254.97` were found.
- No English-edition addresses found anywhere; every code found is for the Japanese release.
- Nothing found on how the game finds its DATA3 tables (needed to grow tables).
