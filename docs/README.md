# Crimean Tom documentation

Crimean Tom is a local-first dynamic campaign for Sea Power. The campaign
application owns the war, saves, orders, intelligence, and strategic map. Sea
Power owns the local tactical battle and tactical map.

The first playable release is local single-player. Private multiplayer remains
on the active roadmap after that loop works. Distributed simulation, hot Unity
replicas, and live tactical migration are future work.

The current pinned game is Sea Power `0.8.2-23444+s358` (Steam app `1286220`,
depot build `24297410`, Unity `6000.0.67f1`). A game update can invalidate type
names, data mappings, runtime fingerprints, UI workflows, and performance
results.

## Read these documents first

1. [Getting started](development/getting-started.md) covers local setup, the
   repository map, supported workflows, and a clean campaign-bound Sea Power
   session.
2. [Product contract](planning/playable-dynamic-campaign-product-contract.md)
   defines the player experience and first-playable boundary.
3. [Current status](planning/current-status.md) records what works, what does
   not, and the next product gaps.
4. [System architecture](architecture/system-architecture.md) defines the
   active local architecture and its invariants.
5. [Full-fidelity server simulation](architecture/full-fidelity-server-simulation.md)
   defines simulation parity, allowed sleeping, and the canonical gap backlog.
6. [Implementation map](architecture/implementation-map.md) maps those roles to
   the current repository.
7. [Implementation plan](planning/implementation-plan.md) orders the work from
   local proof of concept through private multiplayer.
8. [Supported claims](planning/supported-claims.md) limits what the project may
   present as proven.

When documents disagree, use this order:

```text
player behavior: product contract
system invariants: system architecture
implemented reality: current status and implementation map
proven behavior: supported claims
delivery order: implementation plan
```

## Architecture

- [System architecture](architecture/system-architecture.md): active product
  topology, ownership, time, fidelity, persistence, recovery, and multiplayer.
- [Full-fidelity server simulation](architecture/full-fidelity-server-simulation.md):
  parity requirements, safe subsystem sleeping, validation, and remaining
  mechanics.
- [Implementation map](architecture/implementation-map.md): current processes,
  projects, and deliberate gaps.
- [Transport stack](architecture/transport-stack.md): local bridge, campaign
  API, and active multiplayer transport decisions.
- [Distributed simulation](future/distributed-simulation.md): deferred workers,
  hot replicas, and live tactical migration.

## Planning and operations

- [Getting started](development/getting-started.md)
- [Product contract](planning/playable-dynamic-campaign-product-contract.md)
- [Current status](planning/current-status.md)
- [Implementation plan](planning/implementation-plan.md)
- [Supported claims](planning/supported-claims.md)
- [Threat model](planning/threat-model.md)
- [Packaged SQLite profile](planning/packaged-sqlite-profile.md)
- [Testing and observation](operations/testing.md)

## Research

Files in `research/` describe the installed game, related systems, or scenario
sources. They inform design but do not define product commitments.

- [Runtime architecture](research/runtime-architecture.md)
- [Tactical bubble](research/tactical-bubble.md)
- [Data and modding](research/data-and-modding.md)
- [Tactical simulation](research/tactical-simulation.md)
- [Campaign systems](research/campaign-systems.md)
- [UI workflows](research/ui-workflows.md)
- [WW3++ 1989 scenario basis](research/ww3-plus-1989-scenario-basis.md)
- [Source map](research/source-map.md)

## Documentation rules

- Put stable setup, repository orientation, and end-to-end development workflows
  in getting started. Link to changing status instead of copying it there.
- Put player-visible requirements in the product contract.
- Put cross-component invariants in system architecture.
- Put current facts and gaps in current status or the implementation map.
- Put delivery order and acceptance gates in the implementation plan.
- Put repeatable commands in testing and observation.
- Put historical measurements in commit history, benchmark output, or release
  notes. Do not create handoff diaries or dated evidence folders.
- Mark facts as **observed**, **code evidence**, **inference**, or **target** when
  their status could be confused.
- A successful build, process launch, health marker, or rendered symbol does not
  prove a complete player flow.

## Reproduce game research

Proprietary assemblies and generated decompilation output are not tracked.
Regenerate the ignored research tree with:

```powershell
pnpm decompile
pnpm decompile:anchorchain
pnpm decompile:all
```

Run the live smoke and compatibility checks again after a game update.
