# UNREP development handoff

Snapshot from 2026-09-12. Read [README.md](README.md) for the current feature
contract and configuration, and the repository's `AGENTS.md` before working.
This is context, not authorization to restart a later user's game session.

Current working changes add stacked inventory rows, per-ship minimum reserves,
and UNREP/VERTREP tabs enabled from Supplies. They are deployed and remain
uncommitted. See README for the current workflow and
[tab verification](verification-20260912.md) for the latest runtime checks.
`SupplyPolicy.cs` owns switches, reserve floors and their save data;
`TransferView.cs` owns UNREP and `VertrepView.cs` owns VERTREP. Save keys use unpadded Base64;
padding broke INI parsing and reset minimums on reload in the first test build.
VERTREP has Helicopters and Transfers subtabs. Helicopters manages the persistent
aircraft pool; Transfers has a receiver queue with expandable supply plans, Use
switches and supply ordering. Plans run to stock/capacity limits without requested
totals. Helicopters take the first runnable receiver/supply on each pickup, keep
their cargo when priorities change, and stay assigned when the queue is blocked.
UNREP now lists compatible receiving ships. Each expandable ship has an ordered,
enabled/disabled supply plan that runs to stock/capacity limits during one
alongside visit. Ship UP/DOWN controls the next free station, and REQUEUE preserves
the plan for a later visit. Both station slots remain supported.

## Starting point

- Branch: `codex/operations-campaign-pivot`, remote `origin` on GitHub.
- `76d248c6`: rename projects/mods to Deck Tweaks, Quickload and Boat Ops.
- `20b53aca`: inventories, alongside UNREP and helicopter VERTREP.
- `bd01edd8`: fix blank Supplies after window/scene replacement.
- All three commits were pushed with valid GPG signatures. Sign future commits
  when requested; signing key configured locally is `772472AD1170838F`.
- Game version tested: `0.8.2-23810+s366`.
- Game installation: `C:/Program Files (x86)/Steam/steamapps/common/Sea Power`.
- Last handoff left the demo paused with Tarawa's Supplies tab open. Treat this
  as historical state. Read `game-status`, `menu` and `status` before acting.

## Code map

| File under `src/UNREP/` | Responsibility |
| --- | --- |
| `Plugin.cs` | Load configuration, Harmony patches and persistent controller |
| `SuppliesController.cs` | Selected ship, tab lifecycle and transfer updates |
| `SuppliesView.cs` | Stacked inventory, minimum editor and method switches |
| `TransferView.cs` | Expandable receiver list, ship queue and per-ship supply controls |
| `VertrepView.cs` | Subtabs, helicopter management and assigned/flying/idle summary |
| `VertrepTransfersView.cs` | Expandable receiver queue and per-ship supply controls |
| `VertrepOperation.cs` | Receiver/supply priority, helicopter assignments and receiving-space reservations |
| `SupplyPolicy.cs` | Per-ship method switches, reserve floors and save data |
| `SupplyInventory.cs` | Native magazines, mounted weapons, aviation stores and aircraft rows |
| `SupplyStock.cs` | UNREP-owned reserves and vessel save integration |
| `SupplySettings.cs`, `supplies.ini` | Displacement-based stock/capacity and handling settings |
| `SupplyLabels.cs` | Human-readable native magazine/location names |
| `SupplyTransfers.cs` | Receiver plans, ordered supplies, queue priority, accounting and shared update/reset |
| `ReplenishmentMovement.cs` | Movement leases, alongside stations, speed limits and collision clearance |
| `HelicopterReplenishment.cs` | Pickup/delivery shuttle and cargo accounting |
| `VertrepLanding.cs` | Compatible deck reservation and native approach, landing and takeoff states |
| `HelicopterSpeedPatch.cs` | Narrow native flight-controller speed override for owned shuttles |

`src/SeaPower.SharedUi/` is source compiled into each consumer, not another mod
or deployed shared DLL. Read its README. Changing its source requires rebuilding
each consumer that needs the change. The Supplies fix was deployed in UNREP only.

## Details worth preserving

