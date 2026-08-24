namespace CrimeanTom.Companion;

public sealed record CampaignCompanionClientOptions(
    Uri GrpcUri,
    Uri HttpUri,
    string CampaignId,
    string ClientInstanceId,
    string TokenFile,
    string StateDirectory,
    string PlayerCoalitionId,
    int TacticalEntityCapacity,
    int TacticalReserveCapacity);
