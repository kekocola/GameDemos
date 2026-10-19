# PokeBattle Implementation Plan

## Goal
Build a deterministic, configuration-driven C#/.NET 10 battle test server with a TypeScript H5 client, representative Generation I-IX mechanics, single/double formats, basic AI, replay/debugging, CSV/JSON data and a reviewed XLSX handbook.

## Phases
- [x] Confirm scope, environment and source constraints.
- [x] Create solution, documentation and configuration contract.
- [x] Implement deterministic battle domain, generation rule profiles and AI.
- [x] Implement in-memory rooms, typed WebSocket messages and replay/reconnect surfaces.
- [x] Implement the H5 battle/debug client.
- [x] Generate and visually verify BattleConfig.xlsx.
- [x] Build, test, run end-to-end browser checks and document remaining coverage gaps.
- [x] Add and verify unified environment, restore, build, test, production-run and development-run scripts.
- [x] Implement an ability hook pipeline and additional battle-relevant abilities with configuration and tests.
- [x] Add structured per-battle debug logging, filtering, persistence/export surfaces and protocol integration.
- [x] Upgrade replay export to a versioned artifact with commands, metadata, verification and an H5 step player.
- [x] Rebuild configuration/workbook, run automated and browser regression, and update coverage documentation.
- [x] Add two-player real-time PVP room joining, side ownership, simultaneous choices and reconnect support.
- [x] Add configurable PVE dungeon definitions, wave progression, rewards metadata and a playable test dungeon.
- [x] Extend H5 setup and battle UX for PVP invitations/joins and PVE dungeon selection/progress.
- [x] Add PVP/PVE tests, rebuild runtime JSON/XLSX, run browser regression and update documentation.
- [x] Trace authoritative battle events into the H5 battlefield rendering state.
- [x] Implement queued attack, skill, impact, damage-number and HP transition animations.
- [x] Add reduced-motion and replay/reconnect-safe behavior.
- [x] Build and run focused frontend regression checks for the animation flow.
- [x] Add a visible in-battle exit action that forfeits the controlling side and renders a failure result.
- [x] Add an in-battle auto-battle control backed by the authoritative server AI.
- [x] Extend AI takeover through all configured PVE dungeon waves while keeping PVP human-only.
- [x] Build, test and run a focused auto-battle regression.
- [x] Inspect configured sprite sources and current battlefield DOM/animation targeting.
- [x] Add independent large 2D battlefield actors with resilient local silhouette fallbacks.
- [x] Retarget move, hit, heal, switch and faint animations to the battlefield actors while retaining compact HUD cards.
- [x] Build and visually verify idle/attack/impact presentation in a live battle.
- [x] Reproduce and trace actions that continue after the target has fainted.
- [x] Stop queued/server-side attacks as soon as a battle reaches a terminal state.
- [x] Rebalance representative teams so typical combatants survive roughly 2-3 hits.
- [x] Add focused regression tests and run the full build/test pipeline.
- [x] Audit every configured combatant for exactly four usable moves and an assigned ability.
- [x] Enforce four-move/ability invariants in configuration compilation and battle creation.
- [x] Add an authoritative 15-second choice deadline with automatic legal fallback choices.
- [x] Expose the deadline/countdown in snapshots and the H5 while preserving manual move and special-action controls.
- [x] Add timeout/configuration regression tests and run the full Release pipeline.

