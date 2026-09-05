using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;

namespace CrimeanTom.SeaPower;

internal sealed record CampaignContractOffer(
    int Seed, FleetClashSize Size, FleetClashMatch Match, RouteOperationVariant Variant,
    int Payment, int CapReward)
{
    internal FleetClashForce Enemy(int mission) => mission == 3 ? Match.First : Match.Second;

    internal string Description(int mission)
    {
        string enemy = mission == 3 ? Match.FirstName : Match.SecondName;
        FleetClashForce force = Enemy(mission);
        string task = mission switch
        {
            1 => "Break the enemy fleet by destroying 75% of its ship value.",
            2 => "Escort the protected group to its destination.",
            3 => "Stop the protected group from reaching its destination.",
            _ => "Get the designated high-value ships through the defended passage.",
        };
        string subject = mission is 2 or 3 ? $" Protected group: {Variant}." : string.Empty;
        return $"{Size} contract in {Match.Theater}. Enemy: {enemy}.\n" +
            $"Estimated threat: surface {Band(force.SurfaceCapability)}, air defence {Band(force.AirDefenseCapability)}, ASW {Band(force.AswCapability)}.\n" +
            $"{task}{subject}\n" +
            $"Victory pays {Payment} points, +{CapReward} point cap, and {CampaignContractRules.SalvageAward(true, Payment)} salvage shares. Defeat pays nothing.\n" +
            "Calm seas. No deadline. Enemy force and terms stay fixed until the next turn; choose your deployment accordingly.";
    }

    private static string Band(int capability) => capability < 8 ? "low" : capability < 22 ? "medium" : "high";
}

internal static class CampaignContractOffers
{
    private static readonly Dictionary<string, CampaignContractOffer> Cache = [];
    private static string _board = string.Empty;

    internal static CampaignContractOffer Get(int mission)
    {
        int campaignSeed = CampaignOperationsState.ContractSeed;
        int turn = CampaignOperationsState.Turn;
        string board = $"{campaignSeed}:{turn}";
        if (_board != board)
        {
            Cache.Clear();
            _board = board;
        }
        string key = mission.ToString();
        if (Cache.TryGetValue(key, out CampaignContractOffer? offer)) return offer;
        int seed = CampaignContractRules.OfferSeed(campaignSeed, turn, mission);
        FleetClashSize size = (FleetClashSize)CampaignContractRules.OfferSize(turn, mission);
        Random random = new(seed);
        FleetClashMatch match = mission == 1 ? FleetClashBalancer.Generate(random, size)
            : ScenarioDebugGenerator.GenerateRouteMatch(random, size, null);
        FleetClashForce enemy = mission == 3 ? match.First : match.Second;
        offer = new(seed, size, match, (RouteOperationVariant)random.Next(3),
            CampaignContractRules.Payment((int)size, enemy.PointCost, mission), 10 + (int)size * 10);
        Cache[key] = offer;
        return offer;
    }

    internal static void Describe(object? mission)
    {
        int index = CampaignOperationsState.GetMissionIndex(mission);
        if (!CampaignOperationsState.IsActiveCampaign || index is < 1 or > 4 || mission is null) return;
        CampaignContractOffer offer = Get(index);
        if (AccessTools.Property(mission.GetType(), "LanguageDataItems")?.GetValue(mission) is IEnumerable languages)
        {
            foreach (object language in languages)
            {
                foreach (string property in new[] { "MissionIntro", "MissionDescription" })
                    AccessTools.Property(language.GetType(), property)?.SetValue(language, offer.Description(index));
                AccessTools.Property(language.GetType(), "MissionResupplyRules")?.SetValue(language,
                    "Initial hull purchases use the builder. After the first sortie, buy hulls in the market. Aircraft, repair, rearm and loadouts remain available in native preparation.");
            }
        }
        AccessTools.Property(mission.GetType(), "MissionDifficulty")?.SetValue(mission, (int)offer.Size + 1);
        AccessTools.Method(mission.GetType(), "NotifyPropertyChanged", [typeof(string)])?.Invoke(mission, ["SelectedLanguage"]);
    }
}
