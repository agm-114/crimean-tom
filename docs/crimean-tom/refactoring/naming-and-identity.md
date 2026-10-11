# Naming and identity

## 19. Clarify contract filename mapping

`CampaignContractRules` converts native mission indexes to contract kinds.
`CampaignScenarioGenerator.ResolveCampaignContract` still uses filename substrings.

Source templates contain `[CrimeanTomCampaign] Contract=`, which repository C# does
not read. Generated missions instead write `[CrimeanTom] CampaignContract=`.
Do not switch to reading the source key without accounting for the rewritten files.
An exact filename mapping may be simpler. Preserve or explicitly decide the current
unknown-filename fallback to fleet clash; remove misleading unused metadata only
after checking its consumers.

Check all four filenames, indexes, enemy sides, payments, descriptions, and offer
seeds. Also check missing/invalid indexes and repeated launches from rewritten files.

## 6. Name taskforce-side operations

Player/enemy force selection and opponent naming repeat in
`Scenarios/ScenarioDebugGenerator.cs`, `Scenarios/ScenarioMissionWriter.cs`,
`Scenarios/MissionTrigger.cs`, and `Campaign/CampaignScenarioGenerator.cs`.

Focus on the remaining opponent and force selection, using a small side helper.
A side model should not absorb ship references, UI labels, physical positions,
and campaign policy into one object.

Preserve native `Taskforce1`/`Taskforce2` output. Player side, world-side identity,
and Blue/Red display perspective are not interchangeable. Compare both command
sides, escort/interdiction, and the Blue-aligned spectator with zero player control.

Back to the [index](README.md).
