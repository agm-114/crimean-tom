using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CrimeanTom.SeaPower;

internal static class CampaignMissionData
{
    private const string Plan = "CrimeanTomPlan";
    private const string UnitPlan = "CrimeanTomPlan_";
    private static readonly string[] TacticalKeys =
    [
        "RelativePositionInNM", "Heading", "Telegraph", "Waypoints", "MissionType",
        "StationRole", "WeaponStatus", "RadarsActive", "ActiveSonarsEnabled",
        "TowedArrayDeployed", "Morale", "RetreatAfterWeaponsExpended",
    ];

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
    }

    internal static void Apply(
        Dictionary<string, Dictionary<string, string>> data,
        Func<string, string> getGroup)
    {
        if (!data.TryGetValue(Plan, out var plan))
            throw new InvalidOperationException("The campaign mission has no saved tactical plan.");
        string side = data["Mission"]["PlayerTaskforce"];
        Regex hull = new("^" + Regex.Escape(side) + @"(?:Vessel|Submarine)\d+$", RegexOptions.IgnoreCase);
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

        Regex referencePattern = new(@"\bTaskforce[12](?:Vessel|Submarine)\d+\b", RegexOptions.IgnoreCase);
        string Remap(string value) => referencePattern.Replace(value,
            match => mapping.TryGetValue(match.Value, out var replacement) ? replacement : match.Value);

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
        for (int i = 0; i < formations.Count; i++) data["Mission"][side + "_Formation" + (i + 1)] = formations[i];

        foreach (var section in data.Where(section =>
                     section.Key.StartsWith("Trigger", StringComparison.OrdinalIgnoreCase) ||
                     section.Key.Equals("CrimeanTom", StringComparison.OrdinalIgnoreCase)))
            foreach (string key in section.Value.Keys.ToArray()) section.Value[key] = Remap(section.Value[key]);
        foreach (var section in data.Where(section =>
                     Regex.IsMatch(section.Key, "^" + Regex.Escape(side) + @"(?:Aircraft|Helicopter)\d+$")))
            if (section.Value.TryGetValue("HomeBase", out var homeBase)) section.Value["HomeBase"] = Remap(homeBase);

        foreach (var trigger in data.Where(section => section.Key.StartsWith("Trigger", StringComparison.OrdinalIgnoreCase)))
            foreach (var condition in trigger.Value.Where(pair => pair.Key.EndsWith("_Units", StringComparison.OrdinalIgnoreCase)))
                foreach (Match reference in referencePattern.Matches(condition.Value))
                    if (!data.ContainsKey(reference.Value))
                        throw new InvalidOperationException($"Objective references missing unit {reference.Value}.");
    }
}
