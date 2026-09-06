# Small items

Independent one-and-two-line cleanups, kept together because none of them justifies its
own entry. Items 12 and 23 supersede parts of this list.

## 9. Smaller, independent items

- **Wrapped text in the UI, 7 sites.** `CreateText(...)` then `TextWrapping = Wrap` then a
  `Margin` assignment: `UI/CampaignAutoresolveWindow.cs:32-38, 66-68`,
  `UI/CampaignManagementWindow.cs:29-31, 38-44, 216-218`, plus the raw `TextBlock`s in
  [item 7](ui.md). A `CreateWrappedText(text, size, brush, margin)` overload next to `CreateText`
  makes each one line.
- **`Debug.Log($"[Crimean Tom Operations] ...")` in 24 places** across 11 files.
  `Campaign/CampaignDebug.cs` and `UI/CampaignManagementWindow.cs` each already wrote a
  private wrapper for it. Promote one.
- **`new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)` at 5 sites** —
  `Campaign/CampaignScenarioGenerator.cs:112`, `Scenarios/ScenarioDebugGenerator.cs:142, 357`,
  `Scenarios/ScenarioRouteOperationGenerator.cs:85`, `TaskForce/TaskForceRosterGenerator.cs:90`.
  `WriteIfChanged` at `ScenarioDebugGenerator.cs:352` already does this correctly; three
  sites bypass it and skip the change check.
- **Game directory layout hardcoded at 8 sites** in 5 files:
  `Path.Combine(Application.streamingAssetsPath, "original", "vessels" | "aircraft")`.
  A `GamePaths` static centralizes an install-layout contract, the same reason
  `NativeCampaignAccess` exists.
- **Launcher path comparison, 4 sites.** `IsLauncherPath` / `IsSpectatorLauncherPath` /
  `IsRouteOperationLauncherPath` (`Scenarios/ScenarioDebugGenerator.cs:52-85`) are 34 lines
  of repeated `Path.GetFullPath` + `OrdinalIgnoreCase` compare. A `SamePath(a, b)` helper
  and an array of launcher paths cuts it to about 10.
- **Int-map persistence, 3 hand-rolled copies.** `GetFatigue`, `SetFatigue`, and
  `DecrementFatigue` in `Campaign/CampaignReadiness.cs` each do
  `ReadMap` → `int.TryParse` → `WriteMap`. A `ReadIntMap` / `WriteIntMap` pair removes
  about 25 lines. `ReadMap` / `WriteMap` also lack a `WriteSet` to pair with `ReadSet`,
  so `string.Join("|", ...)` is inlined at the call sites.
- **Three persistence keys bypass the `const` convention.**
  `"CrimeanTomContractSeed"` (`CampaignOperationsState.cs:34, 38`),
  `"CrimeanTomFirstSortie"` (`:45, 66`), and `"TaskForceModeCampaignCompleted"` (`:145`)
  are raw literals while the other ten have a `private const string ...Key` in
  `Campaign/CampaignPersistence.cs`.
- **`AdvanceTurn` is inlined twice.** `Write(TurnKey, Turn + 1)` + describe every mission +
  `SaveCampaign()` appears in `CompleteManagement:141-149` and `WaitInPort:155-161`.
- **Three near-identical Harmony patch classes.**
  `ServiceRecordWindowWidthPatch` / `HeightPatch` / `MinHeightPatch`
  (`TaskForce/TaskForceCommanderPatches.cs:70-104`) are 10 lines each, differing only in
  target property and value. The `Math.Max` versus direct-assignment split means this is
  worth maybe 15 lines — low priority.
- **`Replace('_', ' ')` display formatting at 4 sites.**
  `TaskForceCoalitions.ToDisplayName:173` is the named version; `FleetClashModels.cs:125`,
  `ScenarioPassageOperationGenerator.cs:67`, and `CampaignManagementWindow.cs:95` inline it.
- **Unit-section write tails.** `DeployedFleetUnit.WriteTo`, `DeployedAircraft.WriteTo`,
  `RouteProtectedUnit.WriteTo`, and `CivilianTrafficUnit.WriteTo` share a five-line tail
  (`CrewSkill`, `RelativePositionInNM`, `Telegraph`, `Heading`, optional waypoints).
  Separately, `if (Waypoints is not null) mission.WriteWaypoints(Waypoints)` appears three
  times; letting `MissionWriter.WriteWaypoints` accept null and no-op removes the guard
  and a class of bug.
- **`tests/CrimeanTom.CampaignChecks/` holds only ignored build artifacts.** No `.cs` or
  `.csproj` is tracked and the project is not in `CrimeanTom.slnx`; the sources went away
  with `3db38eb8`. The leftover `bin/` and `obj/` directories can be deleted.

---

Back to the [index](README.md).
