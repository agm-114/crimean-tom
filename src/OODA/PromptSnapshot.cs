using Newtonsoft.Json.Linq;

namespace OODA;

internal static class PromptSnapshot
{
    internal static JObject Compact(JObject input, DecisionRole role)
    {
        JObject snapshot = (JObject)input.DeepClone();
        CollapseCivilianTraffic(snapshot);
        CompactEngagementOptions(snapshot);
        CompactSensors(snapshot);
        if (snapshot["availableTools"] is JArray tools)
            snapshot["availableTools"] = new JObject(tools.OfType<JObject>().Where(tool => (string?)tool["name"] != null)
                .Select(tool => new JProperty((string)tool["name"]!, tool["requestField"])));
        if (snapshot["timeInterrupts"] is JObject time) Limit(time, "recentSpeedChanges", 4);
        if (role == DecisionRole.AirDefense)
        {
            snapshot.Remove("nearbySurfaceThreats");
            snapshot.Remove("ownWeaponFire");
            snapshot.Remove("ownMissileTargetsInFlight");
        }
        StripEmpty(snapshot);
        return snapshot;
    }

    private static void CollapseCivilianTraffic(JObject snapshot)
    {
        if (snapshot["contacts"] is not JArray contacts) return;
        HashSet<string> relevant = new(StringComparer.Ordinal);
        foreach (string field in new[] { "protectedUnits", "helicopterScreenAnchors", "observedGunfire", "nearbySurfaceThreats",
            "lastOrderResults", "conditionalOrders", "conditionalAttacks", "orderStatuses" })
            foreach (JObject entry in snapshot[field]?.OfType<JObject>() ?? [])
                foreach (string key in new[] { "id", "targetId", "conditionId", "victimId", "attackerId" })
                    if ((string?)entry[key] is { } id) relevant.Add(id);
        foreach (string field in new[] { "ownWeaponFire", "airWeaponFire" })
            foreach (JObject entry in snapshot[field]?.OfType<JObject>() ?? [])
                if ((string?)entry["targetId"] is { } id) relevant.Add(id);
        string[] reports = (snapshot["missionMessages"]?.OfType<JObject>() ?? []).Select(report =>
            $"{report["title"]}\n{report["from"]}\n{report["body"]}").Append((string?)snapshot["briefing"] ?? "").ToArray();

        JObject counts = new();
        foreach (JObject track in contacts.OfType<JObject>().ToArray())
        {
            if ((string?)track["posture"] != "Neutral" || (bool?)track["identified"] != true
                || (string?)track["observedClass"] is not { } observedClass || !observedClass.StartsWith("civ_", StringComparison.OrdinalIgnoreCase)
                || (string?)track["id"] is not { } id || relevant.Contains(id)) continue;
            string? name = (string?)track["displayLabel"];
            if (name is { Length: >= 4 } && reports.Any(report => report.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            string category = (string?)track["category"] ?? "Other";
            counts[category] = ((int?)counts[category] ?? 0) + 1;
            track.Remove();
        }
        if (counts.HasValues) snapshot["identifiedCivilianTraffic"] = counts;
    }

    private static void CompactEngagementOptions(JObject snapshot)
    {
        foreach (JObject target in snapshot["engagementOptions"]?.OfType<JObject>() ?? [])
            foreach (JObject shooter in target["shooters"]?.OfType<JObject>() ?? [])
            {
                if (shooter["weapons"] is not JArray weapons || weapons.First is not JObject first) continue;
                foreach (string shared in new[] { "rangeNm", "weaponPosture", "committed" })
                {
                    shooter[shared] = first[shared];
                    foreach (JObject weapon in weapons.OfType<JObject>()) weapon.Remove(shared);
                }
            }
    }

    private static void CompactSensors(JObject snapshot)
    {
        if (snapshot["sensorOptions"] is not JArray units) return;
        foreach (JObject unit in units.OfType<JObject>())
        {
            if (unit["sensors"] is not JArray sensors) continue;
            unit["sensors"] = new JArray(sensors.OfType<JObject>().GroupBy(sensor => (string?)sensor["id"]).Select(group =>
            {
                JObject sensor = (JObject)group.First().DeepClone();
                sensor["on"] = group.Any(item => (bool?)item["on"] == true);
                return sensor;
            }));
        }
    }

    private static void Limit(JObject owner, string key, int count)
    {
        if (owner[key] is JArray rows && rows.Count > count) owner[key] = new JArray(rows.Take(count));
    }

    private static void StripEmpty(JToken item)
    {
        if (item is JObject obj)
        {
            foreach (JProperty property in obj.Properties().ToArray())
            {
                StripEmpty(property.Value);
                if (property.Value.Type == JTokenType.Null || property.Value is JContainer { HasValues: false }) property.Remove();
            }
        }
        else if (item is JArray items)
            foreach (JToken child in items) StripEmpty(child);
    }
}
