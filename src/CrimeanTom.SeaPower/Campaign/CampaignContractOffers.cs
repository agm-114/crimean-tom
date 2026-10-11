using static CrimeanTom.SeaPower.NativeCampaignAccess;

namespace CrimeanTom.SeaPower;

internal record CampaignContractOffer(
    int Seed, FleetClashSize Size, FleetClashMatch Match, RouteOperationVariant Variant,
    int Payment, int CapReward, ScenarioActivityPlan Activity, double ChaseAllowance)
{
    internal FleetClashForce Enemy(CampaignContractKind kind) => kind == CampaignContractKind.RouteInterdiction ? Match.First : Match.Second;
    internal int MinimumDeploymentPoints => Activity.MinimumFriendlyFleetPoints;

    internal string Description(CampaignContractKind kind)
    {
        string enemy = kind == CampaignContractKind.RouteInterdiction ? Match.FirstName : Match.SecondName;
        FleetClashForce force = Enemy(kind);
        string task = kind switch
        {
            CampaignContractKind.FleetClash => "Break the enemy fleet by destroying 75% of its ship value.",
            CampaignContractKind.RouteEscort => "Escort the protected group to its destination.",
            CampaignContractKind.RouteInterdiction => "Stop the protected group from reaching its destination.",
            CampaignContractKind.Passage => "Get the designated high-value ships through the defended passage.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        string subject = kind is CampaignContractKind.RouteEscort or CampaignContractKind.RouteInterdiction ? $" Protected group: {Variant}. Route: {ShippingRoutes.Pick(Match.Theater, Seed, Size, RouteEncounterKind.Random).Summary}." : string.Empty;
        return $"{Size} contract in {Match.Theater}. Enemy: {enemy}.\n" +
            CampaignOperationsState.Difficulty.Label + "\n" +
            $"Composition: {ScenarioDebugGenerator.GetComposition(force)}.\n" +
            $"Estimated threat: surface {Band(force.SurfaceCapability)}, air defence {Band(force.AirDefenseCapability)}, ASW {Band(force.AswCapability)}.\n" +
            $"{task}{subject}\n" +
            Activity.Summary + "\n" +
            (MinimumDeploymentPoints > 0 ? $"Minimum deployment: {MinimumDeploymentPoints} ship points for this contract's support allowance.\n" : string.Empty) +
            (ChaseAllowance > 0 ? $"Pursuit allowance: {ChaseAllowance:P0}, already included in these forces.\n" : string.Empty) +
            $"Victory pays {Payment} points, +{CapReward} point cap, and {CampaignContractRules.SalvageAward(true, Payment)} salvage shares. Defeat pays nothing.\n" +
            "Calm seas. No deadline. Enemy force and terms stay fixed until the next turn; choose your deployment accordingly.";
    }

    private static string Band(int capability) => capability < 8 ? "low" : capability < 22 ? "medium" : "high";
}

internal static class CampaignContractOffers
{
    private static readonly Dictionary<CampaignContractKind, CampaignContractOffer> Cache = [];
    private static string _board = string.Empty;

    internal static CampaignContractOffer Get(int mission)
    {
        var kind = CampaignContractRules.FromMissionIndex(mission);
        int campaignSeed = CampaignOperationsState.ContractSeed;
        int turn = CampaignOperationsState.Turn;
        ScenarioDifficulty difficulty = CampaignOperationsState.Difficulty;
        string board = $"{campaignSeed}:{turn}:{difficulty.Id}";
        if (_board != board)
        {
            Cache.Clear();
            _board = board;
        }
        if (Cache.TryGetValue(kind, out CampaignContractOffer? offer)) return offer;
        int seed = CampaignContractRules.OfferSeed(campaignSeed, turn, mission);
        FleetClashSize size = (FleetClashSize)CampaignContractRules.OfferSize(turn, mission);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            try
            {
                offer = Create(kind, unchecked(seed + attempt * 104729), size, difficulty);
                Cache[kind] = offer;
                return offer;
            }
            catch (InvalidOperationException) when (attempt < 15) { }
        }
        throw new InvalidOperationException("No valid contract layout could be generated.");
    }

    private static CampaignContractOffer Create(CampaignContractKind kind, int seed, FleetClashSize size, ScenarioDifficulty difficulty)
    {
        Random random = new(seed);
        string playerTaskforce = kind == CampaignContractKind.RouteInterdiction ? "Taskforce2" : "Taskforce1";
        FleetClashMatch match = kind == CampaignContractKind.FleetClash ? FleetClashBalancer.Generate(random, size, playerTaskforce: playerTaskforce)
            : ScenarioDebugGenerator.GenerateRouteMatch(random, size, null, playerTaskforce);
        match = ScenarioDebugGenerator.ApplyEnemyMultiplier(match, random, size,
            playerTaskforce, difficulty.Multiplier, excludeCarriers: kind != CampaignContractKind.FleetClash,
            preserveComposition: false);
        RouteOperationVariant variant = (RouteOperationVariant)random.Next(3);
        double chaseAllowance = 0;
        if (kind is CampaignContractKind.RouteEscort or CampaignContractKind.RouteInterdiction)
            (match, chaseAllowance) = ScenarioDebugGenerator.ApplyRouteChaseAllowance(match, seed, size, variant,
                RouteEncounterKind.Random, persistentAttackers: kind == CampaignContractKind.RouteInterdiction);
        FleetClashForce enemy = kind == CampaignContractKind.RouteInterdiction ? match.First : match.Second;
        FleetClashMatch activityMatch = match;
        if (kind is CampaignContractKind.RouteEscort or CampaignContractKind.RouteInterdiction)
        {
            RouteEncounter encounter = ShippingRoutes.Pick(match.Theater, seed, size, RouteEncounterKind.Random);
            activityMatch = match with { Latitude = (float)encounter.Center.Latitude, Longitude = (float)encounter.Center.Longitude };
        }
        ScenarioActivityPlan activity = ScenarioActivityPlan.Generate(activityMatch, seed, ScenarioActivitySettings.Campaign,
            playerTaskforce, CampaignOperationsState.ContractActivityBudget);
        return new(seed, size, match, variant,
            CampaignContractRules.Payment((int)size, enemy.PointCost + activity.Cost(playerTaskforce == "Taskforce1" ? "Taskforce2" : "Taskforce1"), kind),
            10 + (int)size * 10, activity, chaseAllowance);
    }

    internal static void Describe(object? mission)
    {
        int index = GetMissionIndex(mission);
        if (!CampaignOperationsState.IsActiveCampaign || index is < 1 or > 4 || mission is null) return;
        CampaignContractOffer offer = Get(index);
        DescribeMission(mission, offer.Description(CampaignContractRules.FromMissionIndex(index)),
            "Initial hull purchases use the builder. After the first sortie, buy hulls in the market. Aircraft, repair, rearm and loadouts remain available in native preparation.",
            (int)offer.Size + 1);
    }
}
