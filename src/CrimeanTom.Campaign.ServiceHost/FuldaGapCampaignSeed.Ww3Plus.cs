using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.ServiceHost;

internal static partial class FuldaGapCampaignSeed
{
    internal static bool IsCivilianTraffic(FuldaEntitySeed entity) =>
        entity.CatalogUnitId.StartsWith("civ_", StringComparison.Ordinal)
        || entity.CatalogUnitId.StartsWith("merchant_", StringComparison.Ordinal);

    internal static bool IsFixedSite(FuldaEntitySeed entity) =>
        entity.SimulationDomain == SimulationDomain.Installation
        || entity.SimulationDomain == SimulationDomain.Logistics
            && (entity.CatalogUnitId.StartsWith("tgt_", StringComparison.Ordinal)
                || entity.CatalogUnitId.StartsWith("warehouses_", StringComparison.Ordinal));

    internal static bool IsResidentStrategicSystem(FuldaEntitySeed entity) =>
        entity.MaximumFidelity == FidelityState.Tactical
        && entity.SimulationDomain is SimulationDomain.Ground or SimulationDomain.Installation
        && (entity.OperationalWeapons.Any(weapon =>
                weapon.TargetClassification == ContactClassification.Air
                && weapon.MaximumRange.Value >= 100_000)
            || entity.OperationalWeapons.Any(weapon =>
                weapon.AmmunitionId.Contains("ballistic", StringComparison.OrdinalIgnoreCase))
            || entity.SimulationDomain == SimulationDomain.Installation
                && (entity.CatalogUnitId.Contains("radar", StringComparison.OrdinalIgnoreCase)
                    || entity.CatalogUnitId.Contains("ewr", StringComparison.OrdinalIgnoreCase)
                    || entity.Name.Contains("early-warning radar", StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<FuldaRegionSeed> Ww3PlusRegions(CampaignId campaignId) =>
    [
        Region(campaignId, "gulfmex", "Gulf of Mexico sea lanes", ["usec"]),
        Region(campaignId, "florida", "Florida approaches and Straits", ["usec", "gulfmex"]),
        Region(campaignId, "caribbean", "Caribbean basin and Windward Passage", ["southatl", "gulfmex", "florida"]),
        Region(campaignId, "cuba", "Cuba, Soviet-aligned forward defense zone", ["florida", "caribbean"]),
        Region(campaignId, "uswc", "US West Coast fleet concentration", ["northpac"]),
        Region(campaignId, "pnw", "Puget Sound and Pacific Northwest", ["uswc", "northpac"]),
        Region(campaignId, "hawaii", "Hawaii and Central Pacific staging area", ["uswc", "northpac", "southpac"]),
        Region(campaignId, "bosporus", "Aegean, Turkish Straits, and Bosporus naval choke point", ["med"]),
        Region(campaignId, "blacksea", "Black Sea Fleet bastion and amphibious operating area", ["bosporus"]),
        .. LandAirExpansionRegions(campaignId),
    ];

    private static IReadOnlyList<FuldaBaseSeed> Ww3PlusBases(
        CampaignId campaignId,
        CoalitionId blue,
        CoalitionId red) =>
    [
        Base(campaignId, "norfolk", "Naval Station Norfolk", blue, "usec", 36_946_000, -76_329_000),
        Base(campaignId, "charleston", "Charleston naval complex", blue, "usec", 32_824_000, -79_933_000),
        Base(campaignId, "mayport", "Naval Station Mayport", blue, "florida", 30_392_000, -81_424_000),
        Base(campaignId, "oceana", "NAS Oceana", blue, "usec", 36_821_000, -76_033_000),
        Base(campaignId, "new-london", "Naval Submarine Base New London", blue, "usec", 41_389_000, -72_086_000),
        Base(campaignId, "san-diego", "Naval Base San Diego", blue, "uswc", 32_684_000, -117_132_000),
        Base(campaignId, "long-beach", "Long Beach naval complex", blue, "uswc", 33_754_000, -118_216_000),
        Base(campaignId, "alameda", "Naval Air Station Alameda", blue, "uswc", 37_786_000, -122_319_000),
        Base(campaignId, "bremerton", "Puget Sound Naval Shipyard", blue, "pnw", 47_563_000, -122_641_000),
        Base(campaignId, "bangor", "Naval Submarine Base Bangor", blue, "pnw", 47_721_000, -122_714_000),
        Base(campaignId, "north-island", "NAS North Island", blue, "uswc", 32_699_000, -117_215_000),
        Base(campaignId, "whidbey", "NAS Whidbey Island", blue, "pnw", 48_352_000, -122_656_000),
        Base(campaignId, "pearl-harbor", "Pearl Harbor", blue, "hawaii", 21_350_000, -157_950_000),
        Base(campaignId, "havana", "Havana naval district", red, "cuba", 23_136_000, -82_358_000),
        Base(campaignId, "mariel", "Mariel military port", red, "cuba", 22_995_000, -82_754_000),
        Base(campaignId, "cienfuegos", "Cienfuegos naval and air complex", red, "cuba", 22_150_000, -80_443_000),
        Base(campaignId, "santiago-cuba", "Santiago de Cuba naval district", red, "cuba", 20_012_000, -75_835_000),
        Base(campaignId, "san-antonio-cuba", "San Antonio de los Banos Air Base", red, "cuba", 22_871_000, -82_509_000),
        Base(campaignId, "holguin", "Holguin eastern air-defense base", red, "cuba", 20_786_000, -76_315_000),
        Base(campaignId, "lourdes", "Lourdes signals-intelligence complex", red, "cuba", 22_995_000, -82_464_000),
        Base(campaignId, "ramstein", "Ramstein Air Base", blue, "northag", 49_437_000, 7_600_000),
        Base(campaignId, "bitburg", "Bitburg Air Base", blue, "northag", 49_946_000, 6_565_000),
        Base(campaignId, "spangdahlem", "Spangdahlem Air Base", blue, "northag", 49_973_000, 6_692_000),
        Base(campaignId, "rhein-main", "Rhein-Main reinforcement airhead", blue, "northag", 50_033_000, 8_570_000),
        Base(campaignId, "pomcus-e", "POMCUS Netherlands-Germany logistics belt", blue, "northag", 50_900_000, 6_050_000),
        Base(campaignId, "polyarny", "Polyarny submarine support base", red, "barents", 69_198_000, 33_450_000),
        Base(campaignId, "olenya", "Olenya long-range aviation base", red, "barents", 68_151_000, 33_463_000),
        Base(campaignId, "gander", "Gander reinforcement airfield", blue, "midatl", 48_937_000, -54_568_000),
        Base(campaignId, "lajes", "Lajes Atlantic staging field", blue, "midatl", 38_762_000, -27_091_000),
        Base(campaignId, "andersen", "Andersen Pacific staging field", blue, "northpac", 13_584_000, 144_930_000),
        Base(campaignId, "sevastopol", "Sevastopol Black Sea Fleet base", red, "blacksea", 44_616_000, 33_525_000),
        Base(campaignId, "novorossiysk", "Novorossiysk naval and reinforcement base", red, "blacksea", 44_720_000, 37_780_000),
        .. LandAirExpansionBases(campaignId, blue, red),
    ];

    private static IReadOnlyList<FuldaEntitySeed> Ww3PlusEntities(
        CampaignId campaignId,
        CoalitionId blue,
        CoalitionId red) =>
        Ww3PlusPackages(blue, red)
            .SelectMany(package => ExpandPackage(campaignId, package))
            .ToArray();

    private static IReadOnlyList<FuldaOrderSeed> Ww3PlusOrders(
        CampaignId campaignId,
        IReadOnlyDictionary<string, GroupId> groupIds,
        IReadOnlyDictionary<string, RegionId> regionIds) =>
        Ww3PlusPackages(default, default)
            .Where(package => package.DestinationRegionKey is not null)
            .SelectMany(package => Enumerable.Range(0, PackageGroups(package).Count).Select(groupIndex =>
            {
                var groupKey = PackageGroupKey(package, groupIndex);
                return groupIds.TryGetValue(groupKey, out var groupId)
                    ? Order(
                    campaignId,
                    $"{groupKey}-{package.OrderKind.ToString().ToLowerInvariant()}",
                    groupId,
                    regionIds[package.DestinationRegionKey!],
                    package.OrderKind)
                    : null;
            }))
            .Where(order => order is not null)
            .Cast<FuldaOrderSeed>()
            .ToArray();

    private static IReadOnlyList<FuldaEntitySeed> Ww3AirbaseComplexEntities(
        CampaignId campaignId,
        CoalitionId blue,
        CoalitionId red) =>
        Ww3AirbaseComplexes(blue, red)
            .SelectMany(complex => ExpandAirbaseComplex(campaignId, complex))
            .ToArray();

    private static IReadOnlyList<FuldaEntitySeed> Ww3FormationPhysicalEntities(
        CampaignId campaignId,
        CoalitionId blue,
        CoalitionId red)
    {
        var originalFormationParents = new[]
        {
            Entity(campaignId, "v-corps", "US V Corps (abstract)", "abstract_land_formation",
                blue, "fulda", 50_550_000, 9_680_000, null, "blue-fulda",
                SimulationDomain.LandAbstract, FidelityState.Operational),
            Entity(campaignId, "eighth-guards", "8th Guards Army (abstract)", "abstract_land_formation",
                red, "fulda", 50_800_000, 10_300_000, null, "red-fulda",
                SimulationDomain.LandAbstract, FidelityState.Operational),
        };
        var expandedFormationParents = Ww3PlusPackages(blue, red)
            .Where(package => package.CatalogUnitIds.Contains("abstract_land_formation", StringComparer.Ordinal))
            .SelectMany(package => ExpandPackage(campaignId, package));

        return originalFormationParents
            .Concat(expandedFormationParents)
            .SelectMany(parent => ExpandFormationPhysicalElements(campaignId, parent, parent.CoalitionId == blue))
            .ToArray();
    }

    private static IEnumerable<FuldaEntitySeed> ExpandFormationPhysicalElements(
        CampaignId campaignId,
        FuldaEntitySeed parent,
        bool isBlue)
    {
        var armorCatalogIds = isBlue
            ? new[] { "usa_mbt_abrams", "usa_mbt_m60" }
            : new[] { "wp_mbt_t-72a", "wp_mbt_t-55m" };
        for (var index = 0; index < armorCatalogIds.Length; index++)
        {
            yield return Entity(
                campaignId,
                $"{parent.Key}-physical-armor-{index + 1:D2}",
                $"{parent.Name} physical armor target {index + 1}",
                armorCatalogIds[index],
                parent.CoalitionId,
                parent.RegionKey,
                parent.Coordinate.LatitudeMicrodegrees + ((index + 1) * 7_000),
                parent.Coordinate.LongitudeMicrodegrees - ((index + 1) * 6_000),
                parent.HomeBaseKey,
                parent.GroupKey,
                SimulationDomain.Ground,
                operationalWeapons: Array.Empty<OperationalWeaponCapability>());
        }

        yield return Entity(
            campaignId,
            $"{parent.Key}-physical-shorad",
            $"{parent.Name} physical low-altitude GBAD target",
            isBlue ? "usa_aaa_vulcan" : "wp_sa-13_launcher",
            parent.CoalitionId,
            parent.RegionKey,
            parent.Coordinate.LatitudeMicrodegrees - 8_000,
            parent.Coordinate.LongitudeMicrodegrees + 9_000,
            parent.HomeBaseKey,
            parent.GroupKey,
            SimulationDomain.Ground,
            operationalWeapons: AirDefense(
                isBlue ? "formation-vulcan-shorad" : "formation-sa-13-shorad",
                isBlue ? 4_500 : 8_000));
    }

    private static IReadOnlyList<FuldaOrderSeed> Ww3AirbaseComplexOrders(
        CampaignId campaignId,
        CoalitionId blue,
        CoalitionId red,
        IReadOnlyDictionary<string, GroupId> groupIds,
        IReadOnlyDictionary<string, RegionId> regionIds) =>
        Ww3AirbaseComplexes(blue, red)
            .SelectMany(complex => new[]
            {
                Order(campaignId, $"{complex.Key}-fighter-cap", groupIds[$"{complex.Key}-fighter-sortie"],
                    regionIds[complex.DestinationRegionKey], OrderKind.Cap),
                Order(campaignId, $"{complex.Key}-strike-patrol", groupIds[$"{complex.Key}-strike-sortie"],
                    regionIds[complex.DestinationRegionKey], OrderKind.Patrol),
                Order(campaignId, $"{complex.Key}-support-patrol", groupIds[$"{complex.Key}-support-sortie"],
                    regionIds[complex.RegionKey], OrderKind.Patrol),
            })
            .ToArray();

    private static IReadOnlyList<Ww3AirbaseComplex> Ww3AirbaseComplexes(CoalitionId blue, CoalitionId red) =>
    [
        Airbase("blue-oceana-complex", "Oceana", blue, "usec", "oceana", 36_821_000, -76_033_000, "midatl", "nato_small_airbase", "usa_radar_tps-43", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a"]),
        Airbase("blue-north-island-complex", "North Island", blue, "uswc", "north-island", 32_699_000, -117_215_000, "hawaii", "nato_small_airbase", "usa_radar_tps-43", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_kc-135a"]),
        Airbase("blue-whidbey-complex", "Whidbey Island", blue, "pnw", "whidbey", 48_352_000, -122_656_000, "northpac", "nato_small_airbase", "usa_radar_tps-63", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a"]),
        Airbase("blue-ramstein-complex", "Ramstein", blue, "northag", "ramstein", 49_437_000, 7_600_000, "fulda", "nato_small_airbase", "nato_fps-20_radar", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a"]),
        Airbase("blue-bitburg-complex", "Bitburg", blue, "northag", "bitburg", 49_946_000, 6_565_000, "fulda", "nato_small_airbase", "nato_tps-27_radar", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_kc-135a"]),
        Airbase("blue-spangdahlem-complex", "Spangdahlem", blue, "northag", "spangdahlem", 49_973_000, 6_692_000, "fulda", "nato_small_airbase", "nato_fps-6_radar", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a"]),
        Airbase("blue-keflavik-complex", "Keflavik", blue, "giuk", "keflavik", 63_980_000, -22_600_000, "norsea", "nato_small_airbase", "nato_fps-20_radar", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a"]),
        Airbase("blue-bodo-complex", "Bodo", blue, "norsea", "bodo", 67_270_000, 14_370_000, "barents", "nato_small_airbase", "nato_tps-27_radar", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a"]),
        Airbase("blue-lajes-complex", "Lajes", blue, "midatl", "lajes", 38_762_000, -27_091_000, "wapp", "nato_small_airbase", "nato_fps-20_radar", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_f-4e", "usaf_kc-135a"]),
        Airbase("blue-andersen-complex", "Andersen", blue, "northpac", "andersen", 13_584_000, 144_930_000, "japan", "nato_small_airbase", "usa_radar_tps-63", ["usa_mim-23_launcher", "nato_sam_skyguard", "usa_sam_site_nike_hercules"], ["usaf_f-15c", "usaf_b-52g", "usaf_kc-135a"]),
        Airbase("red-san-antonio-complex", "San Antonio de los Banos", red, "cuba", "san-antonio-cuba", 22_871_000, -82_509_000, "florida", "wp_large_pvo_airbase", "wp_p-35_radar", ["wp_sa-6_launcher", "wp_sam_site_sa-3", "wp_sam_site_sa-2_modernized"], ["wp_mig-23mld", "wp_mig-21", "wp_mi-14"]),
        Airbase("red-holguin-complex", "Holguin", red, "cuba", "holguin", 20_786_000, -76_315_000, "caribbean", "wp_airbase_2", "wp_p-18_radar", ["wp_sa-6_launcher", "wp_sam_site_sa-3", "wp_sam_site_sa-2_modernized"], ["wp_mig-23mld", "wp_mig-21", "wp_mi-14"]),
        Airbase("red-cienfuegos-complex", "Cienfuegos", red, "cuba", "cienfuegos", 22_150_000, -80_443_000, "caribbean", "wp_airbase_1", "wp_p-35_radar", ["wp_sa-6_launcher", "wp_sam_site_sa-3", "wp_sam_site_sa-2_modernized"], ["wp_mig-23mld", "wp_mig-21", "wp_mi-14"]),
        Airbase("red-olenya-complex", "Olenya", red, "barents", "olenya", 68_151_000, 33_463_000, "norsea", "wp_large_pvo_airbase", "wp_p-14_radar", ["wp_sam_site_sa-11", "wp_sam_site_sa-5", "wp_sam_site_sa-10"], ["wp_mig-25pd", "wp_tu-22m2", "wp_tu-95rt"]),
        Airbase("red-peenemunde-complex", "Peenemunde", red, "baltic", "peenemunde", 54_140_000, 13_770_000, "baltap", "wp_airbase_3", "wp_p-35_radar", ["wp_sa-6_launcher", "wp_sam_site_sa-3", "wp_sam_site_sa-10"], ["wp_mig-23mld", "wp_su-24m", "wp_mig-25pd"]),
        Airbase("red-vladivostok-complex", "Vladivostok", red, "japan", "vladivostok", 43_110_000, 131_890_000, "northpac", "wp_large_pvo_airbase", "wp_p-14_radar", ["wp_sam_site_sa-11", "wp_sam_site_sa-5", "wp_sam_site_sa-10"], ["wp_mig-23mld", "wp_su-24m", "wp_tu-95rt"]),
        Airbase("red-wonsan-complex", "Wonsan", red, "korea", "wonsan", 39_150_000, 127_440_000, "korea", "wp_airbase_2", "wp_p-18_radar", ["wp_sa-6_launcher", "wp_sam_site_sa-3", "wp_sam_site_sa-2_modernized"], ["wp_mig-21", "wp_mig-23mld", "wp_su-24a"]),
        Airbase("red-cam-ranh-complex", "Cam Ranh", red, "southchina", "cam-ranh", 11_890_000, 109_170_000, "southchina", "wp_airbase_1", "wp_p-35_radar", ["wp_sa-6_launcher", "wp_sam_site_sa-3", "wp_sam_site_sa-2_modernized"], ["wp_mig-21", "wp_su-24m", "wp_tu-16k"]),
        Airbase("red-severomorsk-complex", "Severomorsk", red, "barents", "severomorsk", 69_070_000, 33_420_000, "giuk", "wp_large_pvo_airbase", "wp_p-14_radar", ["wp_sam_site_sa-11", "wp_sam_site_sa-5", "wp_sam_site_sa-10"], ["wp_mig-25pd", "wp_tu-142m", "wp_il-38"]),
        Airbase("red-baltiysk-complex", "Baltiysk", red, "baltic", "baltiysk", 54_650_000, 19_910_000, "baltap", "wp_airbase_4", "wp_p-35_radar", ["wp_sa-6_launcher", "wp_sam_site_sa-3", "wp_sam_site_sa-10"], ["wp_mig-23mld", "wp_su-24m", "wp_mig-25pd"]),
        .. LandAirExpansionAirbaseComplexes(blue, red),
    ];

    private static Ww3AirbaseComplex Airbase(
        string key,
        string name,
        CoalitionId coalitionId,
        string regionKey,
        string homeBaseKey,
        int latitude,
        int longitude,
        string destinationRegionKey,
        string airfieldCatalogId,
        string radarCatalogId,
        string[] samCatalogIds,
        string[] aircraftCatalogIds) =>
        new(key, name, coalitionId, regionKey, homeBaseKey, latitude, longitude,
            destinationRegionKey, airfieldCatalogId, radarCatalogId, samCatalogIds,
            aircraftCatalogIds);

    private static IEnumerable<FuldaEntitySeed> ExpandAirbaseComplex(
        CampaignId campaignId,
        Ww3AirbaseComplex complex)
    {
        var defenseGroup = $"{complex.Key}-defense-complex";
        yield return Entity(campaignId, $"{complex.Key}-airfield", $"{complex.Name} runway and operations complex",
            complex.AirfieldCatalogId, complex.CoalitionId, complex.RegionKey,
            complex.LatitudeMicrodegrees, complex.LongitudeMicrodegrees, complex.HomeBaseKey,
            defenseGroup, SimulationDomain.LandAbstract);
        yield return Entity(campaignId, $"{complex.Key}-radar", $"{complex.Name} early-warning radar",
            complex.RadarCatalogId, complex.CoalitionId, complex.RegionKey,
            complex.LatitudeMicrodegrees + 18_000, complex.LongitudeMicrodegrees - 14_000, complex.HomeBaseKey,
            defenseGroup, SimulationDomain.LandAbstract);

        var samRanges = new[] { 25_000L, 65_000L, 140_000L };
        for (var index = 0; index < 3; index++)
        {
            yield return Entity(campaignId, $"{complex.Key}-gbad-{index + 1:D2}",
                $"{complex.Name} GBAD layer {index + 1}", complex.SamCatalogIds[index], complex.CoalitionId,
                complex.RegionKey, complex.LatitudeMicrodegrees - ((index + 1) * 16_000),
                complex.LongitudeMicrodegrees + ((index + 1) * 18_000), complex.HomeBaseKey,
                defenseGroup, SimulationDomain.LandAbstract,
                operationalWeapons: AirDefense($"{complex.Key}-gbad-{index + 1:D2}", samRanges[index]));
        }

        yield return Entity(campaignId, $"{complex.Key}-fuel", $"{complex.Name} protected fuel farm",
            "tgt_fueltanks_large", complex.CoalitionId, complex.RegionKey,
            complex.LatitudeMicrodegrees + 9_000, complex.LongitudeMicrodegrees + 8_000, complex.HomeBaseKey,
            defenseGroup, SimulationDomain.Logistics);
        yield return Entity(campaignId, $"{complex.Key}-ammo", $"{complex.Name} ammunition storage",
            "tgt_ammo_depot_small", complex.CoalitionId, complex.RegionKey,
            complex.LatitudeMicrodegrees - 8_000, complex.LongitudeMicrodegrees - 9_000, complex.HomeBaseKey,
            defenseGroup, SimulationDomain.Logistics);

        var sortieRoles = new[] { "fighter", "strike", "support" };
        for (var index = 0; index < sortieRoles.Length; index++)
        {
            var role = sortieRoles[index];
            yield return Entity(campaignId, $"{complex.Key}-{role}-sortie", $"{complex.Name} opening {role} sortie",
                complex.AircraftCatalogIds[index], complex.CoalitionId, complex.RegionKey,
                complex.LatitudeMicrodegrees + ((index + 1) * 5_000),
                complex.LongitudeMicrodegrees - ((index + 1) * 5_000), complex.HomeBaseKey,
                $"{complex.Key}-{role}-sortie", SimulationDomain.Air);
        }
    }

    private static IReadOnlyList<Ww3PlusPackage> Ww3PlusPackages(CoalitionId blue, CoalitionId red) =>
    [
        // United States Atlantic Fleet and East Coast mobilization.
        FormationPackage("blue-usec-carriers", "US Atlantic carrier strike group",
        [
            ["usn_cv_america_79", "usn_cg_ticonderoga", "usn_dd_spruance", "usn_ff_knox_84", "usn_aoe_sacramento"],
            ["usn_cv_forrestal_75", "usn_cgn_virginia", "usn_dd_spruance_abl", "usn_ffg_oliver_hazard_perry", "usn_ao_neosho"],
            ["usn_cvn_nimitz", "usn_cg_belknap", "usn_ddg_adams_late", "usn_ffg_oliver_hazard_perry_longhull", "usn_aoe_sacramento"],
        ], blue, "usec", "norfolk", SimulationDomain.Maritime, 35_800_000, -73_000_000, "midatl", OrderKind.Patrol),
        Package("blue-usec-cruisers", "US Atlantic cruiser", ["usn_cg_ticonderoga", "usn_cgn_virginia", "usn_cg_belknap"], blue, "usec", "norfolk", SimulationDomain.Maritime, 6, 3, 36_100_000, -73_500_000, "midatl", OrderKind.Escort),
        Package("blue-usec-destroyers", "US Atlantic destroyer", ["usn_dd_spruance", "usn_dd_spruance_abl", "usn_ddg_adams_late"], blue, "usec", "charleston", SimulationDomain.Maritime, 10, 5, 34_800_000, -75_500_000, "midatl", OrderKind.Escort),
        Package("blue-usec-frigates", "US Atlantic frigate", ["usn_ffg_oliver_hazard_perry", "usn_ffg_oliver_hazard_perry_longhull", "usn_ff_knox_84"], blue, "usec", "mayport", SimulationDomain.Maritime, 16, 4, 31_500_000, -78_000_000, "midatl", OrderKind.Escort),
        Package("blue-usec-ssn", "US Atlantic attack submarine", ["usn_ssn_los_angeles", "usn_ssn_sturgeon"], blue, "usec", "new-london", SimulationDomain.Maritime, 8, 2, 39_500_000, -70_500_000, "giuk", OrderKind.Asw),
        Package("blue-usec-ssbn", "US Atlantic ballistic-missile submarine", ["usn_ssbn_lafayette", "usn_ssbn_james_madison"], blue, "usec", "new-london", SimulationDomain.Maritime, 3, 1, 38_000_000, -68_500_000, "midatl", OrderKind.Patrol),
        Package("blue-usec-amphib", "US Atlantic amphibious ship", ["usn_lha_tarawa", "usn_lpd_austin"], blue, "usec", "norfolk", SimulationDomain.Maritime, 6, 3, 35_000_000, -74_500_000, "caribbean", OrderKind.Reinforce),
        Package("blue-usec-aux", "US Atlantic fleet auxiliary", ["usn_aoe_sacramento", "civ_ms_sealift_pacific", "civ_ms_seabee"], blue, "usec", "charleston", SimulationDomain.Logistics, 6, 3, 33_500_000, -76_500_000, "midatl", OrderKind.Resupply),

        // United States Pacific Fleet, West Coast, and Hawaii.
        FormationPackage("blue-uswc-carriers", "US Pacific carrier strike group",
        [
            ["usn_cvn_enterprise", "usn_cgn_california", "usn_dd_spruance_abl", "usn_ff_knox_84", "usn_aoe_sacramento"],
            ["usn_cv_kitty_hawk", "usn_cg_ticonderoga", "usn_ddg_kidd", "usn_ffg_oliver_hazard_perry_longhull", "usn_aoe_sacramento"],
            ["usn_cvn_nimitz", "usn_cgn_long_beach_83", "usn_dd_spruance", "usn_ffg_oliver_hazard_perry", "usn_ao_t2"],
            ["usn_cv_kitty_hawk", "usn_cg_ticonderoga", "usn_ddg_adams_late", "usn_ff_garcia", "usn_ao_neosho"],
        ], blue, "uswc", "alameda", SimulationDomain.Maritime, 35_000_000, -124_000_000, "northpac", OrderKind.Patrol),
        Package("blue-uswc-cruisers", "US Pacific cruiser", ["usn_cg_ticonderoga", "usn_cgn_california", "usn_cgn_long_beach_83"], blue, "uswc", "long-beach", SimulationDomain.Maritime, 7, 3, 34_000_000, -123_000_000, "northpac", OrderKind.Escort),
        Package("blue-uswc-destroyers", "US Pacific destroyer", ["usn_dd_spruance_abl", "usn_ddg_kidd", "usn_ddg_adams_late"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 12, 4, 32_000_000, -121_500_000, "hawaii", OrderKind.Escort),
        Package("blue-uswc-frigates", "US Pacific frigate", ["usn_ffg_oliver_hazard_perry_longhull", "usn_ff_knox_84", "usn_ff_garcia"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 18, 6, 31_500_000, -122_500_000, "hawaii", OrderKind.Escort),
        Package("blue-uswc-ssn", "US Pacific attack submarine", ["usn_ssn_los_angeles", "usn_ssn_sturgeon", "usn_ssn_permit"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 10, 2, 30_500_000, -125_000_000, "northpac", OrderKind.Asw),
        Package("blue-pnw-ssbn", "US Pacific ballistic-missile submarine", ["usn_ssbn_lafayette", "usn_ssbn_franklin"], blue, "pnw", "bangor", SimulationDomain.Maritime, 4, 1, 47_500_000, -126_000_000, "northpac", OrderKind.Patrol),
        Package("blue-uswc-amphib", "US Pacific amphibious ship", ["usn_lha_tarawa", "usn_lpd_austin"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 8, 4, 30_000_000, -120_000_000, "hawaii", OrderKind.Reinforce),
        Package("blue-uswc-aux", "US Pacific fleet auxiliary", ["usn_aoe_sacramento", "civ_ms_sealift_pacific", "civ_ms_c7s68"], blue, "uswc", "long-beach", SimulationDomain.Logistics, 8, 4, 33_000_000, -121_000_000, "hawaii", OrderKind.Resupply),
        Package("blue-hawaii-screen", "US Hawaii surface combatant", ["usn_cg_ticonderoga", "usn_dd_spruance_abl", "usn_ffg_oliver_hazard_perry_longhull"], blue, "hawaii", "pearl-harbor", SimulationDomain.Maritime, 8, 4, 22_000_000, -160_000_000, "northpac", OrderKind.Patrol),
        Package("blue-hawaii-sub", "US Hawaii attack submarine", ["usn_ssn_los_angeles", "usn_ssn_sturgeon"], blue, "hawaii", "pearl-harbor", SimulationDomain.Maritime, 4, 2, 20_500_000, -160_500_000, "northpac", OrderKind.Asw),

        // Soviet global fleet packages.
        Package("red-northern-surface", "Soviet Northern Fleet combatant", ["wp_rkr_kirov", "wp_rkr_slava", "wp_bpk_udaloy", "wp_em_sovremenny"], red, "barents", "severomorsk", SimulationDomain.Maritime, 12, 4, 71_000_000, 31_000_000, "norsea", OrderKind.Patrol),
        Package("red-northern-sub", "Soviet Northern Fleet submarine", ["wp_ssn_victor3", "wp_ssgn_oscar1", "wp_ssbn_delta3", "wp_ss_kilo"], red, "barents", "polyarny", SimulationDomain.Maritime, 16, 4, 70_500_000, 29_000_000, "giuk", OrderKind.Asw),
        Package("red-northern-aux", "Soviet Northern Fleet amphibious or auxiliary ship", ["wp_bdk_ivan_rogov", "wp_bdk_ropucha", "wp_vt_boris_chilikin"], red, "barents", "severomorsk", SimulationDomain.Logistics, 10, 5, 69_800_000, 35_000_000, "norsea", OrderKind.Resupply),
        Package("red-baltic-surface", "Soviet Baltic Fleet combatant", ["wp_em_sovremenny", "wp_skr_krivak2", "wp_skr_petya3", "wp_ptg_osa1"], red, "baltic", "baltiysk", SimulationDomain.Maritime, 12, 4, 55_500_000, 18_000_000, "baltap", OrderKind.Patrol),
        Package("red-baltic-aux", "Warsaw Pact Baltic amphibious or auxiliary ship", ["wp_bdk_ropucha", "wp_bdk_alligator", "civ_ms_kommunist"], red, "baltic", "baltiysk", SimulationDomain.Logistics, 6, 3, 55_200_000, 19_000_000, "baltap", OrderKind.Reinforce),
        Package("red-pacific-surface", "Soviet Pacific Fleet combatant", ["wp_rkr_kirov", "wp_rkr_slava", "wp_em_sovremenny", "wp_bpk_udaloy", "wp_skr_krivak2"], red, "japan", "vladivostok", SimulationDomain.Maritime, 18, 6, 43_000_000, 135_000_000, "northpac", OrderKind.Patrol),
        Package("red-pacific-sub", "Soviet Pacific Fleet submarine", ["wp_ssn_victor3", "wp_ssgn_charlie2", "wp_ssgn_oscar1", "wp_ssbn_delta3", "wp_ss_kilo"], red, "okhotsk", "vladivostok", SimulationDomain.Maritime, 18, 3, 47_000_000, 150_000_000, "northpac", OrderKind.Asw),
        Package("red-pacific-aux", "Soviet Pacific Fleet amphibious or auxiliary ship", ["wp_bdk_ivan_rogov", "wp_bdk_ropucha", "wp_vt_boris_chilikin", "civ_ms_irkutsk"], red, "japan", "vladivostok", SimulationDomain.Logistics, 8, 4, 42_000_000, 133_000_000, "northpac", OrderKind.Resupply),
        Package("red-med-surface", "Soviet Mediterranean squadron combatant", ["wp_rkr_kynda", "wp_bpk_kara", "wp_skr_krivak2"], red, "med", "tartus", SimulationDomain.Maritime, 10, 5, 34_000_000, 25_000_000, "levant", OrderKind.Patrol),
        Package("red-med-sub", "Soviet Mediterranean submarine", ["wp_ss_kilo", "wp_ss_foxtrot", "wp_ssn_victor2"], red, "med", "tartus", SimulationDomain.Maritime, 4, 2, 35_000_000, 23_000_000, "levant", OrderKind.Asw),
        Package("red-med-aux", "Soviet Mediterranean auxiliary", ["wp_bdk_ropucha", "wp_vt_boris_chilikin"], red, "med", "tartus", SimulationDomain.Logistics, 2, 2, 34_500_000, 24_000_000, "levant", OrderKind.Resupply),

        // Cuban forces and representative allied fleets.
        Package("red-cuba-navy", "Cuban Revolutionary Navy unit", ["wp_ptg_osa1", "wp_skr_petya3", "wp_ss_foxtrot", "civ_ms_kommunist"], red, "cuba", "cienfuegos", SimulationDomain.Maritime, 12, 4, 21_500_000, -79_500_000, "florida", OrderKind.Patrol),
        Package("blue-allied-atlantic", "NATO Atlantic allied combatant", ["rn_dd_battle", "rcn_ss_oberon", "nrp_ssk_albacora", "fgs_ddg_lutjens"], blue, "wapp", "plymouth", SimulationDomain.Maritime, 16, 4, 50_000_000, -12_000_000, "giuk", OrderKind.Escort),
        Package("blue-allied-europe", "NATO European littoral combatant", ["fgs_ddg_lutjens", "knm_ptg_hauk", "es_ffg_baleares", "fr_ss_agosta"], blue, "nsea", "wilhelmshaven", SimulationDomain.Maritime, 12, 4, 55_000_000, 5_000_000, "baltap", OrderKind.Patrol),
        Package("blue-allied-pacific", "Pacific allied combatant", ["jmsdf_ddh_haruna", "jmsdf_ddg_tachikaze", "jmsdf_ss_yushio", "ran_ffg_adelaide_shorthull"], blue, "japan", "yokosuka", SimulationDomain.Maritime, 12, 4, 36_000_000, 142_000_000, "okhotsk", OrderKind.Patrol),
        Package("blue-allied-south", "Mediterranean or Indian Ocean allied combatant", ["es_ffg_baleares", "fr_ss_agosta", "wp_skr_petya3", "ran_ddg_perth"], blue, "med", "rota", SimulationDomain.Maritime, 8, 4, 37_000_000, 8_000_000, "levant", OrderKind.Patrol),

        // Civilian and merchant traffic. They remain coalition-affiliated until diplomacy/noncombatant rules exist.
        Package("blue-civil-atlantic", "Western transatlantic merchant", ["civ_ms_c7s68", "civ_ms_roro_a", "civ_ms_bulk", "civ_ms_car_carrier_a", "civ_ms_act_1", "civ_ms_amra", "civ_ms_c8", "civ_ms_encounter", "civ_ms_freighter_a", "civ_ms_freighter_b", "civ_ms_freighter_d", "civ_ms_super_p"], blue, "midatl", "hampton", SimulationDomain.Logistics, 18, 6, 43_000_000, -38_000_000, "wapp", OrderKind.Transit),
        Package("blue-civil-pacific", "Western Pacific merchant", ["civ_ms_sealift_pacific", "civ_ms_mairangi_bay", "civ_ms_roro_c", "civ_ms_bulk", "civ_ms_mercur", "civ_ms_ritina", "civ_ms_roro_b"], blue, "northpac", "pearl-harbor", SimulationDomain.Logistics, 18, 6, 35_000_000, -170_000_000, "japan", OrderKind.Transit),
        Package("red-civil-atlantic", "Soviet-bloc Atlantic merchant", ["civ_ms_kommunist", "civ_ms_slavyansk", "civ_ms_poltava", "civ_ms_metallurg_anosov", "civ_ms_ivan_franko"], red, "southatl", "havana", SimulationDomain.Logistics, 12, 6, 10_000_000, -35_000_000, "caribbean", OrderKind.Transit),
        Package("red-civil-pacific", "Soviet-bloc Pacific merchant", ["civ_ms_irkutsk", "civ_ms_andizhan", "civ_ms_yuniy_partizan", "civ_ms_murom"], red, "northpac", "vladivostok", SimulationDomain.Logistics, 8, 4, 45_000_000, 165_000_000, "japan", OrderKind.Transit),
        Package("blue-fishing-traffic", "Western fishing vessel", ["civ_fv_crabboat", "civ_fv_sterntrawler_a", "civ_fv_fishingboat_b", "civ_fv_fishingboat_a", "civ_fv_fishingboat_c", "civ_fv_fishingboat_d", "civ_fv_sterntrawler_b"], blue, "giuk", "keflavik", SimulationDomain.Logistics, 8, 4, 61_000_000, -22_000_000, "norsea", OrderKind.Transit),
        Package("red-fishing-traffic", "Soviet fishing or intelligence-adjacent vessel", ["civ_fv_okean", "civ_fv_sidetrawler", "civ_fv_sterntrawler_c", "civ_fv_dhow", "civ_fv_sampan", "civ_fv_sterntrawler_d"], red, "barents", "severomorsk", SimulationDomain.Logistics, 8, 4, 68_000_000, 25_000_000, "norsea", OrderKind.Transit),

        // Air formations: one campaign entity represents a squadron-, wing-, or mission-sized package.
        Package("blue-usec-air", "US East Coast air formation", ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a", "usaf_kc-135a"], blue, "usec", "oceana", SimulationDomain.Air, 8, 4, 36_500_000, -77_000_000, "midatl", OrderKind.Cap),
        Package("blue-uswc-air", "US West Coast air formation", ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a", "usaf_kc-135a"], blue, "uswc", "north-island", SimulationDomain.Air, 8, 4, 33_000_000, -119_000_000, "hawaii", OrderKind.Cap),
        Package("blue-central-air", "NATO Central Region air formation", ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a", "usaf_kc-135a"], blue, "northag", "ramstein", SimulationDomain.Air, 8, 4, 50_000_000, 7_000_000, "fulda", OrderKind.Cap),
        Package("red-west-air", "Warsaw Pact western air formation", ["wp_mig-23mld", "wp_su-24m", "wp_mig-25pd", "wp_tu-16k"], red, "baltic", "peenemunde", SimulationDomain.Air, 8, 4, 53_500_000, 14_000_000, "fulda", OrderKind.Cap),
        Package("red-fareast-air", "Soviet Far East air formation", ["wp_mig-23mld", "wp_su-24m", "wp_tu-95rt", "wp_tu-142m"], red, "japan", "vladivostok", SimulationDomain.Air, 6, 3, 44_000_000, 135_000_000, "northpac", OrderKind.Cap),
        Package("red-cuba-air", "Cuban-Soviet air formation", ["wp_mig-21", "wp_mig-23mld", "wp_mi-14"], red, "cuba", "san-antonio-cuba", SimulationDomain.Air, 6, 3, 22_500_000, -81_500_000, "florida", OrderKind.Cap),
        Package("blue-allied-pacific-air", "Allied Pacific air formation", ["raaf_f-111c", "usaf_f-5e"], blue, "southpac", "andersen", SimulationDomain.Air, 4, 2, 5_000_000, 155_000_000, "northpac", OrderKind.Cap),

        // Rotary-wing packages exercise attack, lift, maritime patrol, and ASW behavior separately from fixed-wing aircraft.
        Package("blue-central-rotary", "NATO attack or lift helicopter flight", ["usmc_ah-1t", "usn_ch-46"], blue, "northag", "ramstein", SimulationDomain.RotaryWing, 4, 2, 50_050_000, 7_250_000, "fulda", OrderKind.Patrol),
        FormationPackage("blue-atlantic-rotary", "USN ASW detachment",
        [
            ["usn_sh-2f", "usn_sh-3h"],
            ["usn_sh-2f", "usn_sh-3h"],
        ], blue, "giuk", "keflavik", SimulationDomain.RotaryWing,
            63_900_000, -22_450_000, "norsea", OrderKind.Asw),
        Package("red-central-rotary", "Warsaw Pact assault helicopter flight", ["wp_ka-29"], red, "fulda", "peenemunde", SimulationDomain.RotaryWing, 4, 2, 51_000_000, 10_600_000, "northag", OrderKind.Patrol),
        Package("red-northern-rotary", "Soviet maritime helicopter flight", ["wp_ka-27", "wp_mi-14"], red, "barents", "severomorsk", SimulationDomain.RotaryWing, 4, 2, 69_250_000, 33_250_000, "giuk", OrderKind.Asw),

        // Sea Power has no period ballistic TEL object. These use catalog-backed launcher silhouettes as explicit visual proxies.
        Package("blue-ballistic-tel", "NATO ballistic-missile TEL visual proxy", ["usa_mim-23_launcher"], blue, "northag", "pomcus-e", SimulationDomain.Ground, 4, 2, 50_650_000, 7_100_000, operationalWeapons: BallisticStrike("nato-ballistic-missile", 500_000)),
        Package("red-ballistic-tel", "Warsaw Pact ballistic-missile TEL visual proxy", ["wp_sa-10_tel"], red, "fulda", null, SimulationDomain.Ground, 6, 2, 51_100_000, 10_800_000, operationalWeapons: BallisticStrike("warsaw-pact-ballistic-missile", 650_000)),

        // Streamable fixed installations. Only corps/army formations below remain operational-only.
        Package("blue-airfields", "NATO reinforcement airfield", ["nato_small_airbase", "airfield_small_1", "nato_very_small_airbase"], blue, "northag", "ramstein", SimulationDomain.LandAbstract, 10, 2, 50_200_000, 7_500_000),
        Package("red-airfields", "Warsaw Pact dispersal airfield", ["wp_large_pvo_airbase", "wp_airbase_1", "wp_airbase_2", "wp_airbase_3"], red, "fulda", null, SimulationDomain.LandAbstract, 10, 2, 51_000_000, 11_000_000),
        Package("red-cuba-airfields", "Cuban military airfield", ["wp_airbase_1", "wp_airbase_2", "airfield_small_1"], red, "cuba", "san-antonio-cuba", SimulationDomain.LandAbstract, 6, 2, 22_000_000, -80_500_000),
        Package("blue-sam", "NATO integrated air-defense site", ["usa_mim-23_launcher", "usa_sam_site_nike_hercules", "nato_sam_skyguard", "usa_radar_tps-43"], blue, "northag", "ramstein", SimulationDomain.LandAbstract, 12, 3, 50_400_000, 8_200_000, operationalWeapons: AirDefense("nato-iads", 80_000)),
        Package("red-sam", "Warsaw Pact integrated air-defense site", ["wp_sam_site_sa-2_modernized", "wp_sam_site_sa-3", "wp_sam_site_sa-5", "wp_sam_site_sa-10"], red, "fulda", null, SimulationDomain.LandAbstract, 16, 4, 51_100_000, 10_500_000, operationalWeapons: AirDefense("warsaw-pact-iads", 120_000)),
        Package("red-cuba-sam", "Cuban air-defense site", ["wp_sam_site_sa-2_modernized", "wp_sam_site_sa-3", "wp_sa-6_launcher", "wp_p-35_radar"], red, "cuba", "lourdes", SimulationDomain.LandAbstract, 8, 2, 22_300_000, -81_000_000, operationalWeapons: AirDefense("cuban-iads", 65_000)),
        Package("blue-shore-defense", "NATO shore-defense battery", ["nato_radar", "usa_mim-23_launcher", "nv_130mm_coastal_artillery"], blue, "baltap", "karup", SimulationDomain.LandAbstract, 6, 2, 56_000_000, 10_500_000, operationalWeapons: ShoreDefense("nato-coastal-defense", 45_000)),
        Package("red-shore-defense", "Soviet-aligned shore-defense battery", ["wp_asc_redut", "wp_asc_rubezh", "wp_silkworm_launcher"], red, "cuba", "cienfuegos", SimulationDomain.LandAbstract, 6, 2, 21_800_000, -80_000_000, operationalWeapons: ShoreDefense("soviet-coastal-defense", 80_000)),
        Package("blue-supply-sites", "NATO fuel, ammunition, or warehouse site", ["tgt_fueltanks_large", "tgt_ammo_depot_small", "warehouses_2"], blue, "northag", "pomcus-e", SimulationDomain.Logistics, 3, 3, 50_700_000, 6_500_000),
        Package("red-supply-sites", "Soviet fuel, ammunition, or warehouse site", ["tgt_fueltanks_large_brown", "tgt_ammo_depot_small", "warehouses_3"], red, "cuba", "mariel", SimulationDomain.Logistics, 3, 3, 22_900_000, -82_600_000),

        // Campaign-scale ground formations stay abstract until an encounter is streamed down.
        Package("blue-ground-formations", "NATO ground formation", ["abstract_land_formation"], blue, "fulda", null, SimulationDomain.LandAbstract, 8, 4, 50_300_000, 9_300_000, maximumFidelity: FidelityState.Operational),
        Package("red-ground-formations", "Warsaw Pact ground formation", ["abstract_land_formation"], red, "fulda", null, SimulationDomain.LandAbstract, 8, 4, 50_900_000, 10_200_000, maximumFidelity: FidelityState.Operational),
        Package("red-cuba-ground", "Cuban ground formation", ["abstract_land_formation"], red, "cuba", null, SimulationDomain.LandAbstract, 4, 2, 22_200_000, -79_800_000, maximumFidelity: FidelityState.Operational),
        Package("blue-rear-area", "NATO rear-area command", ["abstract_land_formation"], blue, "northag", null, SimulationDomain.LandAbstract, 2, 2, 50_600_000, 8_000_000, maximumFidelity: FidelityState.Operational),
        Package("red-rear-area", "Soviet rear-area command", ["abstract_land_formation"], red, "baltic", null, SimulationDomain.LandAbstract, 2, 2, 53_900_000, 13_500_000, maximumFidelity: FidelityState.Operational),
        .. LandAirExpansionPackages(blue, red),
        .. NavalExpansionPackages(blue, red),
    ];

    private static Ww3PlusPackage Package(
        string key,
        string name,
        string[] catalogUnitIds,
        CoalitionId coalitionId,
        string regionKey,
        string? homeBaseKey,
        SimulationDomain simulationDomain,
        int count,
        int groupSize,
        int latitude,
        int longitude,
        string? destinationRegionKey = null,
        OrderKind orderKind = OrderKind.Hold,
        FidelityState maximumFidelity = FidelityState.Tactical,
        IReadOnlyList<OperationalWeaponCapability>? operationalWeapons = null,
        bool dedicatedFormations = false,
        string? formationOwner = null) =>
        new(key, name, catalogUnitIds, coalitionId, regionKey, homeBaseKey, simulationDomain,
            maximumFidelity, count, groupSize, latitude, longitude, destinationRegionKey, orderKind,
            operationalWeapons, dedicatedFormations, formationOwner);

    private static Ww3PlusPackage FormationPackage(
        string key,
        string name,
        string[][] formations,
        CoalitionId coalitionId,
        string regionKey,
        string? homeBaseKey,
        SimulationDomain simulationDomain,
        int latitude,
        int longitude,
        string? destinationRegionKey = null,
        OrderKind orderKind = OrderKind.Hold,
        FidelityState maximumFidelity = FidelityState.Tactical,
        IReadOnlyList<OperationalWeaponCapability>? operationalWeapons = null)
    {
        if (formations.Length == 0
            || formations[0].Length == 0
            || formations.Any(formation => formation.Length != formations[0].Length))
        {
            throw new ArgumentException(
                "Dedicated formations must contain equally sized, non-empty rosters.",
                nameof(formations));
        }
        var roster = formations.SelectMany(formation => formation).ToArray();
        return Package(
            key,
            name,
            roster,
            coalitionId,
            regionKey,
            homeBaseKey,
            simulationDomain,
            roster.Length,
            formations[0].Length,
            latitude,
            longitude,
            destinationRegionKey,
            orderKind,
            maximumFidelity,
            operationalWeapons,
            dedicatedFormations: true);
    }

    private static IEnumerable<FuldaEntitySeed> ExpandPackage(CampaignId campaignId, Ww3PlusPackage package)
    {
        var ordinal = 0;
        var groups = PackageGroups(package);
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            for (var memberIndex = 0; memberIndex < group.Count; memberIndex++)
            {
                ordinal++;
                var key = $"{package.Key}-{ordinal:D3}";
                var catalogUnitId = group[memberIndex];
                var position = DeploymentPosition(
                    package,
                    groupIndex,
                    memberIndex,
                    group.Count,
                    catalogUnitId);
                var hostedByYorktown = string.Equals(
                        package.Key,
                        "blue-atlantic-rotary",
                        StringComparison.Ordinal)
                    && ordinal <= 2;
                var usesAswLoadout = string.Equals(
                    package.Key,
                    "blue-atlantic-rotary",
                    StringComparison.Ordinal);
                yield return Entity(
                    campaignId,
                    key,
                    GeneratedUnitName(catalogUnitId, package.SimulationDomain, ordinal),
                    catalogUnitId,
                    package.CoalitionId,
                    hostedByYorktown ? "nsea" : package.RegionKey,
                    hostedByYorktown ? 57_000_000 : position.LatitudeMicrodegrees,
                    hostedByYorktown ? 3_000_000 : position.LongitudeMicrodegrees,
                    hostedByYorktown ? "bremerhaven" : package.HomeBaseKey,
                    hostedByYorktown ? "blue-north-sea" : PackageGroupKey(package, groupIndex),
                    package.SimulationDomain,
                    package.MaximumFidelity,
                    catalogLoadoutId: usesAswLoadout ? "ASW" : "Default",
                    operationalWeapons: package.OperationalWeapons,
                    hostEntityKey: hostedByYorktown ? "yorktown" : null);
            }
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> PackageGroups(Ww3PlusPackage package)
    {
        if (package.DedicatedFormations)
        {
            return Enumerable.Range(0, package.Count / package.GroupSize)
                .Select(groupIndex => (IReadOnlyList<string>)package.CatalogUnitIds
                    .Skip(groupIndex * package.GroupSize)
                    .Take(package.GroupSize)
                    .ToArray())
                .ToArray();
        }

        // Ordinary packages describe a pool of available platforms, not a recipe
        // for mixing every platform into each formation. Allocate each platform's
        // share into one or more homogeneous groups. Deliberately mixed formations
        // must use FormationPackage and provide their roster explicitly.
        var groups = new List<IReadOnlyList<string>>();
        var activeCatalogCount = Math.Min(package.Count, package.CatalogUnitIds.Count);
        for (var catalogIndex = 0; catalogIndex < activeCatalogCount; catalogIndex++)
        {
            var quantity = package.Count / activeCatalogCount
                + (catalogIndex < package.Count % activeCatalogCount ? 1 : 0);
            var formationCount = (quantity + package.GroupSize - 1) / package.GroupSize;
            for (var formationIndex = 0; formationIndex < formationCount; formationIndex++)
            {
                var formationSize = quantity / formationCount
                    + (formationIndex < quantity % formationCount ? 1 : 0);
                groups.Add(Enumerable.Repeat(
                    package.CatalogUnitIds[catalogIndex],
                    formationSize).ToArray());
            }
        }
        return groups;
    }

    private static SeedPosition DeploymentPosition(
        Ww3PlusPackage package,
        int groupIndex,
        int memberIndex,
        int memberCount,
        string catalogUnitId)
    {
        var center = DeploymentGroupCenter(package, groupIndex);
        var station = ResolveFormationStation(
            package,
            memberIndex,
            memberCount,
            catalogUnitId);
        return OffsetPosition(
            center,
            station.DistanceNauticalMiles,
            NormalizeBearing(DeploymentBearing(package) + station.RelativeBearingDegrees));
    }

    private static SeedPosition DeploymentGroupCenter(Ww3PlusPackage package, int groupIndex)
    {
        var groupCount = PackageGroups(package).Count;
        var isFrontFormation = package.SimulationDomain is SimulationDomain.Ground
            or SimulationDomain.LandAbstract;
        var groupsPerLine = isFrontFormation
            || package.SimulationDomain == SimulationDomain.Installation
            ? Math.Min(groupCount, 8)
            : Math.Min(groupCount, 10);
        var lineCount = (groupCount + groupsPerLine - 1) / groupsPerLine;
        var slot = groupIndex % groupsPerLine;
        var line = groupIndex / groupsPerLine;
        var slotsOnLine = Math.Min(groupsPerLine, groupCount - (line * groupsPerLine));
        var spacing = DeploymentGroupSpacingNauticalMiles(package);
        var alongTrack = (slot - ((slotsOnLine - 1) / 2d)) * spacing;
        if ((line & 1) == 1)
        {
            alongTrack += spacing * 0.5d;
        }
        var crossTrack = (line - ((lineCount - 1) / 2d)) * spacing * 0.55d;
        var origin = new SeedPosition(
            package.LatitudeMicrodegrees,
            package.LongitudeMicrodegrees);
        var bearing = DeploymentBearing(package);
        if (isFrontFormation)
        {
            var frontagePosition = OffsetSigned(origin, alongTrack, bearing + 90d);
            return OffsetPosition(
                frontagePosition,
                line * spacing * 0.55d,
                bearing + 180d);
        }
        var alongPosition = OffsetSigned(origin, alongTrack, bearing);
        return OffsetSigned(alongPosition, crossTrack, bearing + 90d);
    }

    private static FormationStation ResolveFormationStation(
        Ww3PlusPackage package,
        int memberIndex,
        int memberCount,
        string catalogUnitId)
    {
        if (memberIndex == 0)
        {
            return new FormationStation(0d, 0d);
        }
        if (package.SimulationDomain is SimulationDomain.Air or SimulationDomain.RotaryWing)
        {
            var spacing = package.SimulationDomain == SimulationDomain.Air ? 0.1d : 0.25d;
            var side = (memberIndex & 1) == 1 ? -1d : 1d;
            var rank = (memberIndex + 1) / 2d;
            return new FormationStation(spacing * rank, side < 0 ? 150d : 210d);
        }
        if (package.SimulationDomain is SimulationDomain.Ground or SimulationDomain.LandAbstract)
        {
            var column = memberIndex % 4;
            var row = memberIndex / 4;
            var lateral = (column - 1.5d) * 0.8d;
            var depth = row * 1.2d;
            return PolarStation(lateral, -depth);
        }
        if (package.SimulationDomain == SimulationDomain.Installation
            || IsFixedInstallationCatalog(catalogUnitId))
        {
            var distance = 2d + ((memberIndex - 1) % 3 * 1.5d);
            return new FormationStation(distance, memberIndex * (360d / memberCount));
        }
        if (package.CatalogUnitIds.All(IsSubmarineCatalog))
        {
            var distance = 12d + (((memberIndex - 1) / 2) * 8d);
            return new FormationStation(distance, (memberIndex & 1) == 1 ? 90d : 270d);
        }
        if (IsSubmarineCatalog(catalogUnitId))
        {
            return new FormationStation(20d, 0d);
        }
        if (IsFleetAuxiliaryCatalog(catalogUnitId))
        {
            return new FormationStation(8d, 180d);
        }
        if (IsAirDefenseEscortCatalog(catalogUnitId))
        {
            var bearings = new[] { 90d, 270d, 30d, 330d, 150d, 210d };
            return new FormationStation(2d, bearings[(memberIndex - 1) % bearings.Length]);
        }
        if (package.CatalogUnitIds.All(IsCivilianCatalog))
        {
            return new FormationStation(memberIndex * 1.5d, 180d);
        }
        var escortBearings = new[] { 30d, 330d, 90d, 270d, 150d, 210d };
        var escortDistance = package.GroupSize <= 3 ? 1.5d : 5d;
        return new FormationStation(
            escortDistance,
            escortBearings[(memberIndex - 1) % escortBearings.Length]);
    }

    private static FormationStation PolarStation(double lateralNauticalMiles, double forwardNauticalMiles)
    {
        var distance = Math.Sqrt(
            (lateralNauticalMiles * lateralNauticalMiles)
            + (forwardNauticalMiles * forwardNauticalMiles));
        if (distance == 0d)
        {
            return new FormationStation(0d, 0d);
        }
        var bearing = Math.Atan2(lateralNauticalMiles, forwardNauticalMiles)
            * 180d / Math.PI;
        return new FormationStation(distance, NormalizeBearing(bearing));
    }

    private static double DeploymentGroupSpacingNauticalMiles(Ww3PlusPackage package)
    {
        if (package.Key.Contains("carrier", StringComparison.Ordinal)
            || package.Key.Contains("aviation-cruiser", StringComparison.Ordinal)
            || package.Key.Contains("battleship", StringComparison.Ordinal))
        {
            return 120d;
        }
        if (package.CatalogUnitIds.All(IsSubmarineCatalog))
        {
            return 70d;
        }
        return package.SimulationDomain switch
        {
            SimulationDomain.Maritime => 45d,
            SimulationDomain.Logistics when package.CatalogUnitIds.All(IsCivilianCatalog) => 35d,
            SimulationDomain.Logistics => 18d,
            SimulationDomain.Air => 70d,
            SimulationDomain.RotaryWing => 20d,
            SimulationDomain.LandAbstract => 18d,
            SimulationDomain.Ground => 8d,
            SimulationDomain.Installation => 10d,
            _ => 20d,
        };
    }

    private static double DeploymentBearing(Ww3PlusPackage package) =>
        (package.RegionKey, package.DestinationRegionKey) switch
        {
            ("usec", "midatl") => 90d,
            ("uswc", "northpac") => 270d,
            ("midatl", "wapp") => 65d,
            ("wapp", "giuk") => 25d,
            ("giuk", "norsea") => 35d,
            ("barents", "giuk" or "norsea") => 225d,
            ("baltic", "baltap") => 245d,
            ("blacksea", "bosporus") => 235d,
            ("cuba", "florida") => 330d,
            ("japan" or "okhotsk", "northpac") => 85d,
            ("southchina", "taiwan" or "japan") => 30d,
            ("med", "levant") => 90d,
            ("northag", "fulda") => 105d,
            ("fulda", "northag") => 285d,
            ("northag", _) => 105d,
            ("fulda", _) => 285d,
            ("korea-land" or "korea", _) => 20d,
            ("taiwan-land" or "taiwan", _) => 15d,
            _ => StableBearing(package.Key),
        };

    private static double StableBearing(string key)
    {
        uint hash = 2_166_136_261;
        foreach (var value in key)
        {
            hash = (hash ^ value) * 16_777_619;
        }
        return hash % 360;
    }

    private static SeedPosition OffsetSigned(
        SeedPosition origin,
        double signedDistanceNauticalMiles,
        double bearingDegrees) =>
        signedDistanceNauticalMiles >= 0d
            ? OffsetPosition(origin, signedDistanceNauticalMiles, bearingDegrees)
            : OffsetPosition(origin, -signedDistanceNauticalMiles, bearingDegrees + 180d);

    private static SeedPosition OffsetPosition(
        SeedPosition origin,
        double distanceNauticalMiles,
        double bearingDegrees)
    {
        if (distanceNauticalMiles == 0d)
        {
            return origin;
        }
        const double earthRadiusNauticalMiles = 3_440.065d;
        var latitude = origin.LatitudeMicrodegrees / 1_000_000d * Math.PI / 180d;
        var longitude = origin.LongitudeMicrodegrees / 1_000_000d * Math.PI / 180d;
        var bearing = NormalizeBearing(bearingDegrees) * Math.PI / 180d;
        var angularDistance = distanceNauticalMiles / earthRadiusNauticalMiles;
        var targetLatitude = Math.Asin(
            (Math.Sin(latitude) * Math.Cos(angularDistance))
            + (Math.Cos(latitude) * Math.Sin(angularDistance) * Math.Cos(bearing)));
        var targetLongitude = longitude + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitude),
            Math.Cos(angularDistance) - (Math.Sin(latitude) * Math.Sin(targetLatitude)));
        var latitudeDegrees = Math.Clamp(targetLatitude * 180d / Math.PI, -85d, 85d);
        var longitudeDegrees = ((targetLongitude * 180d / Math.PI + 540d) % 360d) - 180d;
        return new SeedPosition(
            checked((int)Math.Round(latitudeDegrees * 1_000_000d)),
            checked((int)Math.Round(longitudeDegrees * 1_000_000d)));
    }

    private static double NormalizeBearing(double bearing) =>
        ((bearing % 360d) + 360d) % 360d;

    private static bool IsCivilianCatalog(string catalogUnitId) =>
        catalogUnitId.StartsWith("civ_", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("merchant_", StringComparison.Ordinal);

    private static bool IsFixedInstallationCatalog(string catalogUnitId) =>
        catalogUnitId.StartsWith("tgt_", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("warehouses_", StringComparison.Ordinal)
        || catalogUnitId.Contains("airbase", StringComparison.Ordinal)
        || catalogUnitId.Contains("radar", StringComparison.Ordinal)
        || catalogUnitId.Contains("sam_site", StringComparison.Ordinal);

    private static bool IsSubmarineCatalog(string catalogUnitId) =>
        HasCatalogRole(catalogUnitId, "ss", "ssn", "ssbn", "ssgn", "ssk", "ssg");

    private static bool IsFleetAuxiliaryCatalog(string catalogUnitId) =>
        HasCatalogRole(catalogUnitId, "ao", "aoe", "aor", "ae", "vt")
        || catalogUnitId.StartsWith("civ_", StringComparison.Ordinal);

    private static bool IsAirDefenseEscortCatalog(string catalogUnitId) =>
        HasCatalogRole(catalogUnitId, "cg", "cgn", "clg", "ddg", "rkr");

    private static bool HasCatalogRole(string catalogUnitId, params string[] roles)
    {
        var parts = catalogUnitId.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => roles.Contains(part, StringComparer.Ordinal));
    }

    private static string GeneratedUnitName(
        string catalogUnitId,
        SimulationDomain simulationDomain,
        int ordinal)
    {
        var parts = catalogUnitId.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return $"Campaign unit {ordinal:D3}";
        }

        var prefix = parts[0] switch
        {
            "usn" when simulationDomain is SimulationDomain.Air or SimulationDomain.RotaryWing => "USN",
            "usn" => "USS",
            "usaf" => "USAF",
            "usmc" => "USMC",
            "usa" => "US Army",
            "rn" => "HMS",
            "rcn" => "HMCS",
            "ran" => "HMAS",
            "fgs" => "FGS",
            "knm" => "KNM",
            "jmsdf" => "JMSDF",
            "jasdf" => "JASDF",
            "jsdf" => "JSDF",
            "nato" => "NATO",
            "raf" => "RAF",
            "raaf" => "RAAF",
            "wp" => "Soviet",
            "pla" => "PLA",
            "plan" => "PLAN",
            "iaf" => "IAF",
            "iriaf" => "IRIAF",
            "irina" => "IRINA",
            "iqaf" => "IQAF",
            "iqa" => "Iraqi Army",
            "fr" => "French",
            "fin" => "Finnish",
            "swe" => "Swedish",
            "nv" => "Norwegian",
            "civ" => "Civilian",
            "port" => string.Empty,
            "tgt" => string.Empty,
            _ => HumanizeCatalogPart(parts[0]),
        };
        var roleParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aaa", "aoe", "apc", "bb", "bdk", "bpk", "car", "cg", "cgn", "clg",
            "cv", "cvn", "dd", "ddg", "em", "ff", "ffg", "fv", "lha", "lpd",
            "fcr", "launcher", "lt", "mbt", "ms", "ptg", "radar", "rkr", "sam",
            "site", "skr", "spaag", "ss", "ssbn", "ssgn", "ssn", "tel", "vt",
        };
        var start = 1;
        while (start < parts.Length - 1 && roleParts.Contains(parts[start]))
        {
            start++;
        }
        var platformParts = parts.Skip(start).ToList();
        if (simulationDomain == SimulationDomain.Maritime
            && platformParts.Count > 1
            && platformParts[^1].All(char.IsDigit))
        {
            platformParts.RemoveAt(platformParts.Count - 1);
        }
        var platform = string.Join(' ', platformParts.Select(HumanizeCatalogPart));
        if (string.IsNullOrWhiteSpace(platform))
        {
            platform = string.Join(' ', parts.Skip(1).Select(HumanizeCatalogPart));
        }
        if (string.Equals(parts[0], "port", StringComparison.OrdinalIgnoreCase))
        {
            platform = $"{platform} Port";
        }
        return string.Join(' ', new[] { prefix, platform, ordinal.ToString("D3") }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string HumanizeCatalogPart(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("type", StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > 4
            && trimmed[4..].All(char.IsDigit))
        {
            return $"Type {trimmed[4..]}";
        }
        if (trimmed.Any(char.IsDigit))
        {
            return trimmed.ToUpperInvariant();
        }
        var normalized = trimmed.Replace('-', ' ');
        if (normalized.Length == 0)
        {
            return normalized;
        }
        if (normalized is "asw" or "cap" or "ddh" or "ew" or "ewr" or "fcr"
            or "gbad" or "rldn" or "tel"
            || normalized is "i" or "ii" or "iii" or "iv" or "v" or "vi")
        {
            return normalized.ToUpperInvariant();
        }
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string PackageGroupKey(Ww3PlusPackage package, int groupIndex) =>
        $"{package.Key}-group-{groupIndex + 1:D2}";

    private static IReadOnlyList<OperationalWeaponCapability> AirDefense(string ammunitionId, long rangeMeters) =>
    [
        new OperationalWeaponCapability(
            ammunitionId,
            ContactClassification.Air,
            DistanceMeters.FromMeters(rangeMeters)),
    ];

    private static IReadOnlyList<OperationalWeaponCapability> ShoreDefense(string ammunitionId, long rangeMeters) =>
    [
        new OperationalWeaponCapability(
            ammunitionId,
            ContactClassification.Surface,
            DistanceMeters.FromMeters(rangeMeters)),
    ];

    private static IReadOnlyList<OperationalWeaponCapability> BallisticStrike(string ammunitionId, long rangeMeters) =>
    [
        new OperationalWeaponCapability(
            ammunitionId,
            ContactClassification.LandFormation,
            DistanceMeters.FromMeters(rangeMeters)),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, Ww3PlusPackage>> Ww3PlusPackagesByKey =
        new(() => Ww3PlusPackages(default, default)
            .ToDictionary(package => package.Key, StringComparer.Ordinal));

    private static string Ww3PlusGroupName(string key)
    {
        var groupMarker = key.LastIndexOf("-group-", StringComparison.Ordinal);
        var packageKey = groupMarker < 0 ? key : key[..groupMarker];
        var groupNumber = groupMarker < 0
            ? 0
            : int.TryParse(key[(groupMarker + 7)..], out var parsed) ? parsed : 0;
        if (Ww3PlusPackagesByKey.Value.TryGetValue(packageKey, out var package))
        {
            var groups = PackageGroups(package);
            var groupIndex = Math.Clamp(groupNumber - 1, 0, groups.Count - 1);
            var group = groups[groupIndex];
            if (!package.DedicatedFormations
                && group.Count > 0
                && group.All(item => string.Equals(item, group[0], StringComparison.Ordinal))
                && !string.Equals(group[0], "abstract_land_formation", StringComparison.Ordinal))
            {
                var matchingGroups = groups
                    .Select((items, index) => new { items, index })
                    .Where(item => item.items.Count > 0
                        && string.Equals(item.items[0], group[0], StringComparison.Ordinal))
                    .ToArray();
                var platformOrdinal = matchingGroups.Count(item => item.index <= groupIndex);
                var platformName = PlatformGroupName(package, group[0]);
                return matchingGroups.Length > 1
                    ? $"{platformName} {platformOrdinal}"
                    : platformName;
            }

            return groups.Count > 1
                ? $"{package.Name} {groupIndex + 1}"
                : package.Name;
        }

        if (key.EndsWith("-defense-complex", StringComparison.Ordinal))
        {
            return $"{HumanizeGroupStem(key[..^16])} air defenses";
        }
        foreach (var role in new[] { "fighter", "strike", "support" })
        {
            var suffix = $"-{role}-sortie";
            if (key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return $"{HumanizeGroupStem(key[..^suffix.Length])} {role} sortie";
            }
        }

        var readableKey = key
            .Replace("blue-", string.Empty, StringComparison.Ordinal)
            .Replace("red-", string.Empty, StringComparison.Ordinal)
            .Replace("usec", "US East Coast", StringComparison.Ordinal)
            .Replace("uswc", "US West Coast", StringComparison.Ordinal)
            .Replace("pnw", "Pacific Northwest", StringComparison.Ordinal);
        return string.Join(' ', readableKey
            .Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(HumanizeCatalogPart));
    }

    private static string HumanizeGroupStem(string key)
    {
        var stem = key
            .Replace("blue-", string.Empty, StringComparison.Ordinal)
            .Replace("red-", string.Empty, StringComparison.Ordinal);
        if (stem.EndsWith("-complex", StringComparison.Ordinal))
        {
            stem = stem[..^8];
        }
        return string.Join(' ', stem
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(HumanizeCatalogPart));
    }

    private static string PlatformGroupName(Ww3PlusPackage package, string catalogUnitId)
    {
        var parts = catalogUnitId.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "Campaign formation";
        }
        var service = package.FormationOwner
            ?? ServiceName(parts[0], package.SimulationDomain);
        var modelParts = parts.Skip(1)
            .Where(part => !CatalogRoleWords.Contains(part))
            .ToList();
        while (modelParts.Count > 1 && IsCatalogVariantNoise(modelParts[^1]))
        {
            modelParts.RemoveAt(modelParts.Count - 1);
        }
        if (parts[0] == "port" && modelParts.Count > 1 && modelParts[0] == "pg")
        {
            modelParts.RemoveAt(0);
        }
        var model = string.Join(' ', modelParts.Select(HumanizeCatalogPart));
        if (string.IsNullOrWhiteSpace(model))
        {
            model = string.Join(' ', parts.Skip(1).Select(HumanizeCatalogPart));
        }
        model = model
            .Replace("130MM", "130 mm", StringComparison.Ordinal)
            .Replace(" Coastal Artillery", string.Empty, StringComparison.Ordinal)
            .Replace(" Emplacement", string.Empty, StringComparison.Ordinal)
            .Replace("Pickup 1983 ", string.Empty, StringComparison.Ordinal)
            .Replace("Oliver Hazard Perry", "Perry", StringComparison.Ordinal)
            .Replace("Ural ", string.Empty, StringComparison.Ordinal);
        if (string.Equals(model, "Coastal Artillery", StringComparison.Ordinal))
        {
            model = string.Empty;
        }
        var formation = FormationNoun(catalogUnitId, package.SimulationDomain);
        return string.Join(' ', new[] { service, model, formation }
            .Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string ServiceName(string prefix, SimulationDomain domain) => prefix switch
    {
        "usaf" => "USAF",
        "usn" => domain is SimulationDomain.Air or SimulationDomain.RotaryWing ? "USN" : "US Navy",
        "usmc" => "USMC",
        "usa" => "US Army",
        "jasdf" => "JASDF",
        "jmsdf" => "JMSDF",
        "jsdf" => "JGSDF",
        "raf" => "RAF",
        "raaf" => "RAAF",
        "ran" => "RAN",
        "rn" => "Royal Navy",
        "rcn" => "RCN",
        "fgs" => "West German Navy",
        "knm" => "Norwegian Navy",
        "nrp" => "Portuguese Navy",
        "es" => "Spanish Navy",
        "fr" => "French",
        "fin" => "Finnish",
        "swe" => "Swedish",
        "nv" => "North Vietnamese",
        "wp" => "Soviet",
        "pla" => "PLA",
        "plan" => "PLAN",
        "iaf" => "Israeli Air Force",
        "ins" => "Israeli Navy",
        "iriaf" => "IRIAF",
        "irina" => "Iranian Navy",
        "iqaf" => "Iraqi Air Force",
        "iqa" => "Iraqi Army",
        "ir" => "Iranian Navy",
        "nato" => "NATO",
        "civ" => "Civilian",
        "port" or "tgt" or "warehouses" or "oil" or "airfield" or "abstract" => string.Empty,
        _ => HumanizeCatalogPart(prefix),
    };

    private static string FormationNoun(string catalogUnitId, SimulationDomain domain)
    {
        if (domain == SimulationDomain.Air)
        {
            return catalogUnitId.StartsWith("civ_", StringComparison.Ordinal)
                ? "flight"
                : "squadron";
        }
        if (domain == SimulationDomain.RotaryWing)
        {
            return "flight";
        }
        if (domain == SimulationDomain.Maritime)
        {
            if (IsSubmarineCatalog(catalogUnitId)) return "submarine group";
            if (HasCatalogRole(catalogUnitId, "cv", "cvn")) return "carrier group";
            if (HasCatalogRole(catalogUnitId, "cg", "cgn", "clg", "rkr", "kr")) return "cruiser group";
            if (HasCatalogRole(catalogUnitId, "dd", "ddg", "em", "bpk")) return "destroyer group";
            if (HasCatalogRole(catalogUnitId, "ff", "ffg", "skr")) return "frigate group";
            if (HasCatalogRole(catalogUnitId, "lha", "lpd", "lka", "lst", "bdk", "ap")) return "amphibious group";
            if (HasCatalogRole(catalogUnitId, "msc", "mso")) return "mine-warfare group";
            if (HasCatalogRole(catalogUnitId, "pt", "ptg", "pb", "mrk", "mpk")) return "patrol group";
            return "naval group";
        }
        if (domain == SimulationDomain.Ground)
        {
            if (catalogUnitId.Contains("coastal_artillery", StringComparison.Ordinal)) return "coastal battery";
            if (HasCatalogRole(catalogUnitId, "mbt", "lt")) return "tank company";
            if (HasCatalogRole(catalogUnitId, "apc", "ifv")) return "mechanized company";
            if (HasCatalogRole(catalogUnitId, "mlrs", "spa")) return "artillery battery";
            if (HasCatalogRole(catalogUnitId, "sam", "aaa", "spaag")
                || catalogUnitId.Contains("launcher", StringComparison.Ordinal)) return "air-defense battery";
            if (HasCatalogRole(catalogUnitId, "car")) return "support company";
            return "ground unit";
        }
        if (domain is SimulationDomain.Installation or SimulationDomain.LandAbstract)
        {
            if (catalogUnitId.Contains("airbase", StringComparison.Ordinal)
                || catalogUnitId.StartsWith("airfield_", StringComparison.Ordinal)) return "airbase";
            if (catalogUnitId.Contains("radar", StringComparison.Ordinal)
                || catalogUnitId.Contains("ewr", StringComparison.Ordinal)) return "radar site";
            if (catalogUnitId.Contains("sam", StringComparison.Ordinal)
                || catalogUnitId.Contains("launcher", StringComparison.Ordinal)) return "SAM site";
            if (catalogUnitId.StartsWith("port_", StringComparison.Ordinal)) return "naval base";
            return "site";
        }
        if (domain == SimulationDomain.Logistics)
        {
            return IsCivilianCatalog(catalogUnitId) ? "convoy" : "logistics group";
        }
        return "formation";
    }

    private static bool IsCatalogVariantNoise(string value) =>
        value is "default" or "early" or "late" or "mod" or "shorthull" or "longhull"
        || value.All(char.IsDigit);

    private static readonly HashSet<string> CatalogRoleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "aaa", "ae", "agi", "ao", "aoe", "aor", "ap", "apc", "avp", "bb", "bdk",
        "bpk", "car", "cg", "cgn", "clg", "cv", "cvn", "dd", "ddg", "em", "fab",
        "fcr", "ff", "ffg", "fs", "fv", "ifv", "k", "kr", "launcher", "lha", "lka", "lpd",
        "lst", "lt", "mbt", "mlrs", "mpk", "mrk", "ms", "msc", "mso", "pb", "pf",
        "pt", "ptg", "radar", "rkr", "sam", "site", "skr", "spa", "spaag", "ss",
        "ssbn", "ssg", "ssgn", "ssk", "ssn", "tel", "vt",
    };

    private sealed record Ww3PlusPackage(
        string Key,
        string Name,
        IReadOnlyList<string> CatalogUnitIds,
        CoalitionId CoalitionId,
        string RegionKey,
        string? HomeBaseKey,
        SimulationDomain SimulationDomain,
        FidelityState MaximumFidelity,
        int Count,
        int GroupSize,
        int LatitudeMicrodegrees,
        int LongitudeMicrodegrees,
        string? DestinationRegionKey,
        OrderKind OrderKind,
        IReadOnlyList<OperationalWeaponCapability>? OperationalWeapons,
        bool DedicatedFormations,
        string? FormationOwner);

    private readonly record struct SeedPosition(
        int LatitudeMicrodegrees,
        int LongitudeMicrodegrees);

    private readonly record struct FormationStation(
        double DistanceNauticalMiles,
        double RelativeBearingDegrees);

    private sealed record Ww3AirbaseComplex(
        string Key,
        string Name,
        CoalitionId CoalitionId,
        string RegionKey,
        string HomeBaseKey,
        int LatitudeMicrodegrees,
        int LongitudeMicrodegrees,
        string DestinationRegionKey,
        string AirfieldCatalogId,
        string RadarCatalogId,
        IReadOnlyList<string> SamCatalogIds,
        IReadOnlyList<string> AircraftCatalogIds);
}
