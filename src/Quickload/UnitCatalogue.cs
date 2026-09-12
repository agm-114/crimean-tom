using System.Collections.ObjectModel;
using HarmonyLib;
using SeaPower;

namespace Quickload;

[HarmonyPatch]
internal static class UnitCatalogue
{
    internal class Entry
    {
        public string Reference { get; set; } = "";
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Nations { get; set; } = "";
        public override string ToString() => $"{Name} | {Category} | {Nations}";
    }
    internal static ObservableCollection<Entry> Entries { get; } = [];
    internal static string? Requested { get; private set; }
    private static readonly Dictionary<string, SimpleObjectParameters> fresh = new(StringComparer.OrdinalIgnoreCase);
    private static bool active;
    internal static void Initialize()
    {
        foreach (var row in CatalogueStore.Read<List<Entry>>("units") ?? [])
            if (row.Folder is "aircraft" or "vessels" or "land_units" or "biologic"
                && Path.GetFileName(row.Reference) == row.Reference) Entries.Add(row);
        active = true;
    }
    internal static void Request(Entry row) => Requested = row.Reference;
    internal static bool IsReady(Entry row) => fresh.ContainsKey(row.Reference);

    [HarmonyPostfix, HarmonyPatch(typeof(UnitMetadataProvider), "GetIniNames")]
    private static void Prioritize(ref IEnumerable<string> __result)
    {
        if (active) __result = Prioritized(__result);
    }
    private static IEnumerable<string> Prioritized(IEnumerable<string> source)
    {
        var remaining = source.ToList();
        while (remaining.Count > 0)
        {
            int index = remaining.FindIndex(name => name.Equals(Requested, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = 0;
            string name = remaining[index];
            remaining.RemoveAt(index);
            yield return name;
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(SimpleObjectParameters), nameof(SimpleObjectParameters.LoadSimpleParameters))]
    private static void Loaded(string objectIniName, string objectIniFolder, SimpleObjectParameters __result)
    {
        if (!active || __result == null || Quickload.Instance.Progress?.Data.Current != "UnitMetadataProvider LoadUnitMetadata") return;
        if (__result._hiddenIn.Contains(ObjectBase.HiddenIn.Encyclopedia)) return;
        fresh[objectIniName] = __result;
        Entry row = new()
        {
            Reference = objectIniName, Folder = objectIniFolder, Name = __result.DefaultName ?? objectIniName,
            Category = __result._objectType.ToString(),
            Nations = string.Join(", ", __result._variants?.Values.Select(variant => variant.Nation).Distinct() ?? []),
        };
        int index = Entries.ToList().FindIndex(entry => entry.Reference == objectIniName);
        if (index < 0) Entries.Add(row);
        else Entries[index] = row;
    }

    internal static void Complete()
    {
        for (int index = Entries.Count - 1; index >= 0; index--)
            if (!fresh.ContainsKey(Entries[index].Reference)) Entries.RemoveAt(index);
        CatalogueStore.Write("units", Entries.ToList());
    }
}
