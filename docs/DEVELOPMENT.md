# Development and architecture

Berwick Workshop reads two supported editions of Berwick Saga, edits project data, and writes a verified copy of the disc. Format decoding, patch construction, desktop presentation and testing should remain separate responsibilities.

## Code boundaries

- `Core` owns disc inspection, format readers and writers, project state, validation, binary patch plans and exports. It must not depend on WPF.
- `App` owns the Windows user interface, input validation and presentation. Large editor pages should have their own files and eventually their own view models or controllers. A window should coordinate pages rather than implement every page's behavior.
- `Tests` covers binary round trips, invalid input, collision detection, project compatibility and file safety. Keep each feature's tests alongside tests for related features instead of extending the central test runner indefinitely.
- `reference` contains credited reference data and older tooling. Reference scripts are not part of the application runtime.

New functionality should be implemented in the owning module. Do not add new pages to the central window file or add new executable patch families to the large rule registry.

## Source review

Keep changes in the subsystem that owns them. Prefer ordinary, explicit C# and WPF code over indirection that only moves complexity elsewhere. Use descriptive names for game concepts, and preserve established technical names for game structures and CPU registers.

Before opening a pull request, read the final diff as source another maintainer will need to change. Check for unrelated edits, duplicated logic, oversized methods, compressed multi-statement lines, unnecessary helper classes, inconsistent formatting and comments that restate the code. Explain non-obvious file layouts, numeric offsets and executable constraints where they are used.

Avoid string-based transformations of application source or assembly files. A generator is appropriate only when it has a maintained input format, a repeatable build and tests for its output. Do not reformat unrelated files to satisfy a local style change.

Pull requests should identify the affected subsystem, testing performed, manual checks still outstanding and any constraints that a later developer must preserve. Keep legitimate third-party credits and source references.

## Game program changes

The game executable contains MIPS machine code, not C source. Existing rules store instruction words, expected originals and runtime addresses in C#; this is retained for compatibility while the patching system is separated.

New custom-code patches should use named routines with readable MIPS assembly source and a reproducible build step. Record the hook address, original instructions, affected registers, delay-slot behavior, return address, code allocation, and supported editions. Treat executable layout and reserved memory as shared resources, not independent patch-local assumptions.

The patching layer should generate a patch plan before it writes. Each plan must report its original-byte expectations, replacement bytes, affected file regions and dependencies. Reject overlaps unless the patch family defines an explicit combination. The application must continue refusing unsupported discs and must never rewrite the input ISO.

For existing patches, first extract the code unchanged. Replace hard-coded instruction arrays only when the assembled bytes compare exactly to the previously verified settings on both editions. Changes to the meaning of a mechanic are separate commits with separate in-game validation.

### First assembly-backed patch

The first migrated hook is the heap allocation increment used when shield guard reserves an extra page. The readable sources are `Core/Mips/heap-original.s` and `Core/Mips/heap-reserved.s`. They compile to the original values `0x24420800` and `0x24421800`. `GameRules` reads these embedded sources instead of repeating the two instruction words as literals.

`Core/MipsAssembly.cs` is intentionally a limited assembler. It recognizes basic R- and I-format instructions, absolute jumps, relative branches, labels, and literal words. It does not support R5900 vector instructions, assembler macros, pseudo-instruction expansion, automatic branch-delay handling, or relocatable objects. Unsupported syntax is rejected. Do not attempt to translate complex game routines through this subset until its opcode coverage and tests have been expanded or a suitable established assembler is integrated.

`Tests/MipsAssemblyTests.cs` checks the known machine words, endianness, branch offsets and bad input. Additional migrations must include explicit source addresses and a binary equivalence test against the verified original bytes. Assembler output alone is not proof of correct game behavior.

## Documentation and visual resources

User-facing descriptions should explain what the program does, what it cannot yet do and which behavior has been verified. Keep help text, issue descriptions and release notes specific to Berwick Workshop. Historical research and unresolved formats belong in the research notes. Keep technical terminology when it identifies actual game structures, and retain the credits attached to borrowed work.

Map and animation graphics come from the disc image opened by the user. Extracted game artwork is not stored in the repository or included in application packages. Any additional artwork or icon must have a recorded creator, source and redistribution terms. Review existing packaged imagery, including `App/workshop.ico`, when updating the release's asset inventory. An unverified origin should be recorded as such rather than inferred from appearance. Changes to icons and artwork should fit the existing interface.

## Editing and UI

The application should expose domain operations through small, testable services. Page-specific state and event handlers should move out of `MainWindow`; extracting partial-class files is a safe first step but does not by itself remove coupling.

Keep the game's identity numbers and edition profiles in the data layer. UI labels and display names must not be used as storage keys. Long-running reads, exports and animation renders need cancellation, bounded allocations and clear error reporting.

Prefer normal source-level refactors. Avoid broad textual substitutions across C# files; if a scripted source migration is necessary, make it deterministic, review its diff and run the complete build and test suite.

The assembler comparison is recorded in [MIPS toolchain selection](MIPS_TOOLCHAIN.md). The release gate is recorded in [Release verification](RELEASING.md).

