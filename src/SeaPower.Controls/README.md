# Controls

Controls is a library for Sea Power mod authors. Build in-game windows, tabs,
tables, search filters, sliders, and progress bars using Sea Power’s native UI
more easily.

Subscribe to Controls on the Steam Workshop and enable it in Sea Power's Mod
Manager. Keel resolves the library from enabled mod folders. Reference
`SeaPower.Controls.dll` from the installed item in your mod
project. Set `Private` to `false` and list Controls as a Workshop dependency
so players install the shared copy. Your project still needs the game's Noesis
and Seapower-Scripts references.

```xml
<Reference Include="SeaPower.Controls" HintPath="$(ControlsDll)" Private="false" />
```

```csharp
using static SeaPower.SharedUi.NativeUi;

var opacity = Range(70, 0, 100, 5, value => UpdateOpacity(value));
var display = Stack(Text("Display options"), SliderField("Opacity", opacity, value => $"{value}%"));
var window = Window("MyModDisplay", "Display", Tabs(("General", display)));
```

Call `SeaPower.SharedUi.WidgetGallery.Show(host)` from a button or command to
open an interactive widget gallery in a mission or the Encyclopedia.
`WidgetGallery.CreatePage()` returns the same tabs for embedding in your own
window.

Use `VerticalRange` for the game's speed or depth slider appearance, including
tick marks and an optional reversed direction. `SliderEntry` pairs the slider
with a number field and units. Pass value and label pairs to mark particular
notches on a horizontal or vertical slider.

```csharp
var speed = VerticalRange(18, 0, 36, 6);
var entry = SliderEntry(speed, "kts", (0, "Stop"), (12, "Cruise"), (36, "Flank"));
```

Use `RangeValue(bar, value => $"{value:0} t")` for a live progress readout
with custom units or formatting. `SegmentedProgress.Refresh` accepts an
optional label for each segment in addition to its tooltip.

The Native controls tab also shows a toolbar,
menu, popup, rich text, and an RGB picker made with native sliders. `ShowFilePicker` opens the
game's file picker and gives your mod the selected path.

Build and open the window on the game's UI thread through the native window
host. The DLL exposes the existing `SeaPower.SharedUi` types directly.
Helpers return native Noesis controls, so callers can use their properties,
styles, and events. For widgets and templates without a dedicated helper, use
the native Noesis type directly or `NativeUi.FromXaml<T>()` with your mod's XAML.
Game-specific screens still require their own view models and data.
For an option that compiles the controls into each mod, use the
[SeaPower.SharedUi source package](../SeaPower.SharedUi/README.md).
