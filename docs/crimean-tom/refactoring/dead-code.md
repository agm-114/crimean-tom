# Dead code

## 24. Remove unused members

Repository C# searches found no callers for:

- `Fleet/FleetClashBalancer.cs`: `GetShipCount`.
- `TaskForce/TaskForceRosterBalance.cs`: `NationTier.StrengthScore` and
  `NationTier.RoleCount`.

Remove their unused constructor arguments too. `RosterStrength.RoleCount` in
`TaskForceRosterGenerator.cs` only feeds `NationTier.RoleCount` and can go with it.

Keep the local `roleCount` calculation in `GetRosterStrength`. It contributes to
`coverage` and therefore `Score` and commander discounts.
Keep `RosterStrength.Score`, which still has live consumers.

Repeat the reference search, including string-based and binding access, before
deletion. Build afterward and compare roster strength and discount outputs.
Do not treat Harmony or AnchorChain entry points as dead because they lack direct
C# calls.

Back to the [index](README.md).
