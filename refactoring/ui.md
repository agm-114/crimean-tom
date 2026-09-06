# UI

Work that commit `e0619323` started and left unfinished, plus the widgets `NativeUi`
still does not own.

## 7. `ScenarioSetupWindow` never got the `NativeUi` treatment

Commit `e0619323` centralized overlay creation and window closing for the two campaign
windows and left the third window out.

- `UI/ScenarioSetupWindow.CreateOverlay:160-190` duplicates `UI/NativeUi.CreateCampaignOverlay:37-72`:
  same Border + ScrollViewer + Grid, same brushes, same `ActualWidth > 0f ? : Screen.width`
  fallback, same clamping. Differs only in the overlay brush and in assigning `_openPanel`.
  About 33 lines to 5.
- `ScenarioSetupWindow.Close:223` duplicates `NativeUi.CloseWindow`, whose existing
  `(panel, host, ref openPanel)` signature already covers this case.
- The window builds seven `TextBlock`s by hand (`:48-53, 57-63, 64-72, 88-95`) and never
  calls `NativeUi.CreateText` at all.

## 17. Further UI repetition

Extending item 7:

- **The two-column label/control row is built three times**, all Grid + `ColumnDefinitions`
  + label in column 0 + control in column 1: `CampaignManagementWindow.CreateActionRow:174-205`,
  `ScenarioSetupWindow.AddField:192-217`, and the inline `selectorRow` in
  `CampaignAutoresolveWindow.cs:42-53`. It is the most repeated widget in the UI and the
  only common one `NativeUi` does not own.
- **The salvage and market list blocks** (`CampaignManagementWindow.cs:47-68, 70-93`) share
  one shape: heading, hint, empty-state hint, then a foreach building a button whose click
  calls a `Try...` method and refreshes. About 22 lines each.

---

Back to the [index](README.md).
