# Phase 3A deterministic campaign-kernel evidence

Date: 20 August 2026

## Disposition

Phase 3A is complete. Crimean Tom now has a working campaign kernel outside Sea
Power. This is an in-memory ordered reducer, not yet a durable campaign service;
SQLite persistence, writer fencing, journal idempotency, snapshots, and crash
recovery remain Phase 3B.

## Implemented boundary

The solution now contains four game-independent projects:

- `CrimeanTom.Campaign.Contracts` defines stable typed IDs, logical time,
  revisions, commit positions, writer/authority epochs, fixed-point geography,
  resources, headings, speed and distance, plus versioned command/event payloads.
- `CrimeanTom.Campaign.Domain` owns immutable sorted canonical state for regions,
  bases, entities, groups, orders, resources, weather, intelligence, assignments,
  lifecycle, and fidelity.
- `CrimeanTom.Campaign.Engine` validates commands, creates deterministic event
  IDs, applies contiguous committed events, enforces optimistic subject
  revisions and coalition scope, emits canonical JSON, and computes SHA-256
  state hashes.
- `CrimeanTom.Campaign.IntegrationTests` contains one broad theater story rather
  than narrow unit tests.

None of the three production campaign projects references `SeaPower.RuntimeBridge`,
Unity, AnchorChain, or a proprietary game assembly. The domain uses immutable,
sorted collections; canonical serialization writes every durable field in an
explicit order. Caches and coalition projections are not part of canonical
state.

## Contract and information boundary

Commands express intent and carry a command ID, campaign/player/subject IDs,
expected subject revision, requested/effective logical time, information scope,
schema version, and a typed payload. Events carry a deterministic event ID,
global commit position, new subject revision, causal command, source/effective
time, optional assignment and authority epoch, schema/algorithm versions, and a
typed payload.

The JSON contract uses explicit versioned discriminators such as
`register-entity/v1` and `entity-registered/v1`, rejects unmapped fields, and
round-trips every command/event family used by the broad story.

Canonical entity state deliberately includes a classified truth sentinel. The
coalition projection type has no member capable of carrying that field. A
hostile contact exposes only its stable contact ID, estimated region and
coordinate, confidence, observation time, and estimate revision.

## Broad integration story

The single Phase 3A story constructs the same synthetic theater twice with
opposite dictionary and group-member insertion orders. Across fifteen committed
commands it creates three connected regions, two coalition bases, three units,
a two-unit group and transit order, regional weather, a hostile intelligence
estimate, a tactical fidelity transition, a primary authority assignment, and a
logical clock advance.

The story proves:

- both input orderings produce byte-identical canonical JSON, SHA-256 hashes,
  and deterministic event IDs;
- command and event JSON round-trips before every reducer application;
- commit position and campaign revision remain contiguous;
- tactical authority requires tactical fidelity and a positive epoch;
- a stale subject revision is rejected without changing the canonical hash;
- the blue projection contains two friendly units and one estimated hostile
  contact, but contains neither coalition's classified sentinel nor the hostile
  true coordinate.

Verification command:

```powershell
dotnet test tests/CrimeanTom.Campaign.IntegrationTests/CrimeanTom.Campaign.IntegrationTests.csproj --configuration Debug
```

Observed result: one broad test passed with zero build warnings and zero errors.

## Remaining Phase 3 gate work

Phase 3 is not complete. Phase 3B must make these events durable with a fenced
SQLite writer and crash-safe snapshots. Phase 3C must advance a multi-region
theater unattended with deterministic scheduling, logistics, movement, AI, and
keyed randomness. Phase 3D must replay snapshots plus journal records, compare
worker schedules, and close the full Phase 3 invariants and fog-of-war exit gate.