- Reserve quantities use `base kg + kg per tonne * displacement`. Native stores
  are read from the game, not synthesized with that formula.
- Hide empty optional rows, but retain depleted critical reserves and ammunition
  the ship uses. Aircraft are inventory entries, not transferable cargo.
- Alongside movement uses native waypoints. Supplier speed is capped at 8 knots;
  receivers rendezvous astern, take a side station, then break away. The default
  transfer gates include 0.2 nm range, 12 knots, heading and relative-speed checks.
- Native collision avoidance now uses a job scheduler. Patching only the old
  collision-check method missed it. The scoped `UnitToAvoid` setter patch handles
  an active pair, subject to clearance checks; third-party traffic stays protected.
- Helicopter AI overrides ordinary speed commands based on waypoint distance.
  The flight-controller patch adjusts that native path only for owned shuttles.
  Keep native banking, yaw and altitude control; do not teleport aircraft.
- VERTREP uses an already airborne helicopter, a 500 kg load and 30 seconds at
  each endpoint by default. Debit at pickup and credit at delivery. Loss of the
  helicopter loses cargo. See README for cancellation and order replacement.
- The empty Supplies bug came from retaining a stale Unit Status view. The shared
  helper now searches the current host for a visible view with the current view
  model, releases the old presenter's content, and attaches to the replacement.
  Checking only a cached view's `IsVisible` was insufficient.
- Native UI trees can retain old windows across scenes. CLI `wait-for` matching
  old text is not proof that a new scenario finished loading. Check units and
  mission time, then a screenshot. Never reuse old control IDs or snapshot tokens.

## Known limits and next work

These are gaps, not approved tasks:

- Fuel reserve is not connected to propulsion or aircraft refuelling. Food,
  water and spare parts do not yet drive crew consumption or repairs.
- Active jobs are not restored from saves. Finish/cancel and complete breakaway
  before saving. Otherwise native saves can retain temporary movement orders
  without the owning job. This needs design before claiming save/load support.
- VERTREP now lands at both ships for handling, reserves compatible native recovery
  spots and takes off with the same aircraft. Initial launch and final hangar
  recovery remain manual. No visible sling, hoses or crane models.
- The native landing change builds without warnings and loads in the fleet
  playground. A full landing/delivery cycle remains unverified. Approach,
  HelicopterLanding and HelicopterTakeOff run through native game callbacks;
  hangar taxiing, elevators and despawn are omitted for VERTREP assignments.
- VERTREP cargo now transfers immediately at touchdown. Its native landing skips
  rotor shutdown and its takeoff skips cold-start timing. Other recovery tasks
  retain vanilla rotor behavior. `HandlingSeconds` is obsolete and ignored.
- Departure now runs PreFlightAnim before HelicopterTakeOff and retains the deck
  reservation through a native flight-control climb to 20 metres above deck.
  The next transfer route is deferred until clearance. Build/startup checks do
  not verify this full departure sequence in motion.
- VERTREP transports food, water and spare parts only. Aircraft-specific lift
  certification and capacity are not modeled.
- No safe-corridor route planning. Stationary suppliers do not start moving.
- Native automatic replenishment operates separately.
- Not live-tested: combat interruptions,
  cargo loss, stationary suppliers, or active-job save/load.
- An unrelated `EnhancedDecoyMod` field-type initialization failure appeared in
  the logs. Do not mistake it for an UNREP failure or alter that mod without scope.

## Verification evidence

The latest [tab verification](verification-20260912.md) covers enabling/disabling
tabs, ship switching, minimum enforcement, save/load and window recreation.
The following checks describe the older transfer implementation and UI fix.

There is no maintained automated test suite. Read `$sea-power-testing` before
live checks. Do not substitute a successful build for a game check.

The tracked [demo scenario](scenarios/unrep-shuttle.ini) contains two Tarawas and
an airborne CH-46, in calm open water. It is the same scenario used in these checks:

- VERTREP delivered 500 kg food by 328.5 simulated seconds. Source food changed
  from 78,100 to 77,600 kg. Receiver matching speed was observed at 9.6 knots.
