# Dead code

Members with no call sites anywhere in the tree. Pure deletion.

## 24. Four unreferenced members

Four members are declared and never referenced anywhere in the tree:

- `TaskForce/TaskForceCoalitions.cs:99` — `GetCoalitions()`, one line.
- `Fleet/FleetClashBalancer.cs:49-56` — `GetShipCount(FleetClashSize)`, eight lines. It is
  also one of the 13 size tables in [item 10](fleet-planning.md), so this is a dead tuning table.
- `TaskForce/TaskForceRosterBalance.cs:192` — `NationTier.StrengthScore`, never read.
- `TaskForce/TaskForceRosterBalance.cs:191` — `NationTier.RoleCount`, never read.

The last two pull a small chain with them. `NationTier`'s constructor parameters and the
matching `strength.Score` / `strength.RoleCount` arguments at `:141-142` exist only to feed
them, and `TaskForceRosterGenerator.RosterStrength.RoleCount:585` exists only to feed
`NationTier.RoleCount`. About 20 lines total.

One thing to keep while unwinding that chain: the `roleCount` *computation* in
`GetRosterStrength:178-182` must stay. It is load-bearing through `coverage` into `Score`;
only the two exposed properties are dead.

Verified by name search across every `.cs` file. `Finalizer`, `Transpiler`, and
`TriggerEntryPoint` also show zero call sites but are invoked by Harmony and AnchorChain by
convention, so they are not dead.

---

Back to the [index](README.md).
