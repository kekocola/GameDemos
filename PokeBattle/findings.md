# Findings

## Environment
- Workspace began empty and is not a Git repository.
- Installed .NET SDKs: 6.0, 9.0 and 10.0.302; .NET 10 runtime is present.
- System Node is v24.14.1 and npm is 11.11.0.
- Bundled artifact runtime is available for XLSX authoring and rendering.

## Rule/Data Sources
- 52poke damage documentation specifies the modern base formula and highlights per-step rounding.
- Priority, weather, critical-hit and status behavior vary by generation; rules must be selected by `rulesetId`.
- PokéAPI exposes species stats, forms, types, moves and sprite URLs but is not sufficient as the only source for exact move behavior.
- Pokémon Showdown is a useful MIT-licensed differential reference, but will not be a runtime dependency.
- Official Pokémon support does not grant permission to reuse Pokémon IP; remote sprites are dev-only and the client must have a local abstract fallback.

## Architecture Constraints
- Choice secrecy requires buffering commands until all required positions have chosen.
- Room mutation must be serialized.
- Client never calculates authoritative damage or win state.
- Exact replay needs configuration hash, ruleset ID, seed, commands and ordered emitted events.

## Test Tooling
- Current stable packages checked on 2026-08-09: xUnit 2.9.3, xunit.runner.visualstudio 3.1.5 and Microsoft.NET.Test.Sdk 18.8.1.
- Current frontend tool versions checked on 2026-08-09: Vite 8.1.5 and TypeScript 7.0.2; installed Node 24 satisfies Vite's requirement.

## Build and Run Scripts
- The repository currently has no PowerShell, CMD, BAT or shell entrypoints.
- The production client build writes directly to `src/PokeBattle.Server/wwwroot`, so it must run before the final server build.
- Vite development mode already proxies `/api`, `/health` and `/ws` to `localhost:5080` and uses strict port 5173.
- The server defaults to `http://localhost:5080` and resolves runtime configuration from the repository `data/runtime` path or `POKEBATTLE_CONFIG`.

## Ability, Logging and Replay Iteration
- The authoritative engine now exposes pure ability-rule hooks plus lifecycle integration for entry, pre-move, immunity, damage, contact, speed and switch-out behavior.
- The representative catalog contains 19 configured abilities; 18 are engine-integrated and `inner-focus` remains explicitly `Planned`. Full canonical ability coverage remains a recorded gap.
- Runtime ability state is separate from immutable configuration, allowing Trace, Protean and per-switch activation limits without mutating source data.
- Battle diagnostics are room-scoped, sequence ordered and filterable by sequence, level and category through HTTP or WebSocket.
- Replay artifacts are versioned and include configuration/rules metadata, initial/final snapshots, accepted commands, ordered events, SHA-256 integrity hash and verification results.
- The H5 test bench can inspect ability IDs, filter server diagnostics, import/download replay JSON and step or autoplay through authoritative replay states.
- The final Release pipeline compiles the same configuration hash used by replay metadata, builds the client/server, and passes all 14 tests with zero warnings.

## PVP and PVE Dungeon Iteration
- The existing engine already buffers both sides' choices and resolves only after both are locked; PVP requires a safe side-1 claim flow rather than a separate combat implementation.
- Room-scoped side tokens are suitable for reconnect, while a separate one-time join code prevents the creator from having to disclose either controller token.
- PVE waves can reuse one `BattleState` and event sequence by replacing only the configured enemy side after a player victory; this preserves deterministic commands, diagnostics and a single replay artifact across the dungeon.
- Test dungeon enemy teams remain six-row valid preset teams, while `enemyPartySize` lets a wave expose only one configured member for fast regression tests.
- Rewards are deterministic completion metadata only; there is still no account inventory or database persistence.
- The rebuilt H5 exposes all three modes and the live `/api/config` reports version 1.1.0, one two-wave dungeon and six preset teams.
- Two live clients successfully claimed P1/P2 and advanced the same room from turn 1 to turn 2 only after both submitted; regression also exposed that locked clients were being resent legal choices, so snapshots now suppress choices while that side is locked.
- The first live dungeon run advanced from wave 1 to the configured boss wave and restored the player, but exposed that a replacement enemy side needs active slots populated explicitly; wave construction now activates the correct single/double slot count.
- After the active-slot fix, the live test dungeon exposed the boss as a legal target, completed in two deterministic player submissions and displayed both completion state and `TestToken × 100` reward metadata.
- Final PVP regression confirms the first player's locked snapshot shows a waiting state with no submit button; the authoritative turn advances to 2 only after the second player locks a choice.
- The completed dungeon UI is visually legible at desktop size, with the dungeon card, defeated boss, completion banner and event history all visible without overlap.
- The final workbook contains 27 filtered/frozen sheets, reports one PVE dungeon, and has no matched formula-error cells; all new or changed sheets passed visual review.
- Final Release configuration hash is `0dc790a0dfb4919999121d584b658c229afc2152618c163c02d634d94bc62d9f`; the full pipeline passes 16 tests with zero warnings and errors.