## Decisions
- Runtime: .NET 10 / ASP.NET Core raw WebSocket; no database or authentication.
- Client: Vite vanilla TypeScript; static build can be served by ASP.NET Core.
- Data: CSV is source of truth, JSON is compiled runtime snapshot, XLSX is the review handbook.
- Engine: deterministic event-producing state machine; generic effect steps plus registered special handlers.
- Scope for this implementation pass: complete playable representative slice with explicit gap matrix; no false claim of all canonical content being implemented.
- Assets: remote PokéAPI sprites with generated CSS/SVG-style fallback, never cached into the repository.
- No Git initialization and no project-local .codex directory.
- Script entrypoints target Windows PowerShell, resolve paths from the repository root, fail fast, and do not depend on the caller's working directory.
- This iteration interprets the user's “核心体能。特性” as core battle mechanics plus Pokémon abilities; configured abilities must affect the authoritative engine and emit explainable events.
- PVP remains local/in-memory and token-based: no accounts, matchmaking ladder or persistent lobby service.
- PVE dungeons reuse the authoritative battle engine; dungeon orchestration owns wave transitions and configured rewards, while each wave remains replayable and deterministic.
- Battle animation remains presentation-only: authoritative snapshots/events determine outcomes, while the client queues short visual sequences without delaying or altering server state.
- Auto battle delegates the controlling side to the server `BasicBattleAi`; PVP remains human-only, while Practice and Dungeon may be entrusted to AI.
- Battlefield actors reuse configured remote sprites for internal visualization; a CSS-rendered animated silhouette remains visible when an image fails, and no generated bitmap asset is required.
- Configured signature loadouts remain deterministic for replay integrity; optional random moves/abilities are not enabled by default.
- All four configured moves remain usable across selectable lab rulesets so every combatant keeps four manual skill buttons; generation rules still govern Mega/Z/Dynamax/Tera availability.

## Completion Gates
- Build and automated tests pass.
- Same seed and command sequence produce identical events.
- WebSocket quick battle works for singles and doubles with AI.
- Workbook exports, inspects without formula errors, and all sheets pass visual review.
- Remaining unimplemented canonical mechanics are explicitly recorded in the coverage matrix.
- Two independent WebSocket clients can join opposite sides, submit hidden choices and reconnect without gaining control of the other side.
- A configured test dungeon can be created, expose wave progress, advance after victory and finish with deterministic completion events.

