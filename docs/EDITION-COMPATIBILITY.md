# Edition compatibility

Verify Japanese retail and English v2.3 separately. Record the application commit, recognized image and executable hashes, Windows/PCSX2 versions, battle, map archive, army record, group/slot, coordinates and starting save conditions. Use pass, fail, not run or not applicable for each result, with a local evidence path. Static inspection and Core tests do not establish desktop or gameplay compatibility.

Use disposable projects, separate export paths and disposable saves or memory cards. Preserve original images and emulator settings. Keep selected logs and screenshots under the ignored `release-handoff/` directory; do not publish game data or copy results between editions.

## Verification coverage

| Area | Checks for each edition |
| --- | --- |
| Maps | Compare artwork, terrain, deployment positions, units, arrivals and event spots with the baseline. Check zoom, scrolling, overlays, stacked-marker selection, hit testing and rapid battle/project switching. Test eligible-unused defaults, access to placed/unsupported records, removal, no-op feedback and shared-map acknowledgement. Reject border, deployment and occupied-hex collisions without edits. |
| Armies | Match army, record index/key/offset, team, group and slot. Placement/removal must change only the coordinate pair, preserving the army record and other consumers. Review shared army and map uses. |
| Scripts | Check both launch layouts and opening-event paths. Verified finite calls must retain every destination; changed or genuinely ambiguous graphs must be refused. Direct script edits that invalidate the original graph must explain why placement is unavailable. |
| Reinforcements | Confirm starting-placement edits leave arrival commands, turns, conditions, origins and destinations unchanged. Reach relevant arrival conditions in game and check duplicate-unit interactions; otherwise record not run. |
| Projects | Add/remove is one undo step. Verify undo/redo, map refresh, save/reopen and identity/refusal details. Rejected or no-op actions must not add project changes. |
| Export | Write a new image for each edition, reopen it and compare added presence, removed absence, unchanged army/script records and unrelated maps. Confirm original hashes remain unchanged and outputs retain their correct base edition. |
| PCSX2 | Enter the selected battle from a state that reloads starting placements; an existing battle/save state is insufficient. Compare unit identity, team, position, removal and deployment with the baseline. Check movement, combat, basic victory/defeat conditions, later arrivals and other affected battles; retain edition-specific evidence. |

## First placement case

Story mission 1, **A Reason to Fight**, is internal battle **26**, `bmap026.ar`, archive **3701**. Its starting army is **158**, team **1**, group **1**. Record **21**, key **158021**, uses slot **22**, initially empty on both original images. Recheck the current project's eligibility before placing it. If that record no longer qualifies, use the actual mission-order table to select an early simple alternative; numeric battle IDs are not story order.

## Current verification

Both original editions pass the typed catalogue and placement-reference checks, including all three fingerprint-guarded mercenary calls. Separate disposable Core sessions placed the first-case record at `(1, 1)`, verified undo/redo and project reload, and preserved the army record and original image hashes. The coordinate was checked by the existing placement rules, not by an in-game movement test.

Manual desktop interaction, original-image export/readback and PCSX2 gameplay remain untested for this change. Complete those checks independently for each edition before claiming gameplay compatibility. Passing automated checks does not approve a release or alter verification flags.

## Further Maps interaction

Selecting an existing unit and clicking a destination hex should eventually provide a preview, validation and explicit confirmation or cancellation, with shared-reference acknowledgement and the existing undo system. That interaction, original `onmap` sprite rendering and reinforcement editing remain outside the current placement change.
