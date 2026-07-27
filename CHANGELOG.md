# Changelog

## 1.2.2

- Moved the Guided Arrow Mastery button to the bottom-right of the native character-development screen.
- Fixed the character-screen overlay input registration so the mastery button receives mouse clicks.
- Added a deferred, overlay-safe transition from the character screen to the mastery tree.
- Restricted Ctrl+U to the campaign map, preventing the mastery screen from being pushed over character-development or other campaign panels.
- Restored the character-screen mastery button after returning from the mastery tree.
- Preserved the original v1.1.17 Guided Arrow gameplay runtime unchanged.

## 1.2.1

- Added a separate MCM entry for enabling or disabling Guided Arrow mastery progression.
- Added in-tree progression enable/disable controls.
- Added a Guided Arrow Mastery button to the native character-development screen.
- Retained the Ctrl+U campaign-map shortcut.
- Fixed startup patching of the inherited `OnAgentHit` callback by resolving its declaring method correctly.
- Added explicit progression-state cleanup when leaving a campaign.
- Preserved the original v1.1.17 Guided Arrow gameplay runtime unchanged.