## Errors Encountered
| Error | Attempt | Resolution |
|---|---:|---|
| `npx tsc --version` attempted an uncached registry request during discovery | 1 | Use project-local dependencies after explicit install/restore. |
| Initial solution build referenced server/test projects before their project files existed | 1 | Create the remaining project files before the next build. |
| Server sequence used `Interlocked` on a property | 1 | Back the sequence with a private field and expose a read-only property. |
| Test source did not import the xUnit namespace | 1 | Add an explicit `using Xunit;`. |
| Solution-wide `dotnet test` produced no output and was terminated after about one minute | 1 | Run the concrete test project with hang diagnostics; all seven tests completed in under one second. |
| Sandboxed `npm install` was restricted to an empty cache | 1 | Retried with approved registry access; 17 fixed-version packages installed with zero reported vulnerabilities. |
| TypeScript 7 required a declaration for the CSS side-effect import | 1 | Add Vite's client type reference. |
| Vite's Rolldown config loader could not spawn a child process in the sandbox (`EPERM`) | 1 | Run the fixed build command with approved process access; production client built successfully. |
| Repeated instance `workbook.fromCSV` calls conflicted with the artifact collaboration document | 1 | Import each CSV into a temporary workbook with static `Workbook.fromCSV`, then block-copy values into the final workbook. |
| Workbook builder referenced row/column counts before initialization after the import refactor | 1 | Move count initialization before the temporary import copy. |
| Summary `COUNTIF` treated unused status cells as non-Planned and reported 95 playable moves | 1 | Add a nonblank move-ID criterion with `COUNTIFS`; expected result is 36. |
| A final test build waited on assemblies locked by the running local server | 1 | Finish browser testing, stop the server, and remove the unused server project reference from domain tests. |
| The first AI soak size made event-log copies unnecessarily expensive | 1 | Use six fixed-seed single/double battles with 20-turn bounds; the invariant soak now completes in milliseconds. |
| Initial `dev.ps1` patch did not remain on disk and the syntax scanner reported access denied | 2 | Avoid hidden background-process orchestration; provide explicit `dev-server.ps1` and `dev-client.ps1` foreground entrypoints for two terminals. |
| Direct Release DLL startup used the repository root as ASP.NET content root, so `wwwroot` was missing and Windows EventLog logging failed under restricted permissions | 1 | Make `run.ps1` switch to `src/PokeBattle.Server` before launching the built DLL; its static web root and copied runtime config then resolve correctly. |
| PowerShell interpreted `$lines.Count-1` inside a nested array as array subtraction while chunking `BattleEngine.cs` | 1 | Compute the final index in a separate scalar before constructing chunk ranges. |
| New Protean test triggered xUnit2031 by filtering before `Assert.Single` | 1 | Use the predicate overload of `Assert.Single` so the build remains warning-free. |
| Combined H5 replay patch expected a CSS line without the existing `.types { ... }` prefix | 1 | Split protocol, TypeScript and CSS edits; patch CSS using its exact current selectors. |
| PowerShell WebSocket smoke test attempted to load an unavailable `System.Net.WebSockets.Client` assembly | 1 | The runtime type was already available; remove the unnecessary `Add-Type` step in future smoke tests.
| Windows sandbox denied `Start-Process` while attempting a hidden run-script smoke server | 1 | Run `scripts/run.ps1` in the foreground, verify it from a separate HTTP request, then terminate the owned tool process. |
| Browser regression attempted a non-existent direct `playwright.selectOption` helper | 1 | Use the documented locator surface (`tab.playwright.locator(selector).selectOption(...)`) for form controls. |
| Browser locator did not expose `inputValue()` in the in-app control facade | 1 | Read the generated battle ID and join code from the semantic snapshot, then fill those exact visible values in the second client. |
| Browser binding did not expose a `finalize()` method | 1 | Both owned regression tabs had already closed successfully; stop browser work without calling a non-existent finalizer. |
| Post-regression replay HTTP query ran after the bounded local server process had timed out | 1 | Browser assertions and domain tests had already captured the completed dungeon; perform the final replay/API check during the bounded Release smoke run instead of querying the expired process. |
| Vite 8 config loading could not spawn its helper process in the restricted sandbox (`EPERM`) | 1 | TypeScript checking completed successfully; rerun the same fixed build command with approved process access. |
| Hidden `Start-Process` server launch did not produce a process or health response in the managed environment | 1 | Avoid detached launch; run the server as an owned foreground tool process during browser verification. |
| In-app browser did not support the documented `networkidle` wait state | 1 | Use a DOM snapshot/targeted locator wait instead; the page itself loaded successfully. |
| Browser sampling missed short-lived effect nodes after the first turn had already advanced | 2 | Verify a subsequent turn in the same interaction call; this captured the acting card, move banner and projectile class while active. |
| The browser security policy blocked a final localhost reload after the theme-order correction | 1 | Stop browser automation as required; rely on the already completed live animation regression and a fresh successful production build for the isolated matcher-order correction. |
| Validation targeted `PokeBattle.sln`, but the workspace uses a different solution filename | 1 | Discover the actual root solution file and rerun the build against that explicit path. |
| Combined sprite load-state and actor animation patch did not match the minified CSS line exactly | 1 | Split the TypeScript and CSS edits, inspect the current selectors, and patch the exact line text separately. |
| `rg --files` piped through `Select-Object -First` returned exit code 1 after producing the requested listing | 1 | Treat the emitted listing as valid and use narrower `rg` searches for subsequent inspection. |
| A combined plan/compiler/docs `rg` inspection returned exit code 1 because the optional docs search had no matches | 1 | Use the successful plan/compiler output and avoid treating an empty optional search as a task failure. |
| The first combined implementation patch could not match mojibake Chinese team-name rows in `PresetTeams.csv` | 1 | Split code and data edits; patch the stable ASCII columns in the PVE rows separately. |
| Vite production bundling failed inside the restricted sandbox with the known `spawn EPERM` error | 1 | Re-ran the fixed `npm run build --prefix web` command with approved process access; it succeeded. |
| Final `git status/diff` review could not run because this workspace intentionally has no Git repository | 1 | Did not initialize Git per project instructions; used successful builds/tests and targeted runtime/source inspection instead. |
| First timer-enabled Release build found a duplicate `System.Text.Json` import and no ASP.NET shared-framework reference in the test project | 1 | Removed the duplicate import and added `Microsoft.AspNetCore.App` as a framework reference so the server logger abstraction is available without a new package. |
| First combined browser-findings plan patch included a heading that exists in `findings.md` but not `task_plan.md` | 1 | Split the status and findings updates and matched each file's actual structure. |
