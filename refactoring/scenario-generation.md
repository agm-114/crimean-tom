# Scenario generation

Repeated trigger setup and the launcher-file registry.

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
