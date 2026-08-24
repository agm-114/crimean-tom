# Crimean Tom documentation

These documents separate intended product behavior, normative architecture,
implemented status, reverse-engineered Sea Power evidence, and dated test
results. Do not infer that a target design is implemented or that an old
evidence result describes the current tree.

The current pinned game is Sea Power `0.8.2-23444+s358` (Steam app `1286220`,
depot build `24297410`, Unity `6000.0.67f1`). Game updates can invalidate type
names, data mappings, UI workflows, and performance evidence.

## Sources of truth

Read these first, in order:

1. [Playable dynamic campaign product contract](planning/playable-dynamic-campaign-product-contract.md)
   defines what the player must experience and the minimum playable gate.
2. [Current code and product status](planning/current-status.md) records what is
   implemented, what is missing, and the recommended next work.
3. [System architecture](architecture/system-architecture.md) defines normative
   ownership, identity, time, fidelity, persistence, protocol, and failure
   behavior.
4. [Implemented architecture map](architecture/implementation-map.md) maps the
   running processes and projects to the target architecture.
5. [Implementation plan](planning/implementation-plan.md) defines dependencies,
   phase gates, deferred decisions, test portfolios, and the maintained
   adversarial checklist.
6. [Supported claims](planning/supported-claims.md) is the narrow ledger of
   behavior the repository is currently prepared to claim.

When documents disagree, use this precedence:

```text
player behavior: product contract
system invariants: system architecture
implemented reality: current status + implementation map
proven behavior: supported claims + dated evidence
delivery order: implementation plan
```

## Architecture

- [Dynamic campaign design](architecture/dynamic-campaign-design.md) — concise
  system overview and the 4,096-entity proving campaign.
- [System architecture](architecture/system-architecture.md) — complete
  normative specification.
- [Implemented architecture map](architecture/implementation-map.md) — current
  code/process boundaries and deliberate gaps.
- [Transport stack](architecture/transport-stack.md) — local-first HTTP/gRPC
  boundaries and the future player realtime channel.
- [Deterministic runtime adapter](architecture/deterministic-runtime-adapter.md)
  — requirements before a Unity process can be a promotable hot replica.
- [Federated simulation](architecture/federated-simulation.md) — assignment,
  lease, replica, reconciliation, and failover semantics.
- [Load management](architecture/load-management.md) — capacity, interaction
  islands, migration, degradation, and global tempo.
- [Standalone simulation worker](architecture/standalone-sim-worker.md) — the
  current Operational worker boundary and its explicit non-parity with Sea Power.

## Planning and operations

- [Current status](planning/current-status.md) — living implementation and gap
  ledger.
- [Implementation plan](planning/implementation-plan.md) — gated delivery plan
  and adversarial review checklist.
- [Threat model](planning/threat-model.md) — local pipe, service, player,
  storage, projection, and update boundaries.
- [Packaged SQLite profile](planning/packaged-sqlite-profile.md) — supported
  local deployment baseline.
- [Testing and observation](operations/testing.md) — repeatable build, launch,
  campaign, performance, and live-game verification procedures.

## Sea Power research

Files in `research/` describe the installed game or scenario provenance. They
are evidence and inference, not Crimean Tom product commitments.

- [Runtime architecture](research/runtime-architecture.md)
- [Tactical bubble](research/tactical-bubble.md)
- [Data and modding](research/data-and-modding.md)
- [Tactical simulation](research/tactical-simulation.md)
- [Campaign systems](research/campaign-systems.md)
- [UI workflows](research/ui-workflows.md)
- [WW3++ 1989 scenario basis](research/ww3-plus-1989-scenario-basis.md)
- [Source map](research/source-map.md)

## Evidence records

Files in `evidence/` are dated proof records. Preserve their measured result and
historical qualification; update the living status and supported-claims files
instead of rewriting old evidence to match new code.

The most relevant current records are:

- [Full 4K catalog and scenario expansion](evidence/full-catalog-ww3-plus-expansion-2026-08-23.md)
- [Native versus dynamic 500-unit performance](evidence/native-vs-dynamic-500-performance-2026-08-23.md)
- [Strategic AI, parity, and land streaming](evidence/strategic-ai-parity-and-land-streaming-2026-08-24.md)
- [Player-selected streaming and bounded sync](evidence/player-selected-streaming-and-bounded-sync-2026-08-24.md)
- [Full-campaign runtime memory and fresh-session sync](evidence/full-campaign-runtime-memory-and-fresh-session-sync-2026-08-24.md)
- [Strategic AI fixed-defense qualification](evidence/strategic-ai-fixed-defense-v16-2026-08-24.md)
- [Strategic AI capability allocation and replanning](evidence/strategic-ai-capability-replanning-v17-2026-08-24.md)
- [Strategic AI operational packages](evidence/strategic-ai-operational-packages-v18-2026-08-24.md)
- [Automatic tactical restart recovery](evidence/automatic-tactical-restart-recovery-2026-08-24.md)
- [Map-first campaign proxy POC](evidence/map-first-campaign-proxy-poc-2026-08-24.md)

Earlier phase records remain in `evidence/` for audit history. A phase filename
does not mean its full product exit gate is closed; consult current status.

## Evidence language

- **Observed**: exercised in the running game.
- **Code evidence**: visible in the installed/decompiled assemblies or repository.
- **Inference**: a conclusion drawn from those facts.
- **Target**: intended behavior not yet proven.

Compilation, a process launch, a plugin health marker, or a rendered symbol is
not sufficient evidence for a complete player flow.

## Documentation maintenance

- Put player-visible decisions in the product contract.
- Put cross-component invariants in system architecture.
- Put implementation reality and prioritized gaps in current status.
- Put proof gates and ordering in the implementation plan.
- Put narrow proven statements in supported claims.
- Put commands and reproducible procedures in operations.
- Put measurements and screenshots in a dated evidence record.
- Do not add handoff diaries, temporary review writeups, or duplicate status
  documents. Fold durable decisions into the appropriate source of truth and
  delete the temporary artifact.

## Reproducing game research

Proprietary assemblies and generated decompilation output are not tracked.
Regenerate the git-ignored local research tree with:

```powershell
pnpm decompile
pnpm decompile:anchorchain
pnpm decompile:all
```

Re-run the live smoke and compatibility checks after a game update before using
old mappings or evidence as current.