## Release checks

Before merging a refactor, run the Windows build and offline tests. Behavior-preserving extraction commits must not change project file formats, exported bytes, game rule defaults or original-byte checks. Tests with the Japanese and English retail images remain required before claiming game compatibility.

Versioned release artifacts contain the executable, user guide and required third-party license credits. Keep test files, local notes, extracted game assets and development caches out of the release package.

## Working branches

`main` is the stable branch. `develop` is the integration branch for changes intended for the next release. Create short-lived task branches from `origin/develop`, review their diffs and tests, and squash-merge them into `develop` through pull requests. Do not push feature work directly to either long-lived branch. Compare local-only history with `origin/main` before rebasing or cherry-picking to avoid reintroducing changes already upstream.

When a release is ready, review a pull request from `develop` to `main`. Use a normal merge commit for that release promotion, rather than squashing the integration branch into a single change; this preserves ancestry and makes subsequent fast-forward synchronization predictable. Both original game editions and PCSX2 require recorded verification before publishing release binaries.

After a task is merged, update a clean local `develop` checkout with `git fetch origin` and `git merge --ff-only origin/develop`. After a release promotion, synchronize `develop` with `main` without rewriting shared history. Tools that need access to ignored verification records must operate in the same local checkout.

The local validation command is `tools/run-local-checks.ps1`. Logs are written to the Git-ignored `release-handoff/checks/` directory, with the source commit and exit code recorded for each check. This command does not edit release evidence or perform game-edition or PCSX2 testing.

`Tests` has a portable .NET 9 target and a Windows/WPF target, using the same runner. Build both with `dotnet build Tests -c Release`. Run focused export checks with `dotnet Tests/bin/Release/net9.0/Berwick.Tests.dll --animation-export`, or use `net9.0-windows` in that path to include PNG encoding and export-dialog controls. Local checks and Windows CI run both the portable suite and the focused Windows checks.

Animation export fixtures are synthetic. They check shared canvases, source records, metadata, PNG pixels, output cleanup and dialog defaults without game files. Checking every retail set, edition-specific palettes, real animation fidelity and PCSX2 playback still requires separate verification with the original Japanese and English discs. Control tests do not replace an interactive export/cancel smoke test.

## Refactor sequence

### Maps smoke test

Map rendering belongs to `MapRenderer`, page selection and load generations to `MapSelectionState`, and mouse/control coordination to `MapEditorView`. `MapEditorDocument` projects the current session into unit/arrival/deployment displays and delegates changes to existing Core operations. `MainWindow` supplies the shared editor, change-list formatting and script-capacity validation. The XAML layout and `MapEditorController` loading path are unchanged.

Run focused geometry/selection and Core map-edit checks with `dotnet Tests/bin/Release/net9.0/Berwick.Tests.dll --maps`. These synthetic tests do not establish retail rendering fidelity. Before accepting a Maps UI change, manually check each supported edition with locally owned data:

- Open Maps, switch battles rapidly and change disc/project; only the latest selection should display, with no old unit, move highlight or arrival pick carried across a reset.
- Check Small/Medium/Large sizes, scrolling, splitter layout, map art and terrain colors. Hover even/odd-column hexes, spots and stacked unit/arrival markers; verify coordinates, labels and legends.
- Toggle Units and Arrivals. Select starting and arriving units by left/right click and the unit list; repeated clicks on a stack should cycle. Open the unit editor with Edit/Ctrl+E, apply/cancel, and switch pages while editing.
- Move a starting unit, cancel a move, and try a border/deployment collision. Add/remove deployment hexes and change shared/battle-specific limits; check unused dashed hexes and feedback.
- Change an arrival's coordinates/turn, including Pick on map and marching arrivals. Check related units, undo/redo and restored values. Leave and return to Maps and reopen a project to check refreshed displays.

Use a disposable project for edit checks. Do not overwrite original images or saves. If validating an exported change in PCSX2, check both modified editions separately and retain selected screenshots/logs with the local collector. Completing the automated checks does not mark this checklist or emulator testing as passed.

### Existing starting-unit placements

Maps offers **Place existing unit...** for an identified battle. The selector initially lists eligible unused slots; enable **Show placed and unsupported records** to remove a starting placement or inspect refusal reasons. Choose an existing record, review its army, opening/source event, team, group, slot and known battle/event uses, then enter a column and row. Team and group come from the opening script; this workflow does not change them. Removal clears the starting coordinates, never the army record or later arrival commands. An operation that makes no change keeps the dialog open with an explanation.

Placement requires one unambiguous army/group assignment and a supported unit index (0 to 62, using slots 1 to 63). Off-map and border hexes, deployment positions, occupied coordinates and half-empty slots are refused. Shared maps require acknowledgement of their listed battles; conflicting consumers, conditional/computed assignments, unresolved event calls and incomplete references are refused. A launch naming an archive proven absent in the archive index is not treated as a consumer of a real map, while an unresolved battle-map reference still blocks edits.

The checks read active project scripts and current placement bytes again before applying a single `EditSession` change. Save/load, undo/redo and the existing exporter remain responsible for persistence. Rendering rebuilds the current starting-unit list from project assignments and slots instead of retaining the original disc's occupied-unit list.

Run `dotnet Tests/bin/Release/net9.0/Berwick.Tests.dll --maps` for focused Core/project/export fixtures, or use the `net9.0-windows` target to include dialog-control checks. Local checks run both targets. These are synthetic tests, not retail or PCSX2 verification. Before release, check both supported editions for add/remove presence, shared effects, original-record preservation, undo/save/reopen and exported-game behavior. Unknown script or catalogue layouts may cause conservative refusal and need investigation rather than bypassing the guard.

### Launch reference verification

Read-only inspection of the recognized Japanese retail and English v2.3 images found 45 `LaunchBMap` calls and one `LaunchBMap2` call in each edition's indexed event scripts. `LaunchBMap` places the battle and map file at argument indexes 0 and 1. The `LaunchBMap2` call in event 3902 at byte offset `0xE0` has a mode expression at index 0, battle 201 at index 1 and `zmap002.ar` at index 2. This confirms the different argument positions independently of the previous army-label parser. The shared reader does not evaluate the mode expression or claim to support computed battle/file arguments.

### Typed event catalogue

Both recognized original editions contain an identical 54,256-byte `evdata.dat` and 2,048-byte `evtype.dat` in archive 553. The engine loader at `0x001667A0` builds exactly 4,096 slot pointers from a length word followed by that many payload bytes. Zero length means an empty slot. Type lookup at `0x00166660` reads two four-bit types per byte, low nibble first. The loader, lookup and battle-type filter ranges agree between editions.

The complete tables contain 1,518 kind-0 battle records, 152 kind-1 general records and 2,426 kind-2 empty slots. Kind 0 stores a packed event/battle identity, a following-word count, then condition/arguments. Kind 1 stores a following-word count followed by its target/value and any additional fields. Kind 2 must have zero payload. The first slot is a valid kind-1 startup-script binding: length 8, payload `[1, 1]`. Startup code at `0x00165980` obtains slot 0 and passes its target 1 into script-context creation. It is not a header or sentinel, and its count word was previously misread as a packed identity.

The strict reader validates every slot and its type, all payload lengths/counts, nonzero targets and exact EOF. It retains all 1,670 populated records, including 18 kind-0 data-only records with zero script target. The battle projection contains 1,500 nonzero-target records. No byte scanning, arbitrary prefix skipping or padding termination is used. `MapEvents()` uses the same verified battle projection, returning no display records on invalid data; the editing path reports the strict error instead.

### First placement acceptance candidate

Both executable mission-order tables identify Story mission 1, **A Reason to Fight**, as internal battle **26**, map **`bmap026.ar`**, archive **3701**. Event 2955 launches that battle. Its opening catalogue references events 2958, 2959, 2965, 2961, 2957, 2960, 2964 and 2968. Event **2958** at offset **`0x14`** places starting army **158**, enemy team **1**, group **1**, with 22 existing records. Record index **21**, key **158021**, slot **22** has both coordinates set to 255 on both originals. This is the preferred first candidate, not battle 1 or `bmap001.ar`.

Reference checks pass on both original editions, and this record is eligible. Disposable Core sessions placed it at `(1, 1)`, verified one-step undo/redo and project reload, then restored the session; the army record and original image hashes remained unchanged. This checks the data operation, not desktop interaction, exported-game behavior, movement suitability or PCSX2 compatibility.

### Computed mercenary calls

`GUnitGetMercenaryCheckScript` reads the 18-row mercenary table at `0x0042D450`, resolves its hash field through the event index at `0x0043BB40`, and returns one of events **142–150** or `-1`. Events 133 and 151 call these checks only after excluding `-1`. Event 133's recruitment-handler argument comes from 17 callers: 15 supply zero, event 769 supplies 768 and event 964 supplies 963. Zero cannot reach the handler call because its result flag remains zero. The destination closure is 132, 142–150, 768 and 963; it contains no map-launch or archive-army reference commands.

All finite destinations remain edges in placement reference analysis. These facts require a recognized original image, its matching executable and the verified fingerprint of the analyzed event graph, including exact source bytes, parsed content and archive identities. Direct script or numeric-script edits that change that graph invalidate the facts and leave computed calls refused. This is deliberately narrower than general computed-call interpretation. Separate price-only export transformations do not change these targets. A finite computed opening path still counts as conditional and cannot authorize an ambiguous army placement.

### Development order

Use [the edition compatibility checklist](EDITION-COMPATIBILITY.md) for separate Japanese retail and English v2.3 acceptance runs. Future placement interaction should support selecting an existing unit and clicking a destination hex with preview, validation and explicit confirmation. Keep that work separate from the current coordinate-entry workflow and reinforcement expansion.

1. Separate page code and cohesive patch families without changing behavior.
2. Move editor operations into testable services, leaving views responsible for presentation.
3. Introduce explicit patch ownership, assembly sources and byte-equivalence fixtures for executable hooks.
4. Split the large test runner by feature and strengthen cross-rule regression tests.
5. Expand mechanics only after the affected game behavior and patch combinations are verified.
