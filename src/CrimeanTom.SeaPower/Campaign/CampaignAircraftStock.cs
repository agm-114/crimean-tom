using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using HarmonyLib;
using SeaPower;

namespace CrimeanTom.SeaPower;

internal static class CampaignAircraftStock
{
    // Filter authored patrols against the native launch roster; the loader deducts actual aircraft and ammunition.
    internal static void TrimPatrols(Dictionary<string, Dictionary<string, string>> data)
    {
        // Standalone missions store only the aircraft left aboard. Campaign loading deducts placed
        // aircraft itself, so provide their reserved stock before native reconciliation runs.
        foreach (var group in data.Values.Where(unit => unit.GetValueOrDefault("CrimeanTomIndependentAircraft") == "True" &&
            unit.ContainsKey("HomeBase")).GroupBy(unit => (Host: unit["HomeBase"], Type: unit["Type"])))
            if (data.TryGetValue(group.Key.Host, out var field))
                field[group.Key.Type] = string.Join('|', group.GroupBy(unit => unit["SquadronReference"])
                    .Select(squadron => $"{squadron.Key},{squadron.Count()}"));
        string side = data["Mission"]["PlayerTaskforce"];
        Regex aircraft = MissionUnitReferences.AirNamesFor(side);
        Dictionary<(string Host, string Type, string Squadron), int> reserved = [];
        HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in data.Where(section => aircraft.IsMatch(section.Key)).ToArray())
        {
            if (unit.Value.TryGetValue("CrimeanTomIndependentAircraft", out string independent) && independent == "True") continue;
            if (unit.Value.ContainsKey("TaskForceModeAirTaskingRole") || unit.Value.ContainsKey("TaskForceModeAirTaskingSlot")) continue;
            string host = unit.Value.TryGetValue("HomeBase", out string home) ? home : string.Empty;
            string type = unit.Value["Type"];
            string squadron = unit.Value.TryGetValue("SquadronReference", out string variant) ? variant : "Default";
            var key = (host, type, squadron);
            int count = 0;
            if (data.TryGetValue(host, out var ship) && ship.TryGetValue(type, out string roster))
            {
                count = roster.Split('|').Select(entry => entry.Split(','))
                    .Where(fields => fields.Length >= 2 && fields[0].Trim().Equals(squadron, StringComparison.OrdinalIgnoreCase))
                    .Sum(fields => int.TryParse(fields[1], out int available) ? Math.Max(0, available) : 0);
            }
            int used = reserved.TryGetValue(key, out int prior) ? prior : 0;
            if (used >= count) removed.Add(unit.Key);
            else reserved[key] = used + 1;
        }
        if (removed.Count == 0) return;
        removed.ForEach(reference => data.Remove(reference));
        List<string> formations = [];
        foreach (var formation in data["Mission"].Where(pair => pair.Key.StartsWith(side + "_Formation", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            string[] fields = formation.Value.Split('|');
            string[] references = fields[0].Split(',').Where(reference => !removed.Contains(reference.Trim())).ToArray();
            data["Mission"].Remove(formation.Key);
            if (references.Length == 0) continue;
            fields[0] = string.Join(',', references);
            formations.Add(string.Join('|', fields));
        }
        data["Mission"][side + "_NumberOfFormations"] = formations.Count.ToString(CultureInfo.InvariantCulture);
        formations.ForEach((formation, index) => data["Mission"][side + "_Formation" + (index + 1)] = formation);

        Dictionary<string, string> mapping = new(StringComparer.OrdinalIgnoreCase);
        foreach (string kind in new[] { "Aircraft", "Helicopter" })
        {
            string prefix = side + kind;
            string[] references = data.Keys.Where(reference => aircraft.IsMatch(reference) && reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(reference => int.Parse(reference[prefix.Length..], CultureInfo.InvariantCulture)).ToArray();
            references.ForEach((reference, index) => mapping[reference] = prefix + (index + 1));
            data["Mission"]["NumberOf" + side + (kind == "Aircraft" ? "Aircraft" : "Helicopters")] = references.Length.ToString(CultureInfo.InvariantCulture);
        }
        var sections = mapping.Select(pair => (Name: pair.Value, Values: data[pair.Key])).ToArray();
        mapping.Keys.ForEach(reference => data.Remove(reference));
        sections.ForEach(section => data[section.Name] = section.Values);
        Regex referencesInValue = new(@"\b" + Regex.Escape(side) + @"(?:Aircraft|Helicopter)\d+\b");
        foreach (var section in data.Values)
            section.Keys.ToArray().ForEach(key => section[key] = referencesInValue.Replace(section[key], match =>
                mapping.TryGetValue(match.Value, out string renamed) ? renamed : match.Value));
    }
}

// Reuse native stock/ammunition reconciliation on the first reusable contract too.
// Stock campaigns retain their original mission-index and restored-state guards.
[HarmonyPatch(typeof(SceneCreator), "ResolvePlacedCampaignAircraft")]
internal static class CampaignAircraftReconciliationPatch
{
    private static int MinimumMissionIndex(SceneCreator scene) => IsGeneratedContract(scene) ? 1 : 2;
    private static bool HasStock(bool restored, SceneCreator scene) => restored || IsGeneratedContract(scene);
    private static bool IsGeneratedContract(SceneCreator scene) =>
        scene.MissionIni?.doesKeyExist("CrimeanTom", "CampaignContract") == true;

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        int indexGuard = code.FindIndex(instruction => instruction.opcode == OpCodes.Ldfld &&
            instruction.operand is FieldInfo field && field.Name == "_index" && field.DeclaringType?.Name == "LinearCampaignMission");
        int stockGuard = code.FindIndex(instruction => instruction.opcode == OpCodes.Ldfld &&
            instruction.operand is FieldInfo field && field.Name == "_affectedByCampaignData");
        if (indexGuard < 0 || indexGuard + 1 >= code.Count || code[indexGuard + 1].opcode != OpCodes.Ldc_I4_2 || stockGuard < 0)
            throw new InvalidOperationException("Sea Power aircraft reconciliation changed; recheck the installed method before deploying.");
        for (int index = 0; index < code.Count; index++)
        {
            CodeInstruction instruction = code[index];
            if (index == indexGuard + 1)
            {
                instruction.opcode = OpCodes.Ldarg_0;
                instruction.operand = null;
                yield return instruction;
                yield return new(OpCodes.Call, AccessTools.Method(typeof(CampaignAircraftReconciliationPatch), nameof(MinimumMissionIndex)));
            }
            else
            {
                yield return instruction;
                if (index != stockGuard) continue;
                yield return new(OpCodes.Ldarg_0);
                yield return new(OpCodes.Call, AccessTools.Method(typeof(CampaignAircraftReconciliationPatch), nameof(HasStock)));
            }
        }
    }
}
