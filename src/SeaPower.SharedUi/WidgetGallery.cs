using Noesis;
using SeapowerUI;
using static SeaPower.SharedUi.NativeUi;

namespace SeaPower.SharedUi;

public static class WidgetGallery
{
    /// <summary>Opens an interactive gallery in the supplied native window host.</summary>
    public static SeapowerUI.Window Show(UIElement host)
    {
        var window = Window("ControlsWidgetGallery", "Controls", CreatePage(), 820, 580);
        WindowHostCommands.OpenWindowCommand.Execute(window, host);
        return window;
    }

    /// <summary>Builds a gallery page that can also be embedded in a mod's own window.</summary>
    public static TabControl CreatePage()
    {
        var output = Text("Change a control to see its value here.");
        output.Name = "ControlsGalleryFeedback";
        var tabs = Tabs(
            ("Inputs", Inputs(output)),
            ("Tables", Tables()),
            ("Lists & trees", Browse()),
            ("Meters", Meters()),
            ("Layout", Layout()));
        tabs.Name = "ControlsGalleryTabs";
        return tabs;
    }

    private static FrameworkElement Inputs(TextBlock output)
    {
        void Show(string label, object? value) => output.Text = $"{label}: {value}";
        var mode = Choice(new[] { "Automatic", "Manual", "Disabled" }, value => value, changed: value => Show("Mode", value));
        var amount = Number(25, 0, 100, value => Show("Amount", value));
        var range = Range(45, 0, 100, 5, value => Show("Allocation", value));
        range.Name = "ControlsGallerySlider";
        var counter = Text("0");
        int count = 0;
        void Step(int direction) => counter.Text = (count += direction).ToString();
        string group = Guid.NewGuid().ToString("N");
        return Scroll(Stack(
            FormRow("Mode", mode), FormRow("Amount", amount),
            FormRow("Call sign", Entry("Alpha", value => Show("Call sign", value))),
            Labelled("Notes", Entry("Enter briefing notes here.", multiline: true)),
            SliderField("Allocation", range, value => $"{value}%"),
            Wrap(Check("Show labels", true, value => Show("Labels", value)),
                TriState("Inherit setting", null, value => Show("Inherit", value ?? (object)"Default")),
                Toggle("Hold", false, value => Show("Hold", value))),
            Wrap(Radio("Primary", group, true, () => Show("Channel", "Primary")),
                Radio("Secondary", group, false, () => Show("Channel", "Secondary"))),
            FormRow("Counter", Stepper(counter, Repeat("−", () => Step(-1)), Repeat("+", () => Step(1)))),
            FormRow("Path", BrowseField(Entry("missions/example.ini"), () => Show("File picker", "The mod supplies the browse action"))),
            Divider(), WithTooltip(Button("Sample action", () => Show("Button", "Clicked"), primary: true), "A native tooltip."),
            output));
    }

    private static FrameworkElement Tables()
    {
        var table = Table("ControlsGalleryTable",
            TextColumn("Aircraft", nameof(AircraftRow.Name), 145),
            TextColumn("State", nameof(AircraftRow.State), 125),
            CheckColumn("Selected", nameof(AircraftRow.Selected), 80),
            ProgressColumn("Ready", nameof(AircraftRow.Progress), 155),
            ContentColumn("Actions", nameof(AircraftRow.Actions), 120));
        var report = Text("Select an aircraft or use Inspect.");
        foreach (var (name, state, progress) in new[] { ("SH-60B", "Ready", 100), ("F-14A", "Preparing", 65), ("A-6E", "Queued", 30) })
            table.Items.Add(new AircraftRow(name, state, progress, Button("Inspect", () => report.Text = name)));
        return FillPanel("ControlsGalleryTablePanel", 10, null, table, report);
    }

    private static FrameworkElement Browse()
    {
        var branch = Branch("Surface ships", null, Branch("Cruiser"), Branch("Destroyer"), Branch("Frigate"));
        branch.IsExpanded = true;
        var tree = Tree(branch, Branch("Aircraft", null, Branch("Fighter"), Branch("Helicopter")));
        var notice = Text("Right-click the tree for its context menu.");
        tree.ContextMenu = Menu(MenuAction("Inspect", () => notice.Text = "Tree item inspected."),
            MenuAction("Unavailable", () => { }, enabled: false));
        var search = Entry("");
        string[] hulls = ["Cruiser", "Destroyer", "Frigate", "Submarine"];
        var picker = new SearchablePicker<string>(search, "ControlsGalleryPicker",
            query => hulls.Where(hull => hull.Contains(query, StringComparison.OrdinalIgnoreCase)), hull => hull, 4);
        var list = List("Alpha", "Bravo", "Charlie");
        var collection = CollectionEditor(list, () => list.Items.Add("New group"), item => list.Items.Remove(item));
        return Split(tree, FillPanel("ControlsGalleryBrowser", 8,
            Stack(SearchBar(search, Check("Available", true)), picker.Results, Divider()), collection, notice));
    }

    private static FrameworkElement Meters()
    {
        var first = Progress(65);
        var value = Text("65 / 100");
        var segments = new SegmentedProgress("ControlsGalleryStage", 3);
        segments.Content.Height = 16;
        void Refresh(float progress)
        {
            first.Value = progress;
            value.Text = $"{progress:0} / 100";
            segments.Refresh(4, i => ($"Stage {i + 1}", i == 0 ? 2 : 1, Math.Clamp(progress / 25 - i, 0, 1), false));
        }
        Refresh(65);
        return Stack(
            CapacityMeters(Button("Complete", () => Refresh(100)),
                ("Hangar", first, value), ("Deck", Progress(3, 10), Text("3 / 10")), ("Crew", Progress(1, 4), Text("1 / 4"))),
            Divider(), Labelled("Segmented progress", segments.Content),
            SliderField("Readiness", Range(65, 0, 100, 5, Refresh), progress => $"{progress}%"),
            Labelled("Indeterminate progress", Progress(0, indeterminate: true)));
    }

    private static FrameworkElement Layout()
    {
        var summary = Text("Expand to show the field details.");
        var detail = new ExpandableDetails(summary, Stack(Text("Type: Escort"), Text("Status: Ready")), "details", 120, _ => { });
        detail.SetState(true, false);
        var heading = Docked(summary, (Dock.Right, detail.Toggle));
        var graphic = FromXaml<FrameworkElement>("""
            <Viewbox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Height="56">
              <Path Fill="#BF4939" Data="M 0,20 L 10,0 L 20,20 Z"/>
            </Viewbox>
            """);
        return Scroll(Stack(Columns(2, Card(heading), Card(graphic)), detail.Content,
            Section("Event cards", Stack(EventRow("07:30:12", "Contact acquired", "Bearing 148°"),
                EventRow("07:30:18", "Formation set", "Screen assigned")))));
    }

    private class AircraftRow(string name, string state, int progress, FrameworkElement actions)
    {
        public string Name { get; } = name;
        public string State { get; } = state;
        public bool Selected { get; set; }
        public int Progress { get; } = progress;
        public FrameworkElement Actions { get; } = actions;
    }
}
