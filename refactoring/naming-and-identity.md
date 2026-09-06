# Naming and identity

Conventions the code depends on but no type owns: taskforce sides, hull references,
contract indexes, and the campaign folder name.

## 6. `playerTaskforce == "Taskforce1"` — 21 sites

69 `"Taskforce1"` / `"Taskforce2"` literals across 8 files, with 21 ternaries branching on
the comparison. Worst instances:

- `Scenarios/ScenarioMissionWriter.cs:19-40` — three consecutive ternaries inside each of
  two otherwise-identical `Plan` calls.
- `Campaign/CampaignScenarioGenerator.cs:216-232` (`BuildCampaignMatch`) — four more.

A small `TaskforceSide` record (`Name`, `Other`, `IsFirst`, `LabelFor(role)`) turns those
six into two arguments and removes the stringly-typed comparisons in
`CampaignValueObjectives`, `MissionTrigger`, and `ScenarioMissionWriter.AppendOutcomeTrigger`.

## 8. Unit reference numbering, written three times

Implemented on 2026-09-06 with item 14. `MissionUnitReferences.NumberHulls`
now serves deployment, value objectives, and persistent ID mapping. Validation found a
real disagreement: player-force construction recognizes a submarine by category **or**
folder, but the old ID map checked category only. The ID map now uses the already-classified
player force in the same source order. Protected convoy offsets use `FormatHull` too.

`{taskforce}Vessel{n}` / `{taskforce}Submarine{n}` with independent surface and submarine
counters is generated in three places:

- `Fleet/FleetOrderOfBattlePlanner.cs:18-25`
- `Campaign/CampaignScenarioGenerator.cs:111-116` (per-hull destruction values)
- `Campaign/CampaignScenarioGenerator.cs:120-129` (`originalUnitIds`)

These must agree for objective remapping to work, so a shared numbering helper is worth
more than the ~20 lines it saves.

## 14. The hull-reference naming scheme has six definitions and no owner

Implemented on 2026-09-06 with item 8. The shared helper owns whole hull
names, INI hull-section headers, embedded hull tokens, and aircraft-section matching.
An in-memory comparison against the previous source passed 2,048 mixed-hull sequences,
repeated enumeration, empty forces, matcher and line-ending parity, and full campaign
remapping for both player sides, including split groups and aircraft home bases. No test
files were added. A fresh Sea Power startup generated all three templates; generated-file
checks confirmed contiguous unit numbers and valid formations, objective references, and
aircraft home bases. This did not exercise a live campaign save/load or tactical battle.
The build had zero warnings and errors. Fresh load marker: `2026-09-06T07:54:11.304Z`.
DLL SHA256: `CE08A6608387908E5C16920536534277CE80C70BBA7EEB1D0D7E6429A2210D00`.
The historical findings below explain the original duplication.

Item 8 covers the three producers of `{taskforce}Vessel{n}` / `{taskforce}Submarine{n}`.
Three regexes independently re-encode the same scheme to consume it:

- `Campaign/CampaignMissionData.cs:41` — `^{side}(?:Vessel|Submarine)\d+$`
- `Campaign/CampaignMissionData.cs:64` — `\bTaskforce[12](?:Vessel|Submarine)\d+\b`
- `Campaign/CampaignScenarioGenerator.cs:258-262` — the same shape as an INI section header

with a fourth at `CampaignMissionData.cs:110` for `(?:Aircraft|Helicopter)\d+`. Six places
encode one naming convention, and objective remapping breaks silently if they disagree.
Whatever type owns the numbering in item 8 should expose the matcher too.

## 19. Contract indexes are raw ints even though the enum exists

`CampaignContractKind` (FleetClash / RouteEscort / RouteInterdiction / Passage) is declared
in `Campaign/CampaignScenarioGenerator.cs:11-17` and `ResolveCampaignContract` maps mission
filenames onto it — but the offer, rules, and patch layers all work in raw 1-to-4 ints,
converting with `(int)contract + 1`:

- `CampaignContractOffer.Enemy:8` and `Description:13-23` — `mission == 3`, then a
  four-arm switch on `1 / 2 / 3 / _`.
- `CampaignContractOffers.Get:53-56` — `mission == 1`, `mission == 3`.
- `CampaignContractRules.Payment:19` — `mission == 4 ? 10 : mission == 1 ? 0 : 5`.
- `CampaignContractOffers.Describe:66`, `CampaignPatches.cs:66, 90` — `is < 1 or > 4`.

About a dozen sites where the domain already has a name for the thing. Using the enum makes
the switches total and removes the three bounds checks. This supersedes the
`missionIndex is < 1 or > 4` note in [item 18](campaign-state.md).

## 23. Campaign folder name declared three ways

`"crimean-tom-operations"` appears as three unrelated constructs:

- `TaskForce/TaskForceRosterGenerator.cs:12` — `const string CampaignFolderName`
- `TaskForce/TaskForceNationRules.cs:19` — the substring `"/crimean-tom-operations/"`
- `TaskForce/TaskForceCoalitions.cs:9` — embedded in `"campaigns/crimean-tom-operations/art/coalitions"`

Folds naturally into the `GamePaths` item in [item 9](small-items.md). (Minor sibling: the plugin id
`"crimean-tom.operations"` is written twice in `Plugin.cs:7, 14` and extended by hand in
`ScenarioSelectionPatches.cs:32`.)

## 28. Item 23 is bigger than it looked

Widening the earlier finding beyond C#, `crimean-tom-operations` is written as a path
component in six kinds of file: three C# constructs (item 23), five places in the csproj,
`scripts/coalition-art.sources.json:2`, four `MissionFile=` lines in `campaign.ini`, and the
self-referential `Base=` line in each of the four mission templates.

The JSON manifest and `TaskForceCoalitions.cs:9` describe the *same* art directory in two
different roots — `content/campaigns/.../art/coalitions` versus
`campaigns/.../art/coalitions` — so the repo-relative and game-relative forms have to be
kept in step by hand.

---

Back to the [index](README.md).
