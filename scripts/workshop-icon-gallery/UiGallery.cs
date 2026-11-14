using Noesis;
using SeapowerUI;
using SeaPower.SharedSettings;
using SeaPower.SharedUi;
using static SeaPower.SharedUi.NativeUi;

namespace SeaPower.GameBridge;

// Replaces the bridge gallery only in a temporary Workshop capture build.
internal static class UiGallery
{
    private static readonly List<SeapowerUI.Window> Panels = [];
    private static SeapowerUI.Window? demo;

    internal static object Open(GameCommand command)
    {
        MenuControls.RequireConfirm(command);
        if (MainGameView.Instance?.IsVisible != true
            || !Singleton<WindowHostManager>.InstanceExists()
            || Singleton<WindowHostManager>.Instance.CurrentHost?.AssociatedObject is not UIElement host)
            throw new InvalidOperationException("Open a mission or the encyclopedia first.");
        if (demo?.IsVisible == true) return new { ok = true, state = "already-open" };
        if (Panels.Count > 0)
        {
            foreach (var panel in Panels.Where(panel => panel.IsVisible))
                panel.Visibility = Visibility.Collapsed;
            Panels.Clear();
            demo = WidgetGallery.Show(host);
            return new { ok = true, state = "demo-opened" };
        }

        var surface = Branch("Surface combatants", null, Branch("Cruisers"), Branch("Destroyers"), Branch("Frigates"));
        surface.IsExpanded = true;
        var tree = Tree(surface, Branch("Submarines", null, Branch("Attack boats")), Branch("Aircraft"));
        var library = Stack(SearchBar(Entry("Search units"), Check("Available", true), Check("Allied", false)), tree);
        OpenPanel("Asset browser", "AssetBrowser", library, 557, 252, 310, 174);

        var meter = new SegmentedProgress("SharedIconSignal", 3);
        meter.Content.Height = 13;
        RefreshMeter(68);
        var second = Meter(85);
        var third = Meter(42);
        var overview = Stack(Caption("Systems online"), FormRow("Signal", meter.Content, 62),
            FormRow("Hull", second, 62), FormRow("Fuel", third, 62));
        OpenPanel("System status", "SystemStatus", Tabs(("Overview", overview),
            ("Details", Stack(Text("Radar online"), Text("Communications ready")))), 885, 245, 307, 171);

        var deck = Table("SharedIconDeck", TextColumn("Unit", nameof(DeckRow.Unit), 82),
            TextColumn("State", nameof(DeckRow.State), 75), ProgressColumn("Ready", nameof(DeckRow.Progress), 77));
        foreach (var row in new[] { new DeckRow("SH-60B", "Ready", 100), new DeckRow("F-14A", "Preparing", 65),
                     new DeckRow("A-6E", "Queued", 35), new DeckRow("E-2C", "Standby", 0) }) deck.Items.Add(row);
        deck.SelectedIndex = 1;
        OpenPanel("Flight deck", "FlightDeck", FillPanel("SharedIconDeckPanel", 0, null, deck), 547, 443, 274, 173);

        var channel = Text("12");
        var audio = Stack(
            Check("Monitor radio", true),
            FormRow("Channel", Stepper(channel, Button("−", () => ChangeChannel(-1)), Button("+", () => ChangeChannel(1))), 52),
            SliderField("Volume", Range(40, 0, 100, 5), value => value + "%"),
            FormRow("Band", Choice(new[] { "UHF", "VHF" }, value => value), 52));
        OpenPanel("Radio console", "Radio", audio, 1034, 436, 253, 173);

        var eventRows = Stack(EventRow("07:30:12", "Surface contact acquired", "Bearing 148°"),
            EventRow("07:30:18", "Formation set", "Screen assigned"),
            EventRow("07:30:25", "Deck ready", "Flight 101"));
        OpenPanel("Event log", "EventLog", eventRows, 582, 637, 306, 178);

        var patrol = List("Alpha", "Bravo", "Charlie", "Delta");
        patrol.SelectedIndex = 1;
        var orders = Stack(Section("Patrol groups", patrol),
            Wrap(Button("Add route", () => patrol.Items.Add("New route")), Button("Hold", () => patrol.SelectedIndex = -1)));
        OpenPanel("Operations", "Operations", orders, 954, 650, 300, 172);

