using System;
using System.Collections.Generic;
using System.Linq;

namespace CrimeanTom.SeaPower;

internal static class FleetClashForceGenerator
{
    private static readonly IReadOnlyDictionary<FleetClashSize, FleetRole[]> Blueprints =
        new Dictionary<FleetClashSize, FleetRole[]>
        {
            [FleetClashSize.Small] =
                [FleetRole.Command, FleetRole.SurfaceStrike, FleetRole.Escort],
            [FleetClashSize.Medium] =
                [FleetRole.Command, FleetRole.AirDefense, FleetRole.SurfaceStrike,
                 FleetRole.AntiSubmarine, FleetRole.Escort],
            [FleetClashSize.Large] =
                [FleetRole.Command, FleetRole.AirDefense, FleetRole.AirDefense,
                 FleetRole.SurfaceStrike, FleetRole.SurfaceStrike,
                 FleetRole.AntiSubmarine, FleetRole.AntiSubmarine, FleetRole.Escort],
        };

    internal static bool IsViable(FleetSidePlan side) =>
        BuildPool(side).Any(candidate => candidate.IsRegional);

    internal static IReadOnlyList<FleetCandidate> GenerateCandidates(
        FleetSidePlan side,
        FleetClashSize size,
        int count,
        System.Random random)
    {
        IReadOnlyList<FleetUnitArchetype> pool = BuildPool(side);
        if (pool.Count == 0 || !pool.Any(candidate => candidate.IsRegional))
        {
            throw new InvalidOperationException($"{side.DisplayName} has no usable regional combatants.");
        }

        return Enumerable.Range(0, count)
            .Select(_ => GenerateCandidate(pool, size, random))
            .ToArray();
    }

    internal static FleetClashForce Finalize(FleetCandidate fleet, System.Random random)
    {
        FleetClashShip[] ships = new FleetClashShip[fleet.Ships.Count];
        for (int index = 0; index < fleet.Ships.Count; index++)
        {
            FleetUnitArchetype ship = fleet.Ships[index];
            FleetRole role = fleet.Roles[index];
            ships[index] = new FleetClashShip(
                ship.UnitType,
                ship.Nation,
                ship.VariantReferences[random.Next(ship.VariantReferences.Count)],
                ResolveStationRole(role),
                role,
                ship.PointCost,
                ship.SurfaceCapability,
                ship.AirDefenseCapability,
                ship.AswCapability,
                ship.Resilience,
                ship.IsSubmarine,
                ship.IsCarrier);
        }

        return new FleetClashForce(
            ships,
            fleet.PointCost,
            fleet.SurfaceCapability,
            fleet.AirDefenseCapability,
            fleet.AswCapability,
            fleet.Resilience);
    }

    private static IReadOnlyList<FleetUnitArchetype> BuildPool(FleetSidePlan side)
    {
        HashSet<string> regionalNations = ExpandSelections(side.RegionalSelections);
        HashSet<string> alliedNations = ExpandSelections(side.AlliedSelections);
        alliedNations.ExceptWith(regionalNations);
        List<FleetUnitArchetype> output = [];
        AddArchetypes(output, regionalNations, isRegional: true);
        AddArchetypes(output, alliedNations, isRegional: false);
        AddSubmarineArchetypes(output, regionalNations, isRegional: true);
        AddSubmarineArchetypes(output, alliedNations, isRegional: false);
        return output;
    }

    private static HashSet<string> ExpandSelections(IEnumerable<string> selections)
    {
        HashSet<string> nations = new(StringComparer.OrdinalIgnoreCase);
        foreach (string selection in selections)
        {
            IReadOnlyCollection<string> coalitionNations =
                TaskForceNationRules.GetCoalitionMemberNations(selection);
            if (coalitionNations.Count > 0)
            {
                nations.UnionWith(coalitionNations);
            }
            else
            {
                nations.Add(selection);
            }
        }

        return nations;
    }

    private static void AddArchetypes(
        ICollection<FleetUnitArchetype> output,
        IReadOnlyCollection<string> nations,
        bool isRegional)
    {
        IEnumerable<IGrouping<(string UnitType, string Nation), TaskForceRosterGenerator.RosterUnitOption>> groups =
            TaskForceRosterGenerator.GetVesselOptions(nations)
                .GroupBy(option => (option.UnitType, option.Nation));
        foreach (IGrouping<(string UnitType, string Nation), TaskForceRosterGenerator.RosterUnitOption> group in groups)
        {
            VesselCombatProfile? profile = VesselCombatProfileReader.Read(group.Key.UnitType);
            if (profile is null)
            {
                continue;
            }

            int surface = profile.SurfaceCapability;
            int airDefense = profile.AirDefenseCapability;
            int asw = profile.AswCapability;
            if (surface + airDefense + asw == 0)
            {
                continue;
            }

            output.Add(new FleetUnitArchetype(
                group.Key.UnitType,
                group.Key.Nation,
                group.Select(option => option.VariantReference).Distinct().ToArray(),
                isRegional,
                profile.ApproximatePointCost,
                surface,
                airDefense,
                asw,
                profile.Resilience,
                isSubmarine: false,
                profile.IsCarrier));
        }
    }

