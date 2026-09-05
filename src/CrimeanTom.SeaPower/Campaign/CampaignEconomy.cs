using static CrimeanTom.SeaPower.NativeCampaignAccess;
using SeaPower;
using SeapowerUI.ViewModels;

namespace CrimeanTom.SeaPower;

internal record CampaignUnitOffer(
    string UnitType,
    string VariantReference,
    string Nation,
    string DisplayName,
    int FullPrice)
{
    internal (string Type, string Variant) Unit => (UnitType, VariantReference);
}

internal record CampaignSalvageCandidate(
    string Key,
    string UnitFolder,
    string UnitType,
    string VariantReference,
    string DisplayName,
    int FullPrice,
    int SalvageCost);

internal static partial class CampaignOperationsState
{
    internal static int GetMarketPrice(CampaignUnitOffer offer)
    {
        var data = GetPersistentData();
        if (data is null) return offer.FullPrice;
        data.TryGetValue("TaskForceCommanderNationOverride", out string selection);
        if (string.IsNullOrWhiteSpace(selection)) data.TryGetValue("TaskForceCommanderNation", out selection);
        return CampaignContractRules.MarketPrice(offer.FullPrice,
            TaskForceNationRules.GetCampaignDiscount(data, selection, offer.Nation));
    }

    internal static IReadOnlyList<CampaignSalvageCandidate> ReadPendingSalvage() =>
        PendingMissionIndex < 1 || !BattleWon(PendingMissionIndex) || ReadSet(ClaimedSalvageKey).Count > 0
            ? []
            : ReadSalvageCandidates(PendingMissionIndex).ToArray();

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

            offers.Add(CreateOffer(fields[0], fields[1], fields[2]));
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
        Write(SalvageSharesKey, (SalvageShares - candidate.SalvageCost));
        MarkNewestMatchingUnitFatigued(candidate.UnitType, candidate.VariantReference, 2, "Reserve");
        SaveCampaign();
        message = $"Recovered {candidate.DisplayName}. It needs two contracts to refit.";
        return true;
    }

    internal static bool TryBuy(CampaignUnitOffer offer, out string message)
    {
        if (!HasPendingBattle || !ReadMarketOffers().Any(item => item.Unit == offer.Unit))
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
            .Where(candidate => candidate.Unit != offer.Unit)
            .ToList();
        Write(MarketOffersKey, SerializeOffers(remaining));
        MarkNewestMatchingUnitFatigued(offer.UnitType, offer.VariantReference, 1, "Reserve");
        SaveCampaign();
        message = $"Purchased {offer.DisplayName} for {price} points. It needs one turn to work up.";
        return true;
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
            .ToArray();
        return pool.OrderBy(_ => random.Next()).Take(2).ToArray();
    }

    private static CampaignUnitOffer CreateOffer(
        string unitType,
        string variantReference,
        string nation)
    {
        int fullPrice = ResolveFullPrice(unitType);
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
        if (!CanCommitPurchases)
        {
            return false;
        }

        LinearCampaignTaskForceBuilderRosterItem item = CreateRosterItem(
            unitFolder,
            unitType,
            variantReference,
            fullPrice);
        int remaining = Math.Max(0, snapshot.AvailablePoints - Math.Max(0, pointsCharged));
        LinearCampaignTaskForceSnapshot? result = CommitPurchases(
            snapshot.TaskForceName, [item], remaining, snapshot.CapPoints);
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

    private static string SerializeOffers(IEnumerable<CampaignUnitOffer> offers) =>
        string.Join("|", offers.Select(offer =>
            $"{offer.UnitType},{offer.VariantReference},{offer.Nation}"));
}