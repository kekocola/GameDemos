# PokeBattle Contributor and Agent Guide

## 1. Purpose of this document

This file is the operating guide for coding agents and human contributors working in PokeBattle. Read it before changing source code, configuration, tests, scripts, or documentation. It describes the repository map, supported features, architectural boundaries, coding conventions, build flow, validation expectations, and common change patterns. It is intentionally more prescriptive than the public README because its main purpose is to keep automated changes coherent across the C# domain engine, ASP.NET Core server, TypeScript client, and configuration pipeline.

PokeBattle is a deterministic, configuration-driven battle laboratory inspired by traditional turn-based creature battles. It is not a production game backend and does not claim complete coverage of every official move, ability, item, or generational edge case. The system favors auditable representative mechanics, reproducible simulations, explicit gaps, structured diagnostics, and versioned replay artifacts. When a behavior is not implemented, record the gap instead of silently approximating it.

Do not initialize a Git repository or create a `.git` directory unless the user explicitly requests Git initialization. Do not create project-local Codex configuration by default. Preserve existing user files and unrelated changes. Generated outputs should be regenerated through the documented tools rather than edited by hand.

## 2. Architectural principles

The server is authoritative. The browser may collect commands, display legal choices, animate ordered events, and inspect snapshots, but it must not calculate canonical damage, decide legal targets, select a winner, mutate battle truth, or invent missing domain events. Any rule that can affect the outcome belongs in `PokeBattle.Domain` and must be exposed through authoritative state or events.

Battles must be deterministic. A battle is identified by its configuration hash, ruleset, teams, format, mode, seed, accepted command sequence, and ordered event sequence. Random decisions use the repository's deterministic random source. Never introduce `System.Random`, time-based seeds, browser randomness, unordered iteration, or other nondeterministic inputs into battle resolution. Cosmetic client effects may be nondeterministic only when they cannot alter protocol data or replay meaning.

Configuration is compiled, not interpreted ad hoc during battle execution. CSV files under `data/csv` are the editable source of truth. The configuration compiler validates and emits `data/runtime/battle-config.json`. The server loads the compiled JSON once at startup. Do not make the battle engine read CSV files, access external websites, or patch definitions while a battle is running.

Events are an ordered public history of state transitions. State snapshots describe current truth; events explain how that truth was reached. Event sequence numbers must be monotonic, replayable, and stable for a given configuration, seed, and command stream. Diagnostics are separate from public battle events and should not leak hidden choices to opponents.

Protocol compatibility matters. Client and server envelopes currently use protocol version 1, while replay files have an independent replay version. Additive protocol changes are preferred. Renaming fields, changing enum serialization, or reinterpreting existing event data requires coordinated server, client, documentation, and compatibility work.

## 3. Repository code map

### Root files

- `PokeBattle.slnx` lists the four .NET projects: domain, server, configuration compiler, and tests.
- `Directory.Build.props` centralizes .NET 10, nullable reference types, implicit usings, the latest C# language version, and deterministic compilation.
- `README.md` is the public quick-start and feature overview.
- `AGENTS.md` is this contributor guide.
- `task_plan.md`, `progress.md`, and `findings.md` contain project planning and historical implementation notes. Treat them as context, not runtime inputs.
- `.gitignore` excludes dependency trees, build products, compiled runtime configuration, static web bundles, reports, logs, and local secrets.

### Domain engine: `src/PokeBattle.Domain`

- `BattleState.cs` defines serializable battle state, sides, creatures, move slots, field state, events, choices, debug entries, replay commands, and core enums. State types form the shared semantic contract used by the engine and server.
- `BattleEngine.cs` owns command validation, turn resolution, action ordering, move execution, effect steps, status processing, switching, faint handling, special mechanics, battle termination, dungeon wave transitions, event emission, and diagnostic tracing. This is the primary location for outcome-affecting behavior.
- `Rules.cs` contains ruleset abstractions, generation-sensitive constants, type effectiveness, category behavior, stat calculations, and damage calculation support.
- `Abilities.cs` implements supported ability hook families such as immunity, power modifiers, defensive modifiers, entry effects, contact reactions, speed changes, survival, and temporary state cleanup.
- `BattleAi.cs` evaluates legal choices and produces deterministic AI decisions and candidate diagnostics. AI must consume the same legal choice surface available to players rather than bypassing domain validation.
- `Configuration.cs` contains compiled configuration models and lookup structures for species, moves, abilities, effects, rulesets, formats, teams, dungeons, and related data.
- `DeterministicRandom.cs` provides the reproducible random source. All battle randomness must pass through this abstraction.
- `PokeBattle.Domain.csproj` is a plain .NET library with repository-wide settings inherited from `Directory.Build.props`.

