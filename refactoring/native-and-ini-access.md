# Native and INI access

Three hand-rolled readers for one game file format, and the reflected type names that a
game update would send you hunting for.

## 5. Three INI parsers for the same game files

`Scenarios/ScenarioUnitCatalog.ReadSections` (`:262-293`) is already a general
section/key/value reader. Two other files reimplement it:

- `Fleet/VesselCombatProfileReader.ReadFromDisk:36-64` walks lines inline to find `[AI]`.
  Its `StripComment` at `:95` is a behavioural duplicate of `ScenarioUnitCatalog.StripComment`
  at `:295` — same rule, one written as a `switch`, one as nested ternaries.
- `TaskForce/TaskForceRosterGenerator.FindReferences:400-470` is a 70-line state machine
  over the same file format, backed by three regexes (`VariantSectionPattern`,
  `SquadronSectionPattern`, `AnySectionPattern`) plus a private `ParseIniValue` at `:452`.

Promoting `ReadSections` to its own file and using it from both would cut
`VesselCombatProfileReader.ReadFromDisk` to roughly five lines and let
`TaskForceRosterGenerator` drop its regexes and state machine. This is the same shape as
`NativeCampaignAccess`: one wrapper for a native-format contract instead of three
independent readings of it.

Related, in the same file: `ReadIniValue` and `ReadFirstIniValue` re-read and re-parse the
whole file per call. `ReadCarrierAirPackage` calls them once per aircraft and
`ReadSonobuoyAmmunition` calls `ReadFirstIniValue` again inside a loop. Parsing once into a
dictionary is both shorter and cheaper.

## 15. Native type names repeated across patch files

`AccessTools.TypeByName("SeapowerUI.ViewModels....")` resolves the same four type names 12
times: `LinearCampaignTaskForceCommanderManager` (4), `LinearCampaignTaskForceSaveManager`
(3), `LinearCampaignTaskForceDebriefViewModel` (3), `LinearCampaignTaskForceModeRules` (2).
These are the reflected-name contract the development guide says to verify with `ilspycmd`
before changing, so a game update currently means finding every copy.

Note the constraint: the guide deliberately keeps *patch-target lookups* in the patches.
Sharing the type-name constants does not violate that; hoisting the `MethodBase` resolution
would.

Also in `Campaign/CampaignPatches.cs:92`: `$"Mission_{missionIndex}_TaskForceDebrief"` is
built inline even though `CampaignPersistence.DebriefSection(int)` exists for exactly this.
It is `private` to a different partial file; making it `internal` fixes the divergence.

---

Back to the [index](README.md).
