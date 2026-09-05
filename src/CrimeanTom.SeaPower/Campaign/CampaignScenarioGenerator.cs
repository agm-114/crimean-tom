using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using HarmonyLib;

using SeaPower;

using SeapowerUI.ViewModels;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal enum CampaignContractKind
{
    FleetClash,
    RouteEscort,
    RouteInterdiction,
    Passage,
}

internal static partial class ScenarioDebugGenerator
{
    internal static bool IsCampaignContractPath(string? path) =>
        TaskForceNationRules.IsOurCampaignPath(path) &&
        path!.Replace('\\', '/').Contains("/missions/", StringComparison.OrdinalIgnoreCase);

    internal static bool GenerateCampaignContractTemplate(
        string sourceMissionPath,
        LinearCampaignTaskForceSnapshot snapshot)
    {
        try
        {
            CampaignContractKind contract = ResolveCampaignContract(sourceMissionPath);
            FleetClashForce playerForce = BuildCampaignPlayerForce(snapshot);
            if (playerForce.Ships.Count == 0)
            {
                throw new InvalidOperationException("The task force has no deployable ships or submarines.");
            }

            CampaignContractOffer offer = CampaignContractOffers.Get((int)contract + 1);
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
                    options.RouteVariant);
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
                foreach (var side in new[] { ("Taskforce1", match.First), ("Taskforce2", match.Second) })
                {
                    int surface = 0, submarine = 0;
                    foreach (var ship in side.Item2.Ships)
                        values[side.Item1 + (ship.IsSubmarine ? "Submarine" + (++submarine) : "Vessel" + (++surface))] = ship.PointCost;
                }
                CampaignValueObjectives.Apply(template.Data, values, seed);
            }
            Dictionary<string, string> originalUnitIds = new(StringComparer.OrdinalIgnoreCase);
            int surfaceIndex = 0;
            int submarineIndex = 0;
            foreach (var item in snapshot.Items.Where(item => !item.IsAirUnit))
            {
                bool submarine = string.Equals(item.Category, "Submarine", StringComparison.OrdinalIgnoreCase);
                string reference = playerTaskforce + (submarine
                    ? "Submarine" + (++submarineIndex)
                    : "Vessel" + (++surfaceIndex));
                originalUnitIds.Add(reference, item.InstanceId);
            }
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
        LinearCampaignTaskForceSnapshot snapshot)
    {
        FleetClashShip[] ships = snapshot.Items
            .Where(item => item is not null && !item.IsAirUnit)
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
        return new FleetClashMatch(
                generated.Theater,
                generated.Latitude,
                generated.Longitude,
                playerTaskforce == "Taskforce1"
                    ? FirstNonEmpty(taskForceName, "Player Task Force")
                    : generated.FirstName,
                playerTaskforce == "Taskforce2"
                    ? FirstNonEmpty(taskForceName, "Player Task Force")
                    : generated.SecondName,
                playerTaskforce == "Taskforce1" ? playerForce : generated.First,
                playerTaskforce == "Taskforce2" ? playerForce : generated.Second,
                generated.BalanceError);
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
        Regex playerUnitSection = new(
            $@"^\[(?<{nameof(playerTaskforce)}>" +
            Regex.Escape(playerTaskforce) +
            @"(?:Vessel|Submarine)\d+)\]\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        bool anchorWritten = false;
        return playerUnitSection.Replace(contents, match =>
        {
            string reference = match.Groups[nameof(playerTaskforce)].Value;
            string marker = protectedReferences.Contains(reference)
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
            failureMessage = "The contract could not be generated. Return to the campaign and try again.";
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
            CampaignOperationsState.RecordLaunch(
                CampaignOperationsState.GetMissionIndex(mission), generatedMissionPath);
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
