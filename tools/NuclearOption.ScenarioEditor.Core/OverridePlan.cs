using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuclearOption.ScenarioEditor.Core;

public record UnitSubstitution(string From, string To,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Disabled = false)
{
    [JsonIgnore] public string FromName => UnitCatalog.Name(From);
    [JsonIgnore] public string ToName => UnitCatalog.Name(To);
    [JsonIgnore] public string Code => UnitCatalog.Get(From).Code;
}

public class OverridePlan : IJsonData<OverridePlan>
{
    public List<UnitSubstitution> Substitutions { get; set; } = [];

    public void SetSubstitution(string from, string to)
    {
        from = UnitCatalog.CanonicalId(from);
        to = UnitCatalog.CanonicalId(to);
        ValidateId(from);
        ValidateId(to);
        if (from == to) throw new ArgumentException("Choose a different replacement unit.");
        var index = Substitutions.FindIndex(r => UnitCatalog.CanonicalId(r.From) == from);
        var rule = new UnitSubstitution(from, to);
        if (index < 0) Substitutions.Add(rule);
        else Substitutions[index] = rule;
    }

    public MissionDocument Apply(string sourceJson)
    {
        Validate();
        var document = new MissionDocument(sourceJson);
        foreach (var rule in Substitutions.Where(r => !r.Disabled))
            document.ReplaceGlobally(rule.From, rule.To);
        return document;
    }

    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    public static OverridePlan Parse(string json)
    {
        var plan = JsonSerializer.Deserialize<OverridePlan>(json) ?? throw new JsonException("Expected an overrides object.");
        plan.Validate();
        return plan;
    }

    private void Validate()
    {
        if (Substitutions is null || Substitutions.Any(r => r is null))
            throw new JsonException("Substitutions must be an array of override rules.");
        if (Substitutions.GroupBy(r => UnitCatalog.CanonicalId(r.From)).Any(g => g.Count() > 1))
            throw new ArgumentException("Overrides contain duplicate source units. Combine rules for variants that share a merged entry.");
        foreach (var rule in Substitutions)
        {
            ValidateId(rule.From);
            ValidateId(rule.To);
            if (UnitCatalog.CanonicalId(rule.From) == UnitCatalog.CanonicalId(rule.To))
                throw new ArgumentException("An override must use different source and replacement units.");
        }
    }

    private static void ValidateId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Override identifiers must be nonempty and have no surrounding spaces or control characters.");
        if (value.StartsWith("merged:", StringComparison.Ordinal) && !UnitCatalog.IsMerged(value))
            throw new ArgumentException($"Override references missing merged unit '{value}'. Remove or change its rule before deleting that group.");
    }
}
