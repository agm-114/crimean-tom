# Sea Power technical notes

This directory records what the installed Sea Power build does, how its major
systems fit together, and which seams are promising for Crimean Tom's eventual
dynamic campaign. It is a research map, not a replacement for the game's
manual and not a claim that decompiled C# is the developer's original source.

The observations were made against Sea Power `0.8.2-23444+s358` on 20 August
2026. A future game update can invalidate names, method shapes, data keys, or UI
flows. Re-run `pnpm decompile` and the smoke tests after an update.

## Reading order

The folders separate evidence from design commitments:

- `research/` — reverse-engineered and live-observed Sea Power behavior;
- `architecture/` — normative Crimean Tom system contracts;
- `planning/` — phased delivery and adversarial review records;
- `operations/` — repeatable test and verification procedures.

1. [Runtime architecture](research/runtime-architecture.md) explains initialization,
   update phases, object registries and lifecycle, the geodetic coordinate
   system and its floating origin, time compression, global state and
   randomness, and persistence.
2. [Data and modding](research/data-and-modding.md) describes the INI-heavy virtual file
   system, load order, object construction, and AnchorChain's separate role.
3. [Tactical simulation](research/tactical-simulation.md) maps contacts, AI, orders,
   weapons, aircraft, and damage.
4. [Campaign systems](research/campaign-systems.md) compares authored linear campaigns,
   task-force mode, and the unfinished dynamic/map campaign framework.
5. [UI and player workflows](research/ui-workflows.md) records the live screens and the
   operational model they expose.
6. [Dynamic campaign architecture](architecture/dynamic-campaign-design.md) gives the concise
   product and architecture overview.
7. [System architecture](architecture/system-architecture.md) is the normative specification
   for ownership, time, identity, materialization, protocols, persistence,
   security, and failure behavior.
8. [Remote transport and local-first backend stack](architecture/transport-stack.md)
   compares gRPC and SignalR, defines their separate boundaries, and fixes the
   SQLite/no-external-infrastructure deployment baseline.
9. [Deterministic tactical runtime adapter](architecture/deterministic-runtime-adapter.md)
   defines how Crimean Tom replaces FPS-driven time, uncontrolled RNG, and
   reverse-insertion scheduling before a client can become a hot replica.
10. [Federated simulation protocol](architecture/federated-simulation.md) specifies redundant
   replicas, authority leases, health checks, reconciliation, and failover.
11. [Load management](architecture/load-management.md) defines stress signals, client
    simulation eligibility, island migration, degradation, and global tempo.
12. [Adversarial review disposition](planning/adversarial-review-disposition-2026-08-20.md)
    records which review findings were accepted, corrected, or left as gates.
13. [Implementation plan](planning/implementation-plan.md) defines the critical path,
    phase deliverables, acceptance gates, deferred-decision deadlines, and
    adversarial review checklist.
14. [Current implementation handoff](planning/current-handoff.md) records the latest
    verified state, working-tree constraints, and the exact next safety slice.
15. [Testing and observation](operations/testing.md) defines repeatable source, launch,
    plugin, UI, and campaign-contract checks.
16. [Source map](research/source-map.md) points future investigators at the highest-value
    decompiled types.
17. [Determinism call-site audit](evidence/determinism-callsite-audit-2026-08-20.md)
    records D0 scan coverage, initial time/RNG/job/coroutine classification, and
    the fail-closed clock-rebase verdict for the pinned build.

## Evidence labels

- **Observed** means the behavior was exercised in the running game.
- **Code evidence** means it is visible in the decompiled installed assemblies.
- **Inference** means it is a design conclusion drawn from those facts.

These distinctions matter. Decompilers can reconstruct invalid-looking syntax,
omit compiler-generated context, and make empty methods look more meaningful
than they are. UI state can also be affected by local settings and installed
Workshop content.

## Reproducing the research tree

The proprietary assemblies and generated source are intentionally not tracked.
Generate the git-ignored local copy with:

```powershell
pnpm decompile
pnpm decompile:anchorchain
```

The main output is `decompiled/game`; AnchorChain output is
`decompiled/anchorchain`. `pnpm decompile:all` is available when a dependency
outside the four primary game assemblies must be inspected.

The first inventory contained 3,077 decompiled game C# files and 10
AnchorChain/preloader C# files. The four primary assemblies are
`Seapower-Scripts.dll`, `Assets.dll`, `Libraries.dll`, and `Plugins.dll`.

## High-level model

Sea Power is two related simulations behind a Noesis/Unity UI:

- The tactical game is a detailed, Unity-coupled real-time simulation of units,
  sensors, tracks, weapons, flight decks, physics, and damage.
- Campaign code either sequences authored tactical missions with persistence or
  moves abstract groups on a strategic graph and materializes tactical missions.

For Crimean Tom, the long-term boundary is a durable simulation outside Unity,
plus a small in-game adapter. The service streams selected entities into one or
more Sea Power clients, accepts ordered tactical events, and changes simulation
authority without stopping the continuous campaign. Conventional generated
missions are a compatibility path rather than the primary architecture. See
[Dynamic campaign architecture](architecture/dynamic-campaign-design.md).
