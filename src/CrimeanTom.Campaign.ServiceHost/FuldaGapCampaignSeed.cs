using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.ServiceHost;

internal static class FuldaGapCampaignSeed
{
    public static FuldaGapSeed Create(CampaignId campaignId, int forceMultiplier = 1)
    {
        if (forceMultiplier is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(forceMultiplier), "Fulda force multiplier must be between 1 and 100.");
        }
        var blue = new CoalitionId(Id(campaignId, "coalition-blue"));
        var red = new CoalitionId(Id(campaignId, "coalition-red"));
        var regions = new[]
        {
            Region(campaignId, "usec", "US East Coast loadout", []),
            Region(campaignId, "midatl", "Mid-Atlantic reinforcement route", ["usec"]),
            Region(campaignId, "wapp", "Western Approaches", ["midatl"]),
            Region(campaignId, "giuk", "GIUK Gap", ["wapp"]),
            Region(campaignId, "norsea", "Norwegian Sea and Vestfjord", ["giuk"]),
            Region(campaignId, "barents", "Barents approaches", ["norsea"]),
            Region(campaignId, "nsea", "Central North Sea", ["wapp", "giuk"]),
            Region(campaignId, "bight", "German Bight and Elbe approaches", ["nsea"]),
            Region(campaignId, "baltap", "Baltic approaches and Danish straits", ["nsea", "bight"]),
            Region(campaignId, "baltic", "Southern Baltic", ["baltap"]),
            Region(campaignId, "northag", "NORTHAG rear and ports (abstract land logistics)", ["bight"]),
            Region(campaignId, "fulda", "Fulda Gap Central Front (abstract land model)", ["northag"]),
        };
        var regionIds = regions.ToDictionary(item => item.Key, item => item.RegionId, StringComparer.Ordinal);
        var bases = new[]
        {
            Base(campaignId, "hampton", "Hampton Roads", blue, "usec", 36_950_000, -76_330_000),
            Base(campaignId, "keflavik", "Keflavik", blue, "giuk", 63_980_000, -22_600_000),
            Base(campaignId, "bodo", "Bodo", blue, "norsea", 67_270_000, 14_370_000),
            Base(campaignId, "bremerhaven", "Bremerhaven reinforcement port", blue, "bight", 53_540_000, 8_580_000),
            Base(campaignId, "karup", "Karup", blue, "baltap", 56_300_000, 9_120_000),
            Base(campaignId, "severomorsk", "Severomorsk", red, "barents", 69_070_000, 33_420_000),
            Base(campaignId, "baltiysk", "Baltiysk", red, "baltic", 54_650_000, 19_910_000),
            Base(campaignId, "peenemunde", "Peenemunde", red, "baltic", 54_140_000, 13_770_000),
        };
        var baseIds = bases.ToDictionary(item => item.Key, item => item.BaseId, StringComparer.Ordinal);
        var entityTemplates = new[]
        {
            Entity(campaignId, "pharris", "USS Pharris", "usn_ff_knox_84", blue, "giuk", 62_700_000, -15_000_000, "keflavik", "blue-giuk", SimulationDomain.Maritime),
            Entity(campaignId, "los-angeles", "USS Providence", "usn_ssn_los_angeles", blue, "giuk", 63_100_000, -17_200_000, "keflavik", "blue-north-sea", SimulationDomain.Maritime),
            Entity(campaignId, "yorktown", "USS Yorktown", "usn_cg_ticonderoga", blue, "nsea", 57_000_000, 3_000_000, "bremerhaven", "blue-north-sea", SimulationDomain.Maritime),
            Entity(campaignId, "spruance", "USS Spruance", "usn_dd_spruance", blue, "nsea", 57_100_000, 3_100_000, "bremerhaven", "blue-north-sea", SimulationDomain.Maritime),
            Entity(campaignId, "reforger", "REFORGER Convoy 261", "merchant_roro_convoy", blue, "midatl", 47_000_000, -31_000_000, "hampton", "blue-reforger", SimulationDomain.Logistics),
            Entity(campaignId, "neosho", "USNS Neosho", "usn_ao_neosho", blue, "midatl", 46_900_000, -31_100_000, "hampton", "blue-reforger", SimulationDomain.Logistics),
            Entity(campaignId, "hauk", "RNoN Hauk Squadron", "nor_ptg_hauk", blue, "baltap", 57_300_000, 10_900_000, "karup", "blue-baltap", SimulationDomain.Maritime),
            Entity(campaignId, "v-corps", "US V Corps (abstract)", "abstract_land_formation", blue, "fulda", 50_550_000, 9_680_000, null, "blue-fulda", SimulationDomain.LandAbstract, FidelityState.Operational),
            Entity(campaignId, "northern-sag", "Northern Fleet Surface Action Group", "wp_cruiser_slava", red, "barents", 70_200_000, 30_500_000, "severomorsk", "red-northern-sag", SimulationDomain.Maritime),
            Entity(campaignId, "udaloy", "Admiral Zakharov", "wp_dd_udaloy", red, "barents", 70_100_000, 30_700_000, "severomorsk", "red-northern-sag", SimulationDomain.Maritime),
            Entity(campaignId, "victor", "Victor III patrol", "wp_ssn_victor3", red, "giuk", 64_000_000, -12_800_000, "severomorsk", "red-giuk", SimulationDomain.Maritime),
            Entity(campaignId, "oscar", "Oscar II SSGN patrol", "wp_ssgn_oscar2", red, "giuk", 64_300_000, -11_900_000, "severomorsk", "red-giuk", SimulationDomain.Maritime),
            Entity(campaignId, "krivak", "Rezkiy", "vmf_krivak", red, "baltic", 55_100_000, 17_800_000, "baltiysk", "red-baltic", SimulationDomain.Maritime),
            Entity(campaignId, "osa", "Osa missile boat squadron", "wp_ptg_osa1", red, "baltic", 55_000_000, 17_600_000, "baltiysk", "red-baltic", SimulationDomain.Maritime),
            Entity(campaignId, "eighth-guards", "8th Guards Army (abstract)", "abstract_land_formation", red, "fulda", 50_800_000, 10_300_000, null, "red-fulda", SimulationDomain.LandAbstract, FidelityState.Operational),
        };
        var entities = Enumerable.Range(0, forceMultiplier)
            .SelectMany(echelon => echelon == 0
                ? entityTemplates
                : entityTemplates.Select(template => Replicate(campaignId, template, echelon)))
            .OrderBy(item => item.EntityId)
            .ToArray();
        var entityIds = entities.ToDictionary(item => item.Key, item => item.EntityId, StringComparer.Ordinal);
        var groups = entities.GroupBy(item => item.GroupKey, StringComparer.Ordinal)
            .Select(group => new FuldaGroupSeed(
                group.Key,
                new GroupId(Id(campaignId, $"group-{group.Key}")),
                GroupName(group.Key),
                group.First().CoalitionId,
                group.Select(item => item.EntityId).OrderBy(item => item).ToArray()))
            .OrderBy(item => item.GroupId)
            .ToArray();
        var groupIds = groups.ToDictionary(item => item.Key, item => item.GroupId, StringComparer.Ordinal);
        var orders = new[]
        {
            Order(campaignId, "reforger-to-bight", groupIds["blue-reforger"], regionIds["bight"], OrderKind.Escort),
            Order(campaignId, "northern-sag-to-norsea", groupIds["red-northern-sag"], regionIds["norsea"], OrderKind.Transit),
            Order(campaignId, "baltic-raid-to-baltap", groupIds["red-baltic"], regionIds["baltap"], OrderKind.Transit),
            Order(campaignId, "blue-sag-to-norsea", groupIds["blue-north-sea"], regionIds["norsea"], OrderKind.Patrol),
        };
        return new FuldaGapSeed(campaignId, forceMultiplier, blue, red, regions, bases, entities, groups, orders, regionIds, baseIds, entityIds);
    }

    private static FuldaRegionSeed Region(CampaignId campaignId, string key, string name, string[] neighbors) =>
        new(key, new RegionId(Id(campaignId, $"region-{key}")), name, neighbors);

    private static FuldaBaseSeed Base(
        CampaignId campaignId, string key, string name, CoalitionId coalitionId, string regionKey,
        int latitude, int longitude) =>
        new(key, new BaseId(Id(campaignId, $"base-{key}")), name, coalitionId, regionKey,
            GeoCoordinate.FromMicrodegrees(latitude, longitude));

    private static FuldaEntitySeed Entity(
        CampaignId campaignId, string key, string name, string catalogUnitId, CoalitionId coalitionId,
        string regionKey, int latitude, int longitude, string? homeBaseKey, string groupKey,
        SimulationDomain simulationDomain, FidelityState maximumFidelity = FidelityState.Tactical) =>
        new(key, new EntityId(Id(campaignId, $"entity-{key}")), name, catalogUnitId, coalitionId, regionKey,
            GeoCoordinate.FromMicrodegrees(latitude, longitude), homeBaseKey, groupKey,
            simulationDomain, maximumFidelity);

    private static FuldaOrderSeed Order(
        CampaignId campaignId, string key, GroupId groupId, RegionId destination, OrderKind kind) =>
        new(new OrderId(Id(campaignId, $"order-{key}")), groupId, kind, destination);

    private static FuldaEntitySeed Replicate(CampaignId campaignId, FuldaEntitySeed template, int echelon)
    {
        var suffix = $"echelon-{echelon + 1:D3}";
        var latitudeOffset = ((echelon % 9) - 4) * 12_000;
        var longitudeOffset = (((echelon / 9) % 9) - 4) * 18_000;
        var key = $"{template.Key}-{suffix}";
        return template with
        {
            Key = key,
            EntityId = new EntityId(Id(campaignId, $"entity-{key}")),
            Name = $"{template.Name} / {echelon + 1:D3}",
            Coordinate = GeoCoordinate.FromMicrodegrees(
                template.Coordinate.LatitudeMicrodegrees + latitudeOffset,
                template.Coordinate.LongitudeMicrodegrees + longitudeOffset),
            GroupKey = $"{template.GroupKey}-{suffix}",
        };
    }

    private static string GroupName(string key)
    {
        var echelonMarker = key.IndexOf("-echelon-", StringComparison.Ordinal);
        if (echelonMarker >= 0)
        {
            return $"{GroupName(key[..echelonMarker])} / {key[(echelonMarker + 9)..]}";
        }
        return key switch
        {
        "blue-giuk" => "NATO GIUK barrier group",
        "blue-north-sea" => "NATO North Sea surface action group",
        "blue-reforger" => "REFORGER Convoy 261",
        "blue-baltap" => "BALTAP fast attack group",
        "blue-fulda" => "US V Corps abstract formation",
        "red-northern-sag" => "Northern Fleet surface action group",
        "red-giuk" => "Northern Fleet submarine barrier group",
        "red-baltic" => "Baltic Fleet missile raid group",
        "red-fulda" => "8th Guards Army abstract formation",
            _ => key,
        };
    }

    private static Guid Id(CampaignId campaignId, string purpose)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{campaignId.Value:N}:fulda-gap-1989:{purpose}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