The domain project must not depend on ASP.NET Core, WebSocket sessions, browser concepts, local storage, HTTP response models, or UI timing. Keep it portable and testable.

### Server: `src/PokeBattle.Server`

- `Program.cs` is the ASP.NET Core composition root. It resolves the compiled configuration, registers services, configures CORS and WebSockets, serves the production client, and maps health, config, room, debug, replay, and WebSocket endpoints.
- `Protocol.cs` defines server-side envelope and payload models used by the WebSocket protocol.
- `WebSocketSession.cs` validates client envelopes, sequence numbers, protocol versions, room ownership, reconnect tokens, and message dispatch.
- `BattleRooms.cs` manages in-memory rooms, session ownership, PvP joining, AI advancement, choice deadlines, snapshots, event broadcasts, dungeon progression, and structured debug delivery. It coordinates the engine but must not duplicate battle rules.
- `BattleReplay.cs` builds replay artifacts, computes event hashes, and performs replay integrity checks.
- `PokeBattle.Server.csproj` references the domain project and copies the compiled runtime configuration into build output.
- `wwwroot` is generated by the Vite production build. Do not hand-edit hashed assets or generated `index.html` there.

The server is intentionally in-memory. There is no account database, inventory service, matchmaking persistence, or durable reward system. Restarting the server discards active rooms.

### Configuration compiler: `tools/PokeBattle.ConfigCompiler`

- `Program.cs` reads UTF-8 CSV source files, parses definitions, validates keys, references, enums, numeric ranges, handlers, team loadouts, coverage status, and dungeon topology, then writes the compiled JSON snapshot.
- `PokeBattle.ConfigCompiler.csproj` is a .NET console application referencing the domain configuration models.

Compiler validation is part of the product boundary. Invalid configuration should fail early with a useful error rather than survive into runtime and cause an incomplete battle. When adding a new handler key, add both compiler acceptance and runtime implementation.

### Configuration data: `data`

- `data/csv` is the versionable source of truth. Major tables include species, forms, moves, abilities, items, learnsets, preset teams, rulesets, generation constants, type chart, formats, effects, effect steps, statuses, field effects, targets, turn phases, scenarios, expected events, mechanic coverage, sources, dungeons, and dungeon waves.
- `data/runtime/battle-config.json` is generated by the configuration compiler. Never edit it manually. Regenerate it after source CSV changes.
- `MechanicsCoverage.csv` records whether a mechanic is verified representative behavior, implemented but unverified, planned, or out of scope. Do not mark work verified without evidence and focused tests.
- `Sources.csv` and source identifiers provide traceability for rules research.

CSV files must remain UTF-8, keep stable headers, use invariant numeric formatting, and preserve referential integrity. A workbook under `outputs` may be generated for review, but it is not a runtime source.

### Web client: `web`

- `src/main.ts` owns application markup, WebSocket connection management, envelope handling, room setup, snapshot rendering, legal-choice controls, team displays, timeline, diagnostics, replay UI, reconnect state, and auto-battle presentation.
- `src/battle-animations.ts` serializes event-driven animations, visual HP, move banners, casting highlights, hits, healing, switching, fainting, and reduced-motion behavior. Animation queues must respect battle boundaries and terminal state.
- `src/protocol.ts` mirrors the protocol and snapshot types used by the client. Keep it synchronized with server JSON shape and enum strings.
- `src/style.css` contains the responsive battle laboratory design, state classes, animations, reduced-motion rules, and mobile breakpoints.
- `vite.config.ts` runs the development proxy and writes production output directly into the server's `wwwroot` directory.
- `package.json` exposes `dev`, `build`, and `preview`; `package-lock.json` pins frontend dependencies.

The client may temporarily maintain presentation state, such as queued events and visual HP, but authoritative values always come from snapshots and ordered server events. Reconnect or manual snapshot handling must not replay historical animations twice. A new battle ID is a hard animation reset boundary.

### Tests: `tests/PokeBattle.Tests`

- `BattleEngineTests.cs` contains focused xUnit coverage for deterministic engine rules, command behavior, event identity, turn limits, abilities, replay diagnostics, PvP flow, and dungeon transitions.
- `PokeBattle.Tests.csproj` references both domain and server projects and uses xUnit with the Microsoft test SDK.

Add the smallest test that proves the changed behavior. Prefer deterministic seeds and explicit event assertions. A rule bug should first become a failing test whenever practical.

### Scripts and documentation

