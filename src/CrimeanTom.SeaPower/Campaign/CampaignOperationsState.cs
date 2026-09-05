using static CrimeanTom.SeaPower.NativeCampaignAccess;
using SeaPower;
using SeapowerUI.ViewModels;
using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class CampaignOperationsState
{
    internal static bool IsActiveCampaign
    {
        get
        {
            string? path = CurrentCampaignPath;
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
            Write("CrimeanTomContractSeed", seed);
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

    internal static bool HasPendingBattle =>
        IsActiveCampaign && !string.IsNullOrWhiteSpace(Read(PendingBattleKey));
    internal static int PendingMissionIndex => ReadInt(ActiveMissionKey, 0);

    internal static void RecordLaunch(int missionIndex, string generatedMissionPath)
    {
        if (!IsActiveCampaign || missionIndex < 1)
        {
            return;
        }

        Write(ActiveMissionKey, missionIndex);
        Write("CrimeanTomFirstSortie", bool.TrueString);
        SaveCampaign();
        Debug.Log(
            $"[Crimean Tom Operations] campaign turn {Turn} launched mission {missionIndex}: " +
            generatedMissionPath);
    }

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
        Write(SalvageSharesKey, (SalvageShares + award));
        Write(ActiveMissionKey, missionIndex);
        Write(PendingBattleKey, battleId);
        Write(RecordedBattleKey, battleId);
        Write(ClaimedSalvageKey, string.Empty);
        Write(MarketOffersKey, SerializeOffers(CreateMarketOffers(battleId)));
        SaveCampaign();
        Debug.Log(
            $"[Crimean Tom Operations] recorded battle {battleId}; " +
            $"awarded {award} salvage shares and found {salvage.Count} recoverable hulls");
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
        Write(TurnKey, (Turn + 1));
        Write("TaskForceModeCampaignCompleted", bool.FalseString);
        foreach (object mission in GetMissions())
        {
            SetMissionComplete(mission, false);
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
        Write(TurnKey, (Turn + 1));
        foreach (object mission in GetMissions()) CampaignContractOffers.Describe(mission);
        SaveCampaign();
    }

}