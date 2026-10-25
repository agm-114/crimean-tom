using System.Text.Json;

namespace NuclearOption.ScenarioEditor.Core;

public record MergedUnit(string Id, string Name, string BDF, string PALA)
{
    public string BdfName => UnitCatalog.GetRaw(BDF).Name;
    public string PalaName => UnitCatalog.GetRaw(PALA).Name;
    public string Category => UnitCatalog.GetRaw(BDF).Category;
    public string Code => string.Join(" / ", new[] { UnitCatalog.GetRaw(BDF).Code, UnitCatalog.GetRaw(PALA).Code }.Distinct());

    public string VariantFor(string faction) => faction switch
    {
        "Boscali" => BDF, "Primeva" => PALA,
        _ => throw new ArgumentException($"Merged unit '{Name}' has no variant for faction '{FactionUsage.DisplayName(faction)}'.")
    };
}

public class MergedUnits : IJsonData<MergedUnits>
{
    public List<MergedUnit> Units { get; set; } = [];

    public void Set(string? id, string name, string bdf, string pala)
    {
        id ??= "merged:" + Guid.NewGuid().ToString("N");
        var unit = new MergedUnit(id, name.Trim(), bdf, pala);
        var index = Units.FindIndex(u => u.Id == id);
        if (index < 0) Units.Add(unit);
        else Units[index] = unit;
        Validate();
    }

    public string Serialize()
    {
        Validate();
        // Persist only the editable fields; labels and categories come from the catalogue.
        return JsonSerializer.Serialize(new { Units = Units.Select(u => new { u.Id, u.Name, u.BDF, u.PALA }) },
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    public static MergedUnits Parse(string json)
    {
        var data = JsonSerializer.Deserialize<MergedUnits>(json) ?? throw new JsonException("Expected a merged-units object.");
        data.Validate();
        return data;
    }

    public void Validate()
    {
        if (Units is null || Units.Any(u => u is null)) throw new JsonException("Units must be an array of merged units.");
        if (Units.GroupBy(u => u.Id, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            Units.GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new ArgumentException("Merged unit IDs and names must be unique.");
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in Units)
        {
            if (string.IsNullOrWhiteSpace(unit.Id) || !unit.Id.StartsWith("merged:", StringComparison.Ordinal) || unit.Id.Length <= 7 ||
                unit.Id != unit.Id.Trim() || unit.Id.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(unit.Name) || unit.Name != unit.Name.Trim() || unit.Name.Any(char.IsControl))
                throw new ArgumentException("Each merged unit needs a stable 'merged:' ID and a nonempty name without surrounding spaces.");
            if (UnitCatalog.RawUnits.Any(u => u.Name.Equals(unit.Name, StringComparison.OrdinalIgnoreCase) || u.Id == unit.Name))
                throw new ArgumentException($"Merged name '{unit.Name}' conflicts with an existing unit name or ID.");
            if (Units.Any(u => u.Id == unit.Name)) throw new ArgumentException("Merged names must not duplicate group IDs.");
            foreach (var id in new[] { unit.BDF, unit.PALA })
            {
                if (!UnitCatalog.RawUnits.Any(u => u.Id == id && u.Category is "Vehicle" or "Aircraft" or "Ship"))
                    throw new ArgumentException($"'{id}' is not a known ground, air or ship variant.");
                if (!members.Add(id)) throw new ArgumentException($"Variant '{id}' belongs to more than one merged entry or faction.");
            }
            if (UnitCatalog.GetRaw(unit.BDF).Category != UnitCatalog.GetRaw(unit.PALA).Category)
                throw new ArgumentException($"The variants of '{unit.Name}' must have the same category.");
        }
    }
}
