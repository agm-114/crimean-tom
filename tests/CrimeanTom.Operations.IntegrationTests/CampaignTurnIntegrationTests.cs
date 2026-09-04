using CrimeanTom.Operations;

using Xunit;

namespace CrimeanTom.Operations.IntegrationTests;

public sealed class CampaignTurnIntegrationTests
{
    [Fact]
    public void Campaign_round_trips_through_a_generated_scenario_and_after_action_report()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"crimean-tom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string campaignPath = Path.Combine(directory, "campaign.json");
            string scenarioPath = Path.Combine(directory, DemoOperation.ScenarioFileName);
            string afterActionPath = Path.Combine(directory, "after-action.json");
            CampaignStore store = new();
            SeaPowerScenarioWriter scenarioWriter = new();

            store.Save(campaignPath, DemoOperation.CreateCampaign());
            CampaignState campaign = store.Load(campaignPath);
            ScenarioPackage scenario = DemoOperation.CreateScenario(campaign);
            campaign = CampaignWorkflow.PrepareScenario(campaign, scenario);
            store.Save(campaignPath, campaign);
            scenarioWriter.Write(scenarioPath, scenario, afterActionPath);
            store.SaveAfterAction(afterActionPath, DemoOperation.CreateAfterActionTemplate(campaign));

            string scenarioText = File.ReadAllText(scenarioPath);
            Assert.Contains("Name=Crimean Tom Operations - Surface Trial", scenarioText);
            Assert.Contains("NumberOfTaskforce1Vessels=1", scenarioText);
            Assert.Contains("Type=usn_ffg_oliver_hazard_perry", scenarioText);
            Assert.Contains("Type=wp_mrk_nanuchka", scenarioText);
            Assert.Contains("[CrimeanTomOperations]", scenarioText);
            Assert.Contains($"AfterActionPath={afterActionPath}", scenarioText);

            AfterActionReport report = store.LoadAfterAction(afterActionPath) with
            {
                Units = [new UnitAfterAction("ffg-01", 72, 41, 9, false)],
            };
            store.SaveAfterAction(afterActionPath, report);

            CampaignState returned = CampaignWorkflow.ApplyAfterAction(
                store.Load(campaignPath),
                store.LoadAfterAction(afterActionPath));
            store.Save(campaignPath, returned);
            CampaignState persisted = store.Load(campaignPath);

            Assert.Equal(CampaignPhase.Headquarters, persisted.Phase);
            Assert.Null(persisted.PendingScenario);
            Assert.Equal(1, persisted.Revision);
            Assert.Equal(2, persisted.Turn);
            Assert.Equal(72, persisted.Units.Single().Condition);
            Assert.Equal(41, persisted.Units.Single().Fuel);
            Assert.Equal(9, persisted.Units.Single().PrimaryAmmunition);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
