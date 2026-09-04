namespace CrimeanTom.Operations;

public enum CampaignPhase
{
    Headquarters,
    ScenarioPrepared,
}

public sealed record CampaignUnit(
    string Id,
    string Name,
    int Condition,
    int Fuel,
    int PrimaryAmmunition,
    bool Destroyed = false);

public sealed record ScenarioPackage(
    string CampaignId,
    long CampaignRevision,
    string OperationId,
    string Title,
    IReadOnlyList<string> UnitIds);

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
    IReadOnlyList<UnitAfterAction> Units);

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

        EnsureUniqueIds(units.Select(unit => unit.Id), nameof(units));
        return new(campaignId, 0, 1, CampaignPhase.Headquarters, units, null);
    }

    private static void EnsureUniqueIds(IEnumerable<string> ids, string parameterName)
    {
        HashSet<string> uniqueIds = new(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id, parameterName);
            if (!uniqueIds.Add(id))
            {
                throw new ArgumentException($"Duplicate unit id '{id}'.", parameterName);
            }
        }
    }
}

public static class CampaignWorkflow
{
    public static CampaignState PrepareScenario(CampaignState state, ScenarioPackage scenario)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scenario);

        if (state.Phase != CampaignPhase.Headquarters || state.PendingScenario is not null)
        {
            throw new InvalidOperationException("The campaign already has a pending scenario.");
        }

        EnsureCampaignVersion(state, scenario.CampaignId, scenario.CampaignRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario.Title);

        HashSet<string> availableUnitIds = state.Units
            .Where(unit => !unit.Destroyed)
            .Select(unit => unit.Id)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> assignedUnitIds = new(StringComparer.Ordinal);

        foreach (string unitId in scenario.UnitIds)
        {
            if (!assignedUnitIds.Add(unitId))
            {
                throw new ArgumentException($"Unit '{unitId}' is assigned more than once.", nameof(scenario));
            }

            if (!availableUnitIds.Contains(unitId))
            {
                throw new ArgumentException($"Unit '{unitId}' is unavailable.", nameof(scenario));
            }
        }

        if (assignedUnitIds.Count == 0)
        {
            throw new ArgumentException("A scenario requires at least one unit.", nameof(scenario));
        }

        return state with
        {
            Phase = CampaignPhase.ScenarioPrepared,
            PendingScenario = scenario,
        };
    }

    public static CampaignState ApplyAfterAction(CampaignState state, AfterActionReport report)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(report);

        ScenarioPackage scenario = state.PendingScenario
            ?? throw new InvalidOperationException("The campaign has no pending scenario.");

        EnsureCampaignVersion(state, report.CampaignId, report.CampaignRevision);
        if (!StringComparer.Ordinal.Equals(scenario.OperationId, report.OperationId))
        {
            throw new ArgumentException("The report belongs to a different operation.", nameof(report));
        }

        Dictionary<string, UnitAfterAction> reportedUnits = report.Units
            .ToDictionary(unit => unit.UnitId, StringComparer.Ordinal);

        if (!scenario.UnitIds.ToHashSet(StringComparer.Ordinal).SetEquals(reportedUnits.Keys))
        {
            throw new ArgumentException("The report must contain every assigned unit exactly once.", nameof(report));
        }

        CampaignUnit[] updatedUnits = state.Units
            .Select(unit => reportedUnits.TryGetValue(unit.Id, out UnitAfterAction? result)
                ? unit with
                {
                    Condition = result.Condition,
                    Fuel = result.Fuel,
                    PrimaryAmmunition = result.PrimaryAmmunition,
                    Destroyed = result.Destroyed,
                }
                : unit)
            .ToArray();

        return state with
        {
            Revision = state.Revision + 1,
            Turn = state.Turn + 1,
            Phase = CampaignPhase.Headquarters,
            Units = updatedUnits,
            PendingScenario = null,
        };
    }

    private static void EnsureCampaignVersion(CampaignState state, string campaignId, long revision)
    {
        if (!StringComparer.Ordinal.Equals(state.CampaignId, campaignId) || state.Revision != revision)
        {
            throw new InvalidOperationException("The campaign id or revision does not match.");
        }
    }
}