    private static void AddSubmarineArchetypes(
        ICollection<FleetUnitArchetype> output,
        IReadOnlyCollection<string> nations,
        bool isRegional)
    {
        foreach (IGrouping<(string UnitType, string Nation), ScenarioSubmarineOption> group in
                 ScenarioUnitCatalog.GetSubmarines(nations)
                     .GroupBy(option => (option.UnitType, option.Nation)))
        {
            VesselCombatProfile? profile = VesselCombatProfileReader.Read(group.Key.UnitType);
            if (profile is null)
            {
                continue;
            }

            output.Add(new FleetUnitArchetype(
                group.Key.UnitType,
                group.Key.Nation,
                group.Select(option => option.VariantReference).Distinct().ToArray(),
                isRegional,
                profile.ApproximatePointCost,
                profile.SurfaceCapability,
                profile.AirDefenseCapability,
                profile.AswCapability,
                profile.Resilience,
                isSubmarine: true,
                isCarrier: false));
        }
    }

    private static FleetCandidate GenerateCandidate(
        IReadOnlyList<FleetUnitArchetype> pool,
        FleetClashSize size,
        System.Random random)
    {
        FleetRole[] roles = Blueprints[size];
        FleetUnitArchetype[] surfacePool = pool.Where(candidate => !candidate.IsSubmarine).ToArray();
        FleetUnitArchetype[] regional = surfacePool.Where(candidate => candidate.IsRegional).ToArray();
        string[] regionalNations = regional
            .Select(candidate => candidate.Nation)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string leadNation = regionalNations[random.Next(regionalNations.Length)];
        List<FleetUnitArchetype> ships = [];

        foreach (FleetRole role in roles)
        {
            IReadOnlyList<FleetUnitArchetype> source = SelectSourcePool(
                surfacePool,
                leadNation,
                size,
                role,
                random);
            ships.Add(WeightedPick(source, surfacePool, ships, role, random));
        }

        FleetUnitArchetype[] submarines = pool.Where(candidate => candidate.IsSubmarine).ToArray();
        int submarineCount = GetSubmarineCount(size, submarines.Length > 0, random);
        int[] replacementSlots = roles
            .Select((role, index) => (Role: role, Index: index))
            .Where(item => item.Role != FleetRole.Command)
            .OrderBy(item => item.Role == FleetRole.AntiSubmarine ? 0 :
                item.Role == FleetRole.SurfaceStrike ? 1 : 2)
            .Select(item => item.Index)
            .Take(submarineCount)
            .ToArray();
        foreach (int index in replacementSlots)
        {
            IReadOnlyList<FleetUnitArchetype> source = SelectSourcePool(
                submarines,
                leadNation,
                size,
                roles[index],
                random);
            ships[index] = WeightedPick(source, submarines, ships, roles[index], random);
        }

        return new FleetCandidate(ships, roles);
    }

    private static int GetSubmarineCount(
        FleetClashSize size,
        bool submarinesAvailable,
        System.Random random)
    {
        if (!submarinesAvailable)
        {
            return 0;
        }

        double roll = random.NextDouble();
        return size switch
        {
            FleetClashSize.Small => roll < 0.25d ? 1 : 0,
            FleetClashSize.Medium => roll < 0.50d ? 1 : 0,
            FleetClashSize.Large => roll < 0.20d ? 2 : roll < 0.80d ? 1 : 0,
            _ => 0,
        };
    }

