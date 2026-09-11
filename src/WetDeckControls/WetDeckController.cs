using Noesis;
using SeaPower;
using SeapowerUI;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;
using SeapowerUI.ViewModels;

namespace WetDeckControls;

public class WetDeckController : MonoBehaviour
{
    private MainGameView? host;
    private UnitStatusViewModel? status;
    private UnitStatusTabViewModel? tab;
    private ContentControl? presenter;
    private UnitStatusView? view;
    private bool listening;
    private ObjectBase? selected;
    private Action? refresh;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        if (host != MainGameView.Instance) { Detach(); host = MainGameView.Instance; }
        if (host == null || !host.IsVisible) { Close(); return; }
        if (!listening)
        {
            EventManager.RegisterClassHandler(typeof(UnitStatusView), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnViewLoaded));
            listening = true;
        }
        WetDeckOperations.UpdateRecovery();
        if (status != Globals._mainGameViewModel?.UnitStatus) { Detach(); Attach(); }
        if (tab == null) return;
        AttachContent();
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
        if (unit != selected) { Close(); selected = unit; }
        bool available = CanUse(selected);
        tab.IsAvailable = available;
        if (!available) Close();
        else if (tab.Content == null) Open();
        refresh?.Invoke();
    }

    private void Attach()
    {
        status = Globals._mainGameViewModel?.UnitStatus;
        if (status == null) return;
        // An extension kind leaves native lazy-content handling untouched.
        tab = new(status, (UnitStatusTab)0x5744, "Well Deck");
        tab.PropertyChanged += OnTabChanged;
        status.Tabs.Insert(status.Tabs.IndexOf(status.FlightDeckTab) + 1, tab);
    }

    private static bool CanUse(ObjectBase? unit) => unit != null && !unit.IsDestroyed && unit._taskforce != null
        && unit.IsPlayerUnit && unit.AcceptsOrdersFromPlayer && unit._obp != null
        && WetDeckCatalog.Decks.ContainsKey(unit._obp._objectIniName);

    private void AttachContent()
    {
        if (presenter != null || view == null || tab == null) return;
        // Native Unit Status creates one content container per tab, not a shared presenter.
        var flight = FindFlightDeck(view);
        if (flight?.Parent is not Grid wrapper || wrapper.Parent is not Grid panels) return;
        presenter = new ContentControl
        {
            Name = "WellDeckContent", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        presenter.SetBinding(ContentControl.ContentProperty, new Binding(nameof(tab.Content)) { Source = tab });
        presenter.Visibility = tab.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        panels.Children.Add(presenter);
    }

    private void OnViewLoaded(object sender, RoutedEventArgs args)
    {
        if (!Equals(sender, args.Source) || sender is not UnitStatusView loaded) return;
        if (view == loaded && presenter != null) return;
        if (presenter?.Parent is Panel panels) panels.Children.Remove(presenter);
        presenter = null;
        view = loaded;
        AttachContent();
    }

    private void OnTabChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (presenter != null && args.PropertyName == nameof(UnitStatusTabViewModel.IsSelected))
            presenter.Visibility = tab?.IsSelected == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FlightDeckContentView? FindFlightDeck(DependencyObject root)
    {
        if (root is FlightDeckContentView flight) return flight;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindFlightDeck(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void Open()
    {
        if (tab == null || tab.Content != null || !CanUse(selected) || !Singleton<UnitMetadataProvider>.InstanceExists()) return;
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
        content.Name = "WetDeckPanel";
        content.DataContext = content;
        tab.Content = content;
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
        refresh = null;
        if (tab == null) return;
        tab.Content = null;
        if (tab.IsSelected && !CanUse(selected)) status?.SelectTab(UnitStatusTab.Engagement);
    }

    private void Detach()
    {
        Close();
        if (presenter?.Parent is Panel panels) panels.Children.Remove(presenter);
        presenter = null;
        view = null;
        if (tab != null)
        {
            tab.PropertyChanged -= OnTabChanged;
            status?.Tabs.Remove(tab);
        }
        tab = null;
        status = null;
        selected = null;
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
