namespace CrimeanTom.Operations;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario.Description);

        Dictionary<string, CampaignUnit> availableUnits = state.Units
            .Where(unit => !unit.Destroyed)
            .ToDictionary(unit => unit.Id, StringComparer.Ordinal);
        string[] assignedUnitIds = scenario.PlayerVessels
            .Select(vessel => vessel.Id)
            .ToArray();

        CampaignValidation.EnsureUniqueIds(assignedUnitIds, nameof(scenario));
        if (assignedUnitIds.Length == 0)
        {
            throw new ArgumentException("A scenario requires at least one player vessel.", nameof(scenario));
        }

        foreach (ScenarioVessel vessel in scenario.PlayerVessels)
        {
            if (!availableUnits.TryGetValue(vessel.Id, out CampaignUnit? unit))
            {
                throw new ArgumentException($"Unit '{vessel.Id}' is unavailable.", nameof(scenario));
            }

            if (!StringComparer.Ordinal.Equals(unit.SeaPowerType, vessel.SeaPowerType))
            {
                throw new ArgumentException(
                    $"Unit '{vessel.Id}' must use Sea Power type '{unit.SeaPowerType}'.",
                    nameof(scenario));
            }
        }

        if (scenario.EnemyVessels.Count == 0)
        {
            throw new ArgumentException("A scenario requires at least one enemy vessel.", nameof(scenario));
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

        foreach (UnitAfterAction unit in report.Units)
        {
            CampaignValidation.ValidateAfterAction(unit);
        }

        Dictionary<string, UnitAfterAction> reportedUnits = report.Units
            .ToDictionary(unit => unit.UnitId, StringComparer.Ordinal);
        HashSet<string> assignedUnitIds = scenario.PlayerVessels
            .Select(vessel => vessel.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (!assignedUnitIds.SetEquals(reportedUnits.Keys))
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
