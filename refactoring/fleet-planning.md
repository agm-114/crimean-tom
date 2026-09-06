# Fleet planning

## 13. Reuse the formation-spacing switch

`Fleet/FleetOrderOfBattlePlanner.cs` repeats the same switch in `GetMemberOffset`
and `GetFormationSpacing`:

- Carrier: `2.75f`.
- Submarine patrol: `5f`.
- Other groups: `1.8f`.

Have `GetMemberOffset` call `GetFormationSpacing`. Both spawn offsets and serialized
formations use these values, and formations specify `OverrideSpawnPositions`.
Compare offsets and formation strings for each group kind and member index.
Do not change route station distances as part of this item.

## 10. Group fleet-size tuning

`Fleet/FleetClashForceGenerator.cs` spreads size-dependent fleet composition across
`Blueprints`, `GetSubmarineCount`, `SelectSourcePool`,
`GetAlliedDetachmentChance`, `GetRegionalPartnerChance`, and `GetLeadNationChance`.

A small per-size composition profile could put the role blueprint and these
probabilities together. Keep role-specific adjustments explicit. Do not collect
mission difficulty, route length, and every other size switch into one large record
just because they use the same enum.

`FleetClashBalancer.GetShipCount` is unused, so remove it under
[item 24](dead-code.md#24-remove-unused-members), not move it into a profile.
The live ship counts come from `Blueprints`.

Preserve short-circuit behavior, submarine thresholds, random draw count/order,
and existing invalid-size handling. Some switches throw; submarine count falls
back to zero. Compare fixed-seed compositions for every size, including pools
without submarines or allied ships. This is a tuning organization task, not a
balance adjustment.

## 29. Share coalition definitions across commander and fleet generation

`TaskForce/TaskForceCoalitions.cs` owns coalition membership and presentation data
inside `TaskForceNationRules`. `Fleet/FleetClashTheaterCatalog.cs` defines battle
locations, opponents, and regional/allied selections. Some national lists overlap,
but the catalogs serve different gameplay rules.

Extract a small, UI-independent `CoalitionCatalog` containing definitions, lookup,
membership, display names, and flag/resource identifiers. Commander rules and
`FleetClashForceGenerator.ExpandSelections` should consume it. Keep native UI
object creation and texture resolution in the TaskForce integration, not the catalog.

Keep theaters separate, including their coordinates, opponent labels, and regional
versus allied selections. `ExpandSelections` already accepts coalition IDs, so no
new selection framework is needed. Where the whole coalition is intentionally local,
replace repeated membership with a reference, for example:

```csharp
new(
    "Scandinavian coalition",
    ["Coalition_Scandinavia"],
    ["US", "UK", "Coalition_NATO"])
```

Scandinavia and Eastern Mediterranean currently have exact membership matches.
Keep deliberate subsets explicit. The Arabian Sea Commonwealth force treats India
and Pakistan as regional; Britain and Australia remain allied reinforcements.
Expanding the whole Commonwealth there would change fleet composition.

A shared regional reference also means future coalition membership changes affect
that theater's local pool. Use that coupling only deliberately. Do not add automatic
geography inference, merge the two model types, or introduce a configuration framework.

Preserve coalition IDs, case-insensitive membership, display/flag values, and member
iteration order. Compare expanded regional/allied pools and fixed-seed fleets for
every theater and size. Check commander options, discounts, and roster filters; use
the Sea Power testing skill for native UI verification. Keep membership and weighting
changes out of this refactor.

Back to the [index](README.md).
