using System.Collections;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeapowerUI.ViewModels;

namespace SeaPower.GameBridge;

internal static class PlayerDoctrine
{
    private record Target(string Id, string Name, string Scope, string Tab, ObjectBase? Unit = null);
    private record Setting(string Id, ITacticalDoctrineItem Item)
    {
        internal Type Type => ValueProperty.PropertyType;
        internal PropertyInfo ValueProperty => Item.GetType().GetProperty("Value")!;
        internal object? Resolved => Item.GetType().GetProperty("ResolvedValue")?.GetValue(Item);
        internal object[] Options => Item.GetType().GetProperty("Options")?.GetValue(Item) is IEnumerable options
            ? options.Cast<object>().ToArray() : [];
        internal string Value => Convert.ToString(Resolved, CultureInfo.InvariantCulture) ?? "";
        internal bool Overridden => Item.IsOverrideable && Item.IsSet;
        internal (int Min, int Max) Bounds => Id.EndsWith(".RemoveEngageTaskAfter", StringComparison.Ordinal) ? (0, 86400) : (0, 5);

        internal JObject Describe()
        {
            JObject value = new()
            {
                ["id"] = Id, ["label"] = Item.Label, ["value"] = Value,
                ["canInherit"] = Item.IsOverrideable, ["overridden"] = Overridden
            };
            if (Type == typeof(bool)) value["options"] = new JArray("True", "False");
            else if (Type == typeof(int)) { value["min"] = Bounds.Min; value["max"] = Bounds.Max; }
            else value["options"] = new JArray(Options.Select(option => option.ToString()));
            return value;
        }

        internal object? Parse(string value)
        {
            if (Type == typeof(bool)) return bool.TryParse(value, out bool flag) ? flag : null;
            if (Type == typeof(int)) return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                && number >= Bounds.Min && number <= Bounds.Max ? number : null;
            return Options.FirstOrDefault(option => string.Equals(option.ToString(), value, StringComparison.OrdinalIgnoreCase));
        }

        internal string Set(string value, string? expected, bool? overridden)
        {
            if (expected != null && (Value != expected || Overridden != overridden)) return "Rejected · Standing Orders changed";
            if (value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
            {
                if (!Item.IsOverrideable) return "Rejected · setting does not inherit";
                if (!Overridden) return "Unchanged · already inherits";
                Item.ClearOverride();
            }
            else
            {
                object? parsed = Parse(value);
                if (parsed == null) return "Rejected · value is outside the native setting's choices";
                if (Equals(Resolved, parsed) && (!Item.IsOverrideable || Overridden)) return "Unchanged · value already set";
                if (Item.IsOverrideable) Item.GetType().GetProperty(nameof(Item.IsSet))!.SetValue(Item, true);
                ValueProperty.SetValue(Item, parsed);
            }
            return $"Applied · {Item.Label} = {Value}{(Item.IsOverrideable && !Overridden ? " (inherited)" : "")}";
        }
    }

    internal static JArray Read(Taskforce side, string? targetId = null)
    {
        using TacticalDoctrineViewModel view = new(side);
        try
        {
            return new JArray(Targets(side).Where(target => targetId == null || target.Id == targetId).Select(target => new JObject
            {
                ["targetId"] = target.Id, ["name"] = target.Name, ["scope"] = target.Scope,
                ["settings"] = new JArray(Settings(view, target).Select(setting => setting.Describe()))
            }));
        }
        finally { view.SetSelectedObject(null); }
    }

    internal static string Set(Taskforce side, string targetId, string settingId, string value, string? expected = null, bool? overridden = null)
    {
        if (PlayerOrders.Blocked || PlayerPicture.Side != side) return "Rejected · player orders are unavailable";
        Target? target = Targets(side).FirstOrDefault(candidate => candidate.Id == targetId);
        if (target == null) return "Rejected · target is outside the player task force";
        using TacticalDoctrineViewModel view = new(side);
        try
        {
            Setting? setting = Settings(view, target).FirstOrDefault(candidate => candidate.Id == settingId);
            return setting?.Set(value, expected, overridden) ?? "Rejected · setting is unavailable for this target";
        }
        finally { view.SetSelectedObject(null); }
    }

    private static IEnumerable<Target> Targets(Taskforce side)
    {
        yield return new("force", side._nameInMissionFile, "force", "Header_GlobalDoctrine");
        ObjectBase[] units = PlayerPicture.Units(side);
        foreach (var group in units.Where(unit => unit.Formation != null).GroupBy(unit => unit.Formation))
        {
            ObjectBase unit = group.FirstOrDefault(candidate => candidate == group.Key.LeaderStation?.UnitObject) ?? group.First();
            yield return new($"formation-{PlayerPicture.Id(unit)}", group.Key.Name, "formation", "Header_FormationDoctrine", unit);
        }
        foreach (ObjectBase unit in units) yield return new(PlayerPicture.Id(unit), unit.DisplayNameShort, "unit", "Header_UnitDoctrine", unit);
    }

    private static IEnumerable<Setting> Settings(TacticalDoctrineViewModel view, Target target)
    {
        view.SetSelectedObject(target.Unit);
        TacticalDoctrineTabViewModel? tab = view.Tabs.FirstOrDefault(tab => tab.HeaderKey == target.Tab && tab.IsVisible);
        if (tab == null) yield break;
        foreach (TacticalDoctrineCategoryViewModel category in tab.Items)
        foreach (ITacticalDoctrineItem item in category.Items)
            if (item.IsVisible && AccessTools.Field(item.GetType(), "_labelKey")?.GetValue(item) is string key)
                yield return new($"{category.IconKey}.{key}", item);
    }
}
