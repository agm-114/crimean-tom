# Fleet planning

Scattered tuning tables in `Fleet/`, plus one duplicated switch
that is a live desynchronization risk.

## 10. Thirteen `FleetClashSize` switch tables

The size tuning surface is scattered across four files as 13 separate
`size switch { Small => ..., Medium => ..., Large => ... }` expressions, eight of which
repeat a `_ => throw new ArgumentOutOfRangeException(nameof(size))` arm:

- `Fleet/FleetClashBalancer.cs:51, 59, 67` — ship count, minimum and maximum separation.
- `Fleet/FleetClashForceGenerator.cs:235, 258, 364, 383, 392, 401` — submarine odds, allied
  command chance, allied detachment, regional partner (twice), lead nation. Plus the
  `Blueprints` dictionary at `:5-17`, which is the same table in another form.
- `Scenarios/ScenarioMissionWriter.cs:163` — mission difficulty.
- `Scenarios/ScenarioRouteOperationGenerator.cs:137, 366` — route length, protected group size.
- `Scenarios/ScenarioPassageOperationGenerator.cs:17` — route length again (see [item 4](scenario-generation.md)).

One `FleetClashSizeProfile` record looked up by size gives a single tuning table instead of
13 sites, and removes all eight throw arms because a record lookup is total. This is the
game-balance surface; having it in one place is worth more than the line count.

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
