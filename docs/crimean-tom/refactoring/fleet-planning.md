# Fleet planning

## 10. Group fleet-size tuning

`Fleet/FleetClashForceGenerator.cs` spreads size-dependent fleet composition across
`Blueprints`, `GetSubmarineCount`, `SelectSourcePool`,
`GetAlliedDetachmentChance`, `GetRegionalPartnerChance`, and `GetLeadNationChance`.

A small per-size composition profile could put the role blueprint and these
probabilities together. Keep role-specific adjustments explicit. Do not collect
mission difficulty, route length, and every other size switch into one large record
just because they use the same enum.

Preserve short-circuit behavior, submarine thresholds, random draw count/order,
and existing invalid-size handling. Some switches throw; submarine count falls
back to zero. Compare fixed-seed compositions for every size, including pools
without submarines or allied ships. This is a tuning organization task, not a
balance adjustment.

Back to the [index](README.md).