- `scripts/_common.ps1` provides strict PowerShell behavior, repository-root resolution, native command checking, environment validation, and frontend dependency checks.
- `check-env.ps1`, `restore.ps1`, `build.ps1`, `test.ps1`, `run.ps1`, `dev-server.ps1`, and `dev-client.ps1` are the supported workflows. Prefer them over undocumented command sequences.
- `docs/battle-rules.zh-CN.md` documents lifecycle and representative battle semantics.
- `docs/protocol.md` documents envelopes, messages, events, debug APIs, and replay APIs.
- `docs/configuration.md` explains CSV compilation, effects, coverage, and data rules.
- `docs/generation-matrix.md` describes generation coverage.
- `docs/pve-dungeons.zh-CN.md` documents dungeon configuration and wave flow.

## 4. Implemented feature inventory

The battle laboratory supports generation-specific ruleset profiles from generations I through IX and both singles and doubles formats. Representative combat includes physical, special, and status moves; priority and speed ordering; deterministic speed ties; accuracy; critical hits; STAB; type effectiveness; burn modification; weather; terrain; major status conditions; stat stages; healing; drain; recoil; fixed damage; multi-hit effects; protection; forced switches; and stage clearing.

Representative special mechanics include Mega Evolution, Z-Moves, Dynamax, and Terastallization with generation restrictions and per-side usage tracking. Representative ability hooks include offensive and defensive modifiers, type immunity, contact reactions, entry weather, entry stat changes, copying, Protean-style type changes, speed changes, Flash Fire-style state, and Sturdy-style survival. Unsupported unique mechanics remain explicit coverage gaps.

Battle modes include local practice, real-time two-player PvP, and configuration-driven PvE dungeons. PvP uses a battle ID, one-time join code, side-specific controller tokens, simultaneous hidden choices, reconnect support, and a server-managed choice deadline. Dungeons reuse the authoritative engine across ordered waves and emit wave, completion, failure, boss, recovery, and reward metadata events.

The server provides in-memory rooms, legal choice generation, AI control, structured diagnostics, snapshots, ordered events, replay export, event hashing, and integrity verification. The client provides setup controls, team and active-creature display, move selection, responsive presentation, event animation, auto battle, timeline inspection, state inspection, debug filtering, protocol inspection, replay import and download, and step or timed replay playback.

## 5. Coding standards

### General standards

Keep changes narrowly scoped and preserve existing behavior outside the request. Read neighboring code before introducing a new abstraction. Favor clear data flow over clever indirection. Use stable names that match domain and protocol terminology. Update documentation when public behavior, configuration schema, or message shape changes.

Do not edit generated files to simulate success. Generated JSON, `wwwroot`, `bin`, `obj`, dependency trees, test reports, and output workbooks must be produced by their owning tool. Never commit secrets, tokens, local certificates, machine-specific paths, or personal environment files.

### C# standards

Use nullable annotations correctly and avoid suppressing warnings without a documented reason. Prefer immutable records or init-only properties for values that should not change, and explicit mutation for battle state that does. Use collection expressions and modern language features only when they improve clarity. Keep public methods small enough to show validation, state transition, and event emission distinctly.

Return stable error codes plus readable messages for rejected commands. Reject invalid input before mutating state. If a command can emit multiple events, capture the event-log starting index and return only newly emitted events. Use ordinal or ordinal-ignore-case comparisons explicitly for identifiers. Do not use culture-sensitive parsing for configuration or protocol values.

Domain changes should emit enough structured event data for the client, diagnostics, and replay tools to identify the exact side, slot, creature instance, move, target, amount, and result where relevant. Prefer instance IDs over display names when identity matters. Display names may be duplicated and may change independently of identity.

Keep rule-specific calculations in rules, damage, ability, or effect helpers instead of the room layer. Keep network concerns in the server. Cancellation tokens should be passed through asynchronous server operations. Room mutations must remain protected by the room gate.

### TypeScript and CSS standards

Keep TypeScript strict and avoid `any`. Model server payloads in `protocol.ts` and narrow unknown payloads at message boundaries. Escape untrusted strings before inserting HTML. Do not use client state to override authoritative battle results. WebSocket sequence handling, battle IDs, and reconnect state must remain monotonic and duplicate-safe.

Animations must be driven by real events. Every asynchronous animation queue needs an explicit reset or generation boundary so stale work cannot affect a new battle. Terminal snapshots must either cancel pending presentation or defer the result until the intended final presentation is complete; never show a final result while unrelated queued moves continue unnoticed. Preserve `prefers-reduced-motion` behavior.

Use existing CSS classes and visual tokens before adding one-off inline styles. Maintain desktop and mobile layouts. State such as active, unavailable, fainted, casting, hit, or disabled should be represented with semantic classes. Interactive controls must remain keyboard-usable, labeled, and disabled when the user no longer owns the decision.

### Configuration standards

