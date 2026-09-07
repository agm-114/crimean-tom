using static CrimeanTom.SeaPower.NativeCampaignAccess;

namespace CrimeanTom.SeaPower;

internal static partial class CampaignOperationsState
{
    internal static ScenarioDifficulty Difficulty => ScenarioDifficulty.Find(Read("TaskForceDifficultyPreset"));
    internal static ScenarioStartingOptions StartingOptions => new(
        ReadBool("CrimeanTomAircraftDeployed", true), ReadBool("CrimeanTomPreloadOrders", true),
        ReadBool("CrimeanTomControlProtectedShips", false));

    internal static int ContractActivityBudget
    {
        get
        {
            string board = $"{ContractSeed}:{Turn}";
            if (Read("CrimeanTomActivityBoard") == board && int.TryParse(Read("CrimeanTomActivityBudget"), out int saved)) return saved;
            int budget = FilterFatiguedUnits(ReadCurrentSnapshot()).Items.Where(item => !item.IsAirUnit)
                .Sum(item => VesselCombatProfileReader.Read(item.UnitType)?.ApproximatePointCost ?? Math.Max(1, item.BaseCostPerUnit));
            Write("CrimeanTomActivityBoard", board);
            Write("CrimeanTomActivityBudget", budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SaveCampaign();
            return budget;
        }
    }

    internal static void SaveStartingOptions(ScenarioStartingOptions options)
    {
        Write("CrimeanTomAircraftDeployed", options.AircraftDeployed.ToString());
        Write("CrimeanTomPreloadOrders", options.PreloadOrders.ToString());
        Write("CrimeanTomControlProtectedShips", options.ControlProtectedShips.ToString());
        SaveCampaign();
    }

    private static bool ReadBool(string key, bool fallback) => bool.TryParse(Read(key), out bool value) ? value : fallback;
}
