using static CrimeanTom.SeaPower.NativeCampaignAccess;
using System.Globalization;
using SeapowerUI.ViewModels;

namespace CrimeanTom.SeaPower;

internal static partial class CampaignOperationsState
{
    internal static string GetGroup(string instanceId)
    {
        Dictionary<string, string> groups = ReadMap(GroupAssignmentsKey);
        return groups.TryGetValue(instanceId, out string group) ? group : "Group 1";
    }

    internal static int GetFatigue(string instanceId)
    {
        Dictionary<string, string> fatigue = ReadMap(FatigueKey);
        return fatigue.TryGetValue(instanceId, out string raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int turns)
                ? Math.Max(0, turns)
                : 0;
    }

    internal static void AssignGroup(string instanceId, string group)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(group) ||
            string.Equals(GetGroup(instanceId), group, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dictionary<string, string> groups = ReadMap(GroupAssignmentsKey);
        groups[instanceId] = group;
        WriteMap(GroupAssignmentsKey, groups);
        SaveCampaign();
    }

    internal static LinearCampaignTaskForceSnapshot FilterFatiguedUnits(
        LinearCampaignTaskForceSnapshot snapshot)
    {
        if (!IsActiveCampaign || !snapshot.Items.Any(item =>
                !item.IsAirUnit &&
                (GetFatigue(item.InstanceId) > 0 || IsReserve(item.InstanceId))))
        {
            return snapshot;
        }

        string[] deployingShipIds = snapshot.Items
            .Where(item =>
                !item.IsAirUnit &&
                GetFatigue(item.InstanceId) <= 0 &&
                !IsReserve(item.InstanceId))
            .Select(item => item.InstanceId)
            .ToArray();
        HashSet<string> deploying = new(deployingShipIds, StringComparer.OrdinalIgnoreCase);
        LinearCampaignTaskForceBuilderRosterItem[] items = snapshot.Items
            .Where(item =>
                !item.IsAirUnit && deploying.Contains(item.InstanceId) ||
                item.IsAirUnit && (item.AircraftAssignments.Count == 0 ||
                    item.AircraftAssignments.Keys.Any(deploying.Contains)))
            .ToArray();
        return new LinearCampaignTaskForceSnapshot(
            snapshot.TaskForceName,
            items,
            snapshot.AvailablePoints,
            snapshot.CapPoints);
    }

    private static bool IsReserve(string instanceId) =>
        string.Equals(GetGroup(instanceId), "Reserve", StringComparison.OrdinalIgnoreCase);

    private static void MarkNewestMatchingUnitFatigued(
        string unitType,
        string variantReference,
        int turns,
        string group)
    {
        LinearCampaignTaskForceBuilderRosterItem? item = ReadCurrentSnapshot().Items
            .LastOrDefault(candidate =>
                !candidate.IsAirUnit &&
                string.Equals(candidate.UnitType, unitType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.VariantReference, variantReference, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        Dictionary<string, string> groups = ReadMap(GroupAssignmentsKey);
        groups[item.InstanceId] = group;
        WriteMap(GroupAssignmentsKey, groups);
        SetFatigue(item.InstanceId, turns);
    }

    private static void SetFatigue(string instanceId, int turns)
    {
        Dictionary<string, string> fatigue = ReadMap(FatigueKey);
        if (turns <= 0)
        {
            fatigue.Remove(instanceId);
        }
        else
        {
            fatigue[instanceId] = turns.ToString(CultureInfo.InvariantCulture);
        }
        WriteMap(FatigueKey, fatigue);
    }

    private static void DecrementFatigue()
    {
        Dictionary<string, string> fatigue = ReadMap(FatigueKey);
        foreach (string key in fatigue.Keys.ToArray())
        {
            int remaining = int.TryParse(
                    fatigue[key],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed)
                ? parsed - 1
                : 0;
            if (remaining <= 0)
            {
                fatigue.Remove(key);
            }
            else
            {
                fatigue[key] = remaining.ToString(CultureInfo.InvariantCulture);
            }
        }
        WriteMap(FatigueKey, fatigue);
    }
}