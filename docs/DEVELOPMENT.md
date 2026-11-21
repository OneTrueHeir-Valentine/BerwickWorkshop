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

## Editing and UI

The application should expose domain operations through small, testable services. Page-specific state and event handlers should move out of `MainWindow`; extracting partial-class files is a safe first step but does not by itself remove coupling.

Keep the game's identity numbers and edition profiles in the data layer. UI labels and display names must not be used as storage keys. Long-running reads, exports and animation renders need cancellation, bounded allocations and clear error reporting.

Prefer normal source-level refactors. Avoid broad textual substitutions across C# files; if a scripted source migration is necessary, make it deterministic, review its diff and run the complete build and test suite.

## Release checks

Before merging a refactor, run the Windows build and offline tests. Behavior-preserving extraction commits must not change project file formats, exported bytes, game rule defaults or original-byte checks. Tests with the Japanese and English retail images remain required before claiming game compatibility.

Versioned release artifacts contain the executable, user guide and required third-party license credits. Keep test files, local notes, extracted game assets and development caches out of the release package.

## Refactor sequence

1. Separate page code and cohesive patch families without changing behavior.
2. Move editor operations into testable services, leaving views responsible for presentation.
3. Introduce explicit patch ownership, assembly sources and byte-equivalence fixtures for executable hooks.
4. Split the large test runner by feature and strengthen cross-rule regression tests.
5. Expand mechanics only after the affected game behavior and patch combinations are verified.
