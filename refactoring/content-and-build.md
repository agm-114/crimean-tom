# Content and build files

The non-C# half of the survey: mission templates, the campaign descriptor, and the
project file.

## 25. The four mission templates are ~85% identical

`content/campaigns/crimean-tom-operations/missions/` holds four 40-line templates, 160
lines total. Diffing them:

- fleet-clash, route-escort and passage differ in **four lines** each — the self-referential
  `Base=` path, `Contract=`, `Name=`, and the map centre.
- route-interdiction adds four more — swapped `PlayerTaskforce` / `EnemyTaskforce`, the two
  placeholder unit types, and which one carries `TaskForceModeAnchor`.

The DLL rewrites the installed copies at launch anyway, so almost all of this is
placeholder content that never reaches the player. The precedent for the fix is already in
the repository: `player_task_force_roster.ini` is a five-line stub with a comment saying
startup replaces it. The templates could be generated the same way, or emitted from one
source at build time.

**The `[CrimeanTomCampaign] Contract=` key is never read.** It appears in all four templates
and no C# code looks at it; `ResolveCampaignContract` matches on the *filename* substring
instead (`Campaign/CampaignScenarioGenerator.cs:146-165`). The section name is ours, so the
native game does not read it either. That is eight lines of dead data that reads as
authoritative.

Worth connecting to [item 19](naming-and-identity.md): the fix for contract identification wants exactly this key.
Rather than deleting it, `ResolveCampaignContract` could read it and parse it into
`CampaignContractKind`, which is both more robust than filename matching and removes the
raw-int conversion.

## 26. `campaign.ini` repeats 22 keys across four mission blocks

`content/campaigns/crimean-tom-operations/campaign.ini:65-183` holds four `[MissionN]`
blocks of ~30 lines each. 22 of those keys are byte-identical across all four — the whole
`TaskForceMode*` capability set, `RequiredResult`, `Onboarding`, `MissionType`,
`MissionSequenceName_en`, `MissionResupplyRules_en`. Only about eight lines per block vary.

Two of the varying lines are misleading rather than merely repetitive:
`TaskForceModeCompletionPoints` and `TaskForceModeCompletionCapPoints` are set per mission
(40, 45, …), but both known read paths are overridden at runtime —
`CampaignContractPaymentPatch` replaces the `ReadMissionIntRule` result and
`CampaignRewardApplicationPatch` replaces the arguments to `ApplyCompletionRewards`. Anyone
tuning those numbers in `campaign.ini` would see no effect. They should carry a comment
saying so, or be flattened to a single obviously-inert value.

## 27. Build file repetition

`src/CrimeanTom.SeaPower/CrimeanTom.SeaPower.csproj`:

- **The campaign content path is written four times.**
  `$(MSBuildProjectDirectory)\..\..\content\campaigns\crimean-tom-operations\` appears in
  `CampaignDescriptor`, `CampaignRoster`, `CampaignArtDirectory`, and
  `CampaignMissionDirectory` (`:19-22`). One `$(CampaignContentDirectory)` property fixes it.
- **The version is duplicated into source.** `<Version>0.3.0</Version>` at `:9` and
  `[ACPlugin("crimean-tom.operations", "Crimean Tom Operations", "0.3.0")]` at
  `Plugin.cs:7`. They are hand-synced, and `Plugin.cs:18` logs
  `assembly.GetName().Version` — the csproj value — so a divergence would not show up in
  the log line that exists to report it.
- **Seven near-identical `<Reference>` blocks** (`:27-53`), each a HintPath under
  `$(GameManaged)` plus `<Private>false</Private>`. An item list of names with one
  transform would cut roughly 28 lines to 10.

---

Back to the [index](README.md).
