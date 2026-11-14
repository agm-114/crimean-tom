using DeckTweaks.Preparation;
using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using SeaPower.SharedSettings;
using Path = System.IO.Path;
using static SeaPower.SharedUi.NativeUi;

namespace DeckTweaks;

internal static class DeckOverrideEditor
{
    private enum Kind { Weapon, Aircraft, Nation }
    private class Table(string section, string title, Kind kind, string valueLabel, double initialValue)
    {
        internal string Section { get; } = section;
        internal string Title { get; } = title;
        internal Kind Kind { get; } = kind;
        internal string ValueLabel { get; } = valueLabel;
        internal double InitialValue { get; } = initialValue;
    }

    private class ChoiceItem(string id, string name)
    {
        internal string Id { get; } = id;
        internal string Name { get; } = name;
    }

    private const int MaxMatches = 40;
    private const double Minimum = 0.001;
    private static readonly Table[] Tables =
    [
        new("WeaponMountSeconds", "Weapon mounting (seconds per item)", Kind.Weapon, "Seconds per item", 60),
        new("WeaponRemoveSeconds", "Weapon removal (seconds per item)", Kind.Weapon, "Seconds per item", 60),
        new("AircraftRefuelSeconds", "Aircraft refueling (seconds to full)", Kind.Aircraft, "Seconds to full", 300),
        new("AircraftOperationTimeMultipliers", "Aircraft time multipliers", Kind.Aircraft, "Time multiplier", 1),
        new("NationOperationTimeMultipliers", "Nation time multipliers", Kind.Nation, "Time multiplier", 1),
    ];

    internal static void AddSections(StackPanel panel, SettingsDraft draft)
    {
        var catalog = new Catalog();
        foreach (var table in Tables) panel.Children.Add(BuildSection(table, draft, catalog));
    }

    private static Expander BuildSection(Table table, SettingsDraft draft, Catalog catalog)
    {
        var rows = new StackPanel();
        var content = Stack(rows);
        var section = Section(table.Title, content, expanded: false);
        section.Name = table.Section + "Overrides";
        Action refreshPicker = () => { };
        RefreshRows();
        if (draft.CanEdit)
        {
            bool loaded = false;
            section.Expanded += (_, _) =>
            {
                if (loaded) return;
                loaded = true;
                refreshPicker = AddPicker(table, draft, catalog, content, RefreshRows);
            };
        }
        return section;

        void RefreshRows()
        {
            rows.Children.Clear();
            foreach (var entry in draft.Values.Entries(table.Section).OrderBy(entry => catalog.Name(table.Kind, entry.Key), StringComparer.CurrentCultureIgnoreCase))
            {
                string id = entry.Key;
                string name = catalog.Name(table.Kind, id);
                double value = Math.Clamp(entry.Value, Minimum, DeckSettings.Maximum);
                var input = draft.NumberEditor(table.Section, id, name, value, Minimum, DeckSettings.Maximum);
                var label = Text(name);
                label.ToolTip = id;
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = new GridLength(82) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(label);
                Grid.SetColumn(input, 1);
                row.Children.Add(input);
                if (draft.CanEdit)
                {
                    var remove = Button("Remove", () =>
                    {
                        draft.Remove(table.Section, id);
                        RefreshRows();
                        refreshPicker();
                    });
                    remove.ToolTip = $"Remove {name}";
                    Grid.SetColumn(remove, 2);
                    row.Children.Add(remove);
                }
                rows.Children.Add(row);
            }
            if (rows.Children.Count == 0) rows.Children.Add(Text("No overrides"));
        }
    }

