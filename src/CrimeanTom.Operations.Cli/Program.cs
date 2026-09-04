using System.Security.Cryptography;

using CrimeanTom.Operations;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        ShowUsage();
        return 0;
    }

    try
    {
        string workspace = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/demo-campaign");
        return args[0].ToLowerInvariant() switch
        {
            "init" => Initialize(workspace),
            "prepare" => Prepare(workspace),
            "install" => Install(workspace, args.Skip(2).ToArray()),
            "status" => ShowStatus(workspace),
            "resolve" => Resolve(workspace),
            _ => UnknownCommand(args[0]),
        };
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static int Initialize(string workspace)
{
    string campaignPath = GetCampaignPath(workspace);
    if (File.Exists(campaignPath))
    {
        throw new InvalidOperationException($"A campaign already exists at '{campaignPath}'.");
    }

    CampaignStore store = new();
    store.Save(campaignPath, DemoOperation.CreateCampaign());
    Console.WriteLine($"Created campaign: {campaignPath}");
    return 0;
}

static int Prepare(string workspace)
{
    CampaignStore store = new();
    CampaignState campaign = store.Load(GetCampaignPath(workspace));
    ScenarioPackage scenario = DemoOperation.CreateScenario(campaign);
    CampaignState prepared = CampaignWorkflow.PrepareScenario(campaign, scenario);

    string scenarioPath = GetScenarioPath(workspace);
    string afterActionPath = GetAfterActionPath(workspace);
    new SeaPowerScenarioWriter().Write(scenarioPath, scenario);
    store.SaveAfterAction(afterActionPath, DemoOperation.CreateAfterActionTemplate(prepared));
    store.Save(GetCampaignPath(workspace), prepared);

    Console.WriteLine($"Prepared operation: {scenario.Title}");
    Console.WriteLine($"Scenario: {scenarioPath}");
    Console.WriteLine($"After-action report: {afterActionPath}");
    Console.WriteLine("Fight the scenario, then edit the after-action report before running resolve.");
    return 0;
}

static int Install(string workspace, string[] options)
{
    string sourcePath = GetScenarioPath(workspace);
    if (!File.Exists(sourcePath))
    {
        throw new FileNotFoundException("Prepare the operation before installing its scenario.", sourcePath);
    }

    bool replace = options.Contains("--replace", StringComparer.OrdinalIgnoreCase);
    string? suppliedGameDirectory = options.FirstOrDefault(option => !option.StartsWith("--", StringComparison.Ordinal));
    string gameDirectory = suppliedGameDirectory
        ?? Environment.GetEnvironmentVariable("SEA_POWER_DIR")
        ?? @"C:\Program Files (x86)\Steam\steamapps\common\Sea Power";
    string missionDirectory = Path.Combine(
        gameDirectory,
        "Sea Power_Data",
        "StreamingAssets",
        "user",
        "missions",
        "user_missions");

    if (!Directory.Exists(missionDirectory))
    {
        throw new DirectoryNotFoundException($"Sea Power user mission directory was not found at '{missionDirectory}'.");
    }

    string targetPath = Path.Combine(missionDirectory, DemoOperation.ScenarioFileName);
    if (File.Exists(targetPath) && !FilesMatch(sourcePath, targetPath) && !replace)
    {
        throw new IOException($"'{targetPath}' already exists with different content. Pass --replace only if it belongs to this test campaign.");
    }

    File.Copy(sourcePath, targetPath, true);
    Console.WriteLine($"Installed scenario: {targetPath}");
    Console.WriteLine($"SHA256: {GetSha256(targetPath)}");
    return 0;
}

static int ShowStatus(string workspace)
{
    CampaignState campaign = new CampaignStore().Load(GetCampaignPath(workspace));
    Console.WriteLine($"Campaign: {campaign.CampaignId}");
    Console.WriteLine($"Turn: {campaign.Turn}");
    Console.WriteLine($"Revision: {campaign.Revision}");
    Console.WriteLine($"Phase: {campaign.Phase}");

    foreach (CampaignUnit unit in campaign.Units)
    {
        string availability = unit.Destroyed ? "destroyed" : "available";
        Console.WriteLine(
            $"{unit.Id}  {unit.Name}  condition {unit.Condition}%  fuel {unit.Fuel}%  ammo {unit.PrimaryAmmunition}  {availability}");
    }

    if (campaign.PendingScenario is not null)
    {
        Console.WriteLine($"Pending operation: {campaign.PendingScenario.Title}");
    }

    return 0;
}

static int Resolve(string workspace)
{
    CampaignStore store = new();
    string campaignPath = GetCampaignPath(workspace);
    CampaignState campaign = store.Load(campaignPath);
    AfterActionReport report = store.LoadAfterAction(GetAfterActionPath(workspace));
    CampaignState resolved = CampaignWorkflow.ApplyAfterAction(campaign, report);
    store.Save(campaignPath, resolved);

    Console.WriteLine($"Resolved {report.OperationId}.");
    Console.WriteLine($"Campaign advanced to turn {resolved.Turn}.");
    return 0;
}

static bool FilesMatch(string firstPath, string secondPath)
{
    return File.ReadAllBytes(firstPath).AsSpan().SequenceEqual(File.ReadAllBytes(secondPath));
}

static string GetSha256(string path)
{
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static string GetCampaignPath(string workspace)
{
    return Path.Combine(workspace, "campaign.json");
}

static string GetScenarioPath(string workspace)
{
    return Path.Combine(workspace, "generated", DemoOperation.ScenarioFileName);
}

static string GetAfterActionPath(string workspace)
{
    return Path.Combine(workspace, "after-action.json");
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    ShowUsage();
    return 1;
}

static void ShowUsage()
{
    Console.WriteLine("Crimean Tom operations campaign trial");
    Console.WriteLine();
    Console.WriteLine("dotnet run --project src/CrimeanTom.Operations.Cli -- init [workspace]");
    Console.WriteLine("dotnet run --project src/CrimeanTom.Operations.Cli -- prepare [workspace]");
    Console.WriteLine("dotnet run --project src/CrimeanTom.Operations.Cli -- install [workspace] [SeaPowerDir] [--replace]");
    Console.WriteLine("dotnet run --project src/CrimeanTom.Operations.Cli -- status [workspace]");
    Console.WriteLine("dotnet run --project src/CrimeanTom.Operations.Cli -- resolve [workspace]");
    Console.WriteLine();
    Console.WriteLine("The default workspace is artifacts/demo-campaign.");
}
