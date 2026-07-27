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
- Added a narrow stable-core penetration safety patch that advances synthetic continuations beyond the impacted agent and marks that entity as pass-through.
- Serialised deferred split-arrow penetration continuations to one custom missile per mission tick instead of creating an entire large volley in one native tick.
- Rejected true/null and incomplete reflected continuation results before the stable core can dereference them.
- Added a targeted deferred-worker recovery path that restores untouched queued continuations, removes invalid tracked entries and repairs leader/camera references after a continuation-specific null-reference failure.
- Removed the destructive native-volley replacement patch after it regressed ordinary split-arrow impacts and stripped TOR ability projectiles of their perk-specific behaviour.
- Preserved every native/TOR ability projectile, including Waywatcher Lethal Shot magic, explosion and other perk callbacks.
- Added the configured Guided Arrow split count on top of a native/TOR volley instead of replacing it. A five-arrow Lethal Shot with a split count of 30 therefore produces 35 total projectiles.
- Kept native/TOR ability arrows on their own collision and penetration path while Guided Arrow's synthetic penetration remains available for the added followers.
- Left native/TOR volleys unchanged when standalone splitting is disabled or set to one projectile.

## 1.2.1

- Added a separate MCM entry for enabling or disabling Guided Arrow mastery progression.
- Added in-tree progression enable/disable controls.
- Added a Guided Arrow Mastery button to the native character-development screen.
- Retained the Ctrl+U campaign-map shortcut.
- Fixed startup patching of the inherited `OnAgentHit` callback by resolving its declaring method correctly.
- Added explicit progression-state cleanup when leaving a campaign.
- Preserved the original v1.1.17 Guided Arrow gameplay runtime unchanged.
