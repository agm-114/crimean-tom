using System.Globalization;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static class VesselCombatProfileReader
{
    private static readonly Dictionary<string, VesselCombatProfile?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static VesselCombatProfile? Read(string unitType)
    {
        if (Cache.TryGetValue(unitType, out VesselCombatProfile? cached))
        {
            return cached;
        }

        VesselCombatProfile? profile = ReadFromDisk(unitType);
        Cache[unitType] = profile;
        return profile;
    }

    private static VesselCombatProfile? ReadFromDisk(string unitType)
    {
        string path = new[] { "vessels", "submarines" }
            .Select(folder => Path.Combine(
                Application.streamingAssetsPath,
                "original",
                folder,
                $"{unitType}.ini"))
            .FirstOrDefault(File.Exists) ?? string.Empty;
        if (!File.Exists(path))
        {
            return null;
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        bool inAiSection = false;
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                inAiSection = string.Equals(line, "[AI]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inAiSection)
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        int unitCost = ReadInt(values, "UnitCostValue");
        int surface = ReadInt(values, "ASuW_Capability");
        int airDefense = ReadInt(values, "AAW_Capability");
        int asw = ReadInt(values, "ASW_Capability");
        double resilience = ReadDouble(values, "MissilesToSaturate");
        bool isCarrier = values.TryGetValue("Role", out string roles) &&
            roles.Split(',').Any(role =>
                role.Trim().Equals("Carrier", StringComparison.OrdinalIgnoreCase) ||
                role.Trim().Equals("CVS", StringComparison.OrdinalIgnoreCase) ||
                role.Trim().Equals("CVL", StringComparison.OrdinalIgnoreCase));
        if (unitCost <= 0 && surface + airDefense + asw <= 0)
        {
            return null;
        }

        // UnitCostValue is an AI targeting value rather than a campaign price.
        // Keeping it on a roughly 1-100 scale makes generated totals readable
        // while preserving the relative value authored by the game designers.
        int approximatePointCost = Math.Max(1, (int)Math.Round(unitCost / 250d));
        return new VesselCombatProfile(
            approximatePointCost,
            surface,
            airDefense,
            asw,
            Math.Max(1d, resilience),
            isCarrier);
    }

    private static string StripComment(string line)
    {
        int slashComment = line.IndexOf("//", StringComparison.Ordinal);
        int hashComment = line.IndexOf('#');
        int comment = slashComment switch
        {
            < 0 => hashComment,
            _ when hashComment < 0 => slashComment,
            _ => Math.Min(slashComment, hashComment),
        };
        return comment < 0 ? line : line[..comment];
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Max(0, parsed)
            : 0;

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string value) &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? Math.Max(0d, parsed)
            : 0d;
}

internal sealed class VesselCombatProfile(
    int approximatePointCost,
    int surfaceCapability,
    int airDefenseCapability,
    int aswCapability,
    double resilience,
    bool isCarrier)
{
    internal int ApproximatePointCost { get; } = approximatePointCost;

    internal int SurfaceCapability { get; } = surfaceCapability;

    internal int AirDefenseCapability { get; } = airDefenseCapability;

    internal int AswCapability { get; } = aswCapability;

    internal double Resilience { get; } = resilience;

    internal bool IsCarrier { get; } = isCarrier;
}