# Naming and identity

## 19. Make contract identity explicit

`CampaignContractKind` already names the four contracts, but
`CampaignContractOffers.cs` and `CampaignContractRules.cs` branch on mission indexes.
`CampaignPatches.cs` guards the native one-based range, and
`CampaignScenarioGenerator.ResolveCampaignContract` uses filename substrings.

Introduce one explicit conversion between native indexes 1 through 4 and contract kinds.
Keep persisted indexes and the seed arithmetic unchanged. Use the enum within offer
descriptions, enemy selection, and payment rules; keep validation at native boundaries.
An enum can contain invalid values and does not make a switch automatically total.

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

## 23. Share runtime campaign paths

Three TaskForce files encode the same campaign folder:

- `TaskForceRosterGenerator.CampaignFolderName`.
- `TaskForceNationRules.IsOurCampaignPath`.
- `TaskForceCoalitions.CoalitionArtPath`.

Share the folder name and derive the runtime paths locally. Preserve slash
normalization, case-insensitive campaign detection, and folder boundaries.
Keep game resource paths separate from filesystem paths.

The csproj, repository art manifest, and native INI files also contain this name,
but their roots differ. Do not generate these formats from C# merely to remove
literal repetition. Check that they still resolve to the same installed campaign
and art assets. The build-only path cleanup is in
[item 27](content-and-build.md#27-reduce-build-metadata-duplication).

Back to the [index](README.md).
