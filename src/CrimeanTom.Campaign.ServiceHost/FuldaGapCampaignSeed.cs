using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.LoadAcceleration;

namespace CrimeanTom.Campaign.ServiceHost;

internal static partial class FuldaGapCampaignSeed
{
    private static readonly Lazy<SeaPowerOperationalCatalog?> OperationalCatalog =
        new(SeaPowerOperationalCatalog.TryCreateFromEnvironment);

    public static FuldaGapSeed Create(CampaignId campaignId)
    {
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
            Region(campaignId, "southatl", "South Atlantic convoy lanes", ["midatl"]),
            Region(campaignId, "patagonia", "Patagonian shelf and Argentine approaches", ["southatl"]),
            Region(campaignId, "drake", "Drake Passage", ["patagonia"]),
            Region(campaignId, "cape", "Cape sea route", ["southatl"]),
            Region(campaignId, "med", "Central and eastern Mediterranean", ["wapp"]),
            Region(campaignId, "levant", "Levant and eastern Mediterranean littoral", ["med"]),
            Region(campaignId, "redsea", "Red Sea and Bab-el-Mandeb", ["levant"]),
            Region(campaignId, "arabian", "Arabian Sea", ["redsea"]),
            Region(campaignId, "gulf", "Persian Gulf and Strait of Hormuz", ["arabian"]),
            Region(campaignId, "indian", "Central Indian Ocean", ["cape", "arabian"]),
            Region(campaignId, "india-west", "Indian west coast and Bombay approaches", ["arabian", "indian"]),
            Region(campaignId, "pakistan-coast", "Karachi approaches", ["arabian", "gulf"]),
            Region(campaignId, "southchina", "South China Sea", ["indian"]),
            Region(campaignId, "taiwan", "Taiwan Strait and northern Philippine Sea", ["southchina"]),
            Region(campaignId, "korea", "Korean Peninsula and Tsushima approaches", ["taiwan"]),
            Region(campaignId, "japan", "Sea of Japan and Philippine Sea", ["korea"]),
            Region(campaignId, "okhotsk", "Sea of Okhotsk and Kuril approaches", ["japan", "barents"]),
            Region(campaignId, "northpac", "North Pacific reinforcement route", ["japan", "usec"]),
            Region(campaignId, "southpac", "South Pacific transit route", ["drake", "southchina"]),
            Region(campaignId, "australia", "Australian approaches", ["southpac", "indian"]),
        }.Concat(Ww3PlusRegions(campaignId)).ToArray();
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
            Base(campaignId, "buenos-aires", "Buenos Aires Soviet-support logistics node", red, "patagonia", -34_580_000, -58_380_000),
            Base(campaignId, "ushuaia", "Ushuaia patrol staging", red, "drake", -54_800_000, -68_300_000),
            Base(campaignId, "diego-garcia", "Diego Garcia", blue, "indian", -7_320_000, 72_420_000),
            Base(campaignId, "haifa", "Haifa", blue, "levant", 32_820_000, 34_990_000),
            Base(campaignId, "tartus", "Tartus Soviet support facility", red, "levant", 34_900_000, 35_870_000),
            Base(campaignId, "bandar-abbas", "Bandar Abbas", red, "gulf", 27_180_000, 56_270_000),
            Base(campaignId, "yokosuka", "Yokosuka", blue, "japan", 35_290_000, 139_670_000),
            Base(campaignId, "vladivostok", "Vladivostok", red, "japan", 43_110_000, 131_890_000),
            Base(campaignId, "subic", "Subic Bay", blue, "southchina", 14_820_000, 120_280_000),
            Base(campaignId, "sanya", "Sanya", red, "southchina", 18_250_000, 109_510_000),
            Base(campaignId, "fremantle", "Fremantle", blue, "australia", -32_060_000, 115_740_000),
            Base(campaignId, "plymouth", "Plymouth", blue, "wapp", 50_370_000, -4_140_000),
            Base(campaignId, "wilhelmshaven", "Wilhelmshaven", blue, "bight", 53_530_000, 8_130_000),
            Base(campaignId, "rota", "Rota", blue, "med", 36_620_000, -6_350_000),
            Base(campaignId, "toulon", "Toulon", blue, "med", 43_120_000, 5_930_000),
            Base(campaignId, "lisbon", "Lisbon", blue, "wapp", 38_700_000, -9_180_000),
            Base(campaignId, "tripoli", "Tripoli", red, "med", 32_890_000, 13_190_000),
            Base(campaignId, "mumbai", "Mumbai", blue, "india-west", 18_940_000, 72_840_000),
            Base(campaignId, "karachi", "Karachi", red, "pakistan-coast", 24_850_000, 66_990_000),
            Base(campaignId, "cam-ranh", "Cam Ranh Bay", red, "southchina", 11_890_000, 109_170_000),
            Base(campaignId, "kaohsiung", "Kaohsiung, Taiwan", blue, "taiwan", 22_620_000, 120_312_000),
            Base(campaignId, "chinhae", "Chinhae, South Korea", blue, "korea", 35_150_000, 128_660_000),
            Base(campaignId, "wonsan", "Wonsan, North Korea", red, "korea", 39_150_000, 127_440_000),
        }.Concat(Ww3PlusBases(campaignId, blue, red)).ToArray();
        var baseIds = bases.ToDictionary(item => item.Key, item => item.BaseId, StringComparer.Ordinal);
        var entityTemplates = new[]
        {
            Entity(campaignId, "pharris", "USS Pharris", "usn_ff_knox_84", blue, "giuk", 62_700_000, -15_000_000, "keflavik", "blue-giuk", SimulationDomain.Maritime),
            Entity(campaignId, "los-angeles", "USS Honolulu", "usn_ssn_los_angeles", blue, "giuk", 63_100_000, -17_200_000, "keflavik", "blue-giuk-sub", SimulationDomain.Maritime),
            Entity(campaignId, "yorktown", "USS Yorktown", "usn_cg_ticonderoga", blue, "nsea", 57_000_000, 3_000_000, "bremerhaven", "blue-north-sea", SimulationDomain.Maritime),
            Entity(campaignId, "spruance", "USS Spruance", "usn_dd_spruance", blue, "nsea", 57_100_000, 3_100_000, "bremerhaven", "blue-north-sea", SimulationDomain.Maritime),
            Entity(campaignId, "reforger", "REFORGER Convoy 261", "merchant_roro_convoy", blue, "midatl", 47_000_000, -31_000_000, "hampton", "blue-reforger", SimulationDomain.Logistics),
            Entity(campaignId, "neosho", "USNS Neosho", "usn_ao_neosho", blue, "midatl", 46_900_000, -31_100_000, "hampton", "blue-reforger", SimulationDomain.Logistics),
            Entity(campaignId, "hauk", "RNoN Hauk Squadron", "knm_ptg_hauk", blue, "baltap", 57_300_000, 10_900_000, "karup", "blue-baltap", SimulationDomain.Maritime),
            Entity(campaignId, "v-corps", "US V Corps (abstract)", "abstract_land_formation", blue, "fulda", 50_550_000, 9_680_000, null, "blue-fulda", SimulationDomain.LandAbstract, FidelityState.Operational),
            Entity(campaignId, "northern-sag", "Northern Fleet Surface Action Group", "wp_rkr_slava", red, "barents", 70_200_000, 30_500_000, "severomorsk", "red-northern-sag", SimulationDomain.Maritime),
            Entity(campaignId, "udaloy", "Admiral Zakharov", "wp_bpk_udaloy", red, "barents", 70_100_000, 30_700_000, "severomorsk", "red-northern-sag", SimulationDomain.Maritime),
            Entity(campaignId, "victor", "Victor III patrol", "wp_ssn_victor3", red, "giuk", 64_000_000, -12_800_000, "severomorsk", "red-giuk", SimulationDomain.Maritime),
            Entity(campaignId, "oscar", "Oscar I SSGN patrol", "wp_ssgn_oscar1", red, "giuk", 64_300_000, -11_900_000, "severomorsk", "red-giuk", SimulationDomain.Maritime),
            Entity(campaignId, "krivak", "Rezvyy", "wp_skr_krivak2", red, "nsea", 57_180_000, 3_260_000, "baltiysk", "red-north-sea-picket", SimulationDomain.Maritime),
            Entity(campaignId, "osa", "Osa missile boat squadron", "wp_ptg_osa1", red, "baltic", 55_000_000, 17_600_000, "baltiysk", "red-baltic", SimulationDomain.Maritime),
            Entity(campaignId, "eighth-guards", "8th Guards Army (abstract)", "abstract_land_formation", red, "fulda", 50_800_000, 10_300_000, null, "red-fulda", SimulationDomain.LandAbstract, FidelityState.Operational),
            Entity(campaignId, "argentine-sealift", "Argentine-Soviet sealift convoy", "civ_ms_roro_a", red, "patagonia", -38_200_000, -57_100_000, "buenos-aires", "red-argentina-support", SimulationDomain.Logistics),
            Entity(campaignId, "argentine-roro", "Patagonian military cargo convoy", "wp_ms_roro_b", red, "southatl", -42_000_000, -48_000_000, "buenos-aires", "red-argentina-support", SimulationDomain.Logistics),
            Entity(campaignId, "southatl-kara", "Soviet South Atlantic Kara group", "wp_bpk_kara", red, "southatl", -37_000_000, -35_000_000, "ushuaia", "red-southatl", SimulationDomain.Maritime),
            Entity(campaignId, "southatl-kynda", "Soviet South Atlantic Kynda group", "wp_rkr_kynda", red, "southatl", -37_200_000, -34_700_000, "ushuaia", "red-southatl", SimulationDomain.Maritime),
            Entity(campaignId, "southatl-escort", "US South Atlantic convoy escort", "usn_ffg_brooke", blue, "southatl", -31_000_000, -28_000_000, "hampton", "blue-southatl", SimulationDomain.Maritime),
            Entity(campaignId, "kidd-gulf", "USS Kidd Gulf action group", "usn_ddg_kidd", blue, "arabian", 23_000_000, 61_000_000, "diego-garcia", "blue-us-indian-ocean", SimulationDomain.Maritime),
            Entity(campaignId, "israel-saar", "Israeli Sa'ar missile group", "ins_ptg_saar_4", blue, "levant", 33_000_000, 34_600_000, "haifa", "blue-israel-levant", SimulationDomain.Maritime),
            Entity(campaignId, "israel-gal", "Israeli Gal submarine patrol", "ins_ss_gal", blue, "levant", 32_500_000, 34_100_000, "haifa", "blue-israel-levant", SimulationDomain.Maritime),
            Entity(campaignId, "iran-alvand", "Iranian Alvand Gulf group", "ir_ffg_alvand", red, "gulf", 26_000_000, 55_000_000, "bandar-abbas", "red-iran", SimulationDomain.Maritime),
            Entity(campaignId, "iran-combattante", "Iranian Combattante II missile group", "ir_ptg_combattante_II", red, "gulf", 26_200_000, 55_300_000, "bandar-abbas", "red-iran", SimulationDomain.Maritime),
            Entity(campaignId, "levant-osa", "Syrian Osa missile group", "wp_ptg_osa2", red, "levant", 34_600_000, 35_600_000, "tartus", "red-syria-levant", SimulationDomain.Maritime),
            Entity(campaignId, "levant-ropucha", "Soviet Ropucha reinforcement ship", "wp_bdk_ropucha", red, "levant", 34_700_000, 35_300_000, "tartus", "red-soviet-levant", SimulationDomain.Logistics),
            Entity(campaignId, "kitty-hawk", "USS Kitty Hawk", "usn_cv_kitty_hawk", blue, "japan", 30_000_000, 142_000_000, "yokosuka", "blue-pacific-cvg", SimulationDomain.Maritime),
            Entity(campaignId, "pacific-tico", "Pacific Fleet Ticonderoga escort", "usn_cg_ticonderoga", blue, "japan", 30_200_000, 142_200_000, "yokosuka", "blue-pacific-cvg", SimulationDomain.Maritime),
            Entity(campaignId, "pacific-spruance", "USS Fife", "usn_dd_spruance_abl", blue, "japan", 29_920_000, 142_050_000, "yokosuka", "blue-pacific-cvg", SimulationDomain.Maritime),
            Entity(campaignId, "pacific-perry", "USS Curts", "usn_ffg_oliver_hazard_perry_longhull", blue, "japan", 30_080_000, 141_920_000, "yokosuka", "blue-pacific-cvg", SimulationDomain.Maritime),
            Entity(campaignId, "pacific-sacramento", "USS Sacramento", "usn_aoe_sacramento", blue, "japan", 29_850_000, 141_850_000, "yokosuka", "blue-pacific-cvg", SimulationDomain.Logistics),
            Entity(campaignId, "haruna", "JDS Haruna", "jmsdf_ddh_haruna", blue, "japan", 36_000_000, 137_000_000, "yokosuka", "blue-japan", SimulationDomain.Maritime),
            Entity(campaignId, "tachikaze", "JDS Tachikaze", "jmsdf_ddg_tachikaze", blue, "japan", 36_100_000, 137_200_000, "yokosuka", "blue-japan", SimulationDomain.Maritime),
            Entity(campaignId, "yushio", "JMSDF Yushio-class patrol", "jmsdf_ss_yushio", blue, "okhotsk", 43_000_000, 146_000_000, "yokosuka", "blue-japan", SimulationDomain.Maritime),
            Entity(campaignId, "plan-luda", "PLAN Luda I surface group", "plan_dd_luda1", red, "southchina", 19_000_000, 113_000_000, "sanya", "red-plan", SimulationDomain.Maritime),
            Entity(campaignId, "plan-jianghu", "PLAN Jianghu III frigate group", "plan_ff_jianghu3", red, "southchina", 19_200_000, 113_300_000, "sanya", "red-plan", SimulationDomain.Maritime),
            Entity(campaignId, "plan-han", "PLAN Han submarine patrol", "plan_ssn_han", red, "southchina", 18_500_000, 115_000_000, "sanya", "red-plan", SimulationDomain.Maritime),
            Entity(campaignId, "pacific-sovremenny", "Soviet Pacific Fleet Sovremenny", "wp_em_sovremenny", red, "japan", 41_000_000, 134_000_000, "vladivostok", "red-pacific", SimulationDomain.Maritime),
            Entity(campaignId, "pacific-charlie", "Soviet Pacific Fleet Charlie II", "wp_ssgn_charlie2", red, "northpac", 44_000_000, 155_000_000, "vladivostok", "red-pacific", SimulationDomain.Maritime),
            Entity(campaignId, "pacific-tanker", "Soviet Pacific replenishment group", "wp_vt_boris_chilikin", red, "okhotsk", 45_000_000, 148_000_000, "vladivostok", "red-pacific", SimulationDomain.Logistics),
            Entity(campaignId, "canadian-oberon", "HMCS Ojibwa Canadian barrier patrol", "rcn_ss_oberon", blue, "giuk", 61_500_000, -20_000_000, "keflavik", "blue-canada-atlantic", SimulationDomain.Maritime),
            Entity(campaignId, "royal-navy-battle", "Royal Navy Battle-class Atlantic escort", "rn_dd_battle", blue, "wapp", 49_000_000, -10_000_000, "plymouth", "blue-uk-atlantic", SimulationDomain.Maritime),
            Entity(campaignId, "lutjens", "FGS Lutjens", "fgs_ddg_lutjens", blue, "bight", 54_000_000, 7_000_000, "wilhelmshaven", "blue-continental-allies", SimulationDomain.Maritime),
            Entity(campaignId, "baleares", "Spanish Baleares-class frigate", "es_ffg_baleares", blue, "med", 37_000_000, -4_000_000, "rota", "blue-spain-med", SimulationDomain.Maritime),
            Entity(campaignId, "french-agosta", "French Agosta submarine patrol", "fr_ss_agosta", blue, "med", 41_000_000, 5_000_000, "toulon", "blue-france-med", SimulationDomain.Maritime),
            Entity(campaignId, "portuguese-albacora", "Portuguese Albacora submarine patrol", "nrp_ssk_albacora", blue, "wapp", 39_000_000, -11_000_000, "lisbon", "blue-portugal-atlantic", SimulationDomain.Maritime),
            Entity(campaignId, "libyan-missile", "Libyan Beir Grassa missile group", "ln_ptg_beir_grassa", red, "med", 33_000_000, 14_000_000, "tripoli", "red-mediterranean", SimulationDomain.Maritime),
            Entity(campaignId, "india-petya", "Indian Navy Petya III escort group", "wp_skr_petya3", blue, "india-west", 17_500_000, 70_500_000, "mumbai", "blue-india", SimulationDomain.Maritime),
            Entity(campaignId, "india-osa", "Indian Navy Osa I missile group", "wp_ptg_osa1", blue, "india-west", 17_300_000, 70_700_000, "mumbai", "blue-india", SimulationDomain.Maritime),
            Entity(campaignId, "pakistan-hangor", "PNS Hangor", "pns_ss_hangor", red, "pakistan-coast", 23_000_000, 65_000_000, "karachi", "red-pakistan", SimulationDomain.Maritime),
            Entity(campaignId, "pakistan-hashmat", "PNS Hashmat", "pns_ss_hashmat", red, "pakistan-coast", 22_700_000, 65_400_000, "karachi", "red-pakistan", SimulationDomain.Maritime),
            Entity(campaignId, "ran-perth", "HMAS Perth", "ran_ddg_perth", blue, "australia", -30_000_000, 112_000_000, "fremantle", "blue-australia", SimulationDomain.Maritime),
            Entity(campaignId, "ran-adelaide", "HMAS Adelaide", "ran_ffg_adelaide_shorthull", blue, "australia", -29_800_000, 112_300_000, "fremantle", "blue-australia", SimulationDomain.Maritime),
            Entity(campaignId, "ran-oberon", "RAN Oberon-class patrol", "ran_ss_oberon", blue, "southpac", -25_000_000, 130_000_000, "fremantle", "blue-australia", SimulationDomain.Maritime),
            Entity(campaignId, "raaf-f111", "RAAF F-111C maritime strike wing", "raaf_f-111c", blue, "australia", -27_000_000, 134_000_000, "fremantle", "blue-australia-air", SimulationDomain.Air),
            Entity(campaignId, "vietnam-agi", "Vietnamese coastal intelligence patrol", "nv_agi_sampan", red, "southchina", 13_000_000, 112_000_000, "cam-ranh", "red-vietnam", SimulationDomain.Maritime),
            Entity(campaignId, "rocaf-f5e", "RoCAF 1st Tactical Fighter Wing", "usaf_f-5e", blue, "taiwan", 24_350_000, 119_550_000, "kaohsiung", "blue-taiwan-air", SimulationDomain.Air, catalogLoadoutId: "CAS"),
            Entity(campaignId, "roc-yung-nien", "ROCN Yung Nien MSC 157", "usn_msc_adjutant", blue, "taiwan", 24_250_000, 119_550_000, "kaohsiung", "blue-taiwan-coastal", SimulationDomain.Maritime),
            Entity(campaignId, "plan-taiwan-jiangdong", "PLAN Taiwan Strait Jiangdong escort", "plan_ffg_jiangdong", red, "taiwan", 24_400_000, 119_450_000, "sanya", "red-taiwan-invasion", SimulationDomain.Maritime),
            Entity(campaignId, "plan-taiwan-yukan", "PLAN Taiwan Strait Yukan landing group", "plan_lst_yukan", red, "taiwan", 24_300_000, 119_350_000, "sanya", "red-taiwan-invasion", SimulationDomain.Logistics),
            Entity(campaignId, "rokaf-f5e", "ROKAF 102nd Tactical Fighter Squadron", "usaf_f-5e", blue, "korea", 38_000_000, 127_000_000, "chinhae", "blue-south-korea-air", SimulationDomain.Air, catalogLoadoutId: "AirToAir"),
            Entity(campaignId, "kpaf-j7c", "KPAF MiG-21/J-7 interceptor regiment", "pla_j-7c", red, "korea", 38_050_000, 127_100_000, "wonsan", "red-north-korea-air", SimulationDomain.Air),
        }.Concat(Ww3PlusEntities(campaignId, blue, red))
            .Concat(Ww3AirbaseComplexEntities(campaignId, blue, red))
            .Concat(Ww3FormationPhysicalEntities(campaignId, blue, red))
            .ToArray();
        var entities = entityTemplates
            .OrderBy(item => item.EntityId)
            .ToArray();
        var entityIds = entities.ToDictionary(item => item.Key, item => item.EntityId, StringComparer.Ordinal);
        var groups = entities.GroupBy(item => item.GroupKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var members = group.OrderBy(item => item.EntityId).ToArray();
                return new FuldaGroupSeed(
                    group.Key,
                    new GroupId(Id(campaignId, $"group-{group.Key}")),
                    members.Length == 1 ? members[0].Name : GroupName(group.Key),
                    members[0].CoalitionId,
                    members.Select(item => item.EntityId).ToArray());
            })
            .OrderBy(item => item.GroupId)
            .ToArray();
        var groupIds = groups.ToDictionary(item => item.Key, item => item.GroupId, StringComparer.Ordinal);
        var orders = new[]
        {
            Order(campaignId, "reforger-to-bight", groupIds["blue-reforger"], regionIds["bight"], OrderKind.Escort),
            Order(campaignId, "northern-sag-to-norsea", groupIds["red-northern-sag"], regionIds["norsea"], OrderKind.Transit),
            Order(campaignId, "baltic-raid-to-baltap", groupIds["red-baltic"], regionIds["baltap"], OrderKind.Transit),
            Order(campaignId, "blue-sag-to-norsea", groupIds["blue-north-sea"], regionIds["norsea"], OrderKind.Patrol),
            Order(campaignId, "argentine-support-to-cape", groupIds["red-argentina-support"], regionIds["cape"], OrderKind.Transit),
            Order(campaignId, "southatl-sag-to-patagonia", groupIds["red-southatl"], regionIds["patagonia"], OrderKind.Patrol),
            Order(campaignId, "blue-southatl-to-patagonia", groupIds["blue-southatl"], regionIds["patagonia"], OrderKind.Escort),
            Order(campaignId, "blue-us-indian-ocean-to-gulf", groupIds["blue-us-indian-ocean"], regionIds["gulf"], OrderKind.Patrol),
            Order(campaignId, "blue-israel-levant-patrol", groupIds["blue-israel-levant"], regionIds["levant"], OrderKind.Patrol),
            Order(campaignId, "iran-to-arabian", groupIds["red-iran"], regionIds["arabian"], OrderKind.Transit),
            Order(campaignId, "syrian-levant-pressure", groupIds["red-syria-levant"], regionIds["med"], OrderKind.Transit),
            Order(campaignId, "soviet-levant-reinforcement", groupIds["red-soviet-levant"], regionIds["med"], OrderKind.Transit),
            Order(campaignId, "pacific-cvg-to-northpac", groupIds["blue-pacific-cvg"], regionIds["northpac"], OrderKind.Patrol),
            Order(campaignId, "japan-barrier-to-okhotsk", groupIds["blue-japan"], regionIds["okhotsk"], OrderKind.Patrol),
            Order(campaignId, "plan-to-japan", groupIds["red-plan"], regionIds["japan"], OrderKind.Transit),
            Order(campaignId, "soviet-pacific-to-northpac", groupIds["red-pacific"], regionIds["northpac"], OrderKind.Patrol),
            Order(campaignId, "canada-atlantic-to-giuk", groupIds["blue-canada-atlantic"], regionIds["giuk"], OrderKind.Patrol),
            Order(campaignId, "uk-atlantic-to-giuk", groupIds["blue-uk-atlantic"], regionIds["giuk"], OrderKind.Patrol),
            Order(campaignId, "portugal-atlantic-to-giuk", groupIds["blue-portugal-atlantic"], regionIds["giuk"], OrderKind.Patrol),
            Order(campaignId, "continental-allies-to-baltap", groupIds["blue-continental-allies"], regionIds["baltap"], OrderKind.Patrol),
            Order(campaignId, "spain-med-to-levant", groupIds["blue-spain-med"], regionIds["levant"], OrderKind.Patrol),
            Order(campaignId, "france-med-to-levant", groupIds["blue-france-med"], regionIds["levant"], OrderKind.Patrol),
            Order(campaignId, "libyan-group-to-levant", groupIds["red-mediterranean"], regionIds["levant"], OrderKind.Transit),
            Order(campaignId, "india-to-arabian", groupIds["blue-india"], regionIds["arabian"], OrderKind.Patrol),
            Order(campaignId, "pakistan-to-arabian", groupIds["red-pakistan"], regionIds["arabian"], OrderKind.Patrol),
            Order(campaignId, "australia-to-southchina", groupIds["blue-australia"], regionIds["southchina"], OrderKind.Transit),
            Order(campaignId, "australia-air-cover", groupIds["blue-australia-air"], regionIds["southpac"], OrderKind.Patrol),
            Order(campaignId, "vietnam-intelligence", groupIds["red-vietnam"], regionIds["southchina"], OrderKind.Patrol),
            Order(campaignId, "taiwan-air-defense", groupIds["blue-taiwan-air"], regionIds["taiwan"], OrderKind.Patrol),
            Order(campaignId, "taiwan-coastal-defense", groupIds["blue-taiwan-coastal"], regionIds["taiwan"], OrderKind.Patrol),
            Order(campaignId, "taiwan-invasion", groupIds["red-taiwan-invasion"], regionIds["taiwan"], OrderKind.Escort),
            Order(campaignId, "south-korea-air-defense", groupIds["blue-south-korea-air"], regionIds["korea"], OrderKind.Patrol),
            Order(campaignId, "north-korea-air-offensive", groupIds["red-north-korea-air"], regionIds["korea"], OrderKind.Patrol),
        }.Concat(Ww3PlusOrders(campaignId, groupIds, regionIds))
            .Concat(Ww3AirbaseComplexOrders(campaignId, blue, red, groupIds, regionIds))
            .ToArray();
        return new FuldaGapSeed(
            campaignId,
            blue,
            red,
            regions,
            bases,
            entities,
            groups,
            orders,
            regionIds,
            baseIds,
            entityIds);
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
        SimulationDomain simulationDomain, FidelityState maximumFidelity = FidelityState.Tactical,
        string catalogLoadoutId = "Default",
        IReadOnlyList<OperationalWeaponCapability>? operationalWeapons = null,
        string? hostEntityKey = null)
    {
        var effectiveDomain = EffectiveSimulationDomain(catalogUnitId, simulationDomain);
        return new(key, new EntityId(Id(campaignId, $"entity-{key}")), name, catalogUnitId, catalogLoadoutId,
            coalitionId, regionKey,
            GeoCoordinate.FromMicrodegrees(latitude, longitude), homeBaseKey, groupKey,
            effectiveDomain, maximumFidelity,
            operationalWeapons ?? ResolveOperationalWeapons(catalogUnitId, catalogLoadoutId, effectiveDomain),
            hostEntityKey);
    }

    private static SimulationDomain EffectiveSimulationDomain(
        string catalogUnitId,
        SimulationDomain requestedDomain)
    {
        if (requestedDomain == SimulationDomain.LandAbstract
            && !string.Equals(catalogUnitId, "abstract_land_formation", StringComparison.Ordinal))
        {
            return SimulationDomain.Installation;
        }
        if (requestedDomain == SimulationDomain.Air && IsRotaryWingCatalogUnit(catalogUnitId))
        {
            return SimulationDomain.RotaryWing;
        }
        return requestedDomain;
    }

    private static bool IsRotaryWingCatalogUnit(string catalogUnitId) =>
        catalogUnitId.StartsWith("wp_ka-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("wp_mi-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("usn_sh-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("usn_ch-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("usmc_ah-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("usaf_hh-", StringComparison.Ordinal)
        || catalogUnitId.Contains("_sh-3", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("fr_alouette_", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("fr_as-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("fr_h700", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("fr_sa-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("iaf_md", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("iqaf_md", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("iriaf_ah-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("jmsdf_hss-", StringComparison.Ordinal)
        || catalogUnitId.StartsWith("pla_z-", StringComparison.Ordinal);

    private static IReadOnlyList<OperationalWeaponCapability> ResolveOperationalWeapons(
        string catalogUnitId,
        string catalogLoadoutId,
        SimulationDomain simulationDomain)
    {
        if (simulationDomain == SimulationDomain.LandAbstract
            && string.Equals(catalogUnitId, "abstract_land_formation", StringComparison.Ordinal))
        {
            return
            [
                new OperationalWeaponCapability(
                    "abstract-corps-fires",
                    ContactClassification.LandFormation,
                    DistanceMeters.FromMeters(80_000)),
            ];
        }
        return OperationalCatalog.Value?.Resolve(catalogUnitId, catalogLoadoutId, scenarioYear: 1989)
            .Select(item => new OperationalWeaponCapability(
                item.AmmunitionId,
                Enum.Parse<ContactClassification>(item.TargetType),
                DistanceMeters.FromMeters(item.MaximumRangeMeters),
                DistanceMeters.FromMeters(item.MinimumRangeMeters),
                OrdnanceKindFor(item.OrdnanceType),
                OrdnanceGuidanceFor(item.GuidanceType),
                item.SpeedMetersPerSecond,
                DistanceMeters.FromMeters(item.SeekerRangeMeters),
                RequiresExternalGuidance(item.GuidanceType, item.MidCourseCorrection),
                item.LaunchReliabilityMilliUnits,
                item.KillProbabilityMilliUnits,
                item.WarheadPower,
                MechanicsSourceFor(item.OrdnanceType, item.SpeedMetersPerSecond),
                checked((int)Math.Clamp(item.MagazineRounds, 0, int.MaxValue)),
                item.Mounts.Select(mount => new OperationalWeaponMount(
                    mount.MountId,
                    checked((int)Math.Clamp(mount.ReloadSeconds, 0, int.MaxValue)),
                    mount.SalvoRounds,
                    mount.GuidanceChannels)).ToArray()))
            .ToArray() ?? [];
    }

    private static WeaponMechanicsSource MechanicsSourceFor(string type, long speedMetersPerSecond) =>
        string.Equals(type.Trim(), "Projectile", StringComparison.OrdinalIgnoreCase)
            || speedMetersPerSecond > 0
            ? WeaponMechanicsSource.SeaPowerCatalog
            : WeaponMechanicsSource.SeaPowerCatalogIncomplete;

    private static OrdnanceKind OrdnanceKindFor(string type) => type.Trim().ToUpperInvariant() switch
    {
        "TORPEDO" => OrdnanceKind.Torpedo,
        "BOMB" => OrdnanceKind.Bomb,
        "DECOY" or "CHAFF" or "NOISEMAKER" => OrdnanceKind.Decoy,
        "PROJECTILE" => OrdnanceKind.Projectile,
        _ => OrdnanceKind.Missile,
    };

    private static OrdnanceGuidance OrdnanceGuidanceFor(int guidanceType) => guidanceType switch
    {
        1 or 4 or 6 or 8 or 9 => OrdnanceGuidance.Passive,
        2 or 5 => OrdnanceGuidance.SemiActive,
        3 or 7 or 10 => OrdnanceGuidance.Active,
        _ => OrdnanceGuidance.Inertial,
    };

    private static bool RequiresExternalGuidance(int guidanceType, int midCourseCorrection) =>
        guidanceType is 2 or 5 || midCourseCorrection == 1;

    private static FuldaOrderSeed Order(
        CampaignId campaignId, string key, GroupId groupId, RegionId destination, OrderKind kind) =>
        new(new OrderId(Id(campaignId, $"order-{key}")), groupId, kind, destination);

    private static string GroupName(string key)
    {
        return key switch
        {
        "blue-giuk" => "NATO GIUK barrier",
        "blue-giuk-sub" => "GIUK submarine patrol",
        "blue-north-sea" => "NATO North Sea group",
        "blue-reforger" => "REFORGER Convoy 261",
        "blue-baltap" => "BALTAP fast attack group",
        "blue-fulda" => "US V Corps abstract formation",
        "red-northern-sag" => "Northern Fleet surface group",
        "red-north-sea-picket" => "Northern Fleet North Sea picket",
        "red-giuk" => "Northern Fleet submarine barrier group",
        "red-baltic" => "Baltic Fleet missile raid group",
        "red-fulda" => "8th Guards Army abstract formation",
        "red-argentina-support" => "Argentine resupply group",
        "red-southatl" => "Soviet South Atlantic action group",
        "blue-southatl" => "NATO South Atlantic escort",
        "blue-us-indian-ocean" => "US Indian Ocean group",
        "blue-israel-levant" => "Israeli Levant group",
        "red-iran" => "Iranian Gulf exclusion group",
        "red-syria-levant" => "Syrian missile group",
        "red-soviet-levant" => "Soviet Levant reinforcement",
        "blue-pacific-cvg" => "US Pacific carrier group",
        "blue-japan" => "JMSDF northern barrier group",
        "red-plan" => "PLAN South China Sea group",
        "red-pacific" => "Soviet Pacific Fleet action group",
        "blue-canada-atlantic" => "RCN Atlantic patrol",
        "blue-uk-atlantic" => "Royal Navy Atlantic escort",
        "blue-portugal-atlantic" => "Portuguese Atlantic patrol",
        "blue-continental-allies" => "West German Baltic reinforcement group",
        "blue-spain-med" => "Spanish Mediterranean group",
        "blue-france-med" => "French Mediterranean group",
        "red-mediterranean" => "Libyan Mediterranean missile group",
        "blue-india" => "Indian Navy western fleet group",
        "red-pakistan" => "Pakistan Navy submarine group",
        "blue-australia" => "Royal Australian Navy task group",
        "blue-australia-air" => "RAAF maritime strike command",
        "red-vietnam" => "Vietnamese coastal intelligence command",
        "blue-taiwan-air" => "ROCAF defense squadron",
        "blue-taiwan-coastal" => "Republic of China Navy coastal defense",
        "red-taiwan-invasion" => "PLAN Taiwan Strait invasion group",
        "blue-south-korea-air" => "ROKAF defense squadron",
        "red-north-korea-air" => "KPAF interceptor regiment",
        _ => Ww3PlusGroupName(key),
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
    string CatalogLoadoutId,
    CoalitionId CoalitionId,
    string RegionKey,
    GeoCoordinate Coordinate,
    string? HomeBaseKey,
    string GroupKey,
    SimulationDomain SimulationDomain,
    FidelityState MaximumFidelity,
    IReadOnlyList<OperationalWeaponCapability> OperationalWeapons,
    string? HostEntityKey);
internal sealed record FuldaGroupSeed(string Key, GroupId GroupId, string Name, CoalitionId CoalitionId, IReadOnlyList<EntityId> Members);
internal sealed record FuldaOrderSeed(OrderId OrderId, GroupId GroupId, OrderKind Kind, RegionId DestinationRegionId);
