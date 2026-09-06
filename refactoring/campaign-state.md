# Campaign state

## 21. Share commander selection lookup

`Campaign/CampaignEconomy.GetMarketPrice` and
`TaskForce/TaskForceCommanderPatches.CommanderDiscountPatch` both read
`TaskForceCommanderNationOverride`, falling back to `TaskForceCommanderNation`
when the override is blank.

Put only that lookup in a shared helper near the nation rules, accepting the
persistent-data dictionary already available to each caller.
Keep `GetCampaignDiscount` as the discount calculation.

The callers have different guards. Market pricing returns full price without
persistent data; the native commander patch also requires a committed commander.
Do not move those conditions into the lookup or silently impose one on the other.

Compare missing keys, whitespace overrides, valid overrides, coalition selections,
and committed/uncommitted commanders. Check both market prices and native discount
results, including saved discount behavior.

Back to the [index](README.md).
