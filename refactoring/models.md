# Model shapes

Restated constructor parameters, a capability bundle that five types carry, and the
argument-by-argument rebuilds that a record would make free.

## 1. Restated properties that `record` would delete outright

Commit `41b833e7` compacted these models but left the primary-constructor-plus-property
form. The codebase already uses `record` for `DeployedFleetUnit`, `FleetFormationPlan`,
`IndexedShip`, and `CarrierAirPlan`, so this finishes a conversion already underway.

| File | `{ get; } = x` lines |
|---|---|
| `Fleet/FleetClashModels.cs:57-196` | 52 |
| `TaskForce/TaskForceRosterGenerator.cs:539-585` | 16 |
| `Scenarios/ScenarioDebugGenerator.cs:364` (`ScenarioParameters`) | 12 |
| `TaskForce/TaskForceRosterBalance.cs:177` (`NationTier`) | 7 |
| `Fleet/VesselCombatProfileReader.cs:121` (`VesselCombatProfile`) | 6 |
| `TaskForce/TaskForceCoalitions.cs:175` (`CoalitionDefinition`) | explicit ctor, ~15 lines |

Roughly 130 lines including braces. Computed members survive unchanged in a record body:
`FleetClashForce.PrimaryNation`, `FleetClashShip.CombatValue`, and `FleetCandidate`'s five
`ships.Sum(...)` initializers.

Two cautions:

- `RosterVariant.SetBaseCost` mutates, so `BaseCost` needs `{ get; set; }` in the record
  body rather than a positional parameter.
- `List.Remove` on `IndexedShip` / `FleetUnitArchetype` (`Fleet/FleetOrderOfBattlePlanner.cs:294-372`)
  moves from reference to value equality. Safe as written because `SourceIndex` and
  unit type / nation disambiguate, but it is a real semantic change.

## 2. A `FleetCapability` bundle carried through five types

`PointCost, SurfaceCapability, AirDefenseCapability, AswCapability, Resilience` is restated
on `VesselCombatProfile`, `FleetUnitArchetype`, `FleetCandidate`, `FleetClashShip`, and
`FleetClashForce`, then copied field-by-field at six call sites:

- `Fleet/FleetClashForceGenerator.cs:51-66, 127-133, 161-166`
- `Scenarios/ScenarioDebugGenerator.cs:341-346` (`CreateForce`)
- `Campaign/CampaignScenarioGenerator.cs:178-190`

One record with an addition operator turns the copies into `profile.Capability` and
`ships.Sum(...)` one-liners. About 45 lines. Same move as `CivilianTrafficUnit`: give the
shared data a name and let it own its own construction.

## 11. `FleetClashMatch` rebuilt argument-by-argument, 3 sites

Each site re-lists all eight constructor arguments to change one or two:

- `Scenarios/ScenarioDebugGenerator.cs:257-266` (`ApplyEnemyMultiplier`)
- `Scenarios/ScenarioDebugGenerator.cs:320-329` (`ReorderPlayerForce`)
- `Campaign/CampaignScenarioGenerator.cs:216-232` (`BuildCampaignMatch`)

This is a direct payoff of item 1: once `FleetClashMatch` is a record, each collapses to
`match with { Second = scaledEnemy }`. About 27 lines beyond the property-block savings.

Similarly `ScoreScaledForce` (`ScenarioDebugGenerator.cs:279-291`) is five near-identical
`RelativeDifference(candidate.X, original.X * multiplier) * weight` terms, which the
`FleetCapability` record from item 2 turns into a weighted zip.

---

Back to the [index](README.md).
