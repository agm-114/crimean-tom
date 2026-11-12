using System.Globalization;
using Newtonsoft.Json.Linq;
using Noesis;
using SeaPower;
using SeaPower.GameBridge;
using SeaPower.SharedUi;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;

namespace OODA;

public class DecisionWindow : MonoBehaviour
{
    private static DecisionWindow? instance;
    private readonly DecisionController controller = new();
    private MainGameView? host;
    private ToolbarToggle? launcher;
    private readonly UnitStatusPage unitPage = new(0x4f44, "OODA");
    private ObjectBase? statusUnit;
    private TextBlock? unitState;
    private int unitRevision = -1;
    private SeapowerUI.Window? window;
    private TabControl? tabs;
    private TextBlock? status;
    private TextBlock? chartStatus;
    private TextBlock? clockStatus;
    private TextBlock? paceSource;
    private TextBlock? paceReason;
    private Button? replanButton;
    private Button? takeControl;
    private float nextRefresh;
    private double nextTimeTick;
    private int revision = -1;
    private string? tabMembership;
    private readonly Dictionary<string, TextBlock> shipStates = new();

    private void Awake() => instance = this;

    internal static JObject ReadStatus() => instance?.controller.Report() ?? new JObject { ["mode"] = "Unavailable" };
    internal static JArray? ReadPlanMarks() => PlayerPicture.Side == null ? null : instance?.controller.PlanMarks();

    internal static void EndMission() => instance?.controller.Reset();

