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

The local player campaign shell is served by the campaign service:

```powershell
pnpm campaign:server
```

Open `http://127.0.0.1:5081/monitor/` to create or open the named 4,096-entity
campaign, inspect the coalition-filtered map and friendly hierarchy, and use the
start, pause, and save controls. Select a movable friendly group and a visible
objective, base, friendly, or known-contact region to issue a durable move order;
`Cancel and hold` replaces it with a persistent hold. New and restored campaigns
open paused. The loopback host gives the page an HttpOnly, same-site,
process-lifetime session accepted only by `/player`; it cannot authorize operator
endpoints. CLI and companion traffic use the current-user-only bearer token file.
The player shell polls HTTP and renews its local session after a service restart.
Realtime delivery, broader orders and policy controls, complete objective
progress, campaign end state, and tactical entry are not implemented.

For iteration with backend restart and frontend hot-module replacement, run:

```powershell
pnpm campaign:dev
```

The Vite monitor is then available at `http://127.0.0.1:5173/monitor/`; its
loopback proxy authenticates to the watched campaign service automatically.
Vite owns frontend file watching; the backend watcher excludes its generated
bundle so production monitor rebuilds do not trigger a backend rescan storm.

`pnpm dev` also starts the local .NET companion before Sea Power. The thin game
plugin connects to it over a named pipe; commands are dispatched on Unity's main
thread. The allow-listed surface includes read state, screenshots, typed UI
automation, collider capture, tactical pause control, and explicitly armed/fenced
owned-surface mutations:

```powershell
pnpm companion:status   # companion health and game-pipe connection
pnpm companion:health   # authenticated local health/session metadata
pnpm game:ping          # prove a Unity-main-thread round trip
pnpm game:capabilities  # negotiate the currently safe command surface
pnpm game:scene         # read active scene and manager/object counts
pnpm game:world         # read live task forces and canonical unit state
pnpm game:ui            # inspect current window, HUD, pause, and selection
pnpm game:screenshot    # capture the Unity framebuffer to a PNG
pnpm game:colliders     # inspect cached live collider geometry
pnpm game:control-test  # exercise and validate all of the above
```

The loopback control API is authenticated with a per-run token in the ignored
`.run/` directory. The repository commands supply it automatically; callers do
not need to place tokens in source or shell history.

The runtime bridge resolves optional per-process isolation settings at startup:

- `CRIMEAN_TOM_INSTANCE_ID` identifies the runtime in its authenticated hello;
- `CRIMEAN_TOM_GAME_PIPE_NAME` selects the matching companion pipe; and
- `CRIMEAN_TOM_EXPERIMENTAL_SAVE_DIRECTORY` isolates bridge-owned artifacts.

The companion already accepts a distinct `-ControlUrl`, `-RunDirectory`, and
`-GamePipeName`. These settings isolate Crimean Tom's control port, token, logs,
pipe, and runtime artifacts, but they do not relocate Sea Power's native
`Application.persistentDataPath` or Unity `PlayerPrefs`; concurrent Sea Power
instances are therefore outside the active local architecture.

The campaign application is local-first: .NET 8, SQLite WAL,
and content-addressed local checkpoint files. Named pipes remain the same-machine
game bridge. Generated gRPC/Protobuf services connect the companion to the
campaign service. SignalR with MessagePack is the target player-facing realtime
transport, but it is not implemented; the current player shell and coalition
projection use HTTP.
Single-player, LAN/private co-op, and a basic dedicated campaign will not require
PostgreSQL, Redis, Docker, or an external object store.

The player-facing source of truth is
[`docs/planning/playable-dynamic-campaign-product-contract.md`](docs/planning/playable-dynamic-campaign-product-contract.md).
It defines the map, task-force, streaming, tactical-entry/return, campaign-time,
orders, recovery, and minimum-playable behavior. The normative ownership, time,
materialization, protocol, persistence, and failure contracts are in
[`docs/architecture/system-architecture.md`](docs/architecture/system-architecture.md).
The code-to-process map is in
[`docs/architecture/implementation-map.md`](docs/architecture/implementation-map.md), and the
current feature/gap ledger is in
[`docs/planning/current-status.md`](docs/planning/current-status.md).
The gated delivery plan and adversarial checklist are in
[`docs/planning/implementation-plan.md`](docs/planning/implementation-plan.md).
Deferred distributed simulation work is summarized in
[`docs/future/distributed-simulation.md`](docs/future/distributed-simulation.md).

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