Treat IDs as stable API values. Use lowercase, descriptive identifiers consistent with existing tables. Validate all cross-table references. A preset team member must have a valid ability and four distinct configured moves. New effect operations or handler keys require compiler validation, runtime behavior, events, and tests.

Every playable mechanic needs generation bounds, implementation status, and a source reference. Do not convert `Planned` content into playable content merely by adding data; runtime support and validation must exist first. Maintain deterministic ordering in emitted JSON so configuration hashes remain meaningful.

### Tests and diagnostics

Tests should assert outcomes and the events that explain them. Use fixed seeds and minimal teams where possible. For bugs involving identity, assert instance IDs. For generational rules, state the ruleset explicitly. For replay changes, validate both content and integrity. For protocol changes, verify authorization and rejection behavior as well as the success path.

Structured debug entries should use stable categories and messages suitable for filtering. Debug data may be detailed, but it must not alter battle resolution or expose hidden opponent choices through ordinary snapshots.

## 6. Build, test, and run workflow

Required tools are PowerShell 5.1 or newer, .NET SDK 10.x, npm, and a supported Node.js version: 20.19 or newer in the 20.x line, 22.12 or newer in the 22.x line, or a later major version.

Use these commands from the repository root:

```powershell
.\scripts\check-env.ps1
.\scripts\restore.ps1
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\run.ps1
```

`check-env.ps1` verifies tool availability and versions. `restore.ps1` runs a locked .NET restore and `npm ci`. `build.ps1` compiles and validates CSV configuration, builds the TypeScript client into server `wwwroot`, builds the .NET solution, and runs automated tests unless skipped. `test.ps1` regenerates runtime configuration, builds the client unless skipped, and runs xUnit tests. `run.ps1` builds without tests by default and starts the combined production site at `http://localhost:5080`.

Useful targeted commands include:

```powershell
.\scripts\build.ps1 -Configuration Debug -SkipRestore
.\scripts\build.ps1 -SkipClient -SkipTests
.\scripts\test.ps1 -SkipRestore
.\scripts\run.ps1 -NoBuild -Url http://localhost:5090
```

For development, use two foreground terminals:

```powershell
.\scripts\dev-server.ps1
.\scripts\dev-client.ps1
```

The server watches C# at `http://localhost:5080`. Vite serves the client at `http://127.0.0.1:5173` and proxies `/api`, `/health`, and `/ws` to the server. Do not create hidden background services unless the user explicitly requests that workflow.

When only frontend source changes, `npm run build --prefix web` performs TypeScript checking and production bundling. Remember that this replaces the generated server `wwwroot` directory. When only C# domain logic changes and runtime configuration is current, a targeted `dotnet test` may be appropriate, but the full script remains the release-level check.

If the user says not to test, do not run builds or tests. You may still inspect source and perform non-executing consistency checks. Report clearly which verification was intentionally skipped.

## 7. Common change recipes

For a battle rule change, identify the authoritative engine phase, add or adjust configuration if necessary, implement the rule in the domain project, emit structured events, add a focused deterministic test, update coverage status, and document generation boundaries.

For a new move or effect, prefer existing effect steps. Add a new handler only when the operation needs context that generic steps cannot express. Update compiler allowlists, runtime dispatch, event output, tests, source references, and expected-event data together.

For an ability, determine its trigger hook: before move, immunity, damage modifier, survival, contact, entry, speed, or switch cleanup. Ensure the ability emits `AbilityActivated` only when it actually affects the battle. Clear temporary ability state on switch where rules require it.

For a protocol change, update server payload models, client types, send or receive logic, protocol documentation, and replay compatibility if events or snapshots are affected. Preserve authorization checks and sequence validation.

For a client animation change, trace the full path from `battleEvents` through the pending queue, snapshot sequence, rendered DOM, animation chain, reset generation, and terminal presentation. Verify reconnect, duplicate events, auto battle, reduced motion, and new battle creation.

For a dungeon change, edit dungeon and wave CSV source, maintain continuous wave numbering and valid team references, regenerate configuration, verify restore and seed-offset semantics, and cover completion and failure paths.

## 8. Completion checklist

Before handing off a normal code change, confirm that edited source is formatted consistently, identifiers and protocol fields match across layers, generated files were produced rather than hand-edited, deterministic behavior is preserved, and relevant documentation is current. Run the narrowest useful validation followed by the repository workflow appropriate to the change, unless the user explicitly asks to skip testing.

In the final report, lead with the outcome. Name the important files changed, summarize behavior, list validation performed, and disclose skipped or blocked checks. Do not claim full rules accuracy when only representative mechanics are implemented. Do not initialize Git, create commits, publish artifacts, or contact external systems unless the user explicitly asks.