        var defaults = new SettingsValues();
        defaults.Set("Display", "Opacity", 68);
        defaults.Set("Display", "Labels", 1);
        defaults.Set("Display", "Layout", 0);
        var editors = new StackPanel();
        var draft = Editable(defaults.Copy());
        var reset = Button("Reset", () =>
        {
            draft = Editable(defaults.Copy());
            RenderEditors(draft);
        });
        var useDefaults = Check("Use mod defaults", false, enabled =>
        {
            reset.IsEnabled = !enabled;
            RenderEditors(enabled ? new SettingsDraft(defaults) : draft);
        });
        var saved = Text("");
        var actions = Wrap(reset, Button("Apply", () =>
        {
            RefreshMeter((float)draft.Values.Get("Display", "Opacity"));
            saved.Text = "Applied";
        }, primary: true), saved);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.Margin = new Thickness(0, 5, 0, 0);
        RenderEditors(draft);

        var body = Stack(useDefaults, editors, actions);
        OpenPanel("Controls", "Settings", Tabs(("Display", body),
            ("Controls", Stack(FormRow("Refresh (s)", draft.NumberEditor("Controls", "Refresh", "Refresh", 5, 1, 30), 82),
                Radio("Automatic", "SharedIconMode", true), Radio("Manual", "SharedIconMode", false)))), 738, 376, 335, 260);
        return new { ok = true, state = "opened" };

        SettingsDraft Editable(SettingsValues values) => new(values, () => RefreshMeter((float)values.Get("Display", "Opacity")));

        void RenderEditors(SettingsDraft active)
        {
            var values = active.Values;
            var range = active.Editor(Range((float)values.Get("Display", "Opacity"), 0, 100, 1,
                value => active.Set("Display", "Opacity", value)));
            var layout = active.Editor(Choice(new[] { "Compact", "Expanded" }, value => value,
                selected: (int)values.Get("Display", "Layout"), changed: value => active.Set("Display", "Layout", value == "Compact" ? 0 : 1)));
            var labels = active.Editor(Check("Show labels", values.Get("Display", "Labels") != 0, value =>
            {
                active.Set("Display", "Labels", value ? 1 : 0);
                meter.Content.Visibility = value ? Visibility.Visible : Visibility.Hidden;
            }));
            editors.Children.Clear();
            editors.Children.Add(FormRow("Layout", layout, 78));
            editors.Children.Add(SliderField("Signal strength", range, value => value.ToString("0") + "%"));
            editors.Children.Add(labels);
            meter.Content.Visibility = values.Get("Display", "Labels") != 0 ? Visibility.Visible : Visibility.Hidden;
            RefreshMeter((float)values.Get("Display", "Opacity"));
        }

        void ChangeChannel(int delta) => channel.Text = Math.Clamp(int.Parse(channel.Text) + delta, 1, 16).ToString();

        void RefreshMeter(float percent) =>
            meter.Refresh(4, i => ($"Signal band {i + 1}", i == 0 ? 1.4f : 1f, Math.Clamp(percent / 25 - i, 0, 1), false));

        SeapowerUI.Window OpenPanel(string title, string name, FrameworkElement content, float x, float y, float width, float height)
        {
            content.Margin = new Thickness(10, 7, 10, 8);
            var window = Window("SharedIcon" + name, title, content, width, height, width, height);
            window.X = x;
            window.Y = y;
            WindowHostCommands.OpenWindowCommand.Execute(window, host);
            Panels.Add(window);
            return window;
        }
    }

    private static TextBlock Caption(string value)
    {
        var label = Text(value);
        label.FontSize = 17;
        label.Margin = new Thickness(0, 2, 0, 3);
        return label;
    }

    private static ProgressBar Meter(float value) => new() { Maximum = 100, Value = value, Height = 11 };

    private class DeckRow(string unit, string state, double progress)
    {
        public string Unit { get; } = unit;
        public string State { get; } = state;
        public double Progress { get; } = progress;
    }
}
