using Noesis;
using SeaPower;
using SeapowerUI;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;
using SeaPower.SharedUi;

namespace WetDeckControls;

public class WetDeckController : MonoBehaviour
{
    private MainGameView? host;
    private ToolbarToggle? toolbar;
    private ToggleButton? toggle;
    private SeapowerUI.Window? window;
    private ObjectBase? selected;
    private Action? refresh;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        if (host != MainGameView.Instance) { Detach(); host = MainGameView.Instance; }
        if (host == null || !host.IsVisible) { Close(); return; }
        WetDeckOperations.UpdateRecovery();
        if (toggle == null) Attach();
        if (toggle == null) return;
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
        if (unit != selected) { Close(); selected = unit; }
        bool available = CanUse(selected);
        toggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available) Close();
        refresh?.Invoke();
    }

    private void Attach()
    {
        if (host == null || (host.FindName("FlightDeckWindow") ?? Find(host, "FlightDeckWindow")) is not ToggleButton flight) return;
        toolbar = ToolbarToggle.Attach(flight, "WetDeckButton", "WD", "Wet Deck");
        toggle = toolbar?.Button;
        if (toggle == null) return;
        toggle.Checked += (_, _) => Open();
        toggle.Unchecked += (_, _) => Close();
    }

    private static bool CanUse(ObjectBase? unit) => unit != null && !unit.IsDestroyed && unit._taskforce != null
        && unit.IsPlayerUnit && unit.AcceptsOrdersFromPlayer && unit._obp != null
        && WetDeckCatalog.Decks.ContainsKey(unit._obp._objectIniName);

    private void Open()
    {
        if (window != null || host == null || !CanUse(selected) || !Singleton<UnitMetadataProvider>.InstanceExists()) return;
        var ship = selected!;
        var inventory = WetDeckOperations.Inventory(ship);
        var content = new Grid { Margin = new Thickness(5) };
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var controls = new StackPanel();
        content.Children.Add(controls);
        var capacity = new ProgressBar { Height = 16, Maximum = inventory.Definition.StorageCapacity };
        var staged = new ProgressBar { Height = 16, Maximum = inventory.Definition.Capacity };
        var capacityText = Text("");
        var stagedText = Text("");
        var crews = new ProgressBar { Height = 16, Maximum = inventory.Definition.Crews };
        var crewsText = Text("");
        var recoverAll = Button("Recover All", () =>
        {
            if (!Valid()) return;
            foreach (var (boat, deployment) in WetDeckOperations.Deployed(inventory)) WetDeckOperations.Recover(ship, boat, deployment);
            inventory.Message = "Recovery requires ship speed at or below 5 knots.";
        });
        controls.Children.Add(Meter("Storage Space", capacity, capacityText, recoverAll));
        controls.Children.Add(Meter("Deck Utilisation", staged, stagedText));
        controls.Children.Add(Meter("Crew Utilisation", crews, crewsText));
        var choices = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        choices.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        choices.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var type = new ComboBox { Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        var assigned = new ComboBox { Height = 30 };
        foreach (var craft in inventory.Definition.Craft) type.Items.Add(new ComboBoxItem { Content = Name(craft), Tag = craft });
        for (int i = 1; i <= inventory.Definition.Craft.Max(craft => craft.Count); i++) assigned.Items.Add(i.ToString());
        type.SelectedIndex = assigned.SelectedIndex = 0;
        AddChoice("Type", type, 0);
        AddChoice("Assigned", assigned, 1);
        controls.Children.Add(choices);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var message = Text("");
        var ready = Button("READY", () =>
        {
            if (!Valid() || SelectedCraft() is not { } craft || !Exists(craft)) return;
            inventory.Queue(craft, Quantity(), false);
            inventory.Message = "";
        }, primary: true);
        var launch = Button("LAUNCH", () =>
        {
            if (Valid() && SelectedCraft() is { } craft && Exists(craft)) { inventory.Queue(craft, Quantity(), true); inventory.Message = ""; }
        }, primary: true);
        buttons.Children.Add(ready);
        buttons.Children.Add(launch);
        buttons.Children.Add(Button("ALL", () => assigned.SelectedIndex = (SelectedCraft()?.Count ?? 1) - 1, primary: true));
        controls.Children.Add(buttons);
        controls.Children.Add(Text("Boat Operations"));
        var rows = Table("WetDeckOperations",
            TextColumn("State", nameof(BoatOperationRow.State), 130),
            TextColumn("Craft", nameof(BoatOperationRow.Craft), 150),
            TextColumn("Info", nameof(BoatOperationRow.Info), 190),
            ContentColumn("Actions", nameof(BoatOperationRow.Action), 190));
        Grid.SetRow(rows, 1);
        Grid.SetRow(message, 2);
        content.Children.Add(rows);
        content.Children.Add(message);
        var panel = Window("WetDeckWindow", $"Wet Deck: {ship.DisplayNameShort}", content);
        panel.Unloaded += (_, _) => { if (window == panel) { window = null; refresh = null; if (toggle != null) toggle.IsChecked = false; } };
        window = panel;
        string previous = "";
        refresh = () =>
        {
            capacity.Value = inventory.OccupiedSpace;
            staged.Value = inventory.StagingSpace;
            crews.Value = inventory.BusyCrews;
            message.Text = inventory.Message;
            capacityText.Text = $"{capacity.Value} / {capacity.Maximum} SUs";
            stagedText.Text = $"{staged.Value} / {staged.Maximum} SUs";
            crewsText.Text = $"{crews.Value} / {crews.Maximum}";
            var craft = SelectedCraft();
            ready.IsEnabled = craft != null && Exists(craft) && inventory.Aboard[craft.Key] > inventory.Reserved(craft);
            launch.IsEnabled = craft != null && Exists(craft) && inventory.Aboard[craft.Key] > 0;
            var deployed = WetDeckOperations.Deployed(inventory).ToArray();
            recoverAll.IsEnabled = deployed.Any(item => !item.Deployment.Recovering && item.Boat.AcceptsOrdersFromPlayer);
            string signature = string.Join(";", inventory.Definition.Craft.Select(item => $"{inventory.Aboard[item.Key]},{inventory.Count(item)}"))
                + string.Join(";", inventory.Tasks.Select(task => $"{task.State},{task.Info}"))
                + string.Join(";", deployed.Select(item => $"{item.Boat.GetInstanceID()},{item.Deployment.Recovering}"));
            if (previous == signature) return;
            previous = signature;
            rows.Items.Clear();
            foreach (var item in inventory.Definition.Craft)
            {
                int stowed = inventory.Aboard[item.Key] - inventory.Reserved(item);
                if (stowed > 0) rows.Items.Add(new BoatOperationRow($"{stowed}x Stowed", Name(item), "", null));
            }
            foreach (var task in inventory.Tasks.ToArray())
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                actions.Children.Add(Button("Abort", () =>
                {
                    if (Valid()) inventory.Tasks.Remove(task);
                }));
                actions.Children.Add(Button(task.AutoLaunch ? "Preparation" : "Launch", () =>
                {
                    if (Valid() && inventory.Tasks.Contains(task)) task.AutoLaunch = !task.AutoLaunch;
                }));
                rows.Items.Add(new BoatOperationRow("1x " + task.State, Name(task.Craft), task.Info, actions));
            }
            foreach (var (boat, deployment) in deployed)
                rows.Items.Add(new BoatOperationRow(deployment.Recovering ? "Recovering" : "Underway", boat.DisplayNameShort,
                    deployment.Recovering ? "Returning to ship" : "", Button(deployment.Recovering ? "Abort" : "Recover", () =>
                    {
                        if (!Valid() || boat == null || boat.IsDestroyed) return;
                        if (deployment.Recovering) { deployment.Recovering = false; boat.RemoveWaypoints(); }
                        else WetDeckOperations.Recover(ship, boat, deployment);
                    })));
        };
        refresh();
        WindowHostCommands.OpenWindowCommand.Execute(panel, host);
        bool Valid() => selected == ship && CanUse(ship);
        CraftDefinition? SelectedCraft() => (type.SelectedItem as ComboBoxItem)?.Tag as CraftDefinition;
        int Quantity() => Math.Max(1, assigned.SelectedIndex + 1);
        void AddChoice(string label, ComboBox box, int index)
        {
            var stack = Labelled(label, box);
            Grid.SetColumn(stack, index);
            choices.Children.Add(stack);
        }
    }

    private void Close()
    {
        var panel = window;
        window = null;
        refresh = null;
        if (toggle != null) toggle.IsChecked = false;
        if (panel != null && host != null) WindowHostCommands.CloseWindowCommand.Execute(panel, host);
    }

    private void Detach()
    {
        Close();
        toolbar?.Dispose();
        toolbar = null;
        toggle = null;
    }
    private void OnDestroy() => Detach();
    private static bool Exists(CraftDefinition craft) => Singleton<UnitMetadataProvider>.Instance.UnitData.ContainsKey((craft.Folder, craft.Reference));
    private static string Name(CraftDefinition craft) => Singleton<UnitMetadataProvider>.Instance.UnitData.TryGetValue((craft.Folder, craft.Reference), out var data)
        ? $"{data.Item2.DefaultName} ({craft.Space} SUs)" : craft.Reference + " (not installed)";

}

public class BoatOperationRow(string state, string craft, string info, FrameworkElement? action)
{
    public string State { get; } = state;
    public string Craft { get; } = craft;
    public string Info { get; } = info;
    public FrameworkElement? Action { get; } = action;
}
