using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPower.GameBridge;

namespace OODA;

internal enum DecisionRole { Operational, RoutineTactical, SurfaceDefense, AirDefense, UrgentTactical }

internal record DecisionPrompt(string Instructions, JObject Input)
{
    private const string ResourcePrefix = "OODA.Prompts.";
    private static readonly string Shared = Read("shared");
    private static readonly string Guide = Read("game-guide");
    private static readonly string Operational = Read("operational");
    private static readonly string Routine = Read("routine-tactical");
    private static readonly string TimeRules = Read("time-rules");
    private static readonly string Helicopters = Read("helicopter-screen");
    private static readonly string FlightOperations = Read("flight-operations");
    private static readonly string MaritimeStrike = Read("maritime-strike");
    private static readonly string Defense = Read("surface-defense");
    private static readonly string AirDefense = Read("air-defense");
    private static readonly string Urgent = Read("urgent-tactical");
    private static readonly string[] ContextFields =
    [
        "scope", "missionClock", "taskforce", "objectiveUpdates", "ownedGroups", "ownedUnits", "ownedOtherUnits", "protectedUnits",
        "contacts", "currentOrders", "plan", "lastOrderResults", "destinations", "allowedTasks", "allowedActions",
        "rulesOfEngagement", "standingOrders", "missionMessages", "availableTimeCompressions", "attackReports", "observedGunfire", "ownWeaponFire",
        "ownMissileTargetsInFlight", "nearbySurfaceThreats", "helicopterDecks", "helicopterScreenAnchors", "airDecks", "airborne", "airTargets", "airWeaponFire",
        "airDefenseAssessment", "airDefenseRouteWarnings", "heldTransportRouteWarnings", "capStations", "maritimeShips", "submarines", "coastalThreats", "strafingOptions", "groundAttackOptions",
        "availableTools", "toolResults", "unit", "threat", "options", "deadline", "timeInterrupts", "conditionalAttacks", "identificationRequests"
    ];

    // Call on the game thread with the snapshot from the player-view reader.
    internal static DecisionPrompt ForMission(DecisionRole role, JObject playerView) =>
        Compose(role, playerView, role == DecisionRole.UrgentTactical ? null : PlayerBriefing.Read());

    internal static DecisionPrompt Compose(DecisionRole role, JObject playerView, string? briefing)
    {
        if ((string?)playerView["scope"] != "player")
            throw new ArgumentException("Expected a player-view snapshot.", nameof(playerView));

        JObject input = new();
        foreach (string field in ContextFields)
            if (playerView.TryGetValue(field, out JToken? value)) input[field] = value.DeepClone();
        string instructions = role switch
        {
            DecisionRole.Operational => string.Join("\n\n", Shared, Guide, Operational, Helicopters, FlightOperations, MaritimeStrike, TimeRules),
            DecisionRole.RoutineTactical => string.Join("\n\n", Shared, Guide, Routine, Helicopters, FlightOperations, MaritimeStrike, TimeRules),
            DecisionRole.SurfaceDefense => string.Join("\n\n", Shared, Defense),
            DecisionRole.AirDefense => string.Join("\n\n", Shared, AirDefense),
            DecisionRole.UrgentTactical => string.Join("\n\n", Shared, Urgent),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        if (role != DecisionRole.UrgentTactical) input["briefing"] = briefing;
        return new(instructions, input);
    }

    internal string ToCodexInput() => $"{Instructions}\n\nCurrent player-view data (JSON):\n{Input.ToString(Formatting.None)}";

    private static string Read(string name)
    {
        using Stream stream = typeof(DecisionPrompt).Assembly.GetManifestResourceStream($"{ResourcePrefix}{name}.md")
            ?? throw new InvalidOperationException($"Missing embedded OODA prompt: {name}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Trim();
    }
}
