# Campaign state

Repetition in the persistence, economy and readiness layers, and one duplicated data
table.

## 16. Coalition membership duplicated into the theater catalog

`Fleet/FleetClashTheaterCatalog.cs` restates two nation lists verbatim from
`TaskForce/TaskForceCoalitions.cs`:

- `["Denmark", "Norway", "Sweden"]` — also `Coalition_Scandinavia`
- `["Greece", "Israel", "Turkey"]` — also `Coalition_EasternMediterranean`

`FleetClashForceGenerator.ExpandSelections` already resolves coalition ids into member
nations, so these theaters could name the coalition instead. As written, adding a nation to
a coalition requires remembering to edit a second file. (Separately,
`["UK", "Canada", "France", "Germany", "Netherlands", "Norway"]` appears twice inside the
theater catalog itself.)

## 18. Further campaign-state repetition

- **`!item.IsAirUnit` at 16 sites** across 6 files — `CampaignEconomy`,
  `CampaignOperationsState`, `CampaignPatches` (5), `CampaignReadiness` (5),
  `CampaignScenarioGenerator`, `CampaignManagementWindow`. "Hull, not aircraft" is a domain
  predicate that deserves a name, especially where it is negated inside a larger condition.
- **`missionIndex is < 1 or > 4`** at `CampaignPatches.cs:66, 90`. Four is the
  `CampaignContractKind` member count; the magic number should come from the enum.
- **`CampaignContractPaymentPatch` and `CampaignRewardApplicationPatch`** both resolve the
  offer, gate on victory, and yield payment/cap reward — about eight lines each of the same
  decision.

## 21. Commander discount nation lookup, written twice

The "which nation earns the commander discount" resolution appears verbatim in two files:
`TryGetValue("TaskForceCommanderNationOverride")`, and if blank,
`TryGetValue("TaskForceCommanderNation")`.

- `Campaign/CampaignEconomy.cs:30-33` (`GetMarketPrice`)
- `TaskForce/TaskForceCommanderPatches.cs:158-163` (`CommanderDiscountPatch`)

More broadly, 15 *native* persistent-data key literals are scattered as raw strings, several
duplicated across files — `TaskForceCommanderNation` and `...NationOverride` (2 files each),
`TaskForceModeCompletionPoints` (2), `TaskForceRule_UnrestrictedRoster` (2),
`TaskForceMode_Record` (2). Our own keys have `const` declarations in
`Campaign/CampaignPersistence.cs`; the native ones we read and write do not, even though
those are the ones a game update can break.

## 22. Seeded distinct-vessel draw, written twice

`CampaignEconomy.CreateMarketOffers:186-196` and
`CampaignDebug.SelectAutoresolveDestroyedUnits:79-89` share the same five-line pipeline:
`RefreshBaseCosts()` → `new Random(StableHash(battleId))` → `GetAllVesselOptions()` →
`GroupBy(UnitType, OrdinalIgnoreCase)` → `Select(group => group.OrderBy(_ => random.Next()).First())`.
Only the final filter and take differ.

---

Back to the [index](README.md).
