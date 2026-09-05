using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;

using HarmonyLib;

using SeaPower;

using SeapowerUI.ViewModels;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal sealed record CampaignUnitOffer(
    string UnitType,
    string VariantReference,
    string Nation,
    string DisplayName,
    int FullPrice);

internal sealed record CampaignSalvageCandidate(
    string Key,
    string UnitFolder,
    string UnitType,
    string VariantReference,
    string DisplayName,
    int FullPrice,
    int SalvageCost);

internal static class CampaignOperationsState
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

    private static readonly MethodInfo? ReadSnapshotMethod = AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceSaveManager"),
        "ReadCurrentTaskForceSnapshot",
        Type.EmptyTypes);

    private static readonly MethodInfo? CommitPurchasesMethod = AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceSaveManager"),
        "CommitBuilderPurchases");

    private static readonly MethodInfo? OpenCampaignStateMethod = AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceModeRules"),
        "OpenCampaignStateIni");

    private static readonly Type? LinearCampaignType = AccessTools.TypeByName("SeaPower.LinearCampaign");

    private static readonly PropertyInfo? LinearCampaignInstanceProperty = LinearCampaignType is null
        ? null
        : AccessTools.Property(typeof(Singleton<>).MakeGenericType(LinearCampaignType), "Instance");

    internal static bool IsActiveCampaign
    {
        get
        {
            object? campaign = GetCampaign();
            string? path = campaign is null
                ? null
                : AccessTools.Field(campaign.GetType(), "currentCampaignFilePath")?.GetValue(campaign) as string;
            if (TaskForceNationRules.IsOurCampaignPath(path))
            {
                return true;
            }

            IniHandler? campaignIni = string.IsNullOrWhiteSpace(path)
                ? null
                : Utils.openIniFile(path, logErrors: false, useCaching: false);
            return TaskForceNationRules.IsOurCampaign(campaignIni);
        }
    }

    internal static int Turn => Math.Max(1, ReadInt(TurnKey, 1));

    internal static int SalvageShares => Math.Max(0, ReadInt(SalvageSharesKey, 0));

    internal static int ContractSeed
    {
        get
        {
            int seed = ReadInt("CrimeanTomContractSeed", 0);
            if (seed != 0) return seed;
            seed = Guid.NewGuid().GetHashCode();
            if (seed == 0) seed = 1;
            Write("CrimeanTomContractSeed", seed.ToString(CultureInfo.InvariantCulture));
            SaveCampaign();
            return seed;
        }
    }

    internal static bool InitialPurchasingClosed => IsActiveCampaign &&
        (Turn > 1 || !string.IsNullOrEmpty(Read("CrimeanTomFirstSortie")) ||
         !string.IsNullOrEmpty(Read(RecordedBattleKey)));

    internal static string? DebriefResult { get; set; }

    internal static bool BattleWon(int missionIndex) => CampaignContractRules.IsVictory(
        DebriefResult ?? OpenCampaignState()?.readValue(DebriefSection(missionIndex),
            "MissionResult", string.Empty, performLogging: false));

    internal static int GetMarketPrice(CampaignUnitOffer offer)
    {
        var data = GetPersistentData();
        if (data is null) return offer.FullPrice;
        data.TryGetValue("TaskForceCommanderNationOverride", out string selection);
        if (string.IsNullOrWhiteSpace(selection)) data.TryGetValue("TaskForceCommanderNation", out selection);
        return CampaignContractRules.MarketPrice(offer.FullPrice,
            TaskForceNationRules.GetCampaignDiscount(data, selection, offer.Nation));
    }

    internal static bool HasPendingBattle =>
        IsActiveCampaign && !string.IsNullOrWhiteSpace(Read(PendingBattleKey));

    internal static int PendingMissionIndex => ReadInt(ActiveMissionKey, 0);

    internal static void RecordLaunch(int missionIndex, string generatedMissionPath)
    {
        if (!IsActiveCampaign || missionIndex < 1)
        {
            return;
        }

        Write(ActiveMissionKey, missionIndex.ToString(CultureInfo.InvariantCulture));
        Write("CrimeanTomFirstSortie", bool.TrueString);
        SaveCampaign();
        Debug.Log(
            $"[Crimean Tom Operations] campaign turn {Turn} launched mission {missionIndex}: " +
            generatedMissionPath);
    }

    internal static int GetMissionIndex(object? mission) =>
        mission is null
            ? 0
            : AccessTools.Field(mission.GetType(), "_index")?.GetValue(mission) is int index
                ? index
                : 0;

    internal static void RecordBattle(object mission)
    {
        int missionIndex = GetMissionIndex(mission);
        RecordBattle(missionIndex);
    }

    private static void RecordBattle(int missionIndex)
    {
        if (!IsActiveCampaign || missionIndex < 1)
        {
            return;
        }

        IniHandler? stateIni = OpenCampaignState();
        if (stateIni is null)
        {
            return;
        }

        string section = DebriefSection(missionIndex);
        string completedAt = stateIni.readValue(
            section,
            "CompletedAt",
            string.Empty,
            performLogging: false);
        string battleId = $"{missionIndex}:{completedAt}";
        if (string.IsNullOrWhiteSpace(completedAt) ||
            string.Equals(Read(RecordedBattleKey), battleId, StringComparison.Ordinal))
        {
            return;
        }

        DecrementFatigue();
        LinearCampaignTaskForceSnapshot roster = ReadCurrentSnapshot();
        CampaignStateData.Capture(stateIni.Data, missionIndex,
            roster.Items.Where(item => !item.IsAirUnit).Select(item => item.InstanceId));
        stateIni.saveFile(overwrite: true);
        IReadOnlyList<CampaignSalvageCandidate> salvage = ReadSalvageCandidates(missionIndex);
        int award = CampaignContractRules.SalvageAward(BattleWon(missionIndex),
            CampaignContractOffers.Get(missionIndex).Payment);
        Write(SalvageSharesKey, (SalvageShares + award).ToString(CultureInfo.InvariantCulture));
        Write(ActiveMissionKey, missionIndex.ToString(CultureInfo.InvariantCulture));
        Write(PendingBattleKey, battleId);
        Write(RecordedBattleKey, battleId);
        Write(ClaimedSalvageKey, string.Empty);
        Write(MarketOffersKey, SerializeOffers(CreateMarketOffers(battleId)));
        SaveCampaign();
        Debug.Log(
            $"[Crimean Tom Operations] recorded battle {battleId}; " +
            $"awarded {award} salvage shares and found {salvage.Count} recoverable hulls");
    }

    internal static bool DebugAutoresolve(
        int missionIndex,
        bool playerVictory,
        out string message)
    {
        if (!IsActiveCampaign || missionIndex < 1)
        {
            message = "Open Crimean Tom Operations before using autoresolve.";
            return false;
        }

        if (HasPendingBattle)
        {
            message = "Finish the pending post-battle management phase first.";
            return false;
        }

        ClearReusableMissionState(missionIndex);
        IniHandler? stateIni = OpenCampaignState();
        if (stateIni is null)
        {
            message = "The active campaign state could not be opened.";
            return false;
        }

        int sequence = Math.Max(0, ReadInt(AutoresolveCounterKey, 0)) + 1;
        string completedAt = FormattableString.Invariant(
            $"AUTO-{Turn}-{missionIndex}-{sequence}-{(playerVictory ? "WIN" : "LOSS")}");
        string debrief = DebriefSection(missionIndex);
        stateIni.writeValue(debrief, "CompletedAt", completedAt);
        stateIni.writeValue(debrief, "MissionResult", playerVictory ? "Victory" : "Defeat");

        TaskForceRosterGenerator.RosterUnitOption[] destroyedUnits = playerVictory
            ? SelectAutoresolveDestroyedUnits(completedAt).ToArray()
            : [];
        stateIni.writeValue(debrief, "KillCount", destroyedUnits.Length);
        for (int index = 0; index < destroyedUnits.Length; index++)
        {
            TaskForceRosterGenerator.RosterUnitOption unit = destroyedUnits[index];
            string kill = CampaignStateData.KillSection(missionIndex, index + 1);
            stateIni.writeValue(kill, "Folder", "vessels");
            stateIni.writeValue(kill, "ObjectType", "Vessel");
            stateIni.writeValue(kill, "IsDestroyed", true);
            stateIni.writeValue(kill, "IniRef", unit.UnitType);
            stateIni.writeValue(kill, "VariantIndex", ParseVariantIndex(unit.VariantReference));
        }
        stateIni.saveFile(overwrite: true);

        CampaignContractOffer contract = CampaignContractOffers.Get(missionIndex);
        AccessTools.Method(AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceSaveManager"),
            "ApplyCompletionRewards")?.Invoke(null,
                [stateIni, missionIndex, playerVictory ? contract.Payment : 0, playerVictory ? contract.CapReward : 0]);
        stateIni.saveFile(overwrite: true);

        Write(AutoresolveCounterKey, sequence.ToString(CultureInfo.InvariantCulture));
        RecordBattle(missionIndex);
        if (!HasPendingBattle)
        {
            message = "Autoresolve wrote a debrief, but the campaign did not accept it.";
            return false;
        }

        message = playerVictory
            ? $"Autoresolved mission {missionIndex} as a victory with {destroyedUnits.Length} enemy hulls destroyed."
            : $"Autoresolved mission {missionIndex} as a defeat with no enemy hulls destroyed.";
        Debug.Log($"[Crimean Tom Operations] {message}");
        return true;
    }

    internal static LinearCampaignTaskForceSnapshot ReadCurrentSnapshot() =>
        ReadSnapshotMethod?.Invoke(null, null) as LinearCampaignTaskForceSnapshot ??
        LinearCampaignTaskForceSnapshot.Empty;

    internal static IReadOnlyList<CampaignSalvageCandidate> ReadPendingSalvage() =>
        PendingMissionIndex < 1 || !BattleWon(PendingMissionIndex) || ReadSet(ClaimedSalvageKey).Count > 0
            ? []
            : ReadSalvageCandidates(PendingMissionIndex)
                .Where(candidate => !ReadSet(ClaimedSalvageKey).Contains(candidate.Key))
                .ToArray();

    internal static IReadOnlyList<CampaignUnitOffer> ReadMarketOffers()
    {
        List<CampaignUnitOffer> offers = [];
        foreach (string row in SplitRows(Read(MarketOffersKey)))
        {
            string[] fields = row.Split(',');
            if (fields.Length < 3)
            {
                continue;
            }

            CampaignUnitOffer? offer = CreateOffer(fields[0], fields[1], fields[2]);
            if (offer is not null)
            {
                offers.Add(offer);
            }
        }

        return offers;
    }

    internal static bool TryRecover(CampaignSalvageCandidate candidate, out string message)
    {
        if (!HasPendingBattle)
        {
            message = "No post-battle salvage is pending.";
            return false;
        }

        if (!ReadPendingSalvage().Any(item => item.Key == candidate.Key))
        {
            message = "Recovery is limited to one listed hull after a victory.";
            return false;
        }

        if (SalvageShares < candidate.SalvageCost)
        {
            message = "Not enough salvage shares.";
            return false;
        }

        if (!AddUnitToTaskForce(
                candidate.UnitFolder,
                candidate.UnitType,
                candidate.VariantReference,
                candidate.FullPrice,
                0))
        {
            message = "The recovered unit could not be added to the task force.";
            return false;
        }

        HashSet<string> claimed = ReadSet(ClaimedSalvageKey);
        claimed.Add(candidate.Key);
        Write(ClaimedSalvageKey, string.Join("|", claimed));
        Write(
            SalvageSharesKey,
            (SalvageShares - candidate.SalvageCost).ToString(CultureInfo.InvariantCulture));
        MarkNewestMatchingUnitFatigued(candidate.UnitType, candidate.VariantReference, 2, "Reserve");
        SaveCampaign();
        message = $"Recovered {candidate.DisplayName}. It needs two contracts to refit.";
        return true;
    }

    internal static bool TryBuy(CampaignUnitOffer offer, out string message)
    {
        if (!HasPendingBattle || !ReadMarketOffers().Any(item =>
                item.UnitType == offer.UnitType && item.VariantReference == offer.VariantReference))
        {
            message = "This ship is no longer offered by the market.";
            return false;
        }
        int price = GetMarketPrice(offer);
        LinearCampaignTaskForceSnapshot snapshot = ReadCurrentSnapshot();
        if (snapshot.AvailablePoints < price)
        {
            message = "Not enough task force points.";
            return false;
        }

        if (!AddUnitToTaskForce(
                "vessels",
                offer.UnitType,
                offer.VariantReference,
                offer.FullPrice,
                price))
        {
            message = "The purchase could not be added to the task force.";
            return false;
        }

        List<CampaignUnitOffer> remaining = ReadMarketOffers()
            .Where(candidate => !ReferenceEquals(candidate, offer) &&
                !(candidate.UnitType == offer.UnitType &&
                  candidate.VariantReference == offer.VariantReference))
            .ToList();
        Write(MarketOffersKey, SerializeOffers(remaining));
        MarkNewestMatchingUnitFatigued(offer.UnitType, offer.VariantReference, 1, "Reserve");
        SaveCampaign();
        message = $"Purchased {offer.DisplayName} for {price} points. It needs one turn to work up.";
        return true;
    }

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

    internal static void ApplyGroupsToGeneratedMission(string generatedMissionPath)
    {
        IniHandler missionIni = Utils.openIniFile(generatedMissionPath, logErrors: true, useCaching: false);
        CampaignMissionData.Apply(missionIni.Data, GetGroup);
        missionIni.saveFile(overwrite: true);
    }

    internal static void CompleteManagement()
    {
        if (!HasPendingBattle)
        {
            return;
        }

        int missionIndex = PendingMissionIndex;
        ClearReusableMissionState(missionIndex);
        Write(PendingBattleKey, string.Empty);
        Write(MarketOffersKey, string.Empty);
        Write(ClaimedSalvageKey, string.Empty);
        Write(TurnKey, (Turn + 1).ToString(CultureInfo.InvariantCulture));
        Write("TaskForceModeCampaignCompleted", bool.FalseString);
        foreach (object mission in GetMissions())
        {
            AccessTools.Property(mission.GetType(), "IsComplete")?.SetValue(mission, false);
            CampaignContractOffers.Describe(mission);
        }
        SaveCampaign();
    }

    internal static bool CanWaitInPort => IsActiveCampaign && !HasPendingBattle &&
        ReadCurrentSnapshot().Items.Any(item => !item.IsAirUnit && GetFatigue(item.InstanceId) > 0);

    internal static void WaitInPort()
    {
        if (!CanWaitInPort) return;
        DecrementFatigue();
        Write(TurnKey, (Turn + 1).ToString(CultureInfo.InvariantCulture));
        foreach (object mission in GetMissions()) CampaignContractOffers.Describe(mission);
        SaveCampaign();
    }

    private static IReadOnlyList<CampaignSalvageCandidate> ReadSalvageCandidates(int missionIndex)
    {
        IniHandler? stateIni = OpenCampaignState();
        if (stateIni is null)
        {
            return [];
        }

        int count = Math.Max(0, stateIni.readValue(DebriefSection(missionIndex), "KillCount", 0));
        List<CampaignSalvageCandidate> candidates = [];
        for (int index = 1; index <= count; index++)
        {
            string section = CampaignStateData.KillSection(missionIndex, index);
            // The native "Kill" list may include surviving enemy register rows.
            if (!stateIni.readValue(section, "IsDestroyed", false)) continue;
            string folder = stateIni.readValue(section, "Folder", string.Empty, performLogging: false);
            string objectType = stateIni.readValue(section, "ObjectType", string.Empty, performLogging: false);
            if (!string.Equals(folder, "vessels", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(folder, "submarines", StringComparison.OrdinalIgnoreCase) &&
                !objectType.Contains("Vessel", StringComparison.OrdinalIgnoreCase) &&
                !objectType.Contains("Submarine", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string unitType = stateIni.readValue(section, "IniRef", string.Empty, performLogging: false);
            if (string.IsNullOrWhiteSpace(unitType))
            {
                continue;
            }

            int variantIndex = Math.Max(0, stateIni.readValue(section, "VariantIndex", 0));
            string variantReference = variantIndex > 0 ? $"Variant{variantIndex}" : "Default";
            int fullPrice = ResolveFullPrice(unitType);
            LinearCampaignTaskForceBuilderRosterItem item = CreateRosterItem(
                folder,
                unitType,
                variantReference,
                fullPrice);
            string key = $"{index}:{unitType}:{variantReference}";
            candidates.Add(new CampaignSalvageCandidate(
                key,
                folder,
                unitType,
                variantReference,
                item.DisplayName,
                fullPrice,
                Math.Max(1, (int)Math.Ceiling(fullPrice * 0.5d))));
        }

        return candidates;
    }

    private static IReadOnlyList<CampaignUnitOffer> CreateMarketOffers(string battleId)
    {
        TaskForceRosterGenerator.RefreshBaseCosts();
        System.Random random = new(StableHash(battleId));
        HashSet<string> owned = ReadCurrentSnapshot().Items
            .Where(item => !item.IsAirUnit)
            .Select(item => $"{item.UnitType}:{item.VariantReference}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        CampaignUnitOffer[] pool = TaskForceRosterGenerator.GetAllVesselOptions()
            .Where(option => !owned.Contains($"{option.UnitType}:{option.VariantReference}"))
            .GroupBy(option => option.UnitType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(_ => random.Next()).First())
            .Select(option => CreateOffer(option.UnitType, option.VariantReference, option.Nation))
            .Where(offer => offer is not null && offer.FullPrice > 0)
            .Cast<CampaignUnitOffer>()
            .ToArray();
        return pool.OrderBy(_ => random.Next()).Take(2).ToArray();
    }

    private static IEnumerable<TaskForceRosterGenerator.RosterUnitOption>
        SelectAutoresolveDestroyedUnits(string battleId)
    {
        TaskForceRosterGenerator.RefreshBaseCosts();
        System.Random random = new(StableHash(battleId));
        return TaskForceRosterGenerator.GetAllVesselOptions()
            .Where(option => ResolveFullPrice(option.UnitType) > 0)
            .GroupBy(option => option.UnitType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(_ => random.Next()).First())
            .OrderBy(option => ResolveFullPrice(option.UnitType))
            .ThenBy(option => option.UnitType, StringComparer.OrdinalIgnoreCase)
            .Take(2);
    }

    private static int ParseVariantIndex(string variantReference)
    {
        const string Prefix = "Variant";
        return variantReference.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                variantReference[Prefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int index)
            ? Math.Max(0, index)
            : 0;
    }

    private static bool IsReserve(string instanceId) =>
        string.Equals(GetGroup(instanceId), "Reserve", StringComparison.OrdinalIgnoreCase);

    private static CampaignUnitOffer? CreateOffer(
        string unitType,
        string variantReference,
        string nation)
    {
        int fullPrice = ResolveFullPrice(unitType);
        if (fullPrice <= 0)
        {
            return null;
        }

        LinearCampaignTaskForceBuilderRosterItem item = CreateRosterItem(
            "vessels",
            unitType,
            variantReference,
            fullPrice);
        return new CampaignUnitOffer(
            unitType,
            variantReference,
            nation,
            item.DisplayName,
            fullPrice);
    }

    private static bool AddUnitToTaskForce(
        string unitFolder,
        string unitType,
        string variantReference,
        int fullPrice,
        int pointsCharged)
    {
        LinearCampaignTaskForceSnapshot snapshot = ReadCurrentSnapshot();
        if (CommitPurchasesMethod is null)
        {
            return false;
        }

        LinearCampaignTaskForceBuilderRosterItem item = CreateRosterItem(
            unitFolder,
            unitType,
            variantReference,
            fullPrice);
        int remaining = Math.Max(0, snapshot.AvailablePoints - Math.Max(0, pointsCharged));
        object? result = CommitPurchasesMethod.Invoke(
            null,
            [snapshot.TaskForceName, new[] { item }, remaining, snapshot.CapPoints]);
        return result is LinearCampaignTaskForceSnapshot updated && updated.Items.Count > snapshot.Items.Count;
    }

    private static LinearCampaignTaskForceBuilderRosterItem CreateRosterItem(
        string unitFolder,
        string unitType,
        string variantReference,
        int fullPrice) =>
        new(
            unitFolder,
            unitType,
            string.Equals(unitFolder, "submarines", StringComparison.OrdinalIgnoreCase)
                ? "Submarine"
                : "Ship",
            variantReference,
            Math.Max(1, fullPrice),
            isFlagshipEligible: true);

    private static int ResolveFullPrice(string unitType)
    {
        TaskForceRosterGenerator.RefreshBaseCosts();
        int price = TaskForceRosterGenerator.GetVesselBaseCost(unitType);
        if (price > 0)
        {
            return price;
        }

        return Math.Max(1, VesselCombatProfileReader.Read(unitType)?.ApproximatePointCost ?? 1);
    }

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

    private static IniHandler? OpenCampaignState() =>
        OpenCampaignStateMethod?.Invoke(null, null) as IniHandler;

    private static string DebriefSection(int missionIndex) =>
        $"Mission_{missionIndex}_TaskForceDebrief";

    private static string SerializeOffers(IEnumerable<CampaignUnitOffer> offers) =>
        string.Join("|", offers.Select(offer =>
            $"{offer.UnitType},{offer.VariantReference},{offer.Nation}"));

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

    private static object? GetCampaign() => LinearCampaignInstanceProperty?.GetValue(null);

    private static Dictionary<string, string>? GetPersistentData()
    {
        object? campaign = GetCampaign();
        return campaign is null
            ? null
            : AccessTools.Property(campaign.GetType(), "PersistentData")?.GetValue(campaign)
                as Dictionary<string, string>;
    }

    private static IEnumerable GetMissions()
    {
        object? campaign = GetCampaign();
        return campaign is null
            ? Array.Empty<object>()
            : AccessTools.Property(campaign.GetType(), "Missions")?.GetValue(campaign)
                as IEnumerable ?? Array.Empty<object>();
    }

    private static void SaveCampaign()
    {
        object? campaign = GetCampaign();
        if (campaign is not null)
        {
            AccessTools.Method(campaign.GetType(), "SaveCampaignPersistentData")?.Invoke(campaign, null);
        }
    }

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
