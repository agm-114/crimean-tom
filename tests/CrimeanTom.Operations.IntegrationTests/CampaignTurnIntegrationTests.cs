using CrimeanTom.Operations;

using Xunit;

namespace CrimeanTom.Operations.IntegrationTests;

public sealed class CampaignTurnIntegrationTests
{
    [Fact]
    public void Scenario_and_after_action_report_complete_a_campaign_turn()
    {
        CampaignState campaign = CampaignState.Create(
            "north-atlantic-1989",
            [new CampaignUnit("ffg-01", "USS Sample", 100, 100, 40)]);

        ScenarioPackage scenario = new(
            campaign.CampaignId,
            campaign.Revision,
            "operation-001",
            "Convoy Escort",
            ["ffg-01"]);

        CampaignState inBattle = CampaignWorkflow.PrepareScenario(campaign, scenario);

        AfterActionReport report = new(
            campaign.CampaignId,
            campaign.Revision,
            scenario.OperationId,
            [new UnitAfterAction("ffg-01", 72, 41, 9, false)]);

        CampaignState returned = CampaignWorkflow.ApplyAfterAction(inBattle, report);

        Assert.Equal(CampaignPhase.Headquarters, returned.Phase);
        Assert.Null(returned.PendingScenario);
        Assert.Equal(1, returned.Revision);
        Assert.Equal(2, returned.Turn);
        Assert.Equal(72, returned.Units.Single().Condition);
        Assert.Equal(41, returned.Units.Single().Fuel);
        Assert.Equal(9, returned.Units.Single().PrimaryAmmunition);
    }
}
