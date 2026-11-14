using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;

namespace Deckpark;

public class DeckparkController : MonoBehaviour
{
    private readonly UnitStatusPage page = new(0x4450, "Deckpark");
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.25f;
        var ship = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() as Vessel : null;
        bool available = DeckOperations.CanUse(ship);
        page.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, available);
        if (available) page.SetContent(ship!, Build);
        page.RefreshContent();
    }

    private (FrameworkElement, Action) Build(Vessel ship)
    {
        var slots = DeckOperations.Slots(ship);
        var rows = slots.Select(slot => new DeckSlotRow(ship, slot)).ToArray();
        var table = Table("DeckparkMounts", TextColumn("Slot", nameof(DeckSlotRow.Number), 50),
            TextColumn("Ground unit", nameof(DeckSlotRow.UnitName), 180), TextColumn("Status", nameof(DeckSlotRow.Status), 135),
            TextColumn("Position m", nameof(DeckSlotRow.Position), 155), ContentColumn("Weapons / radar", nameof(DeckSlotRow.Actions), 220));
        foreach (var row in rows) table.Items.Add(row);
        var summary = Text("Slots are fixed mounts. Check deck clearance before mounting.");
        var footer = Text("");
        footer.Name = "DeckparkStatus";
        var editor = new ContentControl();
        var slotChoice = new ChoicePicker<DeckSlot?>(slots.Select(slot => ($"Slot {slot.Number}", (DeckSlot?)slot)));
        slotChoice.Control.Name = "DeckparkSlot";
        slotChoice.Control.Width = 100;
        slotChoice.Control.SelectionChanged += (_, _) => { if (slotChoice.Value is { } slot) Edit(slot); };
        var types = new ChoicePicker<string?>(DeckCatalog.Units.Select(reference => (DeckCatalog.Name(reference), (string?)reference)));
        types.Control.Name = "DeckparkGroundUnit";
        types.Control.Width = 230;
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
        choices.Children.Add(slotChoice.Control);
        choices.Children.Add(types.Control);
        var mountButton = Button("MOUNT", () =>
        {
            if (Valid() && slotChoice.Value is { } slot
                && types.Value is { } reference)
                footer.Text = DeckOperations.Mount(ship, slot, reference);
        }, primary: true);
        mountButton.Name = "DeckparkMount";
        choices.Children.Add(mountButton);
        var root = FillPanel("DeckparkPanel", 5, summary, table, Stack(choices, editor, footer));
        root.DataContext = root;
        Edit(slots[0]);
        return (root, Refresh);

        void Refresh()
        {
            foreach (var row in rows) row.Update();
            var slot = slotChoice.Value;
            mountButton.IsEnabled = Valid() && slot is { Used: false } && types.Value is { } reference
                && DeckCatalog.Available(reference);
        }
        bool Valid() => page.IsShowing(ship) && DeckOperations.CanUse(ship);
        void Edit(DeckSlot slot)
        {
            var mounted = DeckOperations.On(ship).FirstOrDefault(item => item.Slot == slot.Number);
            if (mounted != null) { slot.Offset = mounted.Offset; slot.Heading = mounted.Heading; }
            float x = slot.Offset.x, y = slot.Offset.y, z = slot.Offset.z, heading = slot.Heading;
            var fields = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            fields.Children.Add(Field("Starboard m", x, -100, 100, value => x = value));
            fields.Children.Add(Field("Deck height m", y, 0, 80, value => y = value));
            fields.Children.Add(Field("Forward m", z, -500, 500, value => z = value));
            fields.Children.Add(Field("Heading °", heading, -360, 360, value => heading = value));
            var apply = Button("SET POSITION", () =>
            {
                if (!Valid()) return;
                var offset = new Vector3(x, y, z);
                if (DeckOperations.On(ship).Any(item => item.Slot != slot.Number && (item.Offset - offset).magnitude < 12))
                {
                    footer.Text = "Leave at least 12 m between deck mounts";
                    return;
                }
                slot.Offset = offset;
                slot.Heading = heading;
                var mount = DeckOperations.On(ship).FirstOrDefault(item => item.Slot == slot.Number);
                if (mount != null) { mount.Offset = offset; mount.Heading = heading; mount.ApplyPose(); }
                footer.Text = "Deck position set";
            });
            apply.Name = "DeckparkSetPosition";
            apply.VerticalAlignment = VerticalAlignment.Bottom;
            fields.Children.Add(apply);
            editor.Content = fields;
        }
    }

    private static FrameworkElement Field(string title, float value, float min, float max, Action<float> changed)
    {
        var field = Number(Math.Round(value, 3), min, max, number => changed((float)number));
        field.Width = 110;
        field.Margin = new Thickness(0, 0, 8, 0);
        return Labelled(title, field);
    }
    private void OnDestroy() => page.Dispose();
}

public class DeckSlotRow : System.ComponentModel.INotifyPropertyChanged
{
    private readonly Vessel ship;
    private readonly DeckSlot slot;
    private DeckMount? mount;
    private readonly Button weapons;
    private readonly Button radar;
    internal DeckSlotRow(Vessel ship, DeckSlot slot)
    {
        this.ship = ship;
        this.slot = slot;
        weapons = Button("WEAPONS", () => { if (Usable) mount!.SetWeapons(!mount.WeaponsEnabled); });
        radar = Button("RADAR", () => { if (Usable) mount!.SetRadar(mount.Unit.Emcon.Value); });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(weapons);
        actions.Children.Add(radar);
        Actions = actions;
    }
    private bool Usable => DeckOperations.CanUse(ship) && mount != null && mount.Ready && !mount.Unit.IsDestroyed;
    public int Number => slot.Number;
    public string UnitName => mount == null ? "—" : mount.Unit.DisplayNameShort;
    public string Status => mount == null ? slot.Used ? "Lost" : "Empty" : mount.Unit.IsDestroyed ? "Destroyed"
        : !mount.Ready ? "Host unavailable" : mount.WeaponsEnabled ? "Weapons free" : "Weapons held";
    public string Position => $"{slot.Offset.x:N1}, {slot.Offset.y:N1}, {slot.Offset.z:N1}";
    public FrameworkElement Actions { get; }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void Update()
    {
        mount = DeckOperations.On(ship).FirstOrDefault(item => item.Slot == slot.Number);
        weapons.IsEnabled = radar.IsEnabled = Usable;
        weapons.Content = mount?.WeaponsEnabled == true ? "HOLD" : "FREE";
        radar.Content = mount?.Unit.Emcon.Value == false ? "RADAR OFF" : "RADAR ON";
        foreach (string property in new[] { nameof(UnitName), nameof(Status), nameof(Position) })
            PropertyChanged?.Invoke(this, new(property));
    }
}
