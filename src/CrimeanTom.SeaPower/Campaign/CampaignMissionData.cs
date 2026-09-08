using System.Text.RegularExpressions;

namespace CrimeanTom.SeaPower;

internal static class CampaignMissionData
{
    private const string Plan = "CrimeanTomPlan";
    private const string UnitPlan = "CrimeanTomPlan_";
    private const string NearbyPlan = "CrimeanTomNearby_";
    private static readonly string[] TacticalKeys =
    [
        "RelativePositionInNM", "Heading", "Telegraph", "Waypoints", "MissionType",
        "StationRole", "WeaponStatus", "OverrideWeaponStatus", "RadarsActive", "ExcludeFromAIRadarRoutine", "ActiveSonarsEnabled",
        "TowedArrayDeployed", "Morale", "RetreatAfterWeaponsExpended",
    ];

    internal static void ApplyStartingOrders(Dictionary<string, Dictionary<string, string>> data)
    {
        if (!data.TryGetValue("CrimeanTom", out var settings) ||
            !settings.TryGetValue("PreloadPlayerOrders", out string raw) || !bool.TryParse(raw, out bool preload) || preload) return;
        string side = data["Mission"]["PlayerTaskforce"];
        HashSet<string> allies = (settings.TryGetValue("AlliedAIUnits", out string ai) ? ai : string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(reference => reference.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Regex hulls = MissionUnitReferences.HullNamesFor(side);
        Regex aircraft = MissionUnitReferences.AirNamesFor(side);
        foreach (var unit in data.Where(section => !allies.Contains(section.Key) &&
            (hulls.IsMatch(section.Key) && section.Value.ContainsKey("CampaignTag") || aircraft.IsMatch(section.Key))))
        {
            unit.Value.Remove("Waypoints");
            unit.Value.Remove("MissionType");
            if (hulls.IsMatch(unit.Key)) unit.Value["Telegraph"] = "0";
        }
    }

    internal static void Remember(
        Dictionary<string, Dictionary<string, string>> data,
        IReadOnlyDictionary<string, string> originalUnitIds)
    {
        string side = data["Mission"]["PlayerTaskforce"];
        data[Plan] = data["Mission"]
            .Where(pair => pair.Key.StartsWith(side + "_Formation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var unit in originalUnitIds)
        {
            if (!data.TryGetValue(unit.Key, out var values))
                throw new InvalidOperationException($"Missing planned unit {unit.Key}.");
            data[UnitPlan + unit.Key] = new(values, StringComparer.OrdinalIgnoreCase)
            {
                ["InstanceId"] = unit.Value,
            };
        }
        // Native preparation preserves ignored vessels, but replaces every player-side submarine section.
        if (data.TryGetValue("CrimeanTom", out var settings) && settings.TryGetValue("NearbyAIUnits", out string nearby))
            foreach (string reference in nearby.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(reference => reference.StartsWith(side + "Submarine", StringComparison.OrdinalIgnoreCase)))
                data[NearbyPlan + reference] = new(data[reference], StringComparer.OrdinalIgnoreCase);
    }

    internal static void Apply(
        Dictionary<string, Dictionary<string, string>> data,
        Func<string, string> getGroup)
    {
        if (!data.TryGetValue(Plan, out var plan))
            throw new InvalidOperationException("The campaign mission has no saved tactical plan.");
        string side = data["Mission"]["PlayerTaskforce"];
        Regex hull = MissionUnitReferences.HullNamesFor(side);
        var actual = data.Where(section => hull.IsMatch(section.Key) &&
                section.Value.ContainsKey("CampaignTag"))
            .ToDictionary(section => section.Value["CampaignTag"], section => section.Key,
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> mapping = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> ids = new(StringComparer.OrdinalIgnoreCase);
        var unitPlans = data.Where(section => section.Key.StartsWith(UnitPlan, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var unit in unitPlans)
        {
            string original = unit.Key[UnitPlan.Length..];
            string id = unit.Value["InstanceId"];
            if (!actual.TryGetValue(id, out var reference))
                throw new InvalidOperationException($"Persistent unit {id} was not inserted into the mission.");
            mapping.Add(original, reference);
            ids.Add(reference, id);
            foreach (string key in TacticalKeys)
            {
                if (unit.Value.TryGetValue(key, out var value)) data[reference][key] = value;
                else data[reference].Remove(key);
            }
        }

        string submarineCount = "NumberOf" + side + "Submarines";
        foreach (var nearby in data.Where(section => section.Key.StartsWith(NearbyPlan, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            int index = int.Parse(data["Mission"].GetValueOrDefault(submarineCount, "0"), System.Globalization.CultureInfo.InvariantCulture) + 1;
            string reference = side + "Submarine" + index;
            data[reference] = new(nearby.Value, StringComparer.OrdinalIgnoreCase);
            data["Mission"][submarineCount] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            mapping.Add(nearby.Key[NearbyPlan.Length..], reference);
        }

        string Remap(string value) => MissionUnitReferences.RemapHulls(value, mapping);

        // Native formations already contain final hull identifiers. Do not remap them a second time.
        List<string> formations = [];
        foreach (var pair in data["Mission"].Where(pair =>
                     pair.Key.StartsWith(side + "_Formation", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            string[] fields = pair.Value.Split('|');
            string[] remaining = fields[0].Split(',').Where(reference => !ids.ContainsKey(reference.Trim())).ToArray();
            if (remaining.Length > 0)
            {
                fields[0] = string.Join(",", remaining);
                formations.Add(string.Join("|", fields));
            }
            data["Mission"].Remove(pair.Key);
        }
        foreach (string formation in plan.Values)
        {
            string[] fields = formation.Split('|');
            string[] planned = fields[0].Split(',').Select(reference => reference.Trim())
                .Where(mapping.ContainsKey).ToArray();
            if (planned.Length == 0) continue;
            var leader = data[UnitPlan + planned[0]];
            foreach (var group in planned.GroupBy(reference => getGroup(ids[mapping[reference]])))
            {
                string[] members = group.Select(reference => mapping[reference]).ToArray();
                string[] grouped = (string[])fields.Clone();
                grouped[0] = string.Join(",", members);
                grouped[1] = group.Key + " / " + fields[1];
                formations.Add(string.Join("|", grouped));
                if (!data[members[0]].ContainsKey("Waypoints") && leader.TryGetValue("Waypoints", out var route))
                    data[members[0]]["Waypoints"] = route;
            }
        }
        data["Mission"][side + "_NumberOfFormations"] = formations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        formations.ForEach((formation, index) => data["Mission"][side + "_Formation" + (index + 1)] = formation);

        data.Where(section =>
                section.Key.StartsWith("Trigger", StringComparison.OrdinalIgnoreCase) ||
                section.Key.Equals("CrimeanTom", StringComparison.OrdinalIgnoreCase))
            .ForEach(section => section.Value.Keys.ToArray()
                .ForEach(key => section.Value[key] = Remap(section.Value[key])));
        Regex aircraft = MissionUnitReferences.AirNamesFor(side);
        foreach (var section in data.Where(section => aircraft.IsMatch(section.Key)))
            if (section.Value.TryGetValue("HomeBase", out var homeBase)) section.Value["HomeBase"] = Remap(homeBase);

        foreach (var trigger in data.Where(section => section.Key.StartsWith("Trigger", StringComparison.OrdinalIgnoreCase)))
            foreach (var condition in trigger.Value.Where(pair => pair.Key.EndsWith("_Units", StringComparison.OrdinalIgnoreCase)))
                foreach (string reference in MissionUnitReferences.FindHulls(condition.Value))
                    if (!data.ContainsKey(reference))
                        throw new InvalidOperationException($"Objective references missing unit {reference}.");
    }
}
