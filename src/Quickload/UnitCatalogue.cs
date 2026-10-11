using System.Collections.ObjectModel;
using SeaPower;

namespace Quickload;

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
        internal static Entry From(string reference, string folder, SimpleObjectParameters parameters) => new()
        {
            Reference = reference, Folder = folder, Name = parameters.DefaultName ?? reference,
            Category = parameters._objectType.ToString(),
            Nations = string.Join(", ", parameters._variants?.Values.Select(variant => variant.Nation).Distinct() ?? []),
        };
    }
    internal static ObservableCollection<Entry> Entries { get; } = [];
    internal static string? Requested { get; private set; }
    internal static void Initialize()
    {
        foreach (var row in CatalogueStore.Read<List<Entry>>("units") ?? [])
            if (row is { Folder: "aircraft" or "vessels" or "land_units" or "biologic" }
                && Path.GetFileName(row.Reference) == row.Reference) Entries.Add(row);
    }
    internal static void Request(Entry row) => Requested = row.Reference;

    internal static void Complete()
    {
        // The native loader finalizes UnitData on the main thread after its workers finish.
        Entries.Clear();
        foreach (var (key, value) in Singleton<UnitMetadataProvider>.Instance.UnitData.OrderBy(item => item.Value.Item1))
        {
            var parameters = value.Item2;
            if (!string.Equals(key.Item2, parameters._objectIniName, StringComparison.OrdinalIgnoreCase)
                || parameters._hiddenIn.Contains(ObjectBase.HiddenIn.Encyclopedia)) continue;
            Entries.Add(Entry.From(key.Item2, key.Item1, parameters));
        }
        CatalogueStore.Write("units", Entries.ToList());
    }
}
