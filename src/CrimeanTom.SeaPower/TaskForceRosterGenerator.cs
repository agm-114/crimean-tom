using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using HarmonyLib;

using SeapowerUI.ViewModels;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static class TaskForceRosterGenerator
{
    private const string CampaignFolderName = "crimean-tom-operations";
    private const RegexOptions PatternOptions =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // Match approved combatant hull families. The game files supply the concrete
    // classes and variants, including additions made by future game updates.
    private static readonly Regex[] ShipClassPatterns =
    [
        new(@"^es_ffg_.*", PatternOptions),
        new(@"^fgs_(?:ddg|ptg)_.*", PatternOptions),
        new(@"^ins_(?:ff|pt|ptg)_.*", PatternOptions),
        new(@"^ir_(?:fab|ffg|pf|pt|ptg)_.*", PatternOptions),
        new(@"^jmsdf_(?:dd|ddg|ddh)_.*", PatternOptions),
        new(@"^knm_(?:ffg|fs|msc|ptg)_.*", PatternOptions),
        new(@"^ln_ptg_.*", PatternOptions),
        new(@"^plan_(?:dd|ff|ffg|k|lst|msc|pt|ptg)_.*", PatternOptions),
        new(@"^ran_(?:ddg|ffg)_.*", PatternOptions),
        new(@"^rn_dd_.*", PatternOptions),
        new(@"^usn_(?:bb|cg|cgn|clg|cv|cvn|dd|ddg|ff|ffg|lha|lka|lpd|lsm|lst|msc|mso|pb|phm)_.*", PatternOptions),
        new(@"^wp_(?:bdk|bpk|cv|em|kr|mpk|mrk|msc|pb|pkr|pt|ptg|rkr|skr|takr)_.*", PatternOptions),
    ];

    private static readonly Regex VariantSectionPattern =
        new(@"^\[Variant(?<number>\d+)\]", PatternOptions);

    private static readonly Regex SquadronSectionPattern =
        new(@"^\[Squadron(?<number>\d+)\]", PatternOptions);

    private static readonly Regex AnySectionPattern =
        new(@"^\[(?<name>[^\]]+)\]", PatternOptions);

    private static readonly MethodInfo? ReadUnitCostMethod = AccessTools.Method(
        AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceModeRules"),
        "ReadUnitIniTaskForceCost",
        [typeof(string), typeof(string)]);

    private static IReadOnlyList<RosterVariant> _rosterVariants = Array.Empty<RosterVariant>();

    private static double _medianBaseCost = 1d;

    private static bool _baseCostsLoaded;

    private static bool _loadingBaseCosts;

    internal static void WriteNativeRoster()
    {
        try
        {
            string vesselsDirectory = Path.Combine(Application.streamingAssetsPath, "original", "vessels");
            string aircraftDirectory = Path.Combine(Application.streamingAssetsPath, "original", "aircraft");
            string rosterPath = Path.Combine(
                Application.streamingAssetsPath,
                "user",
                "campaigns",
                CampaignFolderName,
                "player_task_force_roster.ini");
            string[] shipClasses = FindShipClasses(vesselsDirectory);
            string[] aircraftClasses = FindAircraftClasses(aircraftDirectory);
            List<RosterVariant> rosterVariants = [];
            string contents = BuildRoster(
                vesselsDirectory,
                shipClasses,
                aircraftDirectory,
                aircraftClasses,
                rosterVariants);
            string? rosterDirectory = Path.GetDirectoryName(rosterPath);
            if (rosterDirectory is null)
            {
                throw new InvalidOperationException($"Could not resolve the roster directory for {rosterPath}");
            }

            Directory.CreateDirectory(rosterDirectory);
            if (!File.Exists(rosterPath) ||
                !string.Equals(File.ReadAllText(rosterPath), contents, StringComparison.Ordinal))
            {
                File.WriteAllText(
                    rosterPath,
                    contents,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            _rosterVariants = rosterVariants;
            _medianBaseCost = 1d;
            _baseCostsLoaded = false;
            TaskForceNationRules.InvalidateRosterStrengthCache();

            Debug.Log(
                $"[Crimean Tom Operations] prepared {shipClasses.Length} ship classes, " +
                $"{aircraftClasses.Length} aircraft classes, and {rosterVariants.Count} national variants");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Crimean Tom Operations] could not prepare the Task Force roster: {exception}");
        }
    }

    internal static int GetShipVariantCount(string? nation) =>
        string.IsNullOrWhiteSpace(nation)
            ? 0
            : _rosterVariants.Count(variant =>
                string.Equals(variant.UnitFolder, "vessels", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(variant.Nation, nation, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyCollection<string> GetKnownNations() =>
        _rosterVariants
            .Select(variant => variant.Nation)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static bool HasRosterUnits(string? nation) =>
        !string.IsNullOrWhiteSpace(nation) &&
        _rosterVariants.Any(variant =>
            string.Equals(variant.Nation, nation, StringComparison.OrdinalIgnoreCase));

    internal static RosterStrength GetRosterStrength(IEnumerable<string> nations)
    {
        HashSet<string> selectedNations = new(
            nations.Where(nation => !string.IsNullOrWhiteSpace(nation)),
            StringComparer.OrdinalIgnoreCase);
        List<RosterVariant> eligibleVariants = _rosterVariants
            .Where(variant => selectedNations.Contains(variant.Nation))
            .ToList();
        if (eligibleVariants.Count == 0)
        {
            return RosterStrength.Empty;
        }

        List<IGrouping<string, RosterVariant>> classes = eligibleVariants
            .GroupBy(variant => GetUnitKey(variant.UnitFolder, variant.UnitType), StringComparer.OrdinalIgnoreCase)
            .ToList();
        double classScore = classes.Sum(group =>
        {
            RosterVariant representative = group.First();
            double normalizedCost = Math.Clamp(representative.BaseCost / _medianBaseCost, 0.25d, 4d);
            double variantDepth = 1d + (0.20d * Math.Log(Math.Max(1, group.Count())));
            return Math.Sqrt(normalizedCost) * variantDepth * representative.RoleWeight;
        });
        int roleCount = eligibleVariants
            .Select(variant => variant.Role)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        double coverage = Math.Min(1d, 0.75d + (0.05d * roleCount));

        return new RosterStrength(
            classScore * coverage,
            classes.Count,
            eligibleVariants.Count,
            roleCount);
    }

    internal static bool RefreshBaseCosts()
    {
        if (_baseCostsLoaded)
        {
            return true;
        }

        if (_loadingBaseCosts)
        {
            return false;
        }

        _loadingBaseCosts = true;
        try
        {
            Dictionary<string, int> costs = _rosterVariants
                .GroupBy(
                    variant => GetUnitKey(variant.UnitFolder, variant.UnitType),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        RosterVariant variant = group.First();
                        return ReadUnitBaseCost(variant.UnitFolder, variant.UnitType);
                    },
                    StringComparer.OrdinalIgnoreCase);
            if (!costs.Values.Any(cost => cost > 0))
            {
                return false;
            }

            foreach (RosterVariant variant in _rosterVariants)
            {
                variant.SetBaseCost(costs[GetUnitKey(variant.UnitFolder, variant.UnitType)]);
            }

            _medianBaseCost = CalculateMedianBaseCost(_rosterVariants);
            _baseCostsLoaded = true;
            TaskForceNationRules.InvalidateRosterStrengthCache();
            Debug.Log($"[Crimean Tom Operations] loaded roster costs; median base cost {_medianBaseCost:0.##}");
            return true;
        }
        finally
        {
            _loadingBaseCosts = false;
        }
    }

    private static string[] FindShipClasses(string vesselsDirectory) =>
        Directory.EnumerateFiles(vesselsDirectory, "*.ini", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name =>
                name is not null &&
                !name.EndsWith("_variants", StringComparison.OrdinalIgnoreCase) &&
                ShipClassPatterns.Any(pattern => pattern.IsMatch(name)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;

    private static string[] FindAircraftClasses(string aircraftDirectory) =>
        Directory.EnumerateFiles(aircraftDirectory, "*_squadrons.ini", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name![..^"_squadrons".Length])
            .Where(name =>
                !name.StartsWith("civ_", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(aircraftDirectory, $"{name}.ini")))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildRoster(
        string vesselsDirectory,
        string[] shipClasses,
        string aircraftDirectory,
        string[] aircraftClasses,
        ICollection<RosterVariant> rosterVariants)
    {
        StringBuilder roster = new();
        roster.AppendLine("[AllowedVessels]");

        foreach (string shipClass in shipClasses)
        {
            List<VariantDefinition> variants = FindReferences(
                vesselsDirectory,
                shipClass,
                "_variants.ini",
                VariantSectionPattern,
                "Variant");
            if (variants.Count == 0)
            {
                Debug.LogWarning($"[Crimean Tom Operations] no installed variants found for {shipClass}");
                continue;
            }

            roster
                .Append(shipClass)
                .Append('=')
                .AppendJoin(',', variants.Select(variant => variant.Reference))
                .AppendLine();

            (string role, double roleWeight) = ResolveShipRole(shipClass);
            foreach (VariantDefinition variant in variants.Where(variant =>
                !string.IsNullOrWhiteSpace(variant.Nation)))
            {
                rosterVariants.Add(new RosterVariant(
                    variant.Nation,
                    "vessels",
                    shipClass,
                    0,
                    role,
                    roleWeight));
            }
        }

        roster.AppendLine().AppendLine("[AllowedHelicopters]");
        AppendAircraftRosterSection(
            roster,
            aircraftDirectory,
            aircraftClasses,
            includeHelicopters: true,
            rosterVariants);

        roster.AppendLine().AppendLine("[AllowedAircraft]");
        AppendAircraftRosterSection(
            roster,
            aircraftDirectory,
            aircraftClasses,
            includeHelicopters: false,
            rosterVariants);

        roster.AppendLine().AppendLine("[LoadoutPrices]");
        return roster.ToString();
    }

    private static void AppendAircraftRosterSection(
        StringBuilder roster,
        string aircraftDirectory,
        IEnumerable<string> aircraftClasses,
        bool includeHelicopters,
        ICollection<RosterVariant> rosterVariants)
    {
        foreach (string aircraftClass in aircraftClasses.Where(aircraftClass =>
            IsHelicopter(aircraftDirectory, aircraftClass) == includeHelicopters))
        {
            List<VariantDefinition> squadrons = FindReferences(
                aircraftDirectory,
                aircraftClass,
                "_squadrons.ini",
                SquadronSectionPattern,
                "Squadron");
            if (squadrons.Count == 0)
            {
                Debug.LogWarning($"[Crimean Tom Operations] no installed squadrons found for {aircraftClass}");
                continue;
            }

            roster
                .Append(aircraftClass)
                .Append('=')
                .AppendJoin(',', squadrons.Select(squadron => squadron.Reference))
                .AppendLine();

            (string role, double roleWeight) = ResolveAircraftRole(
                aircraftDirectory,
                aircraftClass,
                includeHelicopters);
            foreach (VariantDefinition squadron in squadrons.Where(squadron =>
                !string.IsNullOrWhiteSpace(squadron.Nation)))
            {
                rosterVariants.Add(new RosterVariant(
                    squadron.Nation,
                    "aircraft",
                    aircraftClass,
                    0,
                    role,
                    roleWeight));
            }
        }
    }

    private static List<VariantDefinition> FindReferences(
        string unitDirectory,
        string unitClass,
        string metadataSuffix,
        Regex referenceSectionPattern,
        string referencePrefix)
    {
        List<VariantDefinition> variants = [];
        string reference = string.Empty;
        string nation = string.Empty;
        string defaultNation = string.Empty;
        bool readingDefault = false;

        foreach (string rawLine in File.ReadLines(Path.Combine(unitDirectory, $"{unitClass}{metadataSuffix}")))
        {
            string line = rawLine.Trim();
            Match sectionMatch = referenceSectionPattern.Match(line);
            if (sectionMatch.Success)
            {
                AddVariantIfPresent(variants, reference, nation, defaultNation);
                reference = $"{referencePrefix}{int.Parse(sectionMatch.Groups["number"].Value)}";
                nation = string.Empty;
                readingDefault = false;
                continue;
            }

            Match anySectionMatch = AnySectionPattern.Match(line);
            if (anySectionMatch.Success)
            {
                AddVariantIfPresent(variants, reference, nation, defaultNation);
                reference = string.Empty;
                nation = string.Empty;
                readingDefault = string.Equals(
                    anySectionMatch.Groups["name"].Value,
                    "Default",
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.StartsWith("Nation=", StringComparison.OrdinalIgnoreCase))
            {
                string parsedNation = ParseIniValue(line["Nation=".Length..]);
                if (readingDefault)
                {
                    defaultNation = parsedNation;
                }
                else if (!string.IsNullOrWhiteSpace(reference))
                {
                    nation = parsedNation;
                }
            }
        }

        AddVariantIfPresent(variants, reference, nation, defaultNation);
        return variants.OrderBy(variant => ParseVariantNumber(variant.Reference)).ToList();
    }

    private static void AddVariantIfPresent(
        ICollection<VariantDefinition> variants,
        string reference,
        string nation,
        string defaultNation)
    {
        if (!string.IsNullOrWhiteSpace(reference))
        {
            variants.Add(new VariantDefinition(
                reference,
                string.IsNullOrWhiteSpace(nation) ? defaultNation : nation));
        }
    }

    private static int ParseVariantNumber(string reference) =>
        int.TryParse(
            new string(reference.SkipWhile(character => !char.IsDigit(character)).ToArray()),
            out int number)
                ? number
                : int.MaxValue;

    private static string ParseIniValue(string value) =>
        value.Split(["//", "#"], StringSplitOptions.None)[0].Trim();

    private static bool IsHelicopter(string aircraftDirectory, string aircraftClass) =>
        File.ReadLines(Path.Combine(aircraftDirectory, $"{aircraftClass}.ini"))
            .Select(line => line.Trim())
            .Any(line =>
                line.StartsWith("UnitType=", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    ParseIniValue(line["UnitType=".Length..]),
                    "Helicopter",
                    StringComparison.OrdinalIgnoreCase));

    private static int ReadUnitBaseCost(string unitFolder, string unitType)
    {
        try
        {
            return ReadUnitCostMethod?.Invoke(null, [unitFolder, unitType]) is int cost
                ? Math.Max(0, cost)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double CalculateMedianBaseCost(IEnumerable<RosterVariant> variants)
    {
        int[] costs = variants
            .Select(variant => variant.BaseCost)
            .Where(cost => cost > 0)
            .Distinct()
            .OrderBy(cost => cost)
            .ToArray();
        if (costs.Length == 0)
        {
            return 1d;
        }

        int middle = costs.Length / 2;
        return costs.Length % 2 == 0
            ? (costs[middle - 1] + costs[middle]) / 2d
            : costs[middle];
    }

    private static (string Role, double Weight) ResolveShipRole(string unitType)
    {
        string designator = unitType.Split('_').Skip(1).FirstOrDefault() ?? string.Empty;
        return designator.ToLowerInvariant() switch
        {
            "cv" or "cvn" or "takr" => ("Carrier", 1.15d),
            "lha" or "lka" or "lpd" or "lsm" or "lst" or "bdk" => ("Amphibious", 0.75d),
            "bb" or "cg" or "cgn" or "clg" or "kr" or "rkr" => ("Major surface combatant", 1.10d),
            "msc" or "mso" => ("Mine warfare", 0.50d),
            "fab" or "fs" or "mpk" or "mrk" or "pb" or "pf" or "phm" or "pt" or "ptg" => ("Patrol", 0.60d),
            _ => ("Surface combatant", 1d),
        };
    }

    private static (string Role, double Weight) ResolveAircraftRole(
        string aircraftDirectory,
        string aircraftClass,
        bool isHelicopter)
    {
        string roles = File.ReadLines(Path.Combine(aircraftDirectory, $"{aircraftClass}.ini"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Role=", StringComparison.OrdinalIgnoreCase))
            .Select(line => ParseIniValue(line["Role=".Length..]))
            .FirstOrDefault() ?? (isHelicopter ? "Helicopter" : "Aircraft");
        string primaryRole = roles.Split(',')[0].Trim();
        double weight = primaryRole.ToLowerInvariant() switch
        {
            "fighter" or "aew" => 1.10d,
            "attack" or "asuw" or "asw" or "bomber" or "heavybomber" or "mpa" => 1d,
            "ew" or "recon" or "sead" => 0.85d,
            "transport" or "airliner" => 0.60d,
            _ => 0.80d,
        };

        return ($"{(isHelicopter ? "Helicopter" : "Aircraft")}: {primaryRole}",
            isHelicopter ? weight * 0.75d : weight);
    }

    private static string GetUnitKey(string unitFolder, string unitType) =>
        $"{unitFolder}/{unitType}";

    private sealed class VariantDefinition(string reference, string nation)
    {
        internal string Reference { get; } = reference;

        internal string Nation { get; } = nation;
    }

    private sealed class RosterVariant(
        string nation,
        string unitFolder,
        string unitType,
        int baseCost,
        string role,
        double roleWeight)
    {
        internal string Nation { get; } = nation;

        internal string UnitFolder { get; } = unitFolder;

        internal string UnitType { get; } = unitType;

        internal int BaseCost { get; private set; } = baseCost;

        internal string Role { get; } = role;

        internal double RoleWeight { get; } = roleWeight;

        internal void SetBaseCost(int baseCost) => BaseCost = baseCost;
    }

    internal sealed class RosterStrength(
        double score,
        int classCount,
        int variantCount,
        int roleCount)
    {
        internal static RosterStrength Empty { get; } = new(0d, 0, 0, 0);

        internal double Score { get; } = score;

        internal int ClassCount { get; } = classCount;

        internal int VariantCount { get; } = variantCount;

        internal int RoleCount { get; } = roleCount;
    }
}