- Alongside delivered 500 kg and entered breakaway by 1250.2 simulated seconds;
  the job was complete by 2473.3 seconds. Supplier capped at 8 knots during the
  job; both ships returned to their prior 9.6-knot demand afterward.
- After the UI fix, encyclopedia -> main menu -> demo in the same process,
  plus closing/reopening Unit Status, kept Supplies populated with 20 rows.
- The UI-fix build had zero warnings/errors and matched the deployed DLL.
  SHA-256: `599D73DD62D3890CB4F41954556149F9D7C4C930224E10732001AE4CE6E2FF22`.
  Fresh load marker at `2026-09-12T04:12:15.518Z`: `[UNREP] Loaded Supplies tab.`
  No UNREP exception was found in that run.

Local-only evidence, not required on a fresh checkout:
`artifacts/unrep-shuttle-check-20260912.md` has the transfer observations and
screenshot paths. The UI regression screenshot is
`C:/Users/Varun/AppData/Local/SeaPowerGameBridge/screenshots/9aeaa5b1f9a74fa8bc3271044f789c7c.png`.

## Build and operate

Run from the repository root. Prefer the CLI over computer use.

```powershell
# Compile without touching the running installation.
dotnet build src/UNREP/UNREP.csproj -p:DeployToGame=false
$cli = './src/SeaPower.Cli/bin/Debug/net10.0/seapower-cli.exe'
& $cli game-status
& $cli menu
& $cli status
```

Normal `dotnet build src/UNREP/UNREP.csproj` deploys by default. Close the game
before deployment and restart to load changed DLLs. Deployment preserves an
existing installed `supplies.ini`; changing the repository defaults does not
overwrite the player's configuration.

When launch or mission replacement is authorized:

```powershell
& $cli launch --confirm --require-pattern '\[UNREP\] Loaded Supplies tab'
# Dismiss any startup popup using a fresh inspect/click before loading.
& $cli load-scenario --scenario "$PWD/docs/unrep/scenarios/unrep-shuttle.ini" --confirm
& $cli units --select 'new(Name,Type,SpeedKnots)'
& $cli pause
& $cli inspect --where 'Name == "UnitStatusWindow" || Type == "RadioButton"'
& $cli screenshot
```

Use fresh `snapshot` and `id` fields for each click. In Supplies, enable a method
and set the desired minimum. In UNREP, expand Saipan, enable and order its supplies,
then QUEUE the ship. `scenarios/unrep-queue.ini` contains four receiving ships
for queue priority and station checks.
For VERTREP, use Transfers to expand/configure and QUEUE a receiver, then use
Helicopters to select CH-46 and ASSIGN. The [receiver demo](scenarios/vertrep-receivers.ini)
has two airborne CH-46s and two receivers. Useful control names: `SupplyInventory`,
`SupplyMinimum`, `SetSupplyMinimum`, `EnableUnrep`, `EnableVertrep`,
`UnrepReceiver`, `UnrepQueueShip`, `UnrepShipUp`, `UnrepShipDown`, `UnrepCancelShip`,
`UnrepShipSupplies`, `UnrepSupplyUp`, `UnrepSupplyDown`, `UnrepEnableAll`, `UnrepDisableAll`,
`VertrepTabs`, `VertrepSummary`, `VertrepReceiver`, `VertrepQueueShip`,
`VertrepShipUp`, `VertrepShipDown`, `VertrepCancelShip`, `VertrepShipSupplies`,
`VertrepSupplyUp`, `VertrepSupplyDown`, `VertrepEnableAll`, `VertrepDisableAll`,
`VertrepAvailableHelicopters`, `AssignVertrepHelicopter`, `VertrepHelicopters`,
`UnassignVertrepHelicopter`. `Options` is an array, so LINQ uses `Options.Length`.

See [CLI README](../seapower-cli/README.md) for exit, encyclopedia, screenshots,
details and time controls. Close agent-launched games after tests unless the user
wants them left open. Do not shut down the machine or manipulate unrelated apps.
