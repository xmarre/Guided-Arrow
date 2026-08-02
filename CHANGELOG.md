# Changelog

## 1.3.6

- Restores battle-command voices and normal mission audio by keeping maintained collision and continuation patches off Bannerlord's native `Mission.*` methods.
- Fixes protected-memory crashes during repeated terminal penetration continuations.
- Restores configured finite and infinite agent penetration across native `PassThrough`, `Stick` and `BecomeInvisible` reactions while keeping shields and world collisions terminal.
- Correlates delayed collision work with the exact tracked projectile so recycled missile indices cannot continue an older impact chain.
- Preserves split-volley camera ownership through valid penetrations and restores the native combat camera cleanly when the guided swarm ends.
- Repairs Autoguidance after impacts and continuation materialization through one coordinated retarget path without duplicate route rebuilds.
- Serializes safe continuation creation behind completed display boundaries without the former fixed 150 ms staircase.
- Improves siege Autoguidance by retaining normal obstacle routing and making the strict direct-line target filter optional and disabled by default.
- Retains the reorganized main MCM settings and compatibility with values saved through the former Simple Controls page.
- Verifies the maintained sidecar against Bannerlord 1.3.15 and 1.4.7 reference assemblies.
- Retains the byte-identical verified v1.1.17 gameplay core and applies the update through the maintained v1.3.6 sidecar.

## 1.3.5

- Creates ordinary generated split followers during the launch callback before manual guidance or Autoguidance initializes.
- Preserves the exact native missile lifetime across repeated configured agent penetrations instead of deleting it after the first promoted pass-through.
- Keeps finite penetration counts authoritative and treats Infinite Agent Penetration as unlimited agent hits while terrain, shields, walls, trees and other world collisions remain terminal.
- Correlates early collision reactions by exact missile index and isolates 48-projectile callback bursts from the core's fixed reaction-queue trimming.
- Moves Follow the Guided Projectile into Projectile Camera, Play Kill Cinematics into Kill Cinematic, and Visible Siege Targets Only into Autonomous Guidance on the main Guided Arrow MCM page.
- Preserves values previously saved through Guided Arrow - Simple Controls and saves or resets that hidden compatibility storage together with the main page.
- Removes the separate Simple Controls page without rewriting the byte-locked gameplay core.
- Verifies the maintained sidecar against Bannerlord 1.3.15 and 1.4.7 reference assemblies.
- Retains the byte-identical verified v1.1.17 gameplay core and applies the update through the maintained v1.3.5 sidecar.

## 1.3.4

- Adds independent MCM switches for the normal guided-projectile camera and confirmed-kill cinematics.
- Keeps manual guidance and Autoguidance active while the player remains in Bannerlord's normal combat camera.
- Clears stale projectile-camera ownership and frame state so the combat camera remains responsive throughout Autoguidance.
- Keeps mission time at 1.0x when Proximity Time Dilation is disabled while preserving Q/E manual time-speed controls.
- Defers disabled-kill-camera terminal handling until the native impact callback and a complete display-frame boundary have finished.
- Restores the proven penetration invariant: only a native `PassThrough` continues the exact live projectile; `Stick`, `BecomeInvisible` and other terminal reactions end it without spawning a synthetic replacement missile.
- Keeps save deserialization limited to raw progression synchronization and migration, with starter-node repair deferred until the campaign is ready.
- Automatically invests and preserves the mandatory rank-one Guided Release starter level when mastery progression is enabled.
- Makes character-screen mastery navigation tolerate intermediate Bannerlord screen transitions and reports the Ctrl+U campaign-map fallback when navigation cannot complete.
- Filters hidden siege targets while the core initially builds its parallel candidate and head-position lists, preserving every route-planning index for the full shot.
- Limits siege visibility checks to siege missions, caches ray-cast results and fails open when an unknown private API shape is encountered.
- Adds a focused configuration guide and a separate Simple Controls MCM page for camera presentation and siege targeting.
- Verifies the maintained sidecar against both Bannerlord 1.3.15 and 1.4.7 reference assemblies.
- Retains the byte-identical verified v1.1.17 gameplay core and applies the update through the maintained v1.3.4 sidecar.

## 1.3.3

