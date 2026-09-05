using static CrimeanTom.SeaPower.NativeCampaignAccess;
using System.Globalization;

namespace CrimeanTom.SeaPower;

internal static partial class CampaignOperationsState
{
    private const string TurnKey = "CrimeanTomTurn";
    private const string SalvageSharesKey = "CrimeanTomSalvageShares";
    private const string ActiveMissionKey = "CrimeanTomActiveMission";
    private const string PendingBattleKey = "CrimeanTomPendingBattle";
    private const string RecordedBattleKey = "CrimeanTomRecordedBattle";
    private const string MarketOffersKey = "CrimeanTomMarketOffers";
    private const string ClaimedSalvageKey = "CrimeanTomClaimedSalvage";
    private const string GroupAssignmentsKey = "CrimeanTomGroupAssignments";
    private const string FatigueKey = "CrimeanTomFatigue";
    private const string AutoresolveCounterKey = "CrimeanTomAutoresolveCounter";

    private static void ClearReusableMissionState(int missionIndex)
    {
        if (missionIndex < 1 || OpenCampaignState() is not { } stateIni)
        {
            return;
        }

        CampaignStateData.ClearMission(stateIni.Data, missionIndex);
        foreach (string key in new[]
                 {
                     "AppliedMissionIndexes",
                     "AppliedAircraftLossMissionIndexes",
                     "AppliedRewardedUnitsMissionIndexes",
                 })
        {
            int[] remaining = stateIni.readValue(
                    "TaskForceMode_Record",
                    key,
                    string.Empty,
                    performLogging: false)
                .Split(['|', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out int parsed) ? parsed : -1)
                .Where(value => value >= 0 && value != missionIndex)
                .ToArray();
            stateIni.writeValue("TaskForceMode_Record", key, string.Join("|", remaining));
        }
        stateIni.saveFile(overwrite: true);
    }

    private static string DebriefSection(int missionIndex) =>
        $"Mission_{missionIndex}_TaskForceDebrief";

    private static IEnumerable<string> SplitRows(string value) =>
        value.Split(['|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Trim())
            .Where(row => row.Length > 0);

    private static HashSet<string> ReadSet(string key) =>
        SplitRows(Read(key)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> ReadMap(string key)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (string row in SplitRows(Read(key)))
        {
            int separator = row.IndexOf(':');
            if (separator > 0 && separator < row.Length - 1)
            {
                map[row[..separator]] = row[(separator + 1)..];
            }
        }
        return map;
    }

    private static void WriteMap(string key, IReadOnlyDictionary<string, string> map) =>
        Write(key, string.Join("|", map.Select(pair => $"{pair.Key}:{pair.Value}")));

    private static string Read(string key)
    {
        Dictionary<string, string>? data = GetPersistentData();
        return data is not null && data.TryGetValue(key, out string value)
            ? value
            : string.Empty;
    }

    private static int ReadInt(string key, int fallback) =>
        int.TryParse(
            Read(key),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value)
                ? value
                : fallback;

    private static void Write(string key, string value)
    {
        if (GetPersistentData() is { } data)
        {
            data[key] = value;
        }
    }

    private static void Write<T>(string key, T value) where T : IFormattable =>
        Write(key, value.ToString(null, CultureInfo.InvariantCulture));

    private static int StableHash(string value)
    {
        unchecked
        {
            int hash = 17;
            foreach (char character in value)
            {
                hash = (hash * 31) + character;
            }
            return hash;
        }
    }
}