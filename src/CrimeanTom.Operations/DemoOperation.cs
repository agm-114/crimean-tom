namespace CrimeanTom.Operations;

public static class DemoOperation
{
    public const string ScenarioFileName = "Crimean Tom Operations - Surface Trial.ini";

    public static CampaignState CreateCampaign()
    {
        return CampaignState.Create(
            "north-atlantic-1989",
            [
                new CampaignUnit(
                    "ffg-01",
                    "USS Sample",
                    "usn_ffg_oliver_hazard_perry",
                    100,
                    100,
                    40),
            ]);
    }

    public static ScenarioPackage CreateScenario(CampaignState campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        CampaignUnit player = campaign.Units.First(unit => !unit.Destroyed);

        return new ScenarioPackage(
            campaign.CampaignId,
            campaign.Revision,
            $"surface-trial-{campaign.Turn:D3}",
            "Crimean Tom Operations - Surface Trial",
            "A generated campaign handoff. Defeat the opposing missile boat and preserve the frigate for the next campaign turn.",
            new ScenarioEnvironment(
                new DateOnly(1989, 9, 11),
                new TimeOnly(12, 0),
                36.0,
                -20.0,
                2,
                "Scattered",
                "Clear"),
            [
                new ScenarioVessel(
                    player.Id,
                    player.SeaPowerType,
                    -6,
                    0,
                    90,
                    2,
                    true,
                    20,
                    0),
            ],
            [
                new ScenarioVessel(
                    "enemy-mrk-01",
                    "wp_mrk_nanuchka",
                    6,
                    0,
                    270,
                    2,
                    true,
                    -20,
                    0),
            ]);
    }

    public static AfterActionReport CreateAfterActionTemplate(CampaignState campaign)
    {
        ScenarioPackage scenario = campaign.PendingScenario
            ?? throw new InvalidOperationException("Prepare the scenario before creating an after-action template.");

        return new AfterActionReport(
            campaign.CampaignId,
            campaign.Revision,
            scenario.OperationId,
            scenario.PlayerVessels
                .Select(vessel => campaign.Units.Single(unit => unit.Id == vessel.Id))
                .Select(unit => new UnitAfterAction(
                    unit.Id,
                    unit.Condition,
                    unit.Fuel,
                    unit.PrimaryAmmunition,
                    unit.Destroyed))
                .ToArray());
    }
}
