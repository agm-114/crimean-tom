# Scenario generation

Duplication between the route, passage and fleet-clash generators, and the launcher-file
registry that is written four different ways.

## 4. Duplicated route-corridor setup

`Scenarios/ScenarioPassageOperationGenerator.cs:15-58` and
`Scenarios/ScenarioRouteOperationGenerator.cs:129-170` are near-identical:

- the same `routeLength` switch (48 / 62 / 78),
- the same `routeStart` / `routeEnd` / `interceptX` derivation,
- the same two `FleetOrderOfBattlePlanner.Plan` calls with the same offsets
  (`routeStart - 7f`, `interceptX + 20f`, `attackLane * 22f`),
- the same two `RetaskRouteFleet` calls.

One helper returning the corridor geometry plus both deployments removes about 35 lines
and makes the two contracts provably share their layout.

## 12. The launcher-file registry, written four ways

`Scenarios/ScenarioDebugGenerator.cs` names its three launcher files in four separate
constructs:

- three constants (`:10-12`),
- three getters that are each `Path.Combine(GetScenarioDirectory(), X)` (`:168-178`),
- `IsLauncherPath` / `IsSpectatorLauncherPath` / `IsRouteOperationLauncherPath` (`:52-85`),
  34 lines of repeated `Path.GetFullPath` + `OrdinalIgnoreCase` compare,
- `DeleteObsoleteEntries` (`:190-203`), four more `string.Equals` against the same names.

One array of `(fileName, kind)` plus a `SamePath(a, b)` helper replaces all of it: about
50 lines down to 15. This supersedes the shorter launcher-path note in [item 9](small-items.md).

## 20. Arrival/loss trigger pairs written twice

`ScenarioRouteOperationGenerator.AppendRouteTriggers:400-419` and
`ScenarioPassageOperationGenerator.AppendPassageTriggers:104-124` are structurally
identical: start trigger, then `MissionTrigger(2, ...)` with an area condition for arrivals,
then `MissionTrigger(3, ...)` with a loss condition, each carrying a mirrored
`MissionOutcome`. Only names, message keys, objective names, and the display side differ.

About 20 lines each. Beyond the line count, a shared helper would enforce what the
development guide already calls out as load-bearing: that the arrival and loss thresholds
stay complementary and that `Trigger2` / `Trigger3` / `Condition1` keep their names, since
campaign remapping reads them.

Related: all three builders (`BuildFleetClash`, `BuildRouteOperation`,
`BuildPassageOperation`) call the same assembly spine in the same order — settings,
environment, mission, deployments, civilian traffic, triggers, objectives, retreat anchors.
A template method taking the varying parts would hold that ordering in one place.

---

Back to the [index](README.md).
