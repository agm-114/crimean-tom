using System.Collections.ObjectModel;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaCatalogue
{
    private class Selection(ObjectInformationViewModel model, string reference)
    {
        internal WeakReference<ObjectInformationViewModel> Model { get; } = new(model);
        internal string Reference { get; set; } = reference;
    }
    private static readonly List<Selection> waiting = [];
    private static readonly FieldInfo selected = AccessTools.Field(typeof(ObjectInformationViewModel), "_selectedMuseumEntry");
    private static readonly MethodInfo notify = AccessTools.Method(typeof(ObjectInformationViewModel), "NotifyPropertyChanged");
    private static readonly Type managerType = AccessTools.TypeByName("SeaPower.EncyclopedicDataManager");
    private static readonly PropertyInfo managerInstance = AccessTools.Property(typeof(Singleton<>).MakeGenericType(managerType), "Instance");
    private static readonly PropertyInfo unitsLoaded = AccessTools.Property(managerType, "UnitsLoaded");

    [HarmonyPrefix, HarmonyPatch(typeof(UnitTreeBrowserViewModel), "CloneMuseumEntries")]
    private static void Catalogue(ref ObservableCollection<MuseumEntry> source)
    {
        // The native completion event refreshes browsers before our data lane finishes.
        if (Quickload.Instance.DataReady || UnitCatalogue.Entries.Count == 0 || unitsLoaded.GetValue(managerInstance.GetValue(null)) is true) return;
        // Keep the shared browser's cloning and visibility filters for cached rows too.
        source = new(UnitCatalogue.Entries.GroupBy(row => row.Category).Select(group => new MuseumEntry
        {
            DisplayName = group.Key, MuseumEntries = new(group.Select(row => (MuseumEntry)new MuseumObjectEntry
            {
                DisplayName = row.Name, DisplayClassName = row.Name, IniRef = row.Reference, Nation = row.Nations,
                MuseumEntries = [], SensorEntries = [], WeaponEntries = [], AircraftEntries = [],
            })),
        }));
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ObjectInformationViewModel), nameof(ObjectInformationViewModel.SelectedMuseumEntry), MethodType.Setter)]
    private static bool Select(ObjectInformationViewModel __instance, ref MuseumEntry value)
    {
        if (value == null || string.IsNullOrEmpty(value.IniRef)) return true;
        string reference = value.IniRef;
        var row = UnitCatalogue.Entries.FirstOrDefault(row => row.Reference == reference);
        if (row == null) return true;
        waiting.RemoveAll(item => !item.Model.TryGetTarget(out var model) || ReferenceEquals(model, __instance));
        UnitCatalogue.Request(row);
        if (EncyclopediaPriority.Details.TryGetValue(row.Reference, out var entry))
        {
            // Native detail rows are authoritative; cached rows are only navigation labels.
            if (!Quickload.Instance.DataReady) value = entry;
            return true;
        }
        if (Quickload.Instance.DataReady) return true;
        waiting.Add(new(__instance, row.Reference));
        selected.SetValue(__instance, null);
        __instance.InfoBoxContent = null;
        notify.Invoke(__instance, [nameof(ObjectInformationViewModel.SelectedMuseumEntry)]);
        return false;
    }

    internal static void Update()
    {
        foreach (var item in waiting.ToArray())
        {
            if (!item.Model.TryGetTarget(out var model)) { waiting.Remove(item); continue; }
            if (!EncyclopediaPriority.Details.TryGetValue(item.Reference, out var entry)) continue;
            waiting.Remove(item);
            model.SelectedMuseumEntry = entry;
        }
    }
}
