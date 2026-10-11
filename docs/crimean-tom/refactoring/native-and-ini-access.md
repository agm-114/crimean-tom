# Native and INI access

## 15. Share repeated native names

`Campaign/CampaignPatches.cs`, `Campaign/NativeCampaignAccess.cs`,
`TaskForce/TaskForceCommanderPatches.cs`, and `TaskForce/TaskForceRosterGenerator.cs`
repeat private native type names for the save manager, debrief, commander manager,
and mode rules.

When editing these integrations, share repeated type-name constants with the
access wrapper. Keep Harmony target-method resolution in each patch. Do not change
resolution timing or add a general reflection registry.

The debrief section string in `CampaignRewardApplicationPatch` also duplicates
`DebriefSection` in `Campaign/CampaignPersistence.cs`, a partial declaration of
`CampaignOperationsState`. Reuse or move that naming helper without changing how
either reward hook obtains its result. One hook reads the supplied state INI before
the debrief is available; the other uses `BattleWon`. They are not duplicate
state-access paths. Verify target names and emitted section names remain identical.

Back to the [index](README.md).
