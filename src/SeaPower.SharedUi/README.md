# Shared UI source

This module compiles into each consuming mod. It produces no DLL and needs no
AnchorChain plugin or deployment step. Boat Ops, Amphibs, UNREP and the CLI's
development gallery consume it.

Import it once in a mod's project:

```xml
<Import Project="../SeaPower.SharedUi/SeaPower.SharedUi.props" />
```

The consumer supplies the game's Noesis, Seapower-Scripts and Unity references.
The source uses the repository's current C# language version and nullable settings.
Linked files appear under `SharedUi` in the consuming project.

```csharp
using static SeaPower.SharedUi.NativeUi;

var rows = Table("Operations",
    TextColumn("State", nameof(OperationRow.State), 130),
    ContentColumn("Actions", nameof(OperationRow.Actions), 190));
var ready = Button("READY", Prepare, primary: true);
var window = Window("OperationsWindow", "Operations", content);
```

Column properties are trusted code-defined binding paths, not user input. Labels
are independent of property names. Content columns bind to native UI elements.
Tables retain native draggable column headers. Put them in a star-sized grid row
to fill remaining window height. The window disables its outer scrolling so the
table can manage overflow without pushing controls outside the window.

The window uses itself as its initial data context for native window-host commands.
Its caller owns opening, closing, selection changes and gameplay actions.

`CapacityMeters` groups capacity bars with centred values and an action beside the lower rows, matching Flight Deck.
`Labelled` places a label above a control. `Find` searches the native visual tree.
`ToolbarToggle.Attach` inserts a styled toggle beside an existing native button,
handling stack, grid-column and margin-positioned toolbars. Its owner attaches
gameplay callbacks to `Button` and calls `Dispose` when the host changes to remove
the inserted slot and restore the native layout. It does not own selection or
window lifecycle.

## Forms and layouts

`Choice<T>` keeps typed values on dropdown items, supports option tooltips, and
populates the initial selection before subscribing to changes. `Entry` supports
single-line and multiline input. `Number` accepts finite values within bounds;
invalid partial edits stay visible until focus leaves, then restore the last valid
value. `Range` and `SliderField` provide a slider with its label and current value.
`Check`, `TriState`, `Radio` and `Toggle` keep native styling. Give radio groups
unique names for each independent view.

`FormRow`, `Stack`, `Wrap`, `Columns`, `Card`, `Section`, `Tabs` and `Split` compose
layouts. `Split` uses a native draggable splitter. `Tree`/`Branch`, `List` and
`CollectionEditor` handle browsable collections. Callers own add/remove callbacks.
`SearchBar` combines an entry with filters. `BrowseField` pairs a path entry with
a browse action; it does not read files or choose a dialog policy. `Stepper`
positions decrement/value/increment controls; callers set their limits and state.

`CheckColumn` binds an editable boolean and `ProgressColumn` displays a percentage.
`Picture` accepts an existing image source. `Menu` and `MenuAction` build a native
context menu. `WithTooltip` wraps long tooltip text. `Bind` uses an explicit source
so helper bindings do not depend on an inherited data context.

## Unit Status tabs

Keep one `UnitStatusPage` instance per mod controller. Use a unique numeric tab ID,
call `Refresh` with the current host, view model and availability, and set `Content`
after the tab exists. Dispose it when the controller is destroyed. The helper
handles selection, replacement hosts and presenter cleanup without global class
handlers. UNREP's `SuppliesController` is a complete caller example.

The [control catalogue](catalogue.md) records the inspected screens and the limits
of the survey. Run `seapower-cli ui-gallery --confirm` in a mission or encyclopedia
to inspect sample controls. Gallery actions change sample values, not gameplay.

All helper types stay internal and use native controls. Do not add globally named
Noesis types, application resource registrations, Harmony patches or shared mutable
registries here. Each mod release contains its own compiled helper implementation;
updating one deployed mod does not update another. This isolates helper versions,
not changes to the game's native UI API.

Build consumers normally. Use `-p:DeployToGame=false` while Sea Power is running.
