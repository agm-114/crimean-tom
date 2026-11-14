using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;

namespace BoatOps;

public class BoatOpsController : MonoBehaviour
{
    private readonly UnitStatusPage page = new(0x5744, "Boat Ops", UnitStatusTab.FlightDeck, "WellDeckContent");
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        var host = MainGameView.Instance;
        if (host?.IsVisible == true) BoatOperations.UpdateRecovery();
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
        bool available = CanUse(unit);
        page.Refresh(host, Globals._mainGameViewModel?.UnitStatus, available);
        if (available && Singleton<UnitMetadataProvider>.InstanceExists()) page.SetContent(unit!, Build);
        page.RefreshContent();
    }

    private static bool CanUse(ObjectBase? unit) => unit != null && !unit.IsDestroyed && unit._taskforce != null
        && unit.IsPlayerUnit && unit.AcceptsOrdersFromPlayer && unit._obp != null
        && BoatCatalog.Decks.ContainsKey(unit._obp._objectIniName);

    private (FrameworkElement, Action) Build(ObjectBase ship)
    {
        var inventory = BoatOperations.Inventory(ship);
        var controls = new StackPanel();
        var capacity = new ProgressBar { Maximum = inventory.Definition.StorageCapacity };
        var staged = new ProgressBar { Maximum = inventory.Definition.Capacity };
        var capacityText = Text("");
        var stagedText = Text("");
        var crews = new ProgressBar { Maximum = inventory.Definition.Crews };
        var crewsText = Text("");
        var recoverAll = Button("Recover All", () =>
        {
            if (!Valid()) return;
            foreach (var (boat, deployment) in BoatOperations.Deployed(inventory)) BoatOperations.Recover(ship, boat, deployment);
            inventory.Message = "Recovery requires ship speed at or below 5 knots.";
        });
        controls.Children.Add(CapacityMeters(recoverAll,
            ("Storage Space", capacity, capacityText),
            ("Deck Utilisation", staged, stagedText),
            ("Crew Utilisation", crews, crewsText)));
        var choices = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        choices.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        choices.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
        choices.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        choices.RowDefinitions.Add(new() { Height = GridLength.Auto });
        choices.RowDefinitions.Add(new() { Height = new GridLength(18) });
        choices.RowDefinitions.Add(new() { Height = new GridLength(30) });
        var type = new ChoicePicker<CraftDefinition?>(inventory.Definition.Craft.Select(craft => (Name(craft), (CraftDefinition?)craft)));
        var assigned = new ChoicePicker<int>(Enumerable.Range(1, inventory.Definition.Craft.Max(craft => craft.Count))
            .Select(count => (count.ToString(), count)), fallback: 1);
        type.Control.Height = assigned.Control.Height = 30;
        choices.Children.Add(Labelled("Type", type.Control));
        var assignment = new Grid();
        assignment.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        assignment.ColumnDefinitions.Add(new() { Width = new GridLength(15) });
        assignment.ColumnDefinitions.Add(new() { Width = new GridLength(1.75f, GridUnitType.Star) });
        assignment.Children.Add(Labelled("Assigned", assigned.Control));
        var all = Button("ALL", () => assigned.Control.SelectedIndex = (type.Value?.Count ?? 1) - 1, primary: true);
        all.Margin = new Thickness(0);
        all.Height = 30;
        all.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(all, 2);
        assignment.Children.Add(all);
        Grid.SetColumn(assignment, 2);
        choices.Children.Add(assignment);
        controls.Children.Add(choices);
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new() { Width = new GridLength(10) });
        buttons.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var message = Text("");
        var ready = Button("READY", () =>
        {
            if (!Valid() || type.Value is not { } craft || !Exists(craft)) return;
            inventory.Queue(craft, assigned.Value, false);
            inventory.Message = "";
        }, primary: true);
        var launch = Button("LAUNCH", () =>
        {
            if (Valid() && type.Value is { } craft && Exists(craft)) { inventory.Queue(craft, assigned.Value, true); inventory.Message = ""; }
        }, primary: true);
        ready.Margin = launch.Margin = new Thickness(0);
        Grid.SetColumn(launch, 2);
        buttons.Children.Add(ready);
        buttons.Children.Add(launch);
        Grid.SetColumn(buttons, 2);
        Grid.SetRow(buttons, 2);
        choices.Children.Add(buttons);
        controls.Children.Add(Text("Boat Operations"));
        var rows = Table("BoatOperations",
            TextColumn("State", nameof(BoatOperationRow.State), 130),
            TextColumn("Craft", nameof(BoatOperationRow.Craft), 150),
            TextColumn("Info", nameof(BoatOperationRow.Info), 190),
            ContentColumn("Actions", nameof(BoatOperationRow.Action), 190));
        var content = FillPanel("BoatOpsPanel", 5, controls, rows, message);
        content.DataContext = content;
        string previous = "";
        return (content, Refresh);

        void Refresh()
        {
            capacity.Value = inventory.OccupiedSpace;
            staged.Value = inventory.StagingSpace;
            crews.Value = inventory.BusyCrews;
            message.Text = inventory.Message;
            capacityText.Text = $"{capacity.Value} / {capacity.Maximum} SUs";
            stagedText.Text = $"{staged.Value} / {staged.Maximum} SUs";
            crewsText.Text = $"{crews.Value} / {crews.Maximum}";
            var craft = type.Value;
            ready.IsEnabled = craft != null && Exists(craft) && inventory.Aboard[craft.Key] > inventory.Reserved(craft);
            launch.IsEnabled = craft != null && Exists(craft) && inventory.Aboard[craft.Key] > 0;
            var deployed = BoatOperations.Deployed(inventory).ToArray();
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
                        else BoatOperations.Recover(ship, boat, deployment);
                    })));
        }
        bool Valid() => page.IsShowing(ship) && CanUse(ship);
    }

    private void OnDestroy() => page.Dispose();
    private static bool Exists(CraftDefinition craft) => craft.AvailableUnit != null;
    private static string Name(CraftDefinition craft) => craft.AvailableUnit is { } unit
        ? $"{unit.DefaultName} ({craft.Space} SUs)" : craft.Reference + " (unavailable)";

}

public class BoatOperationRow(string state, string craft, string info, FrameworkElement? action)
{
    public string State { get; } = state;
    public string Craft { get; } = craft;
    public string Info { get; } = info;
    public FrameworkElement? Action { get; } = action;
}
