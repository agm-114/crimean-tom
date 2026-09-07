using static CrimeanTom.SeaPower.FleetClashTheaterCatalog;

namespace CrimeanTom.SeaPower;

internal static class FactionCatalog
{
    private const string CoalitionArtPath = "campaigns/crimean-tom-operations/art/coalitions";

    internal static readonly FactionDefinition Algeria =
        new("Algeria", FleetAlignment.Eastern, [], [EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Australia =
        new("Australia", FleetAlignment.Western, [], [Atlantic, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Belgium =
        new("Belgium", FleetAlignment.Western, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Bulgaria =
        new("Bulgaria", FleetAlignment.Eastern, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Canada =
        new("Canada", FleetAlignment.Western, [Atlantic, NorthernAtlantic],
            [NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition China =
        new("China", FleetAlignment.Eastern, [SouthChinaSea], [NorthernAtlantic]);
    internal static readonly FactionDefinition Cuba =
        new("Cuba", FleetAlignment.Eastern, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Czech =
        new("Czech", FleetAlignment.Eastern, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition DDR =
        new("DDR", FleetAlignment.Eastern, [BalticSea],
            [Atlantic, NorthernAtlantic, NorwegianSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Denmark =
        new("Denmark", FleetAlignment.Western, [NorwegianSea, BalticSea],
            [Atlantic, NorthernAtlantic, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Egypt =
        new("Egypt", FleetAlignment.Eastern, [EasternMediterranean], [ArabianSea]);
    internal static readonly FactionDefinition France =
        new("France", FleetAlignment.Western, [Atlantic, NorthernAtlantic],
            [NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Germany =
        new("Germany", FleetAlignment.Western, [Atlantic, NorthernAtlantic, BalticSea],
            [NorwegianSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Greece =
        new("Greece", FleetAlignment.Western, [EasternMediterranean],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, ArabianSea]);
    internal static readonly FactionDefinition Hungary =
        new("Hungary", FleetAlignment.Eastern, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Iceland =
        new("Iceland", FleetAlignment.Western, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition India =
        new("India", FleetAlignment.Western, [ArabianSea], [Atlantic, SouthChinaSea]);
    internal static readonly FactionDefinition Indonesia =
        new("Indonesia", FleetAlignment.Western, [], [NorthernAtlantic, SouthChinaSea]);
    internal static readonly FactionDefinition Iran =
        new("Iran", FleetAlignment.Eastern, [ArabianSea], [EasternMediterranean]);
    internal static readonly FactionDefinition Iraq =
        new("Iraq", FleetAlignment.Eastern, [ArabianSea], [EasternMediterranean]);
    internal static readonly FactionDefinition Israel =
        new("Israel", FleetAlignment.Western, [EasternMediterranean], []);
    internal static readonly FactionDefinition Italy =
        new("Italy", FleetAlignment.Western, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Japan =
        new("Japan", FleetAlignment.Western, [], [NorthernAtlantic, SouthChinaSea]);
    internal static readonly FactionDefinition Jordan =
        new("Jordan", FleetAlignment.Eastern, [], [EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Kuwait =
        new("Kuwait", FleetAlignment.Eastern, [ArabianSea], [EasternMediterranean]);
    internal static readonly FactionDefinition Libya =
        new("Libya", FleetAlignment.Eastern, [EasternMediterranean], [ArabianSea]);
    internal static readonly FactionDefinition Malaysia =
        new("Malaysia", FleetAlignment.Western, [], [Atlantic, NorthernAtlantic, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Morocco =
        new("Morocco", FleetAlignment.Eastern, [], [EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Netherlands =
        new("Netherlands", FleetAlignment.Western, [Atlantic, NorthernAtlantic],
            [NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition NewZealand =
        new("NewZealand", FleetAlignment.Western, [], [Atlantic, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition NorthKorea =
        new("North_Korea", FleetAlignment.Eastern, [], [NorthernAtlantic, SouthChinaSea]);
    internal static readonly FactionDefinition Norway =
        new("Norway", FleetAlignment.Western, [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea],
            [EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Pakistan =
        new("Pakistan", FleetAlignment.Western, [ArabianSea], [Atlantic, SouthChinaSea]);
    internal static readonly FactionDefinition Philippines =
        new("Philippines", FleetAlignment.Western, [SouthChinaSea], [NorthernAtlantic]);
    internal static readonly FactionDefinition Poland =
        new("Poland", FleetAlignment.Eastern, [BalticSea],
            [Atlantic, NorthernAtlantic, NorwegianSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Portugal =
        new("Portugal", FleetAlignment.Western, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Qatar =
        new("Qatar", FleetAlignment.Eastern, [], [EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition RoC =
        new("RoC", FleetAlignment.Western, [], [NorthernAtlantic, SouthChinaSea]);
    internal static readonly FactionDefinition Romania =
        new("Romania", FleetAlignment.Eastern, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Saudi =
        new("Saudi", FleetAlignment.Eastern, [ArabianSea], [EasternMediterranean]);
    internal static readonly FactionDefinition SouthKorea =
        new("South_Korea", FleetAlignment.Western, [SouthChinaSea], [NorthernAtlantic]);
    internal static readonly FactionDefinition SouthVietnam =
        new("South_Vietnam", FleetAlignment.Western, [SouthChinaSea], [NorthernAtlantic]);
    internal static readonly FactionDefinition SouthYemen =
        new("South_Yemen", FleetAlignment.Eastern, [ArabianSea], [EasternMediterranean]);
    internal static readonly FactionDefinition Soviet =
        new("Soviet", FleetAlignment.Eastern, [Atlantic, NorthernAtlantic, NorwegianSea],
            [BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition Spain =
        new("Spain", FleetAlignment.Western, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Sudan =
        new("Sudan", FleetAlignment.Eastern, [], [EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Sweden =
        new("Sweden", FleetAlignment.Western, [NorwegianSea, BalticSea], []);
    internal static readonly FactionDefinition Switzerland =
        new("Switzerland", FleetAlignment.Western, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Syria =
        new("Syria", FleetAlignment.Eastern, [EasternMediterranean], [ArabianSea]);
    internal static readonly FactionDefinition Thailand =
        new("Thailand", FleetAlignment.Western, [], [NorthernAtlantic, SouthChinaSea]);
    internal static readonly FactionDefinition Tunisia =
        new("Tunisia", FleetAlignment.Eastern, [], [EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Turkey =
        new("Turkey", FleetAlignment.Western, [EasternMediterranean],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, ArabianSea]);
    internal static readonly FactionDefinition UK =
        new("UK", FleetAlignment.Western, [Atlantic, NorthernAtlantic],
            [NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea]);
    internal static readonly FactionDefinition US =
        new("US", FleetAlignment.Western, [],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea]);
    internal static readonly FactionDefinition Vietnam =
        new("Vietnam", FleetAlignment.Eastern, [SouthChinaSea],
            [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, ArabianSea]);
    internal static readonly FactionDefinition Argentina = new("Argentina");
    internal static readonly FactionDefinition Brazil = new("Brazil");
    internal static readonly FactionDefinition Chile = new("Chile");
    internal static readonly FactionDefinition Ecuador = new("Ecuador");
    internal static readonly FactionDefinition Mexico = new("Mexico");
    internal static readonly FactionDefinition SouthAfrica = new("South_Africa");

    internal static IReadOnlyList<FactionDefinition> Coalitions { get; } =
    [
        new(
            "Coalition_NATO",
            "NATO",
            "Grand alliance",
            $"{CoalitionArtPath}/nato.png",
            [Belgium, Canada, Denmark, France, Germany, Greece, Iceland, Italy, Netherlands, Norway, Portugal, Spain, Switzerland, Turkey, UK]),
        new(
            "Coalition_WarsawPact",
            "Warsaw Pact",
            "Grand alliance",
            $"{CoalitionArtPath}/warsaw-pact.png",
            [Bulgaria, Cuba, Czech, DDR, Hungary, Poland, Romania, Vietnam]),
        new(
            "Coalition_ArabLeague",
            "Arab League",
            "Regional coalition",
            $"{CoalitionArtPath}/arab-league.png",
            [Algeria, Egypt, Iran, Iraq, Jordan, Kuwait, Libya, Morocco, Qatar, Saudi, SouthYemen, Sudan, Syria, Tunisia]),
        new(
            "Coalition_BlueDragons",
            "Blue Dragons",
            "Pacific defense coalition",
            $"{CoalitionArtPath}/blue-dragons.png",
            [Indonesia, Japan, Malaysia, Philippines, RoC, SouthKorea, SouthVietnam, Thailand]),
        new(
            "Coalition_Commonwealth",
            "Commonwealth",
            "Regional coalition",
            $"{CoalitionArtPath}/commonwealth.png",
            [Australia, Canada, India, Malaysia, NewZealand, Pakistan, UK]),
        new(
            "Coalition_EasternMediterranean",
            "Eastern Mediterranean",
            "Regional coalition",
            string.Empty,
            [Greece, Israel, Turkey],
            Israel.Id),
        new(
            "Coalition_Eurocorps",
            "Eurocorps",
            "Regional coalition",
            $"{CoalitionArtPath}/eurocorps.png",
            [Belgium, Canada, Denmark, France, Germany, Greece, Norway, Portugal, Spain, Sweden, Switzerland, UK]),
        new(
            "Coalition_UnifiedGermany",
            "Unified Germany",
            "Unified nation",
            string.Empty,
            [DDR, Germany],
            DDR.Id),
        new(
            "Coalition_UnifiedVietnam",
            "Unified Vietnam",
            "Unified nation",
            string.Empty,
            [SouthVietnam, Vietnam],
            Vietnam.Id),
        new(
            "Coalition_RedDragons",
            "Red Dragons",
            "Regional coalition",
            $"{CoalitionArtPath}/red-dragons.png",
            [China, NorthKorea, Vietnam]),
        new(
            "Coalition_Scandinavia",
            "Scandinavia",
            "Regional coalition",
            $"{CoalitionArtPath}/scandinavia.png",
            [Denmark, Norway, Sweden]),
        new(
            "Coalition_SouthAmerica",
            "South America",
            "Regional coalition",
            string.Empty,
            [Argentina, Brazil, Chile, Ecuador, Mexico, SouthAfrica],
            Brazil.Id),
    ];

    internal static IReadOnlyList<FactionDefinition> Nations { get; } =
    [
        Algeria, Australia, Belgium, Bulgaria, Canada, China, Cuba, Czech, DDR, Denmark, Egypt, France,
        Germany, Greece, Hungary, Iceland, India, Indonesia, Iran, Iraq, Israel, Italy, Japan, Jordan,
        Kuwait, Libya, Malaysia, Morocco, Netherlands, NewZealand, NorthKorea, Norway, Pakistan, Philippines,
        Poland, Portugal, Qatar, RoC, Romania, Saudi, SouthKorea, SouthVietnam, SouthYemen, Soviet, Spain,
        Sudan, Sweden, Switzerland, Syria, Thailand, Tunisia, Turkey, UK, US, Vietnam, Argentina, Brazil,
        Chile, Ecuador, Mexico, SouthAfrica,
    ];

    internal static FactionDefinition? Get(string? selection) =>
        Coalitions.Concat(Nations).FirstOrDefault(faction =>
            string.Equals(faction.Id, selection, StringComparison.OrdinalIgnoreCase));

    internal static FactionDefinition? GetCoalition(string? selection) =>
        Get(selection) is { IsCoalition: true } coalition ? coalition : null;

    internal static bool IsCoalition(string? selection) => GetCoalition(selection) is not null;

    internal static FleetSidePlan CreateSide(FleetClashTheater theater, FleetAlignment alignment)
    {
        FactionDefinition[] aligned = Nations.Where(nation => nation.Alignment == alignment).ToArray();
        FactionDefinition[] regional = aligned.Where(nation => nation.ActiveTheaters.Contains(theater)).ToArray();
        string[] regionalNations = regional.Select(nation => nation.Id).ToArray();
        string[] alliedNations = aligned
            .Where(nation => nation.ReinforcementTheaters.Contains(theater))
            .Select(nation => nation.Id)
            .Except(regionalNations, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string name = regional.Length == 1
            ? regional[0].DisplayName
            : Coalitions
                .Where(coalition => regional.Length > 0 &&
                    coalition.ActiveTheaters.Contains(theater) &&
                    regionalNations.All(coalition.MemberNations.Contains))
                .OrderBy(coalition => coalition.MemberNations.Count)
                .Select(coalition => coalition.DisplayName)
                .FirstOrDefault() ?? $"{alignment} fleet";
        return new(name, regionalNations, alliedNations);
    }
}

internal enum FleetAlignment
{
    Unaligned,
    Western,
    Eastern,
}

internal class FactionDefinition(
    string id,
    string displayName,
    string category,
    string emblemResourcePath,
    IReadOnlyList<FactionDefinition>? memberNations = null,
    string flagNation = "",
    FleetAlignment alignment = FleetAlignment.Unaligned,
    IReadOnlyList<FleetClashTheater>? activeTheaters = null,
    IReadOnlyList<FleetClashTheater>? reinforcementTheaters = null)
{
    internal FactionDefinition(
        string nation,
        FleetAlignment alignment = FleetAlignment.Unaligned,
        IReadOnlyList<FleetClashTheater>? activeTheaters = null,
        IReadOnlyList<FleetClashTheater>? reinforcementTheaters = null)
        : this(nation, nation.Replace('_', ' '), "Nation", string.Empty,
            flagNation: nation, alignment: alignment,
            activeTheaters: activeTheaters, reinforcementTheaters: reinforcementTheaters)
    { }

    internal string Id { get; } = id;
    internal string DisplayName { get; } = displayName;
    internal string Category { get; } = category;
    internal string EmblemResourcePath { get; } = emblemResourcePath;
    internal string FlagNation { get; } = flagNation;
    internal bool IsCoalition { get; } = memberNations is not null;
    internal FleetAlignment Alignment { get; } = alignment;
    internal IReadOnlyList<FactionDefinition> Members => memberNations ?? [this];
    internal HashSet<string> MemberNations { get; } =
        new(memberNations?.Select(nation => nation.Id) ?? [id], StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyCollection<FleetClashTheater> ActiveTheaters =>
        GetTheaters(activeTheaters, nation => nation.ActiveTheaters);
    internal IReadOnlyCollection<FleetClashTheater> ReinforcementTheaters =>
        GetTheaters(reinforcementTheaters, nation => nation.ReinforcementTheaters);

    private IReadOnlyCollection<FleetClashTheater> GetTheaters(
        IReadOnlyList<FleetClashTheater>? additions,
        Func<FactionDefinition, IReadOnlyCollection<FleetClashTheater>> select) =>
        IsCoalition
            ? (additions ?? []).Concat(Members.SelectMany(select)).Distinct().ToArray()
            : additions ?? [];
}

internal static class FleetClashTheaterCatalog
{
    internal static readonly FleetClashTheater Atlantic = new("North Atlantic", 58f, -23f);
    internal static readonly FleetClashTheater NorthernAtlantic = new("North Atlantic", 61f, -18f);
    internal static readonly FleetClashTheater NorwegianSea = new("Norwegian Sea", 67f, 5f);
    internal static readonly FleetClashTheater BalticSea = new("Baltic Sea", 56f, 18f);
    internal static readonly FleetClashTheater EasternMediterranean = new("Eastern Mediterranean", 35f, 25f);
    internal static readonly FleetClashTheater SouthChinaSea = new("South China Sea", 14f, 115f);
    internal static readonly FleetClashTheater ArabianSea = new("Arabian Sea", 20f, 65f);

    private static readonly FleetClashTheater[] Theaters =
        [Atlantic, NorthernAtlantic, NorwegianSea, BalticSea, EasternMediterranean, SouthChinaSea, ArabianSea];

    internal static IReadOnlyList<string> RegionNames { get; } = Theaters
        .Select(theater => theater.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal static (FleetClashTheater Theater, FleetSidePlan First, FleetSidePlan Second) Pick(
        System.Random random,
        Func<FleetSidePlan, bool> isViable,
        string? region = null)
    {
        List<(FleetClashTheater Theater, FleetSidePlan First, FleetSidePlan Second)> viable = [];
        foreach (FleetClashTheater theater in Theaters)
        {
            if (!string.IsNullOrWhiteSpace(region) &&
                !string.Equals(theater.Name, region, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FleetSidePlan first = FactionCatalog.CreateSide(theater, FleetAlignment.Western);
            FleetSidePlan second = FactionCatalog.CreateSide(theater, FleetAlignment.Eastern);
            if (isViable(first) && isViable(second))
            {
                viable.Add((theater, first, second));
            }
        }

        if (viable.Count == 0)
        {
            throw new InvalidOperationException("No fleet-clash theater has usable combatants on both sides.");
        }

        return viable[random.Next(viable.Count)];
    }
}
