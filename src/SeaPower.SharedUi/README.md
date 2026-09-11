# Shared UI source

This module compiles into each consuming mod. It produces no DLL and needs no
AnchorChain plugin or deployment step. Wet Deck and Amphibs consume it.

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

`Meter` builds a labelled capacity bar with centred values and an optional action.
`Labelled` places a label above a control. `Find` searches the native visual tree.
`ToolbarToggle.Attach` inserts a styled toggle beside an existing native button,
handling stack, grid-column and margin-positioned toolbars. Its owner attaches
gameplay callbacks to `Button` and calls `Dispose` when the host changes to remove
the inserted slot and restore the native layout. It does not own selection or
window lifecycle.

All helper types stay internal and use native controls. Do not add globally named
Noesis types, application resource registrations, Harmony patches or shared mutable
registries here. Each mod release contains its own compiled helper implementation;
updating one deployed mod does not update another. This isolates helper versions,
not changes to the game's native UI API.

Build consumers normally. Use `-p:DeployToGame=false` while Sea Power is running.
