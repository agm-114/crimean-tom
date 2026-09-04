namespace CrimeanTom.Operations;

public enum CampaignPhase
{
    Headquarters,
    ScenarioPrepared,
}

public sealed record CampaignUnit(
    string Id,
    string Name,
    string SeaPowerType,
    int Condition,
    int Fuel,
    int PrimaryAmmunition,
    bool Destroyed = false);

public sealed record ScenarioEnvironment(
    DateOnly Date,
    TimeOnly Time,
    double MapCenterLatitude,
    double MapCenterLongitude,
    int SeaState,
    string Clouds,
    string Weather);

public sealed record ScenarioVessel(
    string Id,
    string SeaPowerType,
    double EastPositionNm,
    double NorthPositionNm,
    int Heading,
    int Telegraph,
    bool RadarsActive,
    double EastWaypointNm,
    double NorthWaypointNm);

public sealed record ScenarioPackage(
    string CampaignId,
    long CampaignRevision,
    string OperationId,
    string Title,
    string Description,
    ScenarioEnvironment Environment,
    IReadOnlyList<ScenarioVessel> PlayerVessels,
    IReadOnlyList<ScenarioVessel> EnemyVessels);

public sealed record UnitAfterAction(
    string UnitId,
    int Condition,
    int Fuel,
    int PrimaryAmmunition,
    bool Destroyed);

public sealed record AfterActionReport(
    string CampaignId,
    long CampaignRevision,
    string OperationId,
    IReadOnlyList<UnitAfterAction> Units,
    string Outcome = "Pending");

public sealed record CampaignState(
    string CampaignId,
    long Revision,
    int Turn,
    CampaignPhase Phase,
    IReadOnlyList<CampaignUnit> Units,
    ScenarioPackage? PendingScenario)
{
    public static CampaignState Create(string campaignId, IReadOnlyList<CampaignUnit> units)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        ArgumentNullException.ThrowIfNull(units);

        if (units.Count == 0)
        {
            throw new ArgumentException("A campaign requires at least one unit.", nameof(units));
        }

        CampaignUnit[] ownedUnits = units.ToArray();
        CampaignValidation.EnsureUniqueIds(ownedUnits.Select(unit => unit.Id), nameof(units));
        foreach (CampaignUnit unit in ownedUnits)
        {
            CampaignValidation.ValidateUnit(unit, nameof(units));
        }

        return new(campaignId, 0, 1, CampaignPhase.Headquarters, ownedUnits, null);
    }
}
