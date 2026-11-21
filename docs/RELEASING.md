# Release verification

Releases are prepared from a reviewed source commit. A successful Windows build is necessary but does not replace tests on the actual supported discs.

## Required checks

1. Run the Windows build, offline tests and source-structure check on the release commit.
2. Run `tools/test-game-editions.ps1` against an original Japanese retail ISO and an English v2.3 ISO. Keep these images outside the repository. Record the result and each image's SHA-256.
3. Create separate modified images from each edition. Confirm in PCSX2 that both boot and that an edited unit or combat rule behaves as expected. Record the emulator version and what was tested.
4. Inspect the packaged executable, user guide and license file. Confirm the version, third-party credits and that no game images, extracted assets, private notes or build caches are included.
5. Record the source commit and final package hash. Run `tools/check-release.ps1` with a local verification record before publishing.

The verification record is a local file and should not be committed. It contains test results and hashes, not the disc images. The record format used by the checker is:

```json
{
  "version": "0.119.0",
  "sourceCommit": "40 lowercase hexadecimal characters",
  "packageSha256": "64 hexadecimal characters",
  "japanese": {
    "imageSha256": "64 hexadecimal characters",
    "tests": "passed",
    "checkedAtUtc": "2026-11-20T23:00:00Z"
  },
  "english": {
    "imageSha256": "64 hexadecimal characters",
    "tests": "passed",
    "checkedAtUtc": "2026-11-20T23:00:00Z"
  },
  "pcsx2": {
    "version": "the tested PCSX2 version",
    "japaneseModifiedBoot": "passed",
    "englishModifiedBoot": "passed",
    "smokeTestNotes": "the edited behavior checked in both versions",
    "checkedAtUtc": "2026-11-20T23:00:00Z"
  },
  "packageContentsReviewed": true,
  "licenseCreditsReviewed": true,
  "releaseNotesReviewed": true
}
```

The checker verifies that the fields are complete, the result dates are recent, the source commit matches the checkout, the package hash matches, and the archive contains only the expected deliverables. It cannot inspect PCSX2, so the recorded emulator results still require an actual manual test.

## Publishing

Prepare the final Windows archive only after the source commit is fixed. Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\check-release.ps1 -Version 0.119.0 -Package .\Berwick.Workshop.0.119.0.zip -Evidence C:\path\outside\repository\release-check.json
```

Alternatively, use the gated publisher after verification:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\publish-release.ps1 -Version 0.119.0 -Package .\Berwick.Workshop.0.119.0.zip -Evidence C:\path\outside\repository\release-check.json -ReleaseNotes .\release-notes.txt
```

Use the verified commit as the release tag target. Keep release notes factual and restricted to changes confirmed by the build and tests. Do not replace the existing public release until the new one has been verified.

GitHub's hosted offline workflow cannot access privately owned game images or directly validate PCSX2. The local checks and signed-off verification record therefore remain a required manual release step.

## Editing historical release descriptions

Versions 0.114.0 and 0.118.0 have edited descriptions in [Release history](RELEASE_HISTORY.md). The existing tags and download archives stay unchanged when only the public release text is edited. With an authenticated GitHub CLI, preview the exact text from the repository root:

```powershell
.\tools\edit-historical-release-notes.ps1
```

To update those two GitHub release descriptions after checking the preview:

```powershell
.\tools\edit-historical-release-notes.ps1 -Apply
```

The command checks that each release still has its expected archive and verifies the updated description. It does not publish a new release or upload files. Avoid adding the same notes in a separate file; the release history is their maintained source.

## Local offline checks

From the repository root, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\run-local-checks.ps1
```

The command runs source checks, release-checker tests, the .NET test build, and the offline test suite. Add `-PublishApp` to build a Windows executable for local inspection. Each run writes a timestamped directory under `release-handoff/checks/` containing stdout, stderr, and `summary.json`. These are development logs, not release evidence. The command does not update the verification record, inspect game images, or mark PCSX2 tests passed.

The next planned release is 0.119.0. Do not publish it until the final source revision and application package have passed all required checks, including both game editions and PCSX2.

## Local verification records

### Collect selected logs and screenshots

From Windows PowerShell 5.1 or PowerShell 7 in the repository root:

```powershell
.\tools\collect-test-evidence.ps1 -Version 0.119.0 `
    -Log @('.\release-handoff\checks\RUN-ID\summary.json', '.\release-handoff\checks\RUN-ID\offline-tests.stdout.log') `
    -Screenshot @('C:\test-captures\japanese-boot.png', 'C:\test-captures\english-boot.png') `
    -Pcsx2Version '2.9.183'
```

Replace the example paths with individual files you have reviewed. Omit `-Screenshot`, `-Log` or `-Pcsx2Version` when not needed; at least one file is required. Run the command within PowerShell to supply arrays. Relative input paths use the current directory; output always uses this checkout.

The command returns a new `release-handoff/collections/VERSION/UTC-TIMESTAMP-UNIQUE-ID/` directory containing `logs/`, `screenshots/`, `manifest.json` and `manifest.txt`. Manifests record the supplied release version, current commit and working-tree cleanliness, collection times, supplied emulator version, original absolute paths, copy paths, byte sizes and SHA-256 hashes. Originals are not renamed, rewritten or removed. Close any process still writing a selected log before collecting it.

Only explicit local files are accepted: `.log`, `.txt` and `.json` logs; `.png`, `.jpg`, `.jpeg` and `.bmp` screenshots. Directories, wildcard expansion, network paths, alternate streams, reparse points, known private/game-data directories and other extensions are refused. Duplicate basenames are rejected case-insensitively across the whole selection; select distinct names or make separate collections. Missing or locked files produce an error rather than silently omitting evidence.

Extension and path checks do not inspect or sanitize file contents. Select only appropriate validation evidence, never credentials, game images, BIOS data, saves or extracted assets renamed to an allowed extension. The utility does not discover evidence, inspect PCSX2, approve tests or releases, edit `release-*.evidence.json`, or upload anything. Manifests contain original paths and remain private in Git-ignored `release-handoff/`.

### Create a verification record

Create a record in the checkout when preparing a new release:

```powershell
.\tools\new-release-record.ps1 -Version 0.119.0
```

With a package already prepared, provide `-Package` to populate its SHA-256 automatically. Records live in `release-handoff/` at the repository root, which Git ignores. The script never marks a check as passed. Update the local JSON only after running each disc test, inspecting the package and completing the PCSX2 smoke tests.

Tools and editors running in the same local checkout can read these files. Separate workspaces and remote jobs do not automatically share the ignored directory. Keep the records on the machine used for the tests and provide an explicit copy only when verification must be reviewed elsewhere.

The release publishing command accepts an evidence path inside `release-handoff/`. Neither the evidence JSON nor the original game images should be committed.
