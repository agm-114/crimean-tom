using Noesis;
using SeapowerUI;
using static SeaPower.SharedUi.NativeUi;

namespace SeaPower.GameBridge;

// Explicit developer command only. Callbacks change sample state, never the mission.
internal static class UiGallery
{
    private static SeapowerUI.Window? window;

    internal static object Open(GameCommand command)
    {
        MenuControls.RequireConfirm(command);
        if (MainGameView.Instance?.IsVisible != true)
            throw new InvalidOperationException("Open a mission or the encyclopedia first. The gallery uses mission window styles.");
        if (!Singleton<WindowHostManager>.InstanceExists()
            || Singleton<WindowHostManager>.Instance.CurrentHost?.AssociatedObject is not UIElement host)
            throw new InvalidOperationException("Open a native menu or mission first.");
        if (window?.IsVisible == true) return new { ok = true, state = "already-open" };
        var feedback = Text("Sample values only.");
        feedback.Name = "GalleryFeedback";
        var choice = Choice(new[] { "Automatic", "Manual", "Disabled" }, value => value,
            changed: value => feedback.Text = "Mode: " + value, tooltip: value => "Sample " + value + " option");
        choice.Name = "GalleryChoice";
        var number = Number(25, 0, 100, value => feedback.Text = "Number: " + value);
        number.Name = "GalleryNumber";
        var range = Range(25, 0, 100, 5, value => feedback.Text = "Slider: " + value);
        range.Name = "GallerySlider";
        var forms = Scroll(Stack(
            Section("Standing orders", Stack(
                Check("Use automatic assignment", true, value => feedback.Text = "Automatic: " + value),
                FormRow("Mode", choice), FormRow("Amount", number))),
            SliderField("Allocation", range, value => value + "%"),
            FormRow("Search", Entry("", value => feedback.Text = "Search: " + value)),
            Labelled("Notes", Entry("Multiline briefing text", multiline: true)),
            Radio("First option", "SeaPowerCliGallery", true), Radio("Second option", "SeaPowerCliGallery", false),
            WithTooltip(Button("Sample action", () => feedback.Text = "Action clicked", primary: true), "This action has no gameplay effects."),
            feedback));
        var table = Table("GalleryTable", TextColumn("Name", nameof(SampleRow.Name), 160),
            TextColumn("State", nameof(SampleRow.State), 150), ContentColumn("Actions", nameof(SampleRow.Actions), 160));
        for (int i = 1; i <= 30; i++) table.Items.Add(new SampleRow("Craft " + i, "Ready", Button("Inspect", () => feedback.Text = "Row action")));
        var tree = Tree(Branch("Surface ships", null, Branch("Carrier"), Branch("Escort")),
            Branch("Aircraft", null, Branch("Fighter"), Branch("Helicopter")));
        tree.Name = "GalleryTree";
        tree.ContextMenu = Menu(MenuAction("Sample menu action", () => feedback.Text = "Context menu"));
        var browser = Split(tree, Tabs(("Summary", Card(Text("Select a category in the sample tree."))),
            ("Properties", Scroll(Stack(FormRow("Role", Text("Escort")), FormRow("Status", Text("Ready")))))));
        var meters = CapacityMeters(Button("Recover All", () => { }),
            ("Storage Space", new ProgressBar { Maximum = 100, Value = 75 }, Text("75 / 100")),
            ("Deck Utilisation", new ProgressBar { Maximum = 10, Value = 3 }, Text("3 / 10")),
            ("Crew Utilisation", new ProgressBar { Maximum = 4, Value = 1 }, Text("1 / 4")));
        var tabs = Tabs(("Forms", forms), ("Operations", table), ("Browser", browser), ("Capacity", meters),
            ("Lists", List("Alpha", "Bravo", "Charlie")));
        tabs.Name = "SharedUiGalleryTabs";
        window = Window("SharedUiGallery", "Shared UI gallery", tabs, 850, 600);
        WindowHostCommands.OpenWindowCommand.Execute(window, host);
        return new { ok = true, state = "opened" };
    }

    private class SampleRow(string name, string state, FrameworkElement actions)
    {
        public string Name { get; } = name;
        public string State { get; } = state;
        public FrameworkElement Actions { get; } = actions;
    }
}
