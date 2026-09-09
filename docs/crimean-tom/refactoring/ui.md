# UI

## 7. Share window construction where layouts agree

`UI/ScenarioSetupWindow.cs` still builds text, overlays, and close behavior beside
the helpers in `UI/NativeUi.cs`. Use the existing text helper where it preserves
defaults. A wrapped-text helper could also serve `CampaignManagementWindow` and
`CampaignAutoresolveWindow`.

Overlay creation is similar, not identical:

| Setting | Scenario setup | Campaign helper |
|---|---|---|
| Width | 500 to 760, host minus 48 | Caller limits, host minus 100 |
| Scroll height | Minimum 420, host minus 48 | Minimum 440, host minus 60 |
| Root sizing | Explicit host width/height | Stretch alignment |
| Background | Scenario overlay brush | Campaign overlay brush |

Share the border/scroll construction only if these differences remain explicit
without a large options object. `CloseWindow` can replace the local close method,
but it clears the reference only when it matches the panel being closed.

The label/control rows also differ. Setup has fixed/star columns and one label;
management has star/auto columns, a subtitle, and a border; autoresolve has
star/auto columns. A two-child grid helper may be enough. Keep salvage and market
transactions and event handlers local.

Use the Sea Power testing skill to check setup, management, and autoresolve at
small and large resolutions. Verify clipping, scrolling, reopen/cancel, launch
failure, and management refresh. A build cannot validate Noesis layout or host
ownership.

Back to the [index](README.md).
