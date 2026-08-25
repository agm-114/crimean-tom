# Getting started

## Purpose

Use this guide when opening the repository in a new development task. It covers
the stable project boundaries, local setup, common workflows, and the clean path
from a campaign save into Sea Power.

Do not use this page as a status report. Read [current status](../planning/current-status.md)
for the next product gap and [supported claims](../planning/supported-claims.md)
for the behavior that has been proven. Those documents change as the project
advances.

## Start with the product boundary

Crimean Tom is a local-first dynamic campaign for Sea Power:

- the campaign application owns the war, strategic AI, orders, intelligence,
  saves, and tactical entry;
- Sea Power owns the local battle and its tactical map;
- the companion and Runtime Bridge translate between those two owners; and
- campaign simulation continues while Sea Power holds a renewable lease over a
  bounded set of tactical entities.

The first playable target is local single-player. Private multiplayer remains
in scope after that loop works. Distributed simulation, hot replicas, and live
tactical migration are deferred.

Read these documents before changing behavior:

1. [Product contract](../planning/playable-dynamic-campaign-product-contract.md)
2. [Current status](../planning/current-status.md)
3. [System architecture](../architecture/system-architecture.md)
4. [Implementation map](../architecture/implementation-map.md)
5. [Implementation plan](../planning/implementation-plan.md)
6. [Supported claims](../planning/supported-claims.md)

When they disagree, use the precedence rules in the
[documentation index](../README.md).

## Prerequisites

Development and live game testing currently assume:

- Windows and PowerShell;
- the .NET SDK selected by `global.json`;
- `pnpm`, using the version declared in `package.json`;
- Steam and Sea Power;
- AnchorChain installed in the Sea Power directory; and
- a repository checkout with Git submodules and package dependencies present.

The scripts use this Sea Power directory by default:

```text
C:\Program Files (x86)\Steam\steamapps\common\Sea Power
```

If the game is elsewhere, set `SEA_POWER_DIR` for the shell before running a
repository command:

```powershell
$env:SEA_POWER_DIR = 'D:\SteamLibrary\steamapps\common\Sea Power'
```

From a fresh checkout:

```powershell
git submodule update --init --recursive
pnpm install --frozen-lockfile
pnpm status
```

`pnpm status` reports missing game, mod-loader, build, deployment, and process
requirements without launching Sea Power.

## Repository map

| Path | Responsibility |
| --- | --- |
| `src/CrimeanTom.Campaign.Contracts` | Campaign identities and cross-layer contracts |
| `src/CrimeanTom.Campaign.Engine` | Deterministic Operational simulation and strategic AI |
| `src/CrimeanTom.Campaign.Service` | SQLite journal, snapshots, blobs, backup, and restore |
| `src/CrimeanTom.Campaign.ServiceHost` | Campaign HTTP/gRPC host, scheduling, player projection, and tactical handoff |
| `src/CrimeanTom.Companion` | Campaign-to-game adapter, tactical lease, map/proxy sync, and checkpoint readback |
| `src/SeaPower.RuntimeBridge` | Typed inspection and controlled mutation inside Sea Power |
| `apps/campaign-monitor` | Local campaign player application |
| `content` | Campaign and Sea Power content shipped or deployed by the project |
| `scripts` | Supported build, launch, control, benchmark, and research entry points |
| `tests` | Broad campaign and protocol integration stories |

Use the [implementation map](../architecture/implementation-map.md) for the
full process and ownership map. Search with `rg` before adding a new type or
endpoint; the repository already contains several narrow runtime primitives
that are not yet connected into a complete player flow.

## Fast local checks

Run the broad automated stories from the repository root:

```powershell
pnpm test
```

For a narrower diagnosis:

```powershell
pnpm monitor:build
pnpm test:campaign
pnpm test:protocol
```

These tests do not prove Unity behavior. Any change to tactical entry, native
objects, map interaction, terrain placement, hosted aircraft, or tactical
return also needs a live Sea Power check.

## Run the campaign application

For a production-like local loop:

```powershell
pnpm campaign:server
```

Open `http://127.0.0.1:5081/monitor/`. Create a campaign, inspect the friendly
hierarchy and known contacts, issue an order, start or pause time, and save it.

For backend restart and frontend hot-module replacement:

```powershell
pnpm campaign:dev
```

The development monitor is at `http://127.0.0.1:5173/monitor/`.

New and restored campaigns open paused. The browser receives a process-lifetime
player session. Operator and companion routes use the bearer token in
`.run/campaign-service/service.token`; never copy that token into source,
documentation, or shell history.

## Run a campaign-bound Sea Power session

Use the repository-local `sea-power-runtime` skill for live Runtime Bridge work.
Start from no running Sea Power or Crimean Tom companion process. Keep the
campaign service in its own terminal.

### 1. Start the service

```powershell
pnpm campaign:server
```

Wait until the service is listening before continuing.

### 2. Create a player campaign

The browser is the normal player path. For a reproducible development session,
the following PowerShell creates the same full campaign and keeps its generated
identifiers in local variables:

```powershell
$campaignRunDirectory = Join-Path (Get-Location) '.run\campaign-service'
$campaignTokenPath = Join-Path $campaignRunDirectory 'service.token'
$campaignToken = (Get-Content -LiteralPath $campaignTokenPath -Raw).Trim()
$campaignHeaders = @{ Authorization = "Bearer $campaignToken" }
$campaignRequest = @{ name = 'Development campaign'; coalition = 'Blue' } |
    ConvertTo-Json
$campaign = Invoke-RestMethod `
    -Uri 'http://127.0.0.1:5081/player/campaigns' `
    -Method Post `
    -Headers $campaignHeaders `
    -ContentType 'application/json' `
    -Body $campaignRequest