    private void Update()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (now >= nextTimeTick)
        {
            nextTimeTick = now + 0.1;
            try { controller.TickTime(now); }
            catch (Exception error)
            {
                controller.SetMode(ControlMode.Manual);
                Debug.LogWarning($"[OODA] Time interrupt failed: {error.Message}");
            }
        }
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.4f;
        if (host != MainGameView.Instance)
        {
            Close();
            DetachLauncher();
            host = MainGameView.Instance;
        }
        try { controller.Tick(); }
        catch (Exception error)
        {
            controller.SetMode(ControlMode.Manual);
            Debug.LogWarning($"[OODA] Decision loop stopped: {error.Message}");
        }
        Taskforce? side = PlayerPicture.Side;
        if (host == null || side == null) { Close(); DetachLauncher(); return; }
        if (launcher?.Button.Parent == null) { DetachLauncher(); AttachLauncher(host); }
        RefreshUnitStatus(side);
        if (window != null && tabs != null) Refresh(PlayerPicture.Ships(side));
    }

    private void AttachLauncher(MainGameView view)
    {
        if (Find<ToggleButton>(view, button => button.Name == "UnitStatusWindow") is not { } anchor) return;
        object icon = GUI.ParseXaml("""
            <Path xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="19" Height="19"
                  Stretch="Uniform" Stroke="#292B2D" StrokeThickness="1.7" StrokeLineJoin="Round"
                  Data="M2,2 H7 V7 H2 Z M13,8 H18 V13 H13 Z M2,14 H7 V19 H2 Z M7,4.5 H11 V10.5 H13 M13,10.5 H11 V16.5 H7" />
            """);
        launcher = ToolbarToggle.Attach(anchor, "OodaDecisionsButton", icon, "OODA Decisions");
        if (launcher == null) return;
        launcher.Button.Visibility = Visibility.Visible;
        launcher.Button.IsChecked = window != null;
        launcher.Button.Click += (_, _) =>
        {
            if (launcher.Button.IsChecked == true) Open();
            else Close();
        };
    }

    private void Open()
    {
        if (host == null || PlayerPicture.Side is not { } side) return;
        if (window != null) { WindowHostCommands.OpenWindowCommand.Execute(window, host); return; }

        status = Text("");
        clockStatus = Text("");
        chartStatus = Text(controller.ChartPoint);
        replanButton = Button("Replan", controller.PlanNow);
        takeControl = Button("Take control", () => controller.SetMode(ControlMode.Manual));
        DockPanel layout = new() { Margin = new Thickness(10) };
        StackPanel header = Stack(Text(controller.Provider), status, clockStatus, Wrap(
            Button("Observe", () => controller.SetMode(ControlMode.Observe)),
            Button("AI control", () => controller.SetMode(ControlMode.AiControl), primary: true), takeControl, replanButton,
            Check("Let AI manage time", controller.ManageTime, controller.SetTimeManagement)),
            Section("Chart waypoint", ChartEditor(), expanded: false));
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        tabs = Tabs();
        layout.Children.Add(tabs);
        revision = -1;
        window = Window("OodaDecisions", "OODA Decisions", layout, 850, 610, 560, 340);
        window.OnClosed += (_, _) => ClearWindow();
        Refresh(PlayerPicture.Ships(side));
        WindowHostCommands.OpenWindowCommand.Execute(window, host);
    }

    private FrameworkElement ChartEditor()
    {
        TextBox label = Entry("Waypoint label");
        TextBox latitude = Entry("Latitude in decimal degrees");
        TextBox longitude = Entry("Longitude in decimal degrees");
        return Stack(chartStatus!, FormRow("Label", label), FormRow("Latitude", latitude), FormRow("Longitude", longitude),
            Button("Set chart point", () =>
            {
                if (!double.TryParse(latitude.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                    || !double.TryParse(longitude.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)
                    || !double.IsFinite(lat) || !double.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
                {
                    chartStatus!.Text = "Enter a valid latitude and longitude in decimal degrees.";
                    return;
                }
                controller.SetChartPoint(label.Text, lat, lon);
                chartStatus!.Text = controller.ChartPoint;
            }));
    }

    private void Refresh(Vessel[] ships)
    {
        status!.Text = controller.Status;
        clockStatus!.Text = controller.ClockStatus;
        takeControl!.IsEnabled = controller.Mode != ControlMode.Manual;
        replanButton!.IsEnabled = controller.Mode != ControlMode.Manual;
        string membership = string.Join("|", ships.Select(PlayerPicture.Id));
        if (revision == controller.Revision && tabMembership == membership)
        {
            RefreshTimeControl();
            foreach (Vessel ship in ships)
                if (shipStates.TryGetValue(PlayerPicture.Id(ship), out TextBlock line)) line.Text = ShipState(ship);
            return;
        }
        revision = controller.Revision;
        tabMembership = membership;
        string? selected = (tabs!.SelectedItem as TabItem)?.Tag as string;
        if (tabs.Items.Count > 0 && selected == null) selected = "mission";
        tabs.Items.Clear();
        shipStates.Clear();
        AddTab("mission", "Mission Command", MissionPage());
        AddTab("trace", "Decision log", Stack(Timeline(controller.Events, showShipNames: true)));
        AddTab("time", "Time Control", TimeControlPage());
        AddTab("contacts", "Contact picture", ContactsPage());
        foreach (Vessel ship in ships)
        {
            TextBlock state = Text(ShipState(ship));
            shipStates[PlayerPicture.Id(ship)] = state;
            AddTab(PlayerPicture.Id(ship), ship.getName(), ShipPage(ship, state));
        }
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(tab => (string?)tab.Tag == selected) ?? tabs.Items[0];
    }

    private void AddTab(string id, string title, FrameworkElement content) => tabs!.Items.Add(new TabItem
        { Header = title, Tag = id, Content = Scroll(content) });

    private FrameworkElement TimeControlPage()
    {
        paceSource = Text("");
        paceSource.FontWeight = FontWeights.SemiBold;
        paceReason = Text("");
        RefreshTimeControl();
        return Stack(Card(Stack(paceSource, paceReason)),
            Section("Recent speed changes", Timeline(controller.Events.Where(item => item.Stage == "Time control"), showShipNames: true)));
    }

    private void RefreshTimeControl()
    {
        if (paceSource == null || paceReason == null) return;
        var (source, reason) = controller.ClockExplanation;
        paceSource.Text = $"{controller.ClockStatus} · {source}";
        paceReason.Text = reason;
    }

    private FrameworkElement MissionPage()
    {
        JObject? input = controller.LastInput;
        return Stack(Section("Current plan", Card(Text(controller.Objective))),
            Section("Mission briefing", Text(controller.Briefing ?? "No opening briefing available"), expanded: false),
            Section("Mission messages", MissionMessages()),
            Section("Map annotations", MapMarks(), expanded: false),
            Section("Last player picture sent", MissionObservations(input), expanded: false),
            Section("Planning history", Timeline(controller.Events.Where(item => item.Stage == "Mission Command"))));
    }

    private FrameworkElement MapMarks()
    {
        StackPanel annotations = Stack();
        foreach (JObject mark in Rows(controller.PlanMarks()))
        {
            string detail = Position(mark["position"]);
            if ((float?)mark["radiusNm"] is { } radius) detail += $" · {radius:0.#} nm radius";
            annotations.Children.Add(EventRow((string?)mark["kind"] ?? "", (string?)mark["label"] ?? "", detail));
        }
        if (annotations.Children.Count == 0) annotations.Children.Add(Text("No map annotations in the current plan"));
        return annotations;
    }

    private static FrameworkElement MissionMessages()
    {
        StackPanel messages = Stack();
        foreach (MissionCommunications.Message message in MissionCommunications.Messages.Reverse())
        {
            string title = string.Join(" · ", new[] { message.Title, message.From }.Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            string detail = string.Equals(message.Body, title, StringComparison.OrdinalIgnoreCase) ? "" : message.Body;
            messages.Children.Add(EventRow($"{Elapsed(message.Time)} · {message.Kind}", title, detail));
        }
        if (messages.Children.Count == 0) messages.Children.Add(Text("No mission messages received"));
        return messages;
    }

    private static FrameworkElement MissionObservations(JObject? input)
    {
        if (input == null) return Text("Waiting for the first decision request");
        StackPanel detail = Stack(
            Field("Task force", (string?)input["taskforce"] ?? "Unknown"),
            Field("Rules of engagement", (string?)input["rulesOfEngagement"] ?? "Unavailable"),
            Field("Plotted contacts", (input["contacts"]?.Count() ?? 0).ToString(CultureInfo.InvariantCulture)));
        foreach (JObject objective in Rows(input["objectiveUpdates"]))
        {
            string state = (int?)objective["status"] switch { 1 => "Complete", -1 => "Failed", 2 => "Canceled", _ => "Pending" };
            detail.Children.Add(Field(state, (string?)objective["text"] ?? ""));
        }
        foreach (JObject unit in Rows(input["ownedUnits"]))
            detail.Children.Add(Field((string?)unit["name"] ?? "Ship", $"{unit["order"]} · {unit["speedKnots"]} kn"));
        foreach (JObject unit in Rows(input["ownedOtherUnits"]))
            detail.Children.Add(Field((string?)unit["name"] ?? "Unit", $"{unit["type"]} · {unit["order"]}"));
        return detail;
    }

    private void RefreshUnitStatus(Taskforce side)
    {
        MainGameViewModel? model = Globals._mainGameViewModel;
        ObjectBase? unit = (model?.SelectedViewModel?.Value as ObjectBaseViewModel)?._objectBase;
        bool available = unit != null && PlayerPicture.CanCommand(unit, side);
        unitPage.Refresh(host, model?.UnitStatus, available);
        if (!available || unit == null) { statusUnit = null; unitState = null; unitPage.Content = null; return; }
        if (statusUnit != unit || unitRevision != controller.Revision || unitPage.Content == null)
        {
            statusUnit = unit;
            unitRevision = controller.Revision;
            unitState = Text("");
            string id = PlayerPicture.Id(unit);
            FrameworkElement content = unit is Vessel ship ? ShipPage(ship, unitState) : Stack(
                Section(unit.DisplayNameShort, unitState),
                Section("Standing Orders observed", StandingOrders(id), expanded: false),
                Section("Decision history", Timeline(controller.Events.Where(item => item.ShipId == id))));
            unitPage.Content = Scroll(content);
        }
        unitState!.Text = unit is Vessel selected ? ShipState(selected) : unit.CurrentOrderText.Value;
    }

    private FrameworkElement ShipPage(Vessel ship, TextBlock current)
    {
        string id = PlayerPicture.Id(ship);
        Vessel? leader = ship.FollowingFormation && !ship.IsFormationLeader ? ship.Formation?.LeaderStation?.UnitObject as Vessel : null;
        string commandId = leader == null ? id : PlayerPicture.Id(leader);
        JObject observed = PlayerPicture.Inspect(ship);
        StackPanel route = Stack(Field("Position", Position(observed["position"])),
            Field("Heading", $"{observed["headingDegrees"]}°"));
        int index = 1;
        foreach (JObject point in Rows(observed["route"]))
            route.Children.Add(Field($"Waypoint {index++}", Position(point)));
        Button preview = Button("Preview urgent decision", () => controller.PreviewUrgent(id));
        preview.IsEnabled = controller.CanPreviewUrgent;
        StackPanel urgency = Stack(Text(controller.UrgentBudget), preview);
        if (controller.UrgentShip == id && controller.LastUrgentInput is { } urgentInput)
        {
            StackPanel options = Stack(Text((string?)urgentInput["threat"] ?? ""));
            foreach (JObject option in Rows(urgentInput["options"]))
                options.Children.Add(Field((string?)option["id"] ?? "", (string?)option["description"] ?? ""));
            urgency.Children.Add(Section("Preview observations and options", options, expanded: false));
        }
        string assignment = controller.Assignment(commandId);
        return Stack(Section("Assignment", Card(Text(leader == null ? assignment : $"Follows {leader.getName()}\n{assignment}"))),
            Section("Current state", current),
            Section("Navigation", route, expanded: false),
            Section("Standing Orders observed", StandingOrders(id), expanded: false),
            Section("Decision history", Timeline(controller.Events.Where(item => item.ShipId == id || item.ShipId == commandId))),
            Section("Urgent decisions", urgency, expanded: false));
    }

    private FrameworkElement StandingOrders(string id)
    {
        StackPanel settings = Stack();
        JObject? target = Rows(controller.LastInput?["standingOrders"]).FirstOrDefault(item => (string?)item["targetId"] == id);
        foreach (JObject setting in Rows(target?["settings"]))
        {
            string value = (string?)setting["value"] ?? "";
            if ((bool?)setting["canInherit"] == true && (bool?)setting["overridden"] == false) value += " (inherited)";
            settings.Children.Add(Field((string?)setting["label"] ?? "", value));
        }
        if (settings.Children.Count == 0) settings.Children.Add(Text("No settings observed in the last request"));
        return settings;
    }

    private static Grid Field(string label, string value) => FormRow(label, Text(value), 125);
    private static IEnumerable<JObject> Rows(JToken? value) => value?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
    private static string Position(JToken? point) => $"{(double?)point?["latitude"]:0.0000}, {(double?)point?["longitude"]:0.0000}";

    private FrameworkElement ContactsPage()
    {
        StackPanel list = Stack();
        if (controller.LastInput?["contacts"] is not JArray { Count: > 0 } contacts)
        {
            list.Children.Add(Text("No plotted contacts in the last request"));
            return list;
        }
        foreach (JObject contact in contacts.OfType<JObject>())
        {
            string id = (string?)contact["id"] ?? "";
            string track = id.StartsWith("track-", StringComparison.Ordinal) ? $"Track {id[6..]}" : id;
            string? label = (string?)contact["displayLabel"];
            string title = string.IsNullOrWhiteSpace(label) || label == id ? track : ReadableContactLabel(label);
            string category = (string?)contact["category"] == "Air" ? "aircraft" : ((string?)contact["category"] ?? "contact").ToLowerInvariant();
            List<string> summary = [$"{contact["posture"]} {category}"];
            if (title != track) summary.Add(track);
            string? source = (bool?)contact["visuallyObserved"] == true ? "Visual" : (string?)contact["sensor"] switch
            {
                "SearchRadar" => "Search radar",
                "FCR" => "Fire-control radar",
                "ARS" => "Active radar seeker",
                "ActiveSonar" => "Active sonar",
                "PassiveSonar" => "Passive sonar",
                "TVSeeker" => "TV seeker",
                "ActiveIntercept" => "Active intercept",
                "Unknown" or "Self" or null => null,
                string sensor => sensor
            };
            if (source != null) summary.Add(source);
            if ((bool?)contact["live"] != true) summary.Add(contact["estimatedPosition"] is JObject ? "Last known position" : "No current fix");
            string[] victims = Rows(controller.LastInput?["observedGunfire"])
                .Where(fire => (string?)fire["attackerId"] == id)
                .Select(fire => (string?)fire["victim"] ?? "unknown ship").Distinct().ToArray();
            string activity = victims.Length > 0 ? $"Seen firing toward {string.Join(", ", victims)}" : "";
            list.Children.Add(EventRow(string.Join(" · ", summary), title, activity));
        }
        return list;
    }

    private static string ReadableContactLabel(string label) => label.Contains('_')
        ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(label.Replace('_', ' ')) : label;

    private static string ShipState(Vessel ship) => $"{ship._velocityInKnots:0.#} kn · {ship._weaponStatus}\n{ship.CurrentOrderText.Value}\n" +
        $"{ship.GetNumberOfWaypoints()} waypoints{(ship.FollowingFormation && !ship.IsFormationLeader ? " · follows formation leader" : "")}";

    private static FrameworkElement Timeline(IEnumerable<DecisionEvent> events, bool showShipNames = false)
    {
        StackPanel history = new();
        foreach (DecisionEvent item in events.Reverse())
        {
            string subject = showShipNames && item.ShipName is { Length: > 0 } name ? $"  ·  {name}" : "";
            string status = item.RoeWarning == null ? item.Status : $"{item.Status} · Possibly illegal fire";
            string detail = item.RoeWarning == null ? item.Detail : $"{item.Detail}\n{item.RoeWarning}";
            history.Children.Add(EventRow($"{Elapsed(item.Time)}  ·  {item.Stage}{subject}", status, detail));
        }
        if (history.Children.Count == 0) history.Children.Add(Text("No decisions yet"));
        return history;
    }

    private static string Elapsed(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }

    private void Close()
    {
        if (window != null && host != null) WindowHostCommands.CloseWindowCommand.Execute(window, host);
        ClearWindow();
    }

    private void ClearWindow()
    {
        window = null;
        tabs = null;
        paceSource = paceReason = null;
        shipStates.Clear();
        if (launcher != null) launcher.Button.IsChecked = false;
    }

    private void DetachLauncher()
    {
        launcher?.Dispose();
        launcher = null;
    }

    private void OnDestroy()
    {
        controller.Reset();
        Close();
        DetachLauncher();
        unitPage.Dispose();
        if (instance == this) instance = null;
    }
}
