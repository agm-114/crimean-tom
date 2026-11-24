using Noesis;
using SeaPower;
using UI;
using SeapowerUI;
using UnityEngine;
using static UI.NativeUi;

namespace Airdrop;

public class AirdropController : MonoBehaviour
{
    private readonly UnitStatusPage page = new(0x4144, "Airdrop");
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
        if (available) page.SetContent(aircraft!, Build);
        page.RefreshContent();
    }

    private (FrameworkElement, Action) Build(Aircraft aircraft)
    {
        var inventory = DropOperations.Inventory(aircraft);
        var summary = Text("");
        summary.Name = "AirdropCapacity";
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        var type = new ChoicePicker<CargoDefinition?>(inventory.Definition.Cargo.Select(cargo => (cargo.Name, (CargoDefinition?)cargo)));
        type.Control.Name = "AirdropCargo";
        type.Control.Width = 250;
        var quantity = new ChoicePicker<int>([("1", 1)], fallback: 1);
        quantity.Control.Name = "AirdropQuantity";
        quantity.Control.Width = 70;
        quantity.Control.Margin = new Thickness(8, 0, 8, 0);
        controls.Children.Add(Labelled("Ground unit", type.Control));
        controls.Children.Add(Labelled("Quantity", quantity.Control));
        var arm = Button("ARM RELEASE", () =>
        {
            if (Valid() && type.Value is { Available: true } cargo) inventory.Arm(cargo, quantity.Value);
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
        var footer = Text("");
        footer.Name = "AirdropStatus";
        footer.Margin = new Thickness(0, 5, 0, 0);
        var content = FillPanel("AirdropPanel", 5, Stack(summary, controls), rows, footer);
        content.DataContext = content;
        string previous = "";
        int quantityLimit = 1;
        return (content, Refresh);

        void Refresh()
        {
            summary.Text = $"Cargo: {inventory.Mass:N0} / {inventory.Definition.Capacity:N0} kg";
            var cargo = type.Value;
            int availableCount = Math.Max(1, cargo == null ? 0 : inventory.Aboard[cargo.Reference]);
            if (quantityLimit != availableCount)
            {
                quantityLimit = availableCount;
                quantity.Replace(Enumerable.Range(1, availableCount), value => value.ToString(),
                    fallbackIndex: availableCount - 1);
            }
            type.Control.IsEnabled = quantity.Control.IsEnabled = inventory.ArmedCargo == null;
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
        }
        bool Valid() => page.IsShowing(aircraft) && DropOperations.CanUse(aircraft);
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