```

Do not print `$campaignToken`. The returned `$campaign` contains the campaign
and player-coalition identifiers needed by the companion.

### 3. Build, deploy, and launch a clean game process

Use a unique companion run directory for each campaign-bound proof:

```powershell
$companionRunDirectory = Join-Path `
    (Get-Location) `
    ('.run\companion-' + [Guid]::NewGuid().ToString('N'))

pnpm build
pnpm launch -- `
    -CampaignId $campaign.campaignId `
    -PlayerCoalitionId $campaign.coalitionId `
    -CampaignGrpcUrl 'http://127.0.0.1:5082' `
    -CampaignHttpUrl 'http://127.0.0.1:5081' `
    -CampaignRunDirectory $campaignRunDirectory `
    -CompanionRunDirectory $companionRunDirectory
```

Sea Power must be launched through Steam. Starting `Sea Power.exe` directly can
cause Steamworks to replace the process and invalidate the startup check.

### 4. Start the dynamic-campaign tactical shell

After Sea Power reaches its main menu, use typed Runtime Bridge automation:

```powershell
& .\scripts\crimean-tom-control.ps1 `
    start-scenario `
    -RunDirectory $companionRunDirectory `
    -ScenarioId 'scenario:missions/user_missions/fulda gap gone hot - giuk dynamic campaign window'

& .\scripts\crimean-tom-control.ps1 `
    dismiss-modal `
    -RunDirectory $companionRunDirectory
```

The authored mission supplies the tactical shell and some temporary local
membership. It is not the campaign source of truth. Replacing that placeholder
membership is an active product gap until the current-status document says
otherwise.

### 5. Enter with a campaign group

Open `http://127.0.0.1:5081/monitor/`, select the new campaign and a friendly
group, then use **Check Sea Power**. Review the support report before choosing
**Enter Sea Power**. The service rejects unsupported or stale group revisions
instead of silently entering a partial force.

Inspect the live result through the same custom run directory:

```powershell
& .\scripts\crimean-tom-control.ps1 health -RunDirectory $companionRunDirectory
& .\scripts\crimean-tom-control.ps1 campaign -RunDirectory $companionRunDirectory
& .\scripts\crimean-tom-control.ps1 world -RunDirectory $companionRunDirectory
& .\scripts\crimean-tom-control.ps1 ui -RunDirectory $companionRunDirectory
& .\scripts\crimean-tom-control.ps1 screenshot -RunDirectory $companionRunDirectory
```

Typed inspection is the default. Use desktop input when testing the actual
player interaction path, especially tactical-map symbol selection, overlap,
focus, and proxy loading.

## Live-test rules

- Use the full dynamic campaign unless a smaller case isolates a specific
  parity or performance question.
- Keep the campaign service running during tactical play. Sea Power holds a
  renewable lease over local entities while unrelated campaign entities keep
  advancing.
- Confirm stable campaign identity, task-force membership, native type,
  position, fuel, stores, damage, and hosted relationships. A visible model is
  not enough.
- Ground objects and installations require finalized terrain, a valid land
  sample, and a final bounds check above the surface.
- Non-local friendlies and known hostile contacts must remain available as map
  records even when no tactical object exists.
- Visual proxies are presentation only. They must not gain physics, AI,
  sensors, weapons, campaign authority, checkpoint rights, or an exact hostile
  position.
- Record unsupported fields and families explicitly. Do not fill them with a
  plausible default and call the round trip complete.
- Run `pnpm verify` and `pnpm logs` after live Runtime Bridge changes.

The complete acceptance and benchmark portfolios are in
[testing and observation](../operations/testing.md).

## Known traps

### Reused game state

Do not treat a second campaign loaded into an existing tactical scene as a clean
proof. Suspended retained objects can still consume materialization capacity.
Restart Sea Power and use a fresh companion run directory until restart,
teardown, and recovery are qualified end to end.

### Default versus custom run directories

The short `pnpm game:*` commands read the default `.run` directory. When a
campaign-bound launch uses `-CompanionRunDirectory`, pass that directory to
`scripts/crimean-tom-control.ps1` for every later command.

### Build success versus player success

A successful build, plugin health marker, API response, or rendered symbol
proves only that layer. A player-facing claim needs the complete path relevant
to the claim.

### Proprietary and generated files

Do not commit Sea Power assemblies, generated decompilation, logs, tokens, save
databases, screenshots, or benchmark captures unless a tracked fixture or
release process explicitly calls for them. Regenerate ignored research output
with the `pnpm decompile*` commands documented in the repository README.

## Choosing the next task

Start with the first incomplete vertical slice in
[current status](../planning/current-status.md), then check its acceptance gate
in the [implementation plan](../planning/implementation-plan.md). Keep the patch
on the local single-player campaign-to-battle path unless the task explicitly
requires a broader architecture change.

Before coding, write down:

1. the player-visible behavior being added or fixed;
2. which process owns each changed state;
3. the failure behavior before and after tactical activation;
4. the broad automated story that protects it; and
5. the live Sea Power observation that will prove it.

Do not remove large bodies of preserved work to make a narrow slice pass. If a
new invariant crosses the campaign service, companion, and game boundary, add
it to system architecture. If only implemented reality changed, update current
status or the implementation map.

## Finish cleanly

Stop the custom companion after a live session:

```powershell
& .\scripts\crimean-tom-control.ps1 stop -RunDirectory $companionRunDirectory
```

Exit Sea Power normally, stop the campaign-service terminal, and check the
worktree:

```powershell
git status --short
git diff --check
```

Do not leave agent-launched applications running unless the user asks to keep
them open.