    private static IReadOnlyList<FleetUnitArchetype> SelectSourcePool(
        IReadOnlyList<FleetUnitArchetype> pool,
        string leadNation,
        FleetClashSize size,
        FleetRole role,
        System.Random random)
    {
        FleetUnitArchetype[] allied = pool.Where(candidate => !candidate.IsRegional).ToArray();
        FleetUnitArchetype[] leadNationShips = pool.Where(candidate =>
            candidate.IsRegional &&
            string.Equals(candidate.Nation, leadNation, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (role == FleetRole.Command)
        {
            double alliedCommandChance = size switch
            {
                FleetClashSize.Small => 0.01d,
                FleetClashSize.Medium => 0.03d,
                FleetClashSize.Large => 0.06d,
                _ => throw new ArgumentOutOfRangeException(nameof(size)),
            };
            if (allied.Length > 0 && random.NextDouble() < alliedCommandChance)
            {
                return allied;
            }

            return leadNationShips.Length > 0
                ? leadNationShips
                : pool.Where(candidate => candidate.IsRegional).ToArray();
        }

        if (allied.Length > 0 && random.NextDouble() < GetAlliedDetachmentChance(size, role))
        {
            return allied;
        }

        FleetUnitArchetype[] regionalPartners = pool.Where(candidate =>
            candidate.IsRegional &&
            !string.Equals(candidate.Nation, leadNation, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (regionalPartners.Length > 0 &&
            random.NextDouble() < GetRegionalPartnerChance(size, role))
        {
            return regionalPartners;
        }

        if (leadNationShips.Length > 0 && random.NextDouble() < GetLeadNationChance(size))
        {
            return leadNationShips;
        }

        FleetUnitArchetype[] regional = pool.Where(candidate => candidate.IsRegional).ToArray();
        return regional.Length > 0 ? regional : pool;
    }

    private static FleetUnitArchetype WeightedPick(
        IReadOnlyList<FleetUnitArchetype> preferredPool,
        IReadOnlyList<FleetUnitArchetype> completePool,
        IReadOnlyCollection<FleetUnitArchetype> selected,
        FleetRole role,
        System.Random random)
    {
        FleetUnitArchetype[] capable = preferredPool
            .Where(candidate => RoleCapability(candidate, role) > 0d)
            .ToArray();
        if (capable.Length == 0)
        {
            capable = completePool.Where(candidate => RoleCapability(candidate, role) > 0d).ToArray();
        }

        IReadOnlyList<FleetUnitArchetype> choices = capable.Length > 0 ? capable : preferredPool;
        double[] weights = choices.Select(candidate =>
        {
            int duplicateCount = selected.Count(existing =>
                string.Equals(existing.UnitType, candidate.UnitType, StringComparison.OrdinalIgnoreCase));
            double varietyFactor = duplicateCount switch
            {
                0 => 1d,
                1 => 0.18d,
                _ => 0.025d,
            };
            return Math.Pow(1d + RoleCapability(candidate, role), 1.25d) * varietyFactor;
        }).ToArray();
        double roll = random.NextDouble() * weights.Sum();
        for (int index = 0; index < choices.Count; index++)
        {
            roll -= weights[index];
            if (roll <= 0d)
            {
                return choices[index];
            }
        }

        return choices[^1];
    }

    private static double RoleCapability(FleetUnitArchetype candidate, FleetRole role) => role switch
    {
        FleetRole.Command =>
            candidate.SurfaceCapability + candidate.AirDefenseCapability +
            (candidate.AswCapability * 0.4d) + (candidate.Resilience * 2d),
        FleetRole.AirDefense => candidate.AirDefenseCapability * 2d + candidate.Resilience,
        FleetRole.SurfaceStrike => candidate.SurfaceCapability * 2d + candidate.Resilience,
        FleetRole.AntiSubmarine => candidate.AswCapability * 2d + candidate.Resilience,
        FleetRole.Escort =>
            candidate.SurfaceCapability + candidate.AirDefenseCapability +
            candidate.AswCapability + candidate.Resilience,
        _ => 0d,
    };

    private static string ResolveStationRole(FleetRole role) => role switch
    {
        FleetRole.Command => "Core",
        FleetRole.AirDefense => "AAW",
        FleetRole.AntiSubmarine => "ASW",
        _ => "Default",
    };

    private static double GetAlliedDetachmentChance(FleetClashSize size, FleetRole role)
    {
        double baseChance = size switch
        {
            FleetClashSize.Small => 0.05d,
            FleetClashSize.Medium => 0.16d,
            FleetClashSize.Large => 0.30d,
            _ => throw new ArgumentOutOfRangeException(nameof(size)),
        };
        return role == FleetRole.Escort ? baseChance + 0.05d : baseChance;
    }

    private static double GetRegionalPartnerChance(FleetClashSize size, FleetRole role)
    {
        if (role == FleetRole.Command)
        {
            return 0.05d;
        }

        if (role == FleetRole.Escort)
        {
            return size switch
            {
                FleetClashSize.Small => 0.70d,
                FleetClashSize.Medium => 0.60d,
                FleetClashSize.Large => 0.50d,
                _ => throw new ArgumentOutOfRangeException(nameof(size)),
            };
        }

        return size switch
        {
            FleetClashSize.Small => 0.28d,
            FleetClashSize.Medium => 0.38d,
            FleetClashSize.Large => 0.45d,
            _ => throw new ArgumentOutOfRangeException(nameof(size)),
        };
    }

    private static double GetLeadNationChance(FleetClashSize size) => size switch
    {
        FleetClashSize.Small => 0.72d,
        FleetClashSize.Medium => 0.58d,
        FleetClashSize.Large => 0.45d,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };
}
