# Refactoring backlog

Candidate cleanups found by surveying `src/CrimeanTom.SeaPower` against the pattern
established by commits `e0619323` through `116096ba`: extract a shared record that owns
its own serialization, wrap repeated native access behind one type, and collapse restated
models. These are intended as behavior-preserving cleanups, but each suggestion needs
validation against its call sites. Line numbers are from the survey and will drift.

Each area below is one file. Items are numbered in the order they were found, not the
order they should be done in — for that, see [Suggested order](#suggested-order).
Keep only outstanding suggestions here. Remove completed or invalidated items and their
index entries as work finishes, and update any references to them. Keep the remaining
numbers stable so cross-references still work; gaps are expected.

## Index

| # | Item | Area |
|---|---|---|
| 1 | Restated properties that `record` would delete outright | [Model shapes](models.md) |
| 2 | A `FleetCapability` bundle carried through five types | [Model shapes](models.md) |
| 4 | Duplicated route-corridor setup | [Scenario generation](scenario-generation.md) |
| 5 | Three INI parsers for the same game files | [Native and INI access](native-and-ini-access.md) |
| 6 | `playerTaskforce == "Taskforce1"` — 21 sites | [Naming and identity](naming-and-identity.md) |
| 7 | `ScenarioSetupWindow` never got the `NativeUi` treatment | [UI](ui.md) |
| 9 | Smaller, independent items | [Small items](small-items.md) |
| 10 | Thirteen `FleetClashSize` switch tables | [Fleet planning](fleet-planning.md) |
| 11 | `FleetClashMatch` rebuilt argument-by-argument, 3 sites | [Model shapes](models.md) |
| 12 | The launcher-file registry, written four ways | [Scenario generation](scenario-generation.md) |
| 13 | Duplicate spacing table inside one file | [Fleet planning](fleet-planning.md) |
| 15 | Native type names repeated across patch files | [Native and INI access](native-and-ini-access.md) |
| 16 | Coalition membership duplicated into the theater catalog | [Campaign state](campaign-state.md) |
| 17 | Further UI repetition | [UI](ui.md) |
| 18 | Further campaign-state repetition | [Campaign state](campaign-state.md) |
| 19 | Contract indexes are raw ints even though the enum exists | [Naming and identity](naming-and-identity.md) |
| 20 | Arrival/loss trigger pairs written twice | [Scenario generation](scenario-generation.md) |
| 21 | Commander discount nation lookup, written twice | [Campaign state](campaign-state.md) |
| 22 | Seeded distinct-vessel draw, written twice | [Campaign state](campaign-state.md) |
| 23 | Campaign folder name declared three ways | [Naming and identity](naming-and-identity.md) |
| 24 | Four unreferenced members | [Dead code](dead-code.md) |
| 25 | The four mission templates are ~85% identical | [Content and build files](content-and-build.md) |
| 26 | `campaign.ini` repeats 22 keys across four mission blocks | [Content and build files](content-and-build.md) |
| 27 | Build file repetition | [Content and build files](content-and-build.md) |
| 28 | The campaign folder name reaches further than item 23 shows | [Naming and identity](naming-and-identity.md) |

## Files reviewed with nothing to report

`Scenarios/GeneratedScenarioRuntime.cs`, `Scenarios/MissionWaypoint.cs`,
`Scenarios/MissionTrigger.cs`, `Scenarios/MissionLoadPatches.cs`, `Scenarios/MissionWriter.cs`,
`Fleet/FleetDeployment.cs`, `UI/ChoicePicker.cs`, `Plugin.cs`,
`Campaign/CampaignContractRules.cs`, `Campaign/CampaignStateData.cs`,
`Fleet/FleetClashTheaterCatalog.cs` and `TaskForce/TaskForceCoalitions.cs` as data tables
(apart from item 16). These are already at the target shape — several are the output of the
recent commits — and are listed so a later pass does not re-derive them.

Also checked and clean: no `TODO` / `HACK` / `FIXME` anywhere in the C# tree; one
exception-swallowing `catch` (`TaskForceRosterGenerator.cs:473`) which is deliberate and
guards a native cost lookup. `scripts/download-coalition-art.ps1` is deeply nested but that
is ordinary GDI+ disposal. `.github/` contains no workflow files.

## Suggested order

Take 1 and 2 together first — same files, mechanical, about 175 lines with the compiler
verifying every step. Item 11 falls out of item 1 for free, so do it in the same pass.
Then 4, 5, and 10, which reduce coupling rather than just volume.
7 and 17 are the UI cleanup and close out work `e0619323` started.
13 is six lines but fixes a real desynchronization risk, so it is worth doing early.
19 is worth doing before anything else touches the contract layer, since every later change
there would otherwise propagate more raw indexes.

24 is pure deletion with no behaviour to preserve, so it is the cheapest thing on this list.
25, 26 and 27 are content and build files rather than C#, so they do not conflict with any
of the code items and can go in parallel.

The rest — 6, 9, 12, 15, 16, 18, 20, 21, 22, 23, 28 — are independent and can go in whenever
a nearby change makes them convenient.

Every `.cs` file under `src/CrimeanTom.SeaPower` has been read at least once, and the
content, build, script and CI files have had a pass as well; the "nothing to report"
section above records what came back clean. The remaining items are proposals;
their line estimates are static reads rather than measured diffs.
