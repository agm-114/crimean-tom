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
    private Vessel? selected;
    private Action? refresh;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.25f;
        var ship = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() as Vessel : null;
        bool available = DeckOperations.CanUse(ship);
        page.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, available);
        if (!available) { selected = null; refresh = null; page.Content = null; return; }
        if (selected != ship || page.Content == null) { selected = ship; Open(ship!); }
        refresh?.Invoke();
    }

    private void Open(Vessel ship)
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
        var slotChoice = Choice(slots, slot => $"Slot {slot.Number}", changed: Edit);
        slotChoice.Name = "DeckparkSlot";
        slotChoice.Width = 100;
        var types = Choice(DeckCatalog.Units, DeckCatalog.Name);
        types.Name = "DeckparkGroundUnit";
        types.Width = 230;
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
        choices.Children.Add(slotChoice);
        choices.Children.Add(types);
        var mountButton = Button("MOUNT", () =>
        {
            if (Valid() && slotChoice.SelectedItem is ComboBoxItem { Tag: DeckSlot slot }
                && types.SelectedItem is ComboBoxItem { Tag: string reference })
                footer.Text = DeckOperations.Mount(ship, slot, reference);
        }, primary: true);
        mountButton.Name = "DeckparkMount";
        choices.Children.Add(mountButton);
        var root = new Grid { Name = "DeckparkPanel", Margin = new Thickness(5) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = height });
        root.Children.Add(summary);
        Grid.SetRow(table, 1);
        root.Children.Add(table);
        Grid.SetRow(choices, 2);
        root.Children.Add(choices);
        Grid.SetRow(editor, 3);
        root.Children.Add(editor);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        root.DataContext = root;
        page.Content = root;
        Edit(slots[0]);
        refresh = () =>
        {
            foreach (var row in rows) row.Update();
            var slot = (slotChoice.SelectedItem as ComboBoxItem)?.Tag as DeckSlot;
            mountButton.IsEnabled = Valid() && slot is { Used: false } && types.SelectedItem is ComboBoxItem { Tag: string reference }
                && DeckCatalog.Available(reference);
        };
        bool Valid() => selected == ship && DeckOperations.CanUse(ship);
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