internal sealed record FuldaGapSeed(
    CampaignId CampaignId,
    int ForceMultiplier,
    CoalitionId BlueCoalitionId,
    CoalitionId RedCoalitionId,
    IReadOnlyList<FuldaRegionSeed> Regions,
    IReadOnlyList<FuldaBaseSeed> Bases,
    IReadOnlyList<FuldaEntitySeed> Entities,
    IReadOnlyList<FuldaGroupSeed> Groups,
    IReadOnlyList<FuldaOrderSeed> Orders,
    IReadOnlyDictionary<string, RegionId> RegionIds,
    IReadOnlyDictionary<string, BaseId> BaseIds,
    IReadOnlyDictionary<string, EntityId> EntityIds);

internal sealed record FuldaRegionSeed(string Key, RegionId RegionId, string Name, IReadOnlyList<string> NeighborKeys);
internal sealed record FuldaBaseSeed(string Key, BaseId BaseId, string Name, CoalitionId CoalitionId, string RegionKey, GeoCoordinate Coordinate);
internal sealed record FuldaEntitySeed(
    string Key,
    EntityId EntityId,
    string Name,
    string CatalogUnitId,
    CoalitionId CoalitionId,
    string RegionKey,
    GeoCoordinate Coordinate,
    string? HomeBaseKey,
    string GroupKey,
    SimulationDomain SimulationDomain,
    FidelityState MaximumFidelity);
internal sealed record FuldaGroupSeed(string Key, GroupId GroupId, string Name, CoalitionId CoalitionId, IReadOnlyList<EntityId> Members);
internal sealed record FuldaOrderSeed(OrderId OrderId, GroupId GroupId, OrderKind Kind, RegionId DestinationRegionId);
