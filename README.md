# Crimean Tom

Crimean Tom is the campaign, simulation, and networking system. Its thin,
campaign-agnostic in-game plugin is **Sea Power Runtime Bridge**, loaded through
[AnchorChain](https://github.com/SeaPower-Modders/AnchorChain).

Technical research on the installed game, tactical and campaign architecture,
UI workflows, testing, and the federated continuous-campaign design is indexed
in [`docs/README.md`](docs/README.md).

## Local development

The default installation is `C:\Program Files (x86)\Steam\steamapps\common\Sea Power`.
Set `SEA_POWER_DIR` when the game is installed elsewhere.

Every successful build deploys only `SeaPower.RuntimeBridge.dll` and its portable
PDB to `Sea Power_Data\StreamingAssets\SeaPowerRuntimeBridge`. It never copies
dependency DLLs. The build removes the obsolete `StreamingAssets\CrimeanTom`
plugin directory created by pre-rename builds.

```powershell
pnpm test        # run the broad control and campaign integration stories
pnpm build       # build and deploy
pnpm launch      # launch the game and wait for the plugin health marker
pnpm dev         # restart as needed, then build, deploy, launch, and verify
pnpm verify      # compare deployed/build hashes and inspect the current Player.log
pnpm logs        # show recent runtime bridge / Crimean Tom / AnchorChain errors
pnpm status      # show paths, process state, hashes, and current load state
pnpm inventory:determinism # generate the pinned compiled call-site manifest
```

The Phase 5 operator monitor is served by the campaign service:

```powershell
pnpm campaign:server
```

Open `http://127.0.0.1:5081/monitor/`, paste the per-run bearer token from
`.run/campaign-service/service.token`, and either connect to a campaign ID or
use **Create Fulda** to create and advance a representative Fulda Gap Gone Hot
campaign. The static Tailwind/Babylon shell is loopback-readable so it can load
before authentication; `/health`, campaign data, and all control routes still
require the bearer token. The page keeps the token in memory only—never in a
URL, cookie, or browser storage. Its map and inspector also expose task-force
interaction islands, current and desired fidelity, client eligibility/backlog,
load-shedding decisions, and fixed-step target tempo.

`pnpm dev` also starts the local .NET companion before Sea Power. The thin game
plugin connects to it over a named pipe; commands are dispatched on Unity's main
thread. The first slice is deliberately read-only:

```powershell
pnpm companion:status   # companion health and game-pipe connection
pnpm companion:health   # authenticated local health/session metadata
pnpm game:ping          # prove a Unity-main-thread round trip
pnpm game:capabilities  # negotiate the currently safe command surface
pnpm game:scene         # read active scene and manager/object counts
pnpm game:world         # read live task forces and canonical unit state
pnpm game:control-test  # exercise and validate all of the above
```

The loopback control API is authenticated with a per-run token in the ignored
`.run/` directory. The repository commands supply it automatically; callers do
not need to place tokens in source or shell history.

The runtime bridge resolves optional per-process isolation settings at startup:

- `CRIMEAN_TOM_INSTANCE_ID` identifies the runtime in its authenticated hello;
- `CRIMEAN_TOM_GAME_PIPE_NAME` selects the matching companion pipe; and
- `CRIMEAN_TOM_EXPERIMENTAL_SAVE_DIRECTORY` isolates bridge-owned probe saves.

The companion already accepts a distinct `-ControlUrl`, `-RunDirectory`, and
`-GamePipeName`. These settings isolate Crimean Tom's control port, token, logs,
pipe, and experimental checkpoint, but they do not relocate Sea Power's native
`Application.persistentDataPath` or Unity `PlayerPrefs`. A process is not a safe
paired replica until those native paths are isolated too.

The planned campaign application is local-first: packaged .NET 8, SQLite WAL,
and content-addressed local checkpoint files. Named pipes remain the same-machine
game bridge. Generated gRPC/Protobuf services connect .NET companions and
simulation workers; SignalR with MessagePack serves player-facing realtime UI.
Single-player, LAN/private co-op, and a basic dedicated campaign will not require
PostgreSQL, Redis, Docker, or an external object store.

The concise design is in
[`docs/architecture/dynamic-campaign-design.md`](docs/architecture/dynamic-campaign-design.md); the
normative ownership, time, materialization, protocol, persistence, and failure
contracts are in [`docs/architecture/system-architecture.md`](docs/architecture/system-architecture.md).
The controlled clock, scheduling, RNG, and ordering layer required for hot Unity
replicas is specified in
[`docs/architecture/deterministic-runtime-adapter.md`](docs/architecture/deterministic-runtime-adapter.md).
Stress handling, simulation-source eligibility, whole-island migration, and the
fixed-step/global-tempo policy are specified in
[`docs/architecture/load-management.md`](docs/architecture/load-management.md).
The gated delivery plan and adversarial checklist are in
[`docs/planning/implementation-plan.md`](docs/planning/implementation-plan.md). Redundant unit
simulation, primary/shadow/observer roles, health, and promotion are specified
in [`docs/architecture/federated-simulation.md`](docs/architecture/federated-simulation.md).

The initial multiplayer target is trusted/private co-op. Public or hostile-client
play is not claimed: a player's Sea Power process cannot safely hold true enemy
objects merely because its UI is filtered, and client tactical outcomes require
additional validation and trusted/neutral execution paths.

For local source inspection, ILSpy output is regenerated under the git-ignored
`decompiled/` directory:

```powershell
pnpm decompile              # Sea Power game assemblies
pnpm decompile:anchorchain  # AnchorChain and its preloader
pnpm decompile:all          # every DLL in Sea Power_Data/Managed
```

Launch commands use Steam app ID `1286220`. Starting `Sea Power.exe` directly
causes Steamworks to stop and restart the process, which breaks a deterministic
BepInEx/AnchorChain startup check.

`pnpm dev` succeeds only after a fresh game launch writes
`[Sea Power Runtime Bridge] loaded via AnchorChain` to
`%USERPROFILE%\AppData\LocalLow\Triassic Games\Sea Power\Player.log`.

Build, deploy, and launch commands stop an existing Sea Power process when a
fresh DLL must be loaded.
