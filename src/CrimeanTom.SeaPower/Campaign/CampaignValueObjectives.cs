using System.Globalization;

namespace CrimeanTom.SeaPower;

internal static class CampaignValueObjectives
{
    internal static void Apply(Dictionary<string, Dictionary<string, string>> data,
        IReadOnlyDictionary<string, int> values, int seed)
    {
        int next = int.Parse(data["Mission"]["NumberOfTriggers"], CultureInfo.InvariantCulture);
        if (!data.TryGetValue("CampaignVariables", out var variables))
            data["CampaignVariables"] = variables = new(StringComparer.OrdinalIgnoreCase);
        foreach (string side in new[] { "Taskforce1", "Taskforce2" })
        {
            var units = values.Where(pair => pair.Key.StartsWith(side, StringComparison.Ordinal)).ToArray();
            if (units.Length == 0) throw new InvalidOperationException("A fleet-clash side has no valued hulls.");
            string counter = $"CT_LostValue_{seed}_{side}";
            variables[counter] = "0";
            var init = NewTrigger(data, ++next, $"Reset {side} loss value");
            init["Condition_Condition1_Type"] = "OnMissionStart";
            init["Action_VariableSet"] = counter + ",0";
            foreach (var unit in units)
            {
                var trigger = NewTrigger(data, ++next, "Record loss of " + unit.Key);
                trigger["Condition_Condition1_Type"] = "UnitDestroyed";
                trigger["Condition_Condition1_Units"] = unit.Key;
                trigger["Condition_Condition1_MinimumUnits"] = "1";
                trigger["Action_VariableAdd"] = counter + "," + Math.Max(1, unit.Value).ToString(CultureInfo.InvariantCulture);
            }
            int threshold = (int)Math.Ceiling(units.Sum(unit => Math.Max(1, unit.Value)) * 0.75d);
            var outcome = data[side == "Taskforce1" ? "Trigger2" : "Trigger3"];
            outcome.Remove("Condition_Condition1_Units");
            outcome.Remove("Condition_Condition1_MinimumUnits");
            outcome["Condition_Condition1_Type"] = "VariableCheck";
            outcome["Condition_Condition1_Variable"] = counter;
            outcome["Condition_Condition1_CheckType"] = "NumberGreaterThan";
            outcome["Condition_Condition1_CompareTo"] = (threshold - 1).ToString(CultureInfo.InvariantCulture);
            string winner = side == "Taskforce1" ? "Taskforce2" : "Taskforce1";
            data["Language_en"]["Objective_" + winner + "BreakEnemy"] =
                $"Destroy 75% of enemy ship value, at least {threshold} undiscounted points";
            data["Language_en"]["Objective_" + side + "PreserveForce"] =
                $"Keep lost ship value below {threshold} undiscounted points";
        }
        data["Mission"]["NumberOfTriggers"] = next.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> NewTrigger(
        Dictionary<string, Dictionary<string, string>> data, int index, string name)
    {
        var trigger = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = name,
            ["ConditionsCompleted"] = "<Condition1>",
        };
        data["Trigger" + index] = trigger;
        return trigger;
    }
}