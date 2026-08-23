using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.ServiceHost;

internal static partial class FuldaGapCampaignSeed
{
    internal static bool IsCivilianTraffic(FuldaEntitySeed entity) =>
        entity.CatalogUnitId.StartsWith("civ_", StringComparison.Ordinal)
        || entity.CatalogUnitId.StartsWith("merchant_", StringComparison.Ordinal);

    internal static bool IsFixedSite(FuldaEntitySeed entity) =>
        entity.SimulationDomain == SimulationDomain.LandAbstract
            && entity.CatalogUnitId != "abstract_land_formation"
        || entity.SimulationDomain == SimulationDomain.Logistics
            && (entity.CatalogUnitId.StartsWith("tgt_", StringComparison.Ordinal)
                || entity.CatalogUnitId.StartsWith("warehouses_", StringComparison.Ordinal));

    private static IReadOnlyList<FuldaRegionSeed> Ww3PlusRegions(CampaignId campaignId) =>
    [
        Region(campaignId, "gulfmex", "Gulf of Mexico sea lanes", ["usec"]),
        Region(campaignId, "florida", "Florida approaches and Straits", ["usec", "gulfmex"]),
        Region(campaignId, "caribbean", "Caribbean basin and Windward Passage", ["southatl", "gulfmex", "florida"]),
        Region(campaignId, "cuba", "Cuba, Soviet-aligned forward defense zone", ["florida", "caribbean"]),
        Region(campaignId, "uswc", "US West Coast fleet concentration", ["northpac"]),
        Region(campaignId, "pnw", "Puget Sound and Pacific Northwest", ["uswc", "northpac"]),
        Region(campaignId, "hawaii", "Hawaii and Central Pacific staging area", ["uswc", "northpac", "southpac"]),
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
            .SelectMany(package => Enumerable.Range(0, GroupCount(package)).Select(groupIndex =>
            {
                var groupKey = PackageGroupKey(package, groupIndex);
                return Order(
                    campaignId,
                    $"{groupKey}-{package.OrderKind.ToString().ToLowerInvariant()}",
                    groupIds[groupKey],
                    regionIds[package.DestinationRegionKey!],
                    package.OrderKind);
            }))
            .ToArray();

    private static IReadOnlyList<FuldaEntitySeed> Ww3AirbaseComplexEntities(
        CampaignId campaignId,
        CoalitionId blue,
        CoalitionId red) =>
        Ww3AirbaseComplexes(blue, red)
            .SelectMany(complex => ExpandAirbaseComplex(campaignId, complex))
            .ToArray();

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
        // United States Atlantic Fleet and East Coast mobilization: 58 entities.
        Package("blue-usec-carriers", "US Atlantic carrier", ["usn_cv_america_79", "usn_cv_forrestal_75", "usn_cvn_nimitz"], blue, "usec", "norfolk", SimulationDomain.Maritime, 3, 1, 35_800_000, -73_000_000, "midatl", OrderKind.Patrol),
        Package("blue-usec-cruisers", "US Atlantic cruiser", ["usn_cg_ticonderoga", "usn_cgn_virginia", "usn_cg_belknap"], blue, "usec", "norfolk", SimulationDomain.Maritime, 6, 3, 36_100_000, -73_500_000, "midatl", OrderKind.Escort),
        Package("blue-usec-destroyers", "US Atlantic destroyer", ["usn_dd_spruance", "usn_dd_spruance_abl", "usn_ddg_adams_late"], blue, "usec", "charleston", SimulationDomain.Maritime, 10, 5, 34_800_000, -75_500_000, "midatl", OrderKind.Escort),
        Package("blue-usec-frigates", "US Atlantic frigate", ["usn_ffg_oliver_hazard_perry", "usn_ffg_oliver_hazard_perry_longhull", "usn_ff_knox_84"], blue, "usec", "mayport", SimulationDomain.Maritime, 16, 4, 31_500_000, -78_000_000, "midatl", OrderKind.Escort),
        Package("blue-usec-ssn", "US Atlantic attack submarine", ["usn_ssn_los_angeles", "usn_ssn_sturgeon"], blue, "usec", "new-london", SimulationDomain.Maritime, 8, 2, 39_500_000, -70_500_000, "giuk", OrderKind.Asw),
        Package("blue-usec-ssbn", "US Atlantic ballistic-missile submarine", ["usn_ssbn_lafayette", "usn_ssbn_james_madison"], blue, "usec", "new-london", SimulationDomain.Maritime, 3, 1, 38_000_000, -68_500_000, "midatl", OrderKind.Patrol),
        Package("blue-usec-amphib", "US Atlantic amphibious ship", ["usn_lha_tarawa", "usn_lpd_austin"], blue, "usec", "norfolk", SimulationDomain.Maritime, 6, 3, 35_000_000, -74_500_000, "caribbean", OrderKind.Reinforce),
        Package("blue-usec-aux", "US Atlantic fleet auxiliary", ["usn_aoe_sacramento", "civ_ms_sealift_pacific", "civ_ms_seabee"], blue, "usec", "charleston", SimulationDomain.Logistics, 6, 3, 33_500_000, -76_500_000, "midatl", OrderKind.Resupply),

        // United States Pacific Fleet, West Coast, and Hawaii: 83 entities.
        Package("blue-uswc-carriers", "US Pacific carrier", ["usn_cvn_enterprise", "usn_cv_kitty_hawk", "usn_cvn_nimitz"], blue, "uswc", "alameda", SimulationDomain.Maritime, 4, 1, 35_000_000, -124_000_000, "northpac", OrderKind.Patrol),
        Package("blue-uswc-cruisers", "US Pacific cruiser", ["usn_cg_ticonderoga", "usn_cgn_california", "usn_cgn_long_beach_83"], blue, "uswc", "long-beach", SimulationDomain.Maritime, 7, 3, 34_000_000, -123_000_000, "northpac", OrderKind.Escort),
        Package("blue-uswc-destroyers", "US Pacific destroyer", ["usn_dd_spruance_abl", "usn_ddg_kidd", "usn_ddg_adams_late"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 12, 4, 32_000_000, -121_500_000, "hawaii", OrderKind.Escort),
        Package("blue-uswc-frigates", "US Pacific frigate", ["usn_ffg_oliver_hazard_perry_longhull", "usn_ff_knox_84", "usn_ff_garcia"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 18, 6, 31_500_000, -122_500_000, "hawaii", OrderKind.Escort),
        Package("blue-uswc-ssn", "US Pacific attack submarine", ["usn_ssn_los_angeles", "usn_ssn_sturgeon", "usn_ssn_permit"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 10, 2, 30_500_000, -125_000_000, "northpac", OrderKind.Asw),
        Package("blue-pnw-ssbn", "US Pacific ballistic-missile submarine", ["usn_ssbn_lafayette", "usn_ssbn_franklin"], blue, "pnw", "bangor", SimulationDomain.Maritime, 4, 1, 47_500_000, -126_000_000, "northpac", OrderKind.Patrol),
        Package("blue-uswc-amphib", "US Pacific amphibious ship", ["usn_lha_tarawa", "usn_lpd_austin"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 8, 4, 30_000_000, -120_000_000, "hawaii", OrderKind.Reinforce),
        Package("blue-uswc-aux", "US Pacific fleet auxiliary", ["usn_aoe_sacramento", "civ_ms_sealift_pacific", "civ_ms_c7s68"], blue, "uswc", "long-beach", SimulationDomain.Logistics, 8, 4, 33_000_000, -121_000_000, "hawaii", OrderKind.Resupply),
        Package("blue-hawaii-screen", "US Hawaii surface combatant", ["usn_cg_ticonderoga", "usn_dd_spruance_abl", "usn_ffg_oliver_hazard_perry_longhull"], blue, "hawaii", "pearl-harbor", SimulationDomain.Maritime, 8, 4, 22_000_000, -160_000_000, "northpac", OrderKind.Patrol),
        Package("blue-hawaii-sub", "US Hawaii attack submarine", ["usn_ssn_los_angeles", "usn_ssn_sturgeon"], blue, "hawaii", "pearl-harbor", SimulationDomain.Maritime, 4, 2, 20_500_000, -160_500_000, "northpac", OrderKind.Asw),

        // Soviet global fleet packages: 116 entities.
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

        // Cuban forces and representative allied fleets: 60 entities.
        Package("red-cuba-navy", "Cuban Revolutionary Navy unit", ["wp_ptg_osa1", "wp_skr_petya3", "wp_ss_foxtrot", "civ_ms_kommunist"], red, "cuba", "cienfuegos", SimulationDomain.Maritime, 12, 4, 21_500_000, -79_500_000, "florida", OrderKind.Patrol),
        Package("blue-allied-atlantic", "NATO Atlantic allied combatant", ["rn_dd_battle", "rcn_ss_oberon", "nrp_ssk_albacora", "fgs_ddg_lutjens"], blue, "wapp", "plymouth", SimulationDomain.Maritime, 16, 4, 50_000_000, -12_000_000, "giuk", OrderKind.Escort),
        Package("blue-allied-europe", "NATO European littoral combatant", ["fgs_ddg_lutjens", "knm_ptg_hauk", "es_ffg_baleares", "fr_ss_agosta"], blue, "nsea", "wilhelmshaven", SimulationDomain.Maritime, 12, 4, 55_000_000, 5_000_000, "baltap", OrderKind.Patrol),
        Package("blue-allied-pacific", "Pacific allied combatant", ["jmsdf_ddh_haruna", "jmsdf_ddg_tachikaze", "jmsdf_ss_yushio", "ran_ffg_adelaide_shorthull"], blue, "japan", "yokosuka", SimulationDomain.Maritime, 12, 4, 36_000_000, 142_000_000, "okhotsk", OrderKind.Patrol),
        Package("blue-allied-south", "Mediterranean or Indian Ocean allied combatant", ["es_ffg_baleares", "fr_ss_agosta", "wp_skr_petya3", "ran_ddg_perth"], blue, "med", "rota", SimulationDomain.Maritime, 8, 4, 37_000_000, 8_000_000, "levant", OrderKind.Patrol),

        // Civilian and merchant traffic. They remain coalition-affiliated until diplomacy/noncombatant rules exist.
        Package("blue-civil-atlantic", "Western transatlantic merchant", ["civ_ms_c7s68", "civ_ms_roro_a", "civ_ms_bulk", "civ_ms_car_carrier_a"], blue, "midatl", "hampton", SimulationDomain.Logistics, 18, 6, 43_000_000, -38_000_000, "wapp", OrderKind.Transit),
        Package("blue-civil-pacific", "Western Pacific merchant", ["civ_ms_sealift_pacific", "civ_ms_mairangi_bay", "civ_ms_roro_c", "civ_ms_bulk"], blue, "northpac", "pearl-harbor", SimulationDomain.Logistics, 18, 6, 35_000_000, -170_000_000, "japan", OrderKind.Transit),
        Package("red-civil-atlantic", "Soviet-bloc Atlantic merchant", ["civ_ms_kommunist", "civ_ms_slavyansk", "civ_ms_poltava", "civ_ms_metallurg_anosov"], red, "southatl", "havana", SimulationDomain.Logistics, 12, 6, 10_000_000, -35_000_000, "caribbean", OrderKind.Transit),
        Package("red-civil-pacific", "Soviet-bloc Pacific merchant", ["civ_ms_irkutsk", "civ_ms_andizhan", "civ_ms_yuniy_partizan", "civ_ms_murom"], red, "northpac", "vladivostok", SimulationDomain.Logistics, 8, 4, 45_000_000, 165_000_000, "japan", OrderKind.Transit),
        Package("blue-fishing-traffic", "Western fishing vessel", ["civ_fv_crabboat", "civ_fv_sterntrawler_a", "civ_fv_fishingboat_b"], blue, "giuk", "keflavik", SimulationDomain.Logistics, 8, 4, 61_000_000, -22_000_000, "norsea", OrderKind.Transit),
        Package("red-fishing-traffic", "Soviet fishing or intelligence-adjacent vessel", ["civ_fv_okean", "civ_fv_sidetrawler", "civ_fv_sterntrawler_c"], red, "barents", "severomorsk", SimulationDomain.Logistics, 8, 4, 68_000_000, 25_000_000, "norsea", OrderKind.Transit),

        // Air formations: one campaign entity represents a squadron-, wing-, or mission-sized package.
        Package("blue-usec-air", "US East Coast air formation", ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a", "usaf_kc-135a"], blue, "usec", "oceana", SimulationDomain.Air, 8, 4, 36_500_000, -77_000_000, "midatl", OrderKind.Cap),
        Package("blue-uswc-air", "US West Coast air formation", ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a", "usaf_kc-135a"], blue, "uswc", "north-island", SimulationDomain.Air, 8, 4, 33_000_000, -119_000_000, "hawaii", OrderKind.Cap),
        Package("blue-central-air", "NATO Central Region air formation", ["usaf_f-15c", "usaf_f-4e", "usaf_e-3a", "usaf_kc-135a"], blue, "northag", "ramstein", SimulationDomain.Air, 8, 4, 50_000_000, 7_000_000, "fulda", OrderKind.Cap),
        Package("red-west-air", "Warsaw Pact western air formation", ["wp_mig-23mld", "wp_su-24m", "wp_mig-25pd", "wp_tu-16k"], red, "baltic", "peenemunde", SimulationDomain.Air, 8, 4, 53_500_000, 14_000_000, "fulda", OrderKind.Cap),
        Package("red-fareast-air", "Soviet Far East air formation", ["wp_mig-23mld", "wp_su-24m", "wp_tu-95rt", "wp_tu-142m"], red, "japan", "vladivostok", SimulationDomain.Air, 6, 3, 44_000_000, 135_000_000, "northpac", OrderKind.Cap),
        Package("red-cuba-air", "Cuban-Soviet air formation", ["wp_mig-21", "wp_mig-23mld", "wp_mi-14"], red, "cuba", "san-antonio-cuba", SimulationDomain.Air, 6, 3, 22_500_000, -81_500_000, "florida", OrderKind.Cap),
        Package("blue-allied-pacific-air", "Allied Pacific air formation", ["raaf_f-111c", "usaf_f-5e"], blue, "southpac", "andersen", SimulationDomain.Air, 4, 2, 5_000_000, 155_000_000, "northpac", OrderKind.Cap),

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
        Package("blue-ground-formations", "NATO division or brigade", ["abstract_land_formation"], blue, "fulda", null, SimulationDomain.LandAbstract, 8, 4, 50_300_000, 9_300_000, maximumFidelity: FidelityState.Operational),
        Package("red-ground-formations", "Warsaw Pact division or brigade", ["abstract_land_formation"], red, "fulda", null, SimulationDomain.LandAbstract, 8, 4, 50_900_000, 10_200_000, maximumFidelity: FidelityState.Operational),
        Package("red-cuba-ground", "Cuban ground formation", ["abstract_land_formation"], red, "cuba", null, SimulationDomain.LandAbstract, 4, 2, 22_200_000, -79_800_000, maximumFidelity: FidelityState.Operational),
        Package("blue-rear-area", "NATO rear-area command", ["abstract_land_formation"], blue, "northag", null, SimulationDomain.LandAbstract, 2, 2, 50_600_000, 8_000_000, maximumFidelity: FidelityState.Operational),
        Package("red-rear-area", "Soviet rear-area command", ["abstract_land_formation"], red, "baltic", null, SimulationDomain.LandAbstract, 2, 2, 53_900_000, 13_500_000, maximumFidelity: FidelityState.Operational),
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
        IReadOnlyList<OperationalWeaponCapability>? operationalWeapons = null) =>
        new(key, name, catalogUnitIds, coalitionId, regionKey, homeBaseKey, simulationDomain,
            maximumFidelity, count, groupSize, latitude, longitude, destinationRegionKey, orderKind,
            operationalWeapons);

    private static IEnumerable<FuldaEntitySeed> ExpandPackage(CampaignId campaignId, Ww3PlusPackage package)
    {
        for (var index = 0; index < package.Count; index++)
        {
            var ordinal = index + 1;
            var key = $"{package.Key}-{ordinal:D3}";
            var catalogUnitId = package.CatalogUnitIds[index % package.CatalogUnitIds.Count];
            var latitudeOffset = ((index % 6) - 2) * 22_000;
            var longitudeOffset = (((index / 6) % 6) - 2) * 28_000;
            yield return Entity(
                campaignId,
                key,
                $"{package.Name} {ordinal:D3}",
                catalogUnitId,
                package.CoalitionId,
                package.RegionKey,
                package.LatitudeMicrodegrees + latitudeOffset,
                package.LongitudeMicrodegrees + longitudeOffset,
                package.HomeBaseKey,
                PackageGroupKey(package, index / package.GroupSize),
                package.SimulationDomain,
                package.MaximumFidelity,
                operationalWeapons: package.OperationalWeapons);
        }
    }

    private static int GroupCount(Ww3PlusPackage package) =>
        (package.Count + package.GroupSize - 1) / package.GroupSize;

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

    private static string Ww3PlusGroupName(string key) =>
        string.Join(' ', key.Split('-', StringSplitOptions.RemoveEmptyEntries))
            .Replace(" group ", " task group ", StringComparison.Ordinal)
            .Replace("usec", "US East Coast", StringComparison.Ordinal)
            .Replace("uswc", "US West Coast", StringComparison.Ordinal)
            .Replace("pnw", "Pacific Northwest", StringComparison.Ordinal);

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
        IReadOnlyList<OperationalWeaponCapability>? OperationalWeapons);

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
