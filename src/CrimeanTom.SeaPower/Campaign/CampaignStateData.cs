namespace CrimeanTom.SeaPower;

// These operations use the native INI dictionaries so the same rules can be checked without Unity.
internal static class CampaignStateData
{
    internal static string UnitPrefix(string id) => "TaskForceMode_CrimeanTom_UnitState_" + id;

    internal static string KillSection(int mission, int index) =>
        $"Mission_{mission}_TaskForceDebrief_Kill_{index}";

    internal static bool Matches(string section, string prefix) =>
        section.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        (section.Length == prefix.Length || char.IsLetter(section[prefix.Length]));

    internal static void Capture(
        Dictionary<string, Dictionary<string, string>> data,
        int mission,
        IEnumerable<string> instanceIds)
    {
        foreach (string id in instanceIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            string source = $"Mission_{mission}_{id}";
            if (!data.ContainsKey(source))
            {
                source = $"TaskForceMode_Mission{mission}_PostState_{id}";
            }
            if (!data.ContainsKey(source)) continue;
            string target = UnitPrefix(id);
            data.Keys.Where(section => Matches(section, target)).ToArray()
                .ForEach(section => data.Remove(section));
            data.Where(section => Matches(section.Key, source)).ToArray()
                .ForEach(section => data[target + section.Key[source.Length..]] = new(section.Value, StringComparer.OrdinalIgnoreCase));
        }
    }

    internal static void ClearMission(Dictionary<string, Dictionary<string, string>> data, int mission)
    {
        string native = $"Mission_{mission}_";
        string snapshots = $"TaskForceMode_Mission{mission}_";
        data.Keys.Where(section =>
                section.StartsWith(native, StringComparison.OrdinalIgnoreCase) ||
                section.StartsWith(snapshots, StringComparison.OrdinalIgnoreCase)).ToArray()
            .ForEach(section => data.Remove(section));
    }
}