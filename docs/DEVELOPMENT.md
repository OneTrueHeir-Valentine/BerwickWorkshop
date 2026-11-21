# Development and architecture

Berwick Workshop reads two supported editions of Berwick Saga, edits project data, and writes a verified copy of the disc. Format decoding, patch construction, desktop presentation and testing should remain separate responsibilities.

## Code boundaries

- `Core` owns disc inspection, format readers and writers, project state, validation, binary patch plans and exports. It must not depend on WPF.
- `App` owns the Windows user interface, input validation and presentation. Large editor pages should have their own files and eventually their own view models or controllers. A window should coordinate pages rather than implement every page's behavior.
- `Tests` covers binary round trips, invalid input, collision detection, project compatibility and file safety. Keep each feature's tests alongside tests for related features instead of extending the central test runner indefinitely.
- `reference` contains credited reference data and older tooling. Reference scripts are not part of the application runtime.

New functionality should be implemented in the owning module. Do not add new pages to the central window file or add new executable patch families to the large rule registry.

## Game program changes

The game executable contains MIPS machine code, not C source. Existing rules store instruction words, expected originals and runtime addresses in C#; this is retained for compatibility while the patching system is separated.

New custom-code patches should use named routines with readable MIPS assembly source and a reproducible build step. Record the hook address, original instructions, affected registers, delay-slot behavior, return address, code allocation, and supported editions. Treat executable layout and reserved memory as shared resources, not independent patch-local assumptions.

The patching layer should generate a patch plan before it writes. Each plan must report its original-byte expectations, replacement bytes, affected file regions and dependencies. Reject overlaps unless the patch family defines an explicit combination. The application must continue refusing unsupported discs and must never rewrite the input ISO.

For existing patches, first extract the code unchanged. Replace hard-coded instruction arrays only when the assembled bytes compare exactly to the previously verified settings on both editions. Changes to the meaning of a mechanic are separate commits with separate in-game validation.

### First assembly-backed patch

The first migrated hook is the heap allocation increment used when shield guard reserves an extra page. The readable sources are `Core/Mips/heap-original.s` and `Core/Mips/heap-reserved.s`. They compile to the original values `0x24420800` and `0x24421800`. `GameRules` reads these embedded sources instead of repeating the two instruction words as literals.

`Core/MipsAssembly.cs` is intentionally a limited assembler. It recognizes basic R- and I-format instructions, absolute jumps, relative branches, labels, and literal words. It does not support R5900 vector instructions, assembler macros, pseudo-instruction expansion, automatic branch-delay handling, or relocatable objects. Unsupported syntax is rejected. Do not attempt to translate complex game routines through this subset until its opcode coverage and tests have been expanded or a suitable established assembler is integrated.

`Tests/MipsAssemblyTests.cs` checks the known machine words, endianness, branch offsets and bad input. Additional migrations must include explicit source addresses and a binary equivalence test against the verified original bytes. Assembler output alone is not proof of correct game behavior.

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

## Refactor sequence

1. Separate page code and cohesive patch families without changing behavior.
2. Move editor operations into testable services, leaving views responsible for presentation.
3. Introduce explicit patch ownership, assembly sources and byte-equivalence fixtures for executable hooks.
4. Split the large test runner by feature and strengthen cross-rule regression tests.
5. Expand mechanics only after the affected game behavior and patch combinations are verified.
