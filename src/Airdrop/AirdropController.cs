using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;

namespace Airdrop;

public class AirdropController : MonoBehaviour
{
    private readonly UnitStatusPage page = new(0x4144, "Airdrop");
    private Aircraft? selected;
    private Action? refresh;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.2f;
        DropOperations.Update();
        var aircraft = Singleton<RenderPosition>.InstanceExists()
            ? Singleton<RenderPosition>.Instance.getSelectedObject() as Aircraft : null;
        bool available = DropOperations.CanUse(aircraft);
        page.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, available);
        if (!available) { selected = null; refresh = null; page.Content = null; return; }
        if (selected != aircraft || page.Content == null)
        {
            selected = aircraft;
            Open(aircraft!);
        }
        refresh?.Invoke();
    }

    private void Open(Aircraft aircraft)
    {
        var inventory = DropOperations.Inventory(aircraft);
        var content = new Grid { Name = "AirdropPanel", Margin = new Thickness(5) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            content.RowDefinitions.Add(new() { Height = height });
        var summary = Text("");
        summary.Name = "AirdropCapacity";
        content.Children.Add(summary);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        Grid.SetRow(controls, 1);
        content.Children.Add(controls);
        var type = new ComboBox { Name = "AirdropCargo", Width = 250, Height = 30 };
        foreach (var cargo in inventory.Definition.Cargo)
            type.Items.Add(new ComboBoxItem { Content = cargo.Name, Tag = cargo });
        type.SelectedIndex = 0;
        var quantity = new ComboBox { Name = "AirdropQuantity", Width = 70, Height = 30, Margin = new Thickness(8, 0, 8, 0) };
        for (int i = 1; i <= inventory.Definition.Cargo.Max(cargo => cargo.Count); i++) quantity.Items.Add(i.ToString());
        quantity.SelectedIndex = 0;
        controls.Children.Add(Labelled("Ground unit", type));
        controls.Children.Add(Labelled("Quantity", quantity));
        var arm = Button("ARM RELEASE", () =>
        {
            if (Valid() && SelectedCargo() is { Available: true } cargo) inventory.Arm(cargo, quantity.SelectedIndex + 1);
        }, primary: true);
        arm.Name = "ArmAirdrop";
        arm.VerticalAlignment = VerticalAlignment.Bottom;
        var cancel = Button("DISARM", () => { if (Valid()) { inventory.Disarm(); inventory.Message = ""; } });
        cancel.Name = "DisarmAirdrop";
        cancel.VerticalAlignment = VerticalAlignment.Bottom;
        controls.Children.Add(arm);
        controls.Children.Add(cancel);
        var rows = Table("AirdropManifest", TextColumn("Ground unit", nameof(CargoRow.Name), 250),
            TextColumn("Each", nameof(CargoRow.Mass), 110), TextColumn("Aboard", nameof(CargoRow.Aboard), 90),
            TextColumn("Released", nameof(CargoRow.Released), 90), TextColumn("Status", nameof(CargoRow.Status), 190));
        Grid.SetRow(rows, 2);
        content.Children.Add(rows);
        var footer = Text("");
        footer.Name = "AirdropStatus";
        footer.Margin = new Thickness(0, 5, 0, 0);
        Grid.SetRow(footer, 3);
        content.Children.Add(footer);
        content.DataContext = content;
        page.Content = content;
        string previous = "";
        refresh = () =>
        {
            summary.Text = $"Cargo: {inventory.Mass:N0} / {inventory.Definition.Capacity:N0} kg";
            var cargo = SelectedCargo();
            int availableCount = Math.Max(1, cargo == null ? 0 : inventory.Aboard[cargo.Reference]);
            if (quantity.Items.Count != availableCount)
            {
                int index = Math.Clamp(quantity.SelectedIndex, 0, availableCount - 1);
                quantity.Items.Clear();
                for (int i = 1; i <= availableCount; i++) quantity.Items.Add(i.ToString());
                quantity.SelectedIndex = index;
            }
            type.IsEnabled = quantity.IsEnabled = inventory.ArmedCargo == null;
            arm.IsEnabled = cargo is { Available: true } && inventory.Aboard[cargo.Reference] > 0 && inventory.ArmedCargo == null;
            cancel.IsEnabled = inventory.ArmedCargo != null;
            string blocked = DropOperations.Blocked(aircraft, inventory, out _);
            footer.Text = inventory.ArmedCargo != null
                ? $"Armed: {inventory.Remaining} remaining · " + (blocked.Length == 0 ? "Releasing along current flight path" : blocked)
                : inventory.Message.Length > 0 ? inventory.Message : "Arm to release along the current flight path";
            string signature = string.Join(";", inventory.Definition.Cargo.Select(item =>
                $"{inventory.Aboard[item.Reference]},{inventory.Released[item.Reference]},{item.Available},{inventory.ArmedCargo == item}"));
            if (signature == previous) return;
            previous = signature;
            rows.Items.Clear();
            foreach (var item in inventory.Definition.Cargo)
                rows.Items.Add(new CargoRow(item.Name, $"{item.Mass:N0} kg", inventory.Aboard[item.Reference], inventory.Released[item.Reference],
                    !item.Available ? "Not installed" : inventory.ArmedCargo == item ? "Armed" : inventory.Aboard[item.Reference] > 0 ? "Stowed" : "Empty"));
        };
        bool Valid() => selected == aircraft && DropOperations.CanUse(aircraft);
        CargoDefinition? SelectedCargo() => (type.SelectedItem as ComboBoxItem)?.Tag as CargoDefinition;
    }

    private void OnDestroy() => page.Dispose();
}

public class CargoRow(string name, string mass, int aboard, int released, string status)
{
    public string Name { get; } = name;
    public string Mass { get; } = mass;
    public int Aboard { get; } = aboard;
    public int Released { get; } = released;
    public string Status { get; } = status;
}
