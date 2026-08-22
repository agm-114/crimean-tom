# Sea Power technical notes

This directory records what the installed Sea Power build does, how its major
systems fit together, and which seams are promising for Crimean Tom's eventual
dynamic campaign. It is a research map, not a replacement for the game's
manual and not a claim that decompiled C# is the developer's original source.

The observations were made against Sea Power `0.8.2-23444+s358` on 20 August
2026 and re-checked against the same installed build on 22 August 2026 (Steam
app `1286220`, depot buildid `24297410`, Unity `6000.0.67f1`, PhysX `4.1.2`).
A future game update can invalidate names, method shapes, data keys, or UI
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
2. [Tactical bubble](research/tactical-bubble.md) separates the camera-origin
   relocation (~67 km), the instantiated `ObjectBase` set, native campaign
   mission pull-in (~500 nmi), and Crimean Tom's operational theater. It also
   records scene-local `UniqueID` versus `CampaignTag` and native `LandUnit`.
3. [Data and modding](research/data-and-modding.md) describes the INI-heavy virtual file
   system, load order, object construction, and AnchorChain's separate role.
4. [Tactical simulation](research/tactical-simulation.md) maps contacts, AI, orders,
   weapons, aircraft, land units, and damage.
5. [Campaign systems](research/campaign-systems.md) compares authored linear campaigns,
   task-force mode, and the unfinished dynamic/map campaign framework.
6. [UI and player workflows](research/ui-workflows.md) records the live screens and the
   operational model they expose.
7. [Dynamic campaign architecture](architecture/dynamic-campaign-design.md) gives the concise
   product and architecture overview.
8. [System architecture](architecture/system-architecture.md) is the normative specification
   for ownership, time, identity, materialization, protocols, persistence,
   security, and failure behavior.
9. [Remote transport and local-first backend stack](architecture/transport-stack.md)
   compares gRPC and SignalR, defines their separate boundaries, and fixes the
   SQLite/no-external-infrastructure deployment baseline.
10. [Deterministic tactical runtime adapter](architecture/deterministic-runtime-adapter.md)
    defines how Crimean Tom replaces FPS-driven time, uncontrolled RNG, and
    reverse-insertion scheduling before a client can become a hot replica.
11. [Federated simulation protocol](architecture/federated-simulation.md) specifies redundant
    replicas, authority leases, health checks, reconciliation, and failover.
12. [Load management](architecture/load-management.md) defines stress signals, client
    simulation eligibility, island migration, degradation, and global tempo.
13. [Adversarial review disposition](planning/adversarial-review-disposition-2026-08-20.md)
    records which review findings were accepted, corrected, or left as gates.
14. [Implementation plan](planning/implementation-plan.md) defines the critical path,
    phase deliverables, acceptance gates, deferred-decision deadlines, and
    adversarial review checklist.
15. [Current implementation handoff](planning/current-handoff.md) records the latest
    verified state, working-tree constraints, and the exact next safety slice.
16. [Supported claims](planning/supported-claims.md) lists what the tree actually
    claims, including fail-closed compatibility and explicit non-claims.
17. [Threat model](planning/threat-model.md) covers pipe, companion, operator API,
    storage, projections, and update fingerprints.
18. [Packaged SQLite profile](planning/packaged-sqlite-profile.md) is the ordinary
    local-file deployment with backup/restore and operator pause/drain.
19. [Testing and observation](operations/testing.md) defines repeatable source, launch,
    plugin, UI, and campaign-contract checks.
20. [Source map](research/source-map.md) points future investigators at the highest-value
    decompiled types.
21. [Determinism call-site audit](evidence/determinism-callsite-audit-2026-08-20.md)
    records D0 scan coverage, initial time/RNG/job/coroutine classification, and
    the fail-closed clock-rebase verdict for the pinned build.
22. [Phase 2 live evidence](evidence/phase-2-live-evidence-2026-08-20.md)
    records authenticated mutation admission, materialization, fenced lifecycle,
    checkpoint restore, scene reload, reconnect, and negative-path observations.
23. [Phase 2 surface field tolerances](evidence/phase-2-surface-field-tolerances.md)
    defines the promotion-critical comparison contract for the first surface
    family.
24. [Phase 2 exit-gate matrix](evidence/phase-2-exit-gate-matrix.md) maps every
    bounded-mutation exit condition to live evidence and records the narrow
    gate disposition.
25. [Phase 3A kernel evidence](evidence/phase-3a-kernel-evidence-2026-08-20.md)
    records the game-independent deterministic domain, contract, canonical hash,
    and information-filtering slice.
26. [Phase 3B journal evidence](evidence/phase-3b-journal-evidence-2026-08-20.md)
    records the in-progress WAL journal, writer-epoch, transactional append,
    idempotency, and durable replay slice.

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
  sensors, tracks, weapons, flight decks, physics, and damage. One process has
  one camera-following origin; it still ticks every instantiated `ObjectBase`,
  including units the player cannot see. See [Tactical bubble](research/tactical-bubble.md).
- Campaign code either sequences authored tactical missions with persistence or
  moves abstract groups on a strategic graph and generates a tactical mission
  from groups within ~500 nmi. That generated-mission seam is not live streaming.

For Crimean Tom, the long-term boundary is a durable simulation outside Unity,
plus a small in-game adapter. The service streams selected entities into one or
more Sea Power clients, accepts ordered tactical events, and changes simulation
authority without stopping the continuous campaign. Conventional generated
missions are a compatibility path rather than the primary architecture. See
[Dynamic campaign architecture](architecture/dynamic-campaign-design.md).
