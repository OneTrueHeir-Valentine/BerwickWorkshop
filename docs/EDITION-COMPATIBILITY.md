# Edition compatibility checklist

Repeat this checklist separately for Japanese retail and English v2.3. A successful build or synthetic test does not establish retail compatibility. Read-only inspection, desktop interaction and PCSX2 execution are separate results.

## Record each run

- Application commit and build configuration.
- Edition, recognized profile, original disc SHA-256 and executable SHA-256.
- Windows and PCSX2 versions, relevant emulator settings and starting save conditions.
- Battle number, map archive, army, record key/index, team, group, slot and opening/source events.
- Original and edited coordinates, shared-reference warnings and expected later arrivals.
- Result for each item: pass, fail, not run or not applicable, with a reason and local evidence path.

Use disposable projects and new export paths. Keep original discs, saves, memory cards and emulator settings intact. Store selected logs and screenshots under the ignored `release-handoff/` directory with the evidence collector. Do not upload game data or copy verification statuses from another edition.

## Maps

- [ ] Open Map 1 first. Identify an existing army record with an eligible unused starting slot. If none qualifies, record the reason and examine battles in ascending order; prefer an early battle with one clear starting assignment and no complex arrival dependency. If no battle passes the guards, stop and report the refusal.
- [ ] Check map artwork, terrain, deployment positions, starting units, event spots and later arrivals. Compare positions with the unmodified game.
- [ ] Check sizing, scrolling, overlays, hover/hit testing, stacked markers and unit selection. Rapid battle switches and page/project changes must not retain stale selections.
- [ ] Confirm the placement selector defaults to eligible unused slots. Show placed and unsupported records; verify removal remains accessible and refusal explanations identify the problem.
- [ ] Add one unit at a valid hex. Check its marker and selection immediately. Try a border, deployment position and occupied coordinate; each must be refused without edits.
- [ ] Remove an originally occupied starting placement. Confirm it disappears from the map and remains available as an existing army record.
- [ ] Inspect a shared map. Verify affected battles and matching assignments, require acknowledgement, reset acknowledgement on record/filter changes and refuse ambiguous consumers.
- [ ] Confirm a no-op leaves the dialog open with an explanation and does not report success or add an undo step.

## Armies and scripts

- [ ] Match army, record index/key/offset and slot to the source data. Team and group must come from the opening event.
- [ ] Confirm placement/removal changes only the intended coordinate pair. Compare underlying army records before and after, including other battles using that army.
- [ ] Inspect both launch commands where present: `LaunchBMap(battle, file)` and `LaunchBMap2(mode, battle, file)`. Use distinct mode and battle values when testing parsing; record event and command offsets.
- [ ] Confirm opening assignments reached through supported literal calls. Unsupported conditional/computed assignments and incomplete references must give a refusal rather than an inferred edit.
- [ ] With a disposable project containing script edits, verify reference checks and map display use the current project scripts. Undo the script edits and confirm the original associations return.

## Reinforcements

- [ ] Record the chosen unit's known later-arrival references before placement. The dialog must warn that starting placement does not cancel those arrivals.
- [ ] Confirm arrival commands, turn conditions, origins, destinations and marching behavior are unchanged by starting-placement edits.
- [ ] In PCSX2, reach the relevant arrival turn/condition and observe duplicate-unit or other interactions. If not reached, leave this item not run.

## Undo, project persistence and export

- [ ] One add or removal is one undo step. Undo/redo restores coordinates, map display and selection consistently.
- [ ] Save, close and reopen the disposable project. Confirm placement, removal and reference warnings persist correctly.
- [ ] Export each edition to its own new output image. Reopen it and verify added presence, removed absence and unchanged army/script records and unrelated maps.
- [ ] Verify original image hashes remain unchanged. A modified output must remain associated with the correct base edition.
- [ ] Confirm rejected/no-op operations introduce no serialized changes or unexpected export patches.

## PCSX2 acceptance

- [ ] Boot the corresponding exported edition using disposable saves/memory cards. Enter the selected battle from a state that actually loads its starting placements; an already-running battle or old save state is insufficient.
- [ ] Compare the unmodified baseline with the exported game. Verify added unit identity/team/position, removed starting absence and unchanged surviving units/deployment behavior.
- [ ] Check a basic turn, movement, combat, victory/defeat triggers and any relevant later arrival. Record crashes, softlocks, duplicate units or unexpected enemy changes.
- [ ] Recheck another battle sharing the army or map and compare its behavior with the reported shared effects.
- [ ] Retain edition-specific screenshots/logs and record remaining untested conditions. Do not approve a release or mark another edition passed from this checklist alone.

## Future interaction

Future Maps work should let the user select an existing unit, click a destination hex on the map, inspect a placement preview and validation result, then confirm or cancel. Preserve shared-reference acknowledgement and existing undo operations. This interaction is outside PR #27 and does not imply reinforcement or army-record creation support.
