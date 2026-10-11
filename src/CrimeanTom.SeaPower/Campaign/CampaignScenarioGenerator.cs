using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;
using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class ScenarioDebugGenerator
{
    internal static bool IsCampaignContractPath(string? path) =>
        TaskForceNationRules.IsOurCampaignPath(path) &&
        path!.Replace('\\', '/').Contains("/missions/", StringComparison.OrdinalIgnoreCase);

    internal static bool GenerateCampaignContractTemplate(
        string sourceMissionPath,
        LinearCampaignTaskForceSnapshot snapshot)
    {
        LastGenerationError = null;
        try
        {
            CampaignContractKind contract = ResolveCampaignContract(sourceMissionPath);
            var playerUnits = snapshot.Items.Where(item => item is not null && !item.IsAirUnit).ToArray();
            FleetClashForce playerForce = BuildCampaignPlayerForce(playerUnits);
            if (playerForce.Ships.Count == 0)
            {
                throw new InvalidOperationException("The task force has no deployable ships or submarines.");
            }

            CampaignContractOffer offer = CampaignContractOffers.Get(CampaignContractRules.MissionIndex(contract));
            if (offer.MinimumDeploymentPoints > playerForce.PointCost)
                throw new InvalidOperationException($"This contract's fixed nearby activity requires at least {offer.MinimumDeploymentPoints} deployed ship points. Deploy more ships or choose another contract.");
            int seed = offer.Seed;
            System.Random random = new(seed);
            FleetClashSize size = offer.Size;
            string playerTaskforce = contract == CampaignContractKind.RouteInterdiction
                ? "Taskforce2"
                : "Taskforce1";
            FleetClashMatch match = BuildCampaignMatch(
                offer.Match,
                playerTaskforce,
                playerForce,
                snapshot.TaskForceName);
            FleetClashGenerationOptions options = new()
            {
                Size = size,
                SeaState = 1,
                PlayerTaskforce = playerTaskforce,
                PlayerControlPercentage = 100,
                Starting = CampaignOperationsState.StartingOptions,
                ActivityPlan = offer.Activity,
                ChaseAllowance = offer.ChaseAllowance,
                EnemySizeMultiplier = CampaignOperationsState.Difficulty.Multiplier,
                RoutePerspective = contract == CampaignContractKind.RouteInterdiction
                    ? RouteOperationPerspective.Interdict
                    : RouteOperationPerspective.Escort,
                RouteVariant = offer.Variant,
            };
            ScenarioParameters parameters = ScenarioParameters.Create(
                random,
                seed,
                size,
                options,
                match);
            string contents;
            if (contract == CampaignContractKind.FleetClash)
            {
                contents = BuildFleetClash(
                    match,
                    parameters,
                    size,
                    playerTaskforce,
                    playerForce.Ships.Count,
                    options,
                    useLauncherMetadata: false);
            }
            else if (contract is CampaignContractKind.RouteEscort or CampaignContractKind.RouteInterdiction)
            {
                RouteOperationPlan route = CreateRouteOperationPlan(
                    random,
                    match,
                    parameters,
                    size,
                    playerTaskforce,
                    playerForce.Ships.Count,
                    options.RouteVariant,
                    convoyRoster: offer.Match.First);
                contents = BuildRouteOperation(
                    match,
                    parameters,
                    size,
                    playerTaskforce,
                    options,
                    route,
                    useLauncherMetadata: false);
            }
            else
            {
                contents = BuildPassageOperation(
                    match,
                    parameters,
                    size,
                    playerForce.Ships.Count,
                    options);
            }

            contents = MarkCampaignTemplate(contents, playerTaskforce, contract, seed);
            File.WriteAllText(
                sourceMissionPath,
                NormalizeLineEndings(contents),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            IniHandler template = Utils.openIniFile(sourceMissionPath, logErrors: true, useCaching: false);
            if (contract == CampaignContractKind.FleetClash)
            {
                Dictionary<string, int> values = new(StringComparer.OrdinalIgnoreCase);
                new[] { ("Taskforce1", match.First), ("Taskforce2", match.Second) }.ForEach(side =>
                    MissionUnitReferences.NumberHulls(side.Item1, side.Item2.Ships, ship => ship.IsSubmarine)
                        .ForEach(entry => values[entry.Reference] = entry.Unit.PointCost));
                CampaignValueObjectives.Apply(template.Data, values, seed);
            }
            // Use the same classified force and source order as deployment, including folder-only submarines.
            var originalUnitIds = MissionUnitReferences.NumberHulls(playerTaskforce, playerForce.Ships, ship => ship.IsSubmarine)
                .Zip(playerUnits, (entry, item) => (entry.Reference, item.InstanceId))
                .ToDictionary(entry => entry.Reference, entry => entry.InstanceId, StringComparer.OrdinalIgnoreCase);
            CampaignMissionData.Remember(template.Data, originalUnitIds);
            template.saveFile(overwrite: true);
            Debug.Log(
                $"[Crimean Tom Operations] prepared campaign contract {contract} seed {seed}; " +
                $"player {playerForce.PointCost} points versus generated " +
                $"{(playerTaskforce == "Taskforce1" ? match.Second.PointCost : match.First.PointCost)} points");
            return true;
        }
        catch (Exception exception)
        {
            LastGenerationError = exception.Message;
            Debug.LogError(
                $"[Crimean Tom Operations] could not prepare campaign contract '{sourceMissionPath}': {exception}");
            return false;
        }
    }

    private static CampaignContractKind ResolveCampaignContract(string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.Contains("passage", StringComparison.OrdinalIgnoreCase))
        {
            return CampaignContractKind.Passage;
        }

        if (fileName.Contains("interdiction", StringComparison.OrdinalIgnoreCase))
        {
            return CampaignContractKind.RouteInterdiction;
        }

        return fileName.Contains("escort", StringComparison.OrdinalIgnoreCase)
            ? CampaignContractKind.RouteEscort
            : CampaignContractKind.FleetClash;
    }

    private static FleetClashForce BuildCampaignPlayerForce(
        IReadOnlyList<LinearCampaignTaskForceBuilderRosterItem> playerUnits)
    {
        FleetClashShip[] ships = playerUnits
            .Select(item =>
            {
                VesselCombatProfile? profile = VesselCombatProfileReader.Read(item.UnitType);
                int surface = profile?.SurfaceCapability ?? 1;
                int airDefense = profile?.AirDefenseCapability ?? 1;
                int asw = profile?.AswCapability ?? 1;
                FleetRole role = ResolveCampaignRole(surface, airDefense, asw);
                bool isSubmarine = string.Equals(
                        item.Category,
                        "Submarine",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.UnitFolder, "submarines", StringComparison.OrdinalIgnoreCase);
                return new FleetClashShip(
                    item.UnitType,
                    item.Nation,
                    item.VariantReference,
                    ResolveCampaignStationRole(role),
                    role,
                    profile?.ApproximatePointCost ?? Math.Max(1, item.BaseCostPerUnit),
                    surface,
                    airDefense,
                    asw,
                    profile?.Resilience ?? 1d,
                    isSubmarine,
                    profile?.IsCarrier ?? item.HasFixedWingFlightDeck);
            })
            .ToArray();
        return CreateForce(ships);
    }

    private static FleetRole ResolveCampaignRole(int surface, int airDefense, int asw)
    {
        int maximum = Math.Max(surface, Math.Max(airDefense, asw));
        if (maximum == airDefense)
        {
            return FleetRole.AirDefense;
        }

        return maximum == asw ? FleetRole.AntiSubmarine : FleetRole.SurfaceStrike;
    }

    private static string ResolveCampaignStationRole(FleetRole role) => role switch
    {
        FleetRole.AirDefense => "AAW",
        FleetRole.AntiSubmarine => "ASW",
        FleetRole.SurfaceStrike => "ASuW",
        _ => "Default",
    };

    private static FleetClashMatch BuildCampaignMatch(
        FleetClashMatch generated,
        string playerTaskforce,
        FleetClashForce playerForce,
        string taskForceName)
    {
        return generated with
        {
            FirstName = playerTaskforce == "Taskforce1"
                ? FirstNonEmpty(taskForceName, "Player Task Force")
                : generated.FirstName,
            SecondName = playerTaskforce == "Taskforce2"
                ? FirstNonEmpty(taskForceName, "Player Task Force")
                : generated.SecondName,
            First = playerTaskforce == "Taskforce1" ? playerForce : generated.First,
            Second = playerTaskforce == "Taskforce2" ? playerForce : generated.Second,
        };
    }

    private static string MarkCampaignTemplate(
        string contents,
        string playerTaskforce,
        CampaignContractKind contract,
        int seed)
    {
        string marker = FormattableString.Invariant(
            $"CampaignContract={contract}{System.Environment.NewLine}CampaignSeed={seed}{System.Environment.NewLine}");
        int settings = contents.IndexOf("[CrimeanTom]", StringComparison.OrdinalIgnoreCase);
        if (settings >= 0)
        {
            int lineEnd = contents.IndexOf('\n', settings);
            contents = contents.Insert(lineEnd + 1, marker);
        }

        HashSet<string> protectedReferences = ReadProtectedRouteReferences(
            contents,
            playerTaskforce,
            contract);
        Match nearby = Regex.Match(contents, @"^NearbyAIUnits=(.*)$", RegexOptions.Multiline);
        HashSet<string> nearbyReferences = nearby.Success
            ? new(nearby.Groups[1].Value.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase) : [];
        Regex playerUnitSection = MissionUnitReferences.HullSectionsFor(playerTaskforce);
        bool anchorWritten = false;
        return playerUnitSection.Replace(contents, match =>
        {
            string reference = match.Groups["reference"].Value;
            string marker = nearbyReferences.Contains(reference) ? string.Empty : protectedReferences.Contains(reference)
                ? "TaskForceModeIgnoreUnit=True"
                : "TaskForceModePlaceholderUnit=True";
            string anchorMarker = anchorWritten
                ? string.Empty
                : $"TaskForceModeAnchor=True{System.Environment.NewLine}";
            anchorWritten = true;
            return match.Value + System.Environment.NewLine + anchorMarker + marker;
        });
    }

    private static HashSet<string> ReadProtectedRouteReferences(
        string contents,
        string playerTaskforce,
        CampaignContractKind contract)
    {
        HashSet<string> references = new(StringComparer.OrdinalIgnoreCase);
        if (contract != CampaignContractKind.RouteEscort)
        {
            return references;
        }

        Match formation = Regex.Match(
            contents,
            $@"^{Regex.Escape(playerTaskforce)}_Formation\d+=(?<units>[^|\r\n]+)\|Protected Route Group\|",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!formation.Success)
        {
            return references;
        }

        foreach (string reference in formation.Groups["units"].Value.Split(','))
        {
            if (!string.IsNullOrWhiteSpace(reference))
            {
                references.Add(reference.Trim());
            }
        }
        return references;
    }

    private static string FirstNonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

[HarmonyPatch]
internal static class CampaignContractGenerationPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceMissionGenerator"),
        "TryGenerate");

    private static bool Prefix(
        string sourceMissionPath,
        LinearCampaignTaskForceSnapshot snapshot,
        ref bool __result,
        ref string generatedMissionPath,
        ref string failureHeader,
        ref string failureMessage)
    {
        if (ScenarioDebugGenerator.IsCampaignContractPath(sourceMissionPath) &&
            !ScenarioDebugGenerator.GenerateCampaignContractTemplate(sourceMissionPath, snapshot))
        {
            __result = false;
            generatedMissionPath = string.Empty;
            failureHeader = "Contract generation failed";
            failureMessage = ScenarioDebugGenerator.LastGenerationError ?? "The contract could not be generated. Return to the campaign and choose another contract.";
            return false;
        }
        return true;
    }

    private static void Postfix(
        object mission,
        string sourceMissionPath,
        ref string generatedMissionPath,
        ref bool __result,
        ref string failureHeader,
        ref string failureMessage)
    {
        if (!__result ||
            !ScenarioDebugGenerator.IsCampaignContractPath(sourceMissionPath) ||
            string.IsNullOrWhiteSpace(generatedMissionPath))
        {
            return;
        }

        try
        {
            CampaignOperationsState.ApplyGroupsToGeneratedMission(generatedMissionPath);
            ScenarioDebugGenerator.FinalizeCampaignContract(generatedMissionPath);
            IniHandler generated = Utils.openIniFile(generatedMissionPath, logErrors: true, useCaching: false);
            CampaignAircraftStock.TrimPatrols(generated.Data);
            CampaignMissionData.ApplyStartingOrders(generated.Data);
            generated.saveFile(overwrite: true);
            CampaignOperationsState.RecordLaunch(
                NativeCampaignAccess.GetMissionIndex(mission), generatedMissionPath);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Crimean Tom Operations] contract validation failed: {exception}");
            __result = false;
            generatedMissionPath = string.Empty;
            failureHeader = "Contract validation failed";
            failureMessage = "The generated contract could not be prepared safely. Return to the campaign and try again.";
        }
    }
}
