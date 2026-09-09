# Models

## 2. Group fleet capability data

The cost, surface, air-defense, ASW, and resilience fields travel together through
`VesselCombatProfile`, `FleetUnitArchetype`, `FleetCandidate`, `FleetClashShip`, and
`FleetClashForce`. The copying is visible in `FleetClashForceGenerator.Finalize`,
`AddArchetypes`, `AddSubmarineArchetypes`, and `BuildCampaignPlayerForce`.
`CreateForce` and the `FleetCandidate` constructor also repeat the five sums.

A small capability value could shorten those constructors. Proceed only if callers
can use it directly, without retaining five forwarding properties on every type.
Keep approximate AI point cost distinct from native purchase price and retain the
campaign fallback values.

LINQ `Sum` does not support arbitrary records merely because they define addition.
Any shared aggregation must preserve integer overflow behavior and floating-point
summation order. Leave the explicit weighted terms in `ScoreScaledForce` readable;
a generic weighted zip is unnecessary. Compare totals and selected fleets for
fixed seeds before accepting the change.

Back to the [index](README.md).