    private static Action AddPicker(Table table, SettingsDraft draft, Catalog catalog, StackPanel content, Action refreshRows)
    {
        var search = draft.Editor(Entry(""));
        search.Name = table.Section + "Search";
        var picker = new SearchablePicker<ChoiceItem>(search, table.Section + "Choices", FindChoices,
            item => item.Name, MaxMatches, item => item.Id);
        bool valid = true;
        double rate = table.InitialValue;
        var add = new Button { Content = "Add", Margin = new Thickness(4, 4, 0, 4), MinWidth = 54 };
        add.Click += (_, _) =>
        {
            if (!valid || !picker.TryGetValue(out var selected)) return;
            draft.Set(table.Section, selected.Id, rate);
            refreshRows();
            picker.Refresh();
        };
        add.Name = table.Section + "Add";
        var number = draft.Editor(Number(rate, Minimum, DeckSettings.Maximum, value => rate = value, error =>
        {
            valid = error == null;
            add.IsEnabled = valid && picker.HasSelection;
        }));
        number.Name = table.Section + "NewValue";
        var valueRow = FormRow(table.ValueLabel, number, 140);
        var addRow = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        addRow.Children.Add(add);
        addRow.Children.Add(valueRow);
        var layout = Stack(FormRow("Search by name or ID", search, 140), picker.Results, addRow);
        layout.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(layout);
        picker.SelectionChanged += () => add.IsEnabled = valid && picker.HasSelection;
        add.IsEnabled = picker.HasSelection;
        return picker.Refresh;

        IEnumerable<ChoiceItem> FindChoices(string query)
        {
            var used = new HashSet<string>(draft.Values.Entries(table.Section).Select(entry => entry.Key), StringComparer.OrdinalIgnoreCase);
            return catalog.Choices(table.Kind)
                .Where(item => !used.Contains(item.Id) && (item.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
                    || item.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
        }
    }

    private class Catalog
    {
        private readonly Dictionary<Kind, ChoiceItem[]> choices = new();
        private readonly Dictionary<(Kind, string), string> names = new();
        private Dictionary<string, string>? nations;
        private IniHandler? aircraftNames;
        private static string Language => Singleton<LanguageResourceHandler>.Instance.Language;

        internal string Name(Kind kind, string id)
        {
            if (names.TryGetValue((kind, id), out string name)) return name;
            name = kind switch
            {
                Kind.Weapon => PreparationCatalog.WeaponName(id),
                Kind.Aircraft => AircraftName(id),
                Kind.Nation => Nations().GetValueOrDefault(id) ?? id,
                _ => id,
            };
            return names[(kind, id)] = name;
        }

        internal ChoiceItem[] Choices(Kind kind)
        {
            if (choices.TryGetValue(kind, out var cached)) return cached;
            IEnumerable<string> ids = kind == Kind.Nation ? Nations().Keys
                : FileManager.Instance.GetFiles(kind == Kind.Weapon ? "ammunition" : "aircraft", "*.ini")
                    .Select(file => Path.GetFileNameWithoutExtension(file))
                    .Where(id => !id.EndsWith("_squadrons", StringComparison.OrdinalIgnoreCase)
                        && !id.EndsWith("_variants", StringComparison.OrdinalIgnoreCase)
                        && id is not ("shared_settings" or "test"));
            var items = ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => new ChoiceItem(id, Name(kind, id))).ToArray();
            var repeatedNames = items.GroupBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            return choices[kind] = items.Select(item => repeatedNames.Contains(item.Name) ? new ChoiceItem(item.Id, $"{item.Name} ({item.Id})") : item)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private string AircraftName(string id)
        {
            if (Singleton<UnitMetadataProvider>.InstanceExists()
                && Singleton<UnitMetadataProvider>.Instance.UnitData is { } units && units.TryGetValue(("aircraft", id), out var entry)
                && !string.IsNullOrWhiteSpace(entry.Item2.DefaultName))
                return entry.Item2.DefaultName;
            aircraftNames ??= LocalizationLoader.OpenVirtualLocalizationFile($"language_{Language}", "aircraft_names", "aircraft_names");
            return aircraftNames != null && aircraftNames.Data.TryGetValue(id, out var aircraft)
                && aircraft.TryGetValue("Default", out string name) ? name.Split(',')[0] : id;
        }

        private Dictionary<string, string> Nations()
        {
            if (nations != null) return nations;
            nations = new(StringComparer.OrdinalIgnoreCase);
            Add("en");
            if (Language != "en") Add(Language);
            return nations;

            void Add(string language)
            {
                var ini = LocalizationLoader.OpenVirtualLocalizationFile($"language_{language}", "nations", "nations");
                if (ini == null) return;
                foreach (var entry in ini.GetSectionKeyValues("Nations"))
                    if (entry.Key != "Undefined" && !string.IsNullOrWhiteSpace(entry.Value)) nations[entry.Key] = entry.Value;
            }
        }
    }
}
