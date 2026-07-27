# Changelog

## 1.2.2

- Moved the Guided Arrow Mastery button to the bottom-right of the native character-development screen.
- Fixed the character-screen overlay input registration so the mastery button receives mouse clicks.
- Changed character-screen navigation to close Bannerlord's native `CharacterDeveloperState` before opening mastery.
- Added a short campaign-map stabilisation delay before the mastery screen is pushed, preventing the map-bar panel index crash.
- Restricted Ctrl+U to the campaign map, preventing the mastery screen from being pushed over character-development or other campaign panels.
- Restored the character-screen mastery button after returning from the mastery tree.
- Restored and SHA-256-locked the exact known-working v1.1.17 `GuidedArrow.dll` after a recovered-core test build caused an immediate native mission-start crash.
- Removed the unsafe recovered core implementation and recovery scripts rather than retaining non-authoritative code as a maintenance target.
- Added a narrow sidecar patch around the stable core's penetration-continuation method instead of rebuilding the core assembly.
- Moved synthetic penetration continuations beyond the impacted agent before the next native mission tick.
- Marked the impacted agent entity as pass-through for the newly spawned continuation, preventing an immediate repeat collision against the same native body.
- Aborted a continuation safely when its impact vector or reflected stable-core metadata cannot be validated.
- Kept TOR native-volley capture changes deferred until they can be implemented and verified separately around the stable core.

## 1.2.1

- Added a separate MCM entry for enabling or disabling Guided Arrow mastery progression.
- Added in-tree progression enable/disable controls.
- Added a Guided Arrow Mastery button to the native character-development screen.
- Retained the Ctrl+U campaign-map shortcut.
- Fixed startup patching of the inherited `OnAgentHit` callback by resolving its declaring method correctly.
- Added explicit progression-state cleanup when leaving a campaign.
- Preserved the original v1.1.17 Guided Arrow gameplay runtime unchanged.
