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

Back to the [index](README.md).