- Restores the stable core's deferred native-missile cleanup after a final `PassThrough` impact exhausts the guided penetration budget.
- Prevents the final cinematic or camera-return handoff from starting while the native removal queue still owns projectile teardown.
- Makes the clean-display-tick gate observe the real native-removal queue instead of an artificially empty queue.
- Marks a terminal shot generation complete only after the terminal handoff returns successfully.
- Preserves normal core-owned terminal transitions and cancels the sidecar fallback when the core has already advanced state.
- Preserves Autoguidance targeting, penetration counts, damage, formations, camera framing and progression balance.
- Validates the correction against the previously failing 48-projectile Autoguidance stress case.
- Retains the byte-identical verified v1.1.17 gameplay core and applies the correction through the maintained v1.3.3 sidecar.

## 1.3.2

- Prevents terminal impacts on already-resolved targets from creating an additional synthetic penetration continuation.
- Preserves legitimate continuation when a guided projectile penetrates into a different live target.
- Defers the final Autoguidance swarm terminal handoff until tracked missiles and all collision-owned deferred queues have drained.
- Requires two consecutive clean display ticks before beginning the final cinematic or return transition.
- Cancels the deferred handoff when the core has already transitioned normally or guidance resumes.
- Keeps progression-derived settings active for the complete guided-shot callback burst and restores the original MCM values afterward.
- Moves mastery XP accounting to completed shot summaries on the display tick and requires a confirmed hostile victim.
- Preserves live-registry missile validation, removal-safe Autoguidance retargeting and camera ownership repair.
- Removes superseded diagnostics and disproven experimental patches from the production source tree.
- Retains the byte-identical verified v1.1.17 gameplay core and applies the update through the maintained v1.3.2 sidecar.

## 1.3.1

- Validates every controlled missile against Bannerlord's live mission registry before guidance, deferred-work and projectile-camera ticks.
- Preserves legitimate native wrapper replacement only after the core's existing shooter/entity/item identity refresh confirms the same projectile.
- Removes registry-missing, identity-mismatched or recycled missile wrappers through the core's existing cleanup path without calling into expired native handles.
- Prevents recycled missile slots from sending long-running Autoguidance projectiles onto unrelated trajectories.
- Defers post-penetration target advancement until the continuing projectile exists on a stable subsequent mission/display tick.
- Prevents collision-time nearby-agent scans, target skeleton/head lookups and route assignment while the impacted missile and victim are undergoing native teardown.
- Captures the depth-aware continuation exit distance during the original collision callback and carries only that float into deferred creation.
- Removes deferred reads of the previous victim's position, visuals and native entity when spawning synthetic penetration continuations.
- Purges removed agents from active targets, planned routes, consumed-target history and shared Autoguidance candidate lists.
- Repairs leader and camera ownership only from exact live members of the current guided group.
- Preserves intentional camera suspension and return states when no projectile camera owner should exist.
- Retains the byte-identical verified v1.1.17 gameplay core and applies the correction through the maintained v1.3.1 sidecar.

## 1.3.0

- Rebuilt Guided Arrow Mastery as a centre-outward specialization tree instead of a left-to-right unlock list.
- Corrected the vertical branch orientation: Piercing Doctrine now grows north, Hunter's Mind grows south, and the matching Convergence capstones follow those branches.
- Raised maximum mastery rank to 99 and grants one mastery point per rank.
- Converted all 19 masteries to multi-level skills, generally with 10 or 20 levels and level-specific bonuses.
- Added current-level, next-level, prerequisite-level and maximum-level information to every mastery node.
- Added a specialization budget: the complete tree requires far more than 99 points, so one character cannot maximize every branch.
- Added a Bannerlord-scaled mastery XP curve reaching approximately 68,000 XP at rank 99.
- Rebalanced XP around unique victims, kills, distance and bounded multi-kill rewards, with a 32 XP cap per guided shot before multipliers.
- Added a separate Mastery XP Multiplier setting from 0.25 to 3.00.
- Migrated old binary mastery unlocks to level 1 of the corresponding v1.3.0 skills.
- Removed Harmony getter patches from the normal Guided Arrow MCM settings, fixing the crash when that MCM page was opened while progression was enabled.
- Applies progression restrictions only during Guided Arrow mission callbacks and restores every original setting immediately afterwards.
- Keeps the normal Guided Arrow MCM values as configurable upper limits rather than rewriting the displayed settings.
- Fixed Guided Release's initial four-second cap with a direct real-time timeout, bypassing the stable core's internal five-second minimum.
- Added per-level runtime scaling for guidance duration, turn radius, time control, steering strength, autoguidance, route planning, obstacle avoidance, native-volley control, generated splitting, formations and penetration.
- Limits how many native/TOR missiles join the controlled group according to Split Awareness without removing any projectile or native effect from the mission.
- Retains the tested character-screen navigation, additive native-volley and split-penetration stability fixes from v1.2.2.

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
