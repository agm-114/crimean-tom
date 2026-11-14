# Shared UI source

This module compiles into each consuming mod. It produces no DLL and needs no
AnchorChain plugin or deployment step. The mods and the CLI's development gallery
import the same source.

Add the source package to your mod project.

```xml
<PackageReference Include="SeaPower.SharedUi" Version="[0.1.0]" PrivateAssets="all" />
```

Build both packages from the repository root, then register the output folder
as a local NuGet source.

```powershell
dotnet pack src/SeaPower.SharedUi -o artifacts/nuget
dotnet pack src/SeaPower.SharedSettings -o artifacts/nuget
dotnet nuget add source (Resolve-Path artifacts/nuget).Path --name SeaPowerLocal
```

Use the direct import for a project that builds from this checkout.
Choose one source per project.

```xml
<Import Project="../SeaPower.SharedUi/SeaPower.SharedUi.props" />
```

The consumer supplies the game's Noesis, Seapower-Scripts and Unity references.
The source uses .NET Standard 2.1 or a compatible target, the repository's current
C# language version, implicit usings and nullable settings.
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

Labels, table text and headers use an explicit light foreground for the game's
dark panels. Do not rely on inherited text colors inside custom tab presenters
or expanders; those containers can supply a black foreground. Shared entry,
choice and checkbox controls use the same foreground.

The window uses itself as its initial data context for native window-host commands.
The caller can open and close it directly or use `HostedWindow`.

`CapacityMeters` groups capacity bars with centred values and an action beside the lower rows, matching Flight Deck.
`Labelled` places a label above a control. `Find` searches the native visual tree.
`ToolbarToggle.Attach` inserts a styled toggle beside an existing native button,
handling stack, grid-column and margin-positioned toolbars. Its owner attaches
gameplay callbacks to `Button` and calls `Dispose` when the host changes to remove
the inserted slot and restore the native layout. It does not own selection or
window lifecycle.

## Forms and layouts

`Choice<T>` builds a dropdown and populates its initial selection before
subscribing to changes. Use `ChoicePicker<T>` for typed selection reads,
replacing options while retaining the selected value, or relabeling them.
`TryGetValue` distinguishes a selected null value from an empty selection.
`SearchablePicker<T>` connects a search box to a capped dropdown with empty and
overflow messages. The caller supplies the filtering rule. `Entry` supports
single-line and multiline input. `Number` accepts finite values within bounds;
invalid partial edits stay visible until focus leaves, then restore the last valid
value. `Range` and `SliderField` provide a slider with its label and current value.
`Check`, `TriState`, `Radio` and `Toggle` keep native styling. Give radio groups
unique names for each independent view.

`FormRow`, `Stack`, `Wrap`, `Columns`, `Card`, `Section`, `Tabs` and `Split` compose
layouts. `Split` uses a native draggable splitter. `Tree`/`Branch`, `List` and
`CollectionEditor` handle browsable collections. Callers own add/remove callbacks.
`FillPanel` gives remaining height to a body between optional header and footer
controls. `SyncSelection` replaces selector items and restores selection by key.
`KeyedPanel<T>` reuses controls when rows move, preserving state such as expanded
sections.
`SearchBar` combines an entry with filters. `BrowseField` pairs a path entry with
a browse action; it does not read files or choose a dialog policy. `Stepper`
positions decrement/value/increment controls, with a compact stacked-button
layout for cells. `ExpandableDetails` pairs a summary with a caret and bounded
scrolling content. `SegmentedProgress` displays weighted progress bars with
per-segment tooltips.

`CheckColumn` binds an editable boolean and `ProgressColumn` displays a percentage.
`Picture` accepts an existing image source. `Menu` and `MenuAction` build a native
context menu. `WithTooltip` wraps long tooltip text. `Bind` uses an explicit source
so helper bindings do not depend on an inherited data context.

`HostedWindow` opens and releases a native window with its host and reports
closure through an optional callback. `Overlay` builds a centered bordered panel
and backdrop for caller-supplied content and dimensions.

## Unit Status tabs

Keep one `UnitStatusPage` instance per mod controller. Use a unique numeric tab ID,
call `Refresh` with the current host, view model and availability, then
`SetContent` with a key and a factory that returns the view and its refresh action.
Call `RefreshContent` on the controller's UI update cadence. Dispose the page when
the controller is destroyed. The helper handles selection, replacement hosts and
presenter cleanup without global class handlers. It attaches to a visible view
belonging to the current host and view model. It clears content when its tab
becomes unavailable or its host changes. Boat Ops uses the optional insertion
anchor to follow Flight Deck.

The [control catalogue](catalogue.md) records the inspected screens and the limits
of the survey. Run `seapower-cli ui-gallery --confirm` in a mission or encyclopedia
to inspect sample controls. Gallery actions change sample values, not gameplay.

All helper types stay internal and use native controls. Do not add globally named
Noesis types, application resource registrations, Harmony patches or shared mutable
registries here. Each mod release contains its own compiled helper implementation;
updating one deployed mod does not update another. This isolates helper versions,
not changes to the game's native UI API.

Build consumers normally. Use `-p:DeployToGame=false` while Sea Power is running.
