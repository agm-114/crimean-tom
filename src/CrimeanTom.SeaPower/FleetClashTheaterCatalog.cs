using System;
using System.Collections.Generic;
using System.Linq;

namespace CrimeanTom.SeaPower;

internal static class FleetClashTheaterCatalog
{
    private static readonly FleetClashTheater[] Theaters =
    [
        new(
            "North Atlantic",
            58f,
            -23f,
            new(
                "NATO",
                ["UK", "Canada", "France", "Germany", "Netherlands", "Norway"],
                ["US", "Coalition_NATO", "Coalition_Commonwealth"]),
            new("Soviet Navy", ["Soviet"], ["Coalition_WarsawPact"])),
        new(
            "North Atlantic",
            61f,
            -18f,
            new(
                "NATO",
                ["UK", "Canada", "France", "Germany", "Netherlands", "Norway"],
                ["US", "Coalition_NATO", "Coalition_BlueDragons"]),
            new("Soviet Navy", ["Soviet"], ["Coalition_WarsawPact", "Coalition_RedDragons"])),
        new(
            "Norwegian Sea",
            67f,
            5f,
            new(
                "Scandinavian coalition",
                ["Denmark", "Norway", "Sweden"],
                ["US", "UK", "Coalition_NATO"]),
            new("Soviet Northern Fleet", ["Soviet"], ["Coalition_WarsawPact"])),
        new(
            "Baltic Sea",
            56f,
            18f,
            new(
                "Baltic NATO force",
                ["Germany", "Denmark", "Norway", "Sweden"],
                ["US", "UK", "Coalition_NATO"]),
            new("Warsaw Pact", ["DDR", "Poland"], ["Soviet", "Coalition_WarsawPact"])),
        new(
            "Eastern Mediterranean",
            35f,
            25f,
            new(
                "Eastern Mediterranean coalition",
                ["Greece", "Israel", "Turkey"],
                ["US", "Coalition_NATO"]),
            new(
                "Arab League",
                ["Egypt", "Libya", "Syria"],
                ["Coalition_ArabLeague", "Soviet", "Coalition_WarsawPact"])),
        new(
            "South China Sea",
            14f,
            115f,
            new(
                "Blue Dragons",
                ["Philippines", "South_Korea", "South_Vietnam"],
                ["Japan", "US", "Coalition_BlueDragons", "Coalition_Commonwealth"]),
            new(
                "Red Dragons",
                ["China", "Vietnam"],
                ["North_Korea", "Soviet", "Coalition_RedDragons", "Coalition_WarsawPact"])),
        new(
            "Arabian Sea",
            20f,
            65f,
            new(
                "Commonwealth",
                ["India", "Pakistan"],
                ["UK", "Australia", "Coalition_Commonwealth", "Coalition_NATO"]),
            new(
                "Arab League",
                ["Iran", "Iraq", "Kuwait", "Saudi", "South_Yemen"],
                ["Coalition_ArabLeague", "Soviet", "Coalition_WarsawPact"])),
    ];

    internal static IReadOnlyList<string> RegionNames { get; } = Theaters
        .Select(theater => theater.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal static FleetClashTheater Pick(
        System.Random random,
        Func<FleetSidePlan, bool> isViable,
        string? region = null)
    {
        List<FleetClashTheater> viable = [];
        foreach (FleetClashTheater theater in Theaters)
        {
            if ((string.IsNullOrWhiteSpace(region) ||
                 string.Equals(theater.Name, region, StringComparison.OrdinalIgnoreCase)) &&
                isViable(theater.First) &&
                isViable(theater.Second))
            {
                viable.Add(theater);
            }
        }

        if (viable.Count == 0)
        {
            throw new InvalidOperationException("No fleet-clash theater has usable combatants on both sides.");
        }

        return viable[random.Next(viable.Count)];
    }
}