## Battlefield Animation Iteration
- The supplied desktop screenshot shows static combatant cards anchored at bottom-left and top-right, with a large clear central arena suitable for transient move/projectile effects.
- Required baseline feedback: attacker anticipation/lunge, move-name/skill effect, defender hit reaction, floating damage/heal text and smoothly animated HP changes.
- Animations must consume real ordered battle events and degrade safely for initial snapshots, reconnects, replay stepping and `prefers-reduced-motion` users.
- The server sends one or more `battleEvents` messages before the authoritative `battleSnapshot`; buffering events until that snapshot preserves the old displayed HP while still rendering the final state and legal actions from the server.
- Event sequences allow duplicate suppression, and battle IDs provide a clean reset boundary so reconnect/manual snapshots do not replay historical animations.
- Live browser regression captured `魔幻假面喵` with `is-acting`, a visible `MOVE / 终极吸取` banner and a projectile node while the animation queue was active; the same turn finished with authoritative HP values (`谜拟丘 130/162`, `魔幻假面喵 138/183`).
- Theme matching must classify nature/drain tokens before the water token `rain`, otherwise `giga-drain` is accidentally detected as a water move by substring overlap.

## Auto Battle Iteration
- The existing `setAi` WebSocket command already validates that a session can only change its own side and delegates choices to `BasicBattleAi`.
- `BasicBattleAi` scores every legal move/target and chooses a legal switch when no move candidate exists; forced replacements are also handled authoritatively by the battle engine.
- `SetAiAsync` currently rejects both PVP and Dungeon. Dungeon takeover requires allowing Dungeon while continuing to reject PVP.
- The existing dungeon advancement processes only one transition. When both dungeon sides are AI, a loop is required to advance every completed wave and emit exactly one completion/failure result.
- Live Practice takeover completed in 28 turns and emitted 51 `MoveUsed`, 8 `PokemonSwitched` and 9 `PokemonFainted` events, confirming automatic skill use and replacement behavior.
- Live Dungeon takeover advanced to wave 2 and emitted `DungeonCompleted`; the configured two-wave test dungeon completed in two turns with three move events.

## 2D Battlefield Actor Iteration
- The current UI renders each sprite only inside its compact status card. The central arena has no independent combat actor, so action animation moves the entire information card rather than the creature itself.
- The user wants a test-viewing presentation and explicitly accepts visual inaccuracy; reuse all configured sprites and prioritize readable motion, size and resilience over canonical rendering.
- All 16 configured species point at PokeAPI Showdown animated GIFs. Player-side URLs can first try the corresponding `/back/` path and fall back to the front sprite if the back animation is unavailable.
- Live validation rendered two independent 210x210 battlefield actors with animated GIFs loaded successfully, each running the idle animation while the compact cards remained dedicated to status information.
- A real move captured `actor-attack-player` with an approximately 127px by 67px lunge, one themed effect node, `actor-hit` on the defender and one combat floating-number node; the browser console remained free of warnings and errors.

