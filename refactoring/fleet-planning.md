# Fleet planning

Scattered tuning tables in `Fleet/`, plus one duplicated switch
that is a live desynchronization risk.

## 10. Scattered `FleetClashSize` tuning tables

Size tuning is scattered across fleet and scenario files as repeated
`size switch { Small => ..., Medium => ..., Large => ... }` expressions. Several also
repeat a `_ => throw new ArgumentOutOfRangeException(nameof(size))` arm:

- `Fleet/FleetClashBalancer.cs:51, 59, 67` — ship count, minimum and maximum separation.
- `Fleet/FleetClashForceGenerator.cs:235, 258, 364, 383, 392, 401` — submarine odds, allied
  command chance, allied detachment, regional partner (twice), lead nation. Plus the
  `Blueprints` dictionary at `:5-17`, which is the same table in another form.
- `Scenarios/ScenarioMissionWriter.cs:163` — mission difficulty.
- `Scenarios/ScenarioRouteOperationGenerator.cs` — protected group size.
- `Scenarios/ScenarioRouteCorridor.cs` — shared route and passage corridor length.

One `FleetClashSizeProfile` record looked up by size could collect these tuning values.
Preserve each caller's current unknown-size behavior; a lookup is not automatically total.
Having the game-balance values in one place is worth more than the line count.

## 13. Duplicate spacing table inside one file

`Fleet/FleetOrderOfBattlePlanner.cs` has the same three-arm switch written twice, 130 lines
apart:

- `GetMemberOffset:466-472` — the local `spacing` variable,
- `GetFormationSpacing:598-603`.

Both are `Carrier => 2.75f, SubmarinePatrol => 5f, _ => 1.8f`. `GetMemberOffset` should
call `GetFormationSpacing(kind)`. This is worth doing for correctness rather than the six
lines: member spawn offsets and formation spacing are meant to agree, since the formation
is written with `OverrideSpawnPositions`. Tuning one switch silently desynchronizes them.

Related but minor: the five `Build*Route` methods use
`Math.Clamp(engagementSeparationNm * k, lo, hi)` nine times. A small `Station(fraction, min, max)`
helper would make the station-keeping table readable.

---

Back to the [index](README.md).