## Faint Guard and Battle Pacing Iteration
- The supplied screenshot shows the enemy at `0 / 51` HP while another move presentation remains visible after the UI already reports battle completion; both authoritative turn resolution and the client animation queue need inspection.
- Desired pacing is approximately 2-3 successful hits for ordinary matchups, without removing legitimate battle-state terminal handling.
- The authoritative resolver already checks faint/end state after each ordered action and breaks once `BattlePhase.Ended`; it also skips an actor that fainted before its queued action.
- The client animation coordinator serializes every received event in a promise chain, including `MoveUsed`, and does not cancel queued animation when a terminal snapshot arrives. A fast AI battle can therefore keep presenting old move events after the authoritative state already shows `BattleEnded`.
- The screenshot matchup is level 50 Meowscarada versus the deliberately low-level `pve-test-boss` Snorlax at level 8 (`51` max HP). That test fixture was originally tuned for instant dungeon regression and is inherently one-shot when selected as a normal opponent.
- Normal presets are level 50 with canonical base stats and mostly 75-120-power attacks; merely raising the PVE test levels fixes the shown mismatch but cannot guarantee the requested 2-3-hit pacing for every type/critical/special-action combination. A per-move damage ceiling is the reliable invariant.
- Generation constants are already CSV-driven and compiled into each ruleset, so the pacing ceiling can be explicit configuration rather than a hidden engine literal. A `45%` max-HP ceiling per move guarantees a full-health target needs at least three direct damaging actions, while preserving relative type, critical, weather and ability modifiers below the cap.
- The ceiling must be applied across an entire move, not independently per hit; otherwise a multi-hit move could still consume all HP in one action.
- Implemented the ceiling as a shared per-target move budget, including fixed-damage effect steps. PVE scout/boss presets are now level 50 with viable four-move sets instead of level 5/8 fast-kill fixtures.
- Terminal snapshots now invalidate the existing frontend animation promise chain, remove active effects/classes, and then play only the terminal snapshot's ready event batch; the final hit/faint remains visible without stale earlier turns continuing afterward.
- When a damage event reaches 0 HP, the move banner now closes immediately instead of remaining visible throughout the faint animation, so the presentation no longer looks like an attack continuing against an already-fainted target.
- Runtime compilation confirms both PVE test presets are now level 50. The generated configuration hash is `9ec3430cc139d5eef7e2ec94d132c739c5d2bae6e0d6928cb1b3b5f57fc46f70`.
- Final validation passed: TypeScript/Vite production build, .NET Release build with zero warnings/errors, and all 18 tests (including the two new regressions).

## Four-Move and Choice Timer Iteration
- User requires every combatant to enter battle with four moves and an ability, while retaining manual move/special-action selection and automatically selecting a move after 15 seconds.
- Random moves/abilities are optional rather than mandatory; deterministic configured signature sets should remain the baseline so replays stay reproducible.
- The timeout must be server-authoritative so closing, throttling, or modifying the browser cannot stall a PVP/PVE room; the client countdown is presentation only.
- Audit result: all 36 configured team members currently have exactly four moves and a non-empty configured ability. The compiler still permits 1-4 moves and optional abilities, so the invariant is not yet protected against future CSV edits.
- The H5 currently exposes moves through a dropdown plus a final lock button; special actions such as Mega/Z/Dynamax/Tera are already returned per legal move and selected through a separate dropdown.
- `BattleRoom` serializes all mutation through `_gate`, making it the correct place for a turn-bound deadline. The timer must retain the original deadline when one PVP side locks early, reset only when the turn/wave changes, and use `BasicBattleAi` to construct legal fallback choices for each unlocked human side.
- Legal move payloads currently expose only move IDs, targets and special actions. Adding the configured Chinese/English name and current/max PP will let the H5 render four real clickable skill buttons without duplicating configuration client-side.
- `BattleState` already serializes `ChoiceLocked`; the TypeScript model simply omits it. The countdown can use this plus a server-provided UTC deadline to hide itself after the controlling side locks.
- The test project references only the domain project. A server-room timer regression will require a server project reference or extracting the scheduler; injecting a short timeout into `BattleRoom` and disposing it after the test provides focused end-to-end coverage without waiting 15 seconds.
- Implemented compiler rejection for missing abilities, non-distinct moves, or move counts other than four. Legal-choice payloads now include localized names and PP for button rendering.
- Implemented a room-owned deadline keyed to the authoritative turn. It auto-submits `BasicBattleAi` choices only for unlocked human sides, keeps an existing PVP deadline after one side locks, and resets after turn or dungeon-wave advancement.
- The H5 now renders localized skills as a 2×2 clickable grid with PP, retains explicit target/special-action (including Mega when legal) selectors and a release button, and drives its countdown from the server UTC deadline.
- Live H5 regression showed exactly four skill buttons for Meowscarada, a visible `15s` countdown, `Mega 进化` under the Gen 6 ruleset, and the configured `protean`/`inner-focus` ability chips.
- Clicking Bullet Seed selected the skill card and releasing it advanced the authoritative battle from turn 1 to turn 2. Letting the next choice expire advanced the battle again and produced one visible `超时自动出招` event.
- Full-page visual inspection found the four-card command panel readable and aligned with the existing battlefield. Browser warning/error logs were empty.
- Final validation passed with the regenerated H5 bundle, .NET Release build at zero warnings/errors, and all 19 tests passing. The owned server stopped and port 5080 was released.
- Final invariant review found that Gen 1/2 battle creation previously blanked configured abilities. The lab now keeps each member's fixed ability across all selectable rulesets while still generation-gating Mega/Z/Dynamax/Tera.
- After the cross-ruleset ability correction, the final .NET Release build remains at zero warnings/errors and all 20 tests pass.
