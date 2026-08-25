using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.ServiceHost;

internal static partial class FuldaGapCampaignSeed
{
    private static IReadOnlyList<Ww3PlusPackage> NavalExpansionPackages(
        CoalitionId blue,
        CoalitionId red) =>
    [
        // US total mobilization. Legacy hulls are reactivated and the four Iowa-class ships return to fleet duty.
        FormationPackage("blue-usec-battleship", "US Atlantic Iowa group",
        [
            ["usn_bb_iowa", "usn_cg_belknap", "usn_dd_spruance", "usn_ff_knox_84"],
            ["usn_bb_iowa", "usn_cg_ticonderoga", "usn_ddg_adams_late", "usn_ffg_oliver_hazard_perry"],
        ], blue, "usec", "norfolk", SimulationDomain.Maritime, 35_900_000, -72_700_000, "midatl", OrderKind.Patrol),
        FormationPackage("blue-uswc-battleship", "US Pacific Iowa group",
        [
            ["usn_bb_iowa", "usn_cgn_california", "usn_dd_spruance_abl", "usn_ffg_oliver_hazard_perry_longhull"],
            ["usn_bb_iowa", "usn_cg_ticonderoga", "usn_ddg_kidd", "usn_ff_knox_84"],
        ], blue, "uswc", "long-beach", SimulationDomain.Maritime, 33_800_000, -122_800_000, "northpac", OrderKind.Patrol),
        Package("blue-hypothetical-strike-cruiser", "US Alaska-class CGN hypothetical mobilization", ["usn_cgn_alaska"], blue, "hawaii", "pearl-harbor", SimulationDomain.Maritime, 4, 2, 23_000_000, -158_500_000, "northpac", OrderKind.Patrol),
        Package("blue-mobilized-cruiser", "US mobilized legacy missile cruiser", ["usn_cg_belknap_72", "usn_cg_leahy_68", "usn_cg_leahy", "usn_cgn_long_beach_73", "usn_clg_oklahoma_city"], blue, "usec", "norfolk", SimulationDomain.Maritime, 25, 5, 36_500_000, -70_500_000, "midatl", OrderKind.Escort),
        Package("blue-mobilized-destroyer", "US mobilized legacy destroyer", ["usn_dd_gearing_fram2_mod", "usn_dd_gearing_fram2", "usn_dd_gearing", "usn_ddg_adams_early_68", "usn_ddg_adams_early", "usn_ddg_adams_late_68", "usn_ddg_coontz_68", "usn_ddg_coontz", "usn_ddg_mahan"], blue, "uswc", "san-diego", SimulationDomain.Maritime, 45, 5, 31_000_000, -126_500_000, "hawaii", OrderKind.Escort),
        Package("blue-mobilized-frigate", "US mobilized legacy frigate", ["usn_ff_knox_72", "usn_ff_knox"], blue, "usec", "mayport", SimulationDomain.Maritime, 20, 4, 34_000_000, -69_500_000, "wapp", OrderKind.Escort),
        Package("blue-mobilized-submarine", "US mobilized attack-submarine reserve", ["usn_ssn_los_angeles_76", "usn_ssn_permit_68", "usn_ssn_skipjack_68", "usn_ssn_skipjack", "usn_ssn_sturgeon_72"], blue, "northpac", "pearl-harbor", SimulationDomain.Maritime, 40, 4, 37_000_000, -168_000_000, "okhotsk", OrderKind.Asw),
        Package("blue-mobilized-amphibious", "US mobilized amphibious warfare ship", ["usn_lka_charleston", "usn_lsm_lsm1", "usn_lst_newport"], blue, "usec", "norfolk", SimulationDomain.Maritime, 24, 4, 32_000_000, -71_000_000, "caribbean", OrderKind.Reinforce),
        Package("blue-mobilized-littoral", "US mobilized mine or littoral warfare craft", ["usn_mso_aggressive", "usn_pb_pgm-39", "usn_phm_pegasus"], blue, "florida", "mayport", SimulationDomain.Maritime, 24, 4, 27_000_000, -78_000_000, "cuba", OrderKind.Patrol),
        Package("blue-fleet-train-expansion", "US mobilized fleet-train auxiliary", ["usn_ae_kilauea", "usn_ao_t2", "usn_avp_barnegat_mod", "usn_avp_barnegat", "usn_takr_algol"], blue, "midatl", "hampton", SimulationDomain.Logistics, 30, 5, 40_000_000, -45_000_000, "wapp", OrderKind.Resupply),
        Package("blue-septar-decoy", "US SEPTAR remote decoy and target craft", ["usn_septar_qst-35", "usn_septar_qst-35_small"], blue, "hawaii", "pearl-harbor", SimulationDomain.Maritime, 12, 3, 21_800_000, -159_000_000, "northpac", OrderKind.Patrol),

        // Soviet WW3++ surge. Orel is explicitly hypothetical; older hulls are a general-war reserve mobilization.
        FormationPackage("red-northern-aviation-cruiser", "Northern Fleet carrier group",
        [
            ["wp_takr_kiev", "wp_rkr_slava", "wp_bpk_udaloy", "wp_em_sovremenny"],
            ["wp_cv_orel", "wp_rkr_kirov", "wp_bpk_kara", "wp_skr_krivak2"],
            ["wp_takr_kiev", "wp_rkr_kresta1", "wp_bpk_kresta2", "wp_vt_boris_chilikin"],
            ["wp_cv_orel", "wp_rkr_slava", "wp_bpk_udaloy", "wp_em_sovremenny"],
        ], red, "barents", "severomorsk", SimulationDomain.Maritime, 72_000_000, 28_000_000, "norsea", OrderKind.Patrol),
        FormationPackage("red-pacific-aviation-cruiser", "Pacific Fleet carrier group",
        [
            ["wp_takr_kiev", "wp_rkr_slava", "wp_bpk_udaloy", "wp_em_sovremenny"],
            ["wp_cv_orel", "wp_rkr_kirov", "wp_bpk_kara", "wp_skr_krivak2"],
            ["wp_takr_kiev", "wp_rkr_kresta1", "wp_bpk_kresta2", "wp_vt_boris_chilikin"],
            ["wp_cv_orel", "wp_rkr_slava", "wp_bpk_udaloy", "wp_em_sovremenny"],
        ], red, "okhotsk", "vladivostok", SimulationDomain.Maritime, 48_000_000, 153_000_000, "northpac", OrderKind.Patrol),
        FormationPackage("red-blacksea-aviation-cruiser", "Black Sea Fleet carrier group",
        [
            ["wp_pkr_moskva", "wp_bpk_kara", "wp_skr_krivak2", "wp_vt_boris_chilikin"],
            ["wp_takr_kiev", "wp_rkr_kynda", "wp_em_sovremenny", "wp_bdk_ropucha"],
            ["wp_cv_orel", "wp_rkr_slava", "wp_bpk_udaloy", "wp_vt_boris_chilikin"],
        ], red, "blacksea", "sevastopol", SimulationDomain.Maritime, 43_000_000, 34_000_000, "bosporus", OrderKind.Patrol),
        Package("red-northern-legacy-major", "Soviet Northern Fleet mobilized major combatant", ["wp_bpk_kanin", "wp_bpk_kashin_mod", "wp_bpk_kashin", "wp_bpk_kresta2", "wp_rkr_kresta1", "wp_kr_sverdlov"], red, "barents", "severomorsk", SimulationDomain.Maritime, 48, 6, 70_800_000, 26_000_000, "giuk", OrderKind.Patrol),
        Package("red-pacific-legacy-major", "Soviet Pacific Fleet mobilized major combatant", ["wp_bpk_kanin", "wp_bpk_kashin_mod", "wp_bpk_kashin", "wp_bpk_kresta2", "wp_rkr_kresta1", "wp_kr_sverdlov"], red, "japan", "vladivostok", SimulationDomain.Maritime, 48, 6, 45_000_000, 138_000_000, "northpac", OrderKind.Patrol),
        Package("red-blacksea-legacy-major", "Soviet Black Sea Fleet mobilized major combatant", ["wp_bpk_kanin", "wp_bpk_kashin_mod", "wp_bpk_kashin", "wp_bpk_kresta2", "wp_rkr_kresta1", "wp_kr_sverdlov"], red, "blacksea", "sevastopol", SimulationDomain.Maritime, 36, 6, 43_500_000, 32_000_000, "bosporus", OrderKind.Patrol),
        Package("red-baltic-littoral-surge", "Warsaw Pact Baltic missile and ASW combatant", ["wp_mpk_grisha3", "wp_mrk_nanuchka", "wp_skr_krivak1", "wp_skr_petya2", "wp_ptg_komar", "wp_ptg_tarantul_re", "wp_ptg_tarantul"], red, "baltic", "baltiysk", SimulationDomain.Maritime, 64, 4, 56_000_000, 20_000_000, "baltap", OrderKind.Patrol),
        Package("red-pacific-littoral-surge", "Soviet Pacific missile and ASW combatant", ["wp_mpk_grisha3", "wp_mrk_nanuchka", "wp_skr_krivak1", "wp_skr_petya2", "wp_ptg_komar", "wp_ptg_tarantul_re", "wp_ptg_tarantul"], red, "japan", "vladivostok", SimulationDomain.Maritime, 64, 4, 44_000_000, 140_000_000, "korea", OrderKind.Patrol),
        Package("red-blacksea-littoral-surge", "Soviet Black Sea missile and ASW combatant", ["wp_mpk_grisha3", "wp_mrk_nanuchka", "wp_skr_krivak1", "wp_skr_petya2", "wp_ptg_komar", "wp_ptg_tarantul_re", "wp_ptg_tarantul"], red, "blacksea", "novorossiysk", SimulationDomain.Maritime, 48, 4, 45_000_000, 36_000_000, "bosporus", OrderKind.Patrol),
        Package("red-northern-submarine-reserve", "Soviet Northern Fleet mobilized submarine", ["wp_ss_romeo", "wp_ss_tango", "wp_ssbn_yankee", "wp_ssgn_charlie1", "wp_ssgn_echo2", "wp_ssn_alfa", "wp_ssn_november", "wp_ssn_victor1"], red, "barents", "polyarny", SimulationDomain.Maritime, 64, 4, 73_000_000, 32_000_000, "giuk", OrderKind.Asw),
        Package("red-pacific-submarine-reserve", "Soviet Pacific Fleet mobilized submarine", ["wp_ss_romeo", "wp_ss_tango", "wp_ssbn_yankee", "wp_ssgn_charlie1", "wp_ssgn_echo2", "wp_ssn_alfa", "wp_ssn_november", "wp_ssn_victor1"], red, "okhotsk", "vladivostok", SimulationDomain.Maritime, 64, 4, 50_000_000, 157_000_000, "northpac", OrderKind.Asw),
        Package("red-blacksea-submarine", "Soviet Black Sea submarine surge", ["wp_ss_romeo", "wp_ss_tango", "wp_ss_foxtrot", "wp_ss_kilo", "wp_ssgn_charlie1"], red, "blacksea", "sevastopol", SimulationDomain.Maritime, 30, 3, 42_000_000, 35_000_000, "bosporus", OrderKind.Asw),
        Package("red-baltic-patrol-mine", "Warsaw Pact Baltic patrol and mine-warfare craft", ["wp_msc_kondor_II_85", "wp_msc_kondor_II", "wp_msc_kondor", "wp_msc_T43", "wp_msc_T43K", "wp_pb_don", "wp_pb_zhuk_ME", "wp_pb_zhuk", "wp_pt_libelle", "wp_pt_p4", "wp_pt_p6", "wp_pt_shershen-e", "wp_pt_shershen-er", "wp_pt_shershen", "wp_pt_stenka", "wp_pt_turya", "wp_pt_wisla"], red, "baltic", "baltiysk", SimulationDomain.Maritime, 68, 4, 55_000_000, 21_000_000, "baltap", OrderKind.Patrol),
        Package("red-blacksea-patrol-mine", "Soviet Black Sea patrol and mine-warfare craft", ["wp_msc_kondor_II_85", "wp_msc_kondor_II", "wp_msc_kondor", "wp_msc_T43", "wp_msc_T43K", "wp_pb_don", "wp_pb_zhuk_ME", "wp_pb_zhuk", "wp_pt_libelle", "wp_pt_p4", "wp_pt_p6", "wp_pt_shershen-e", "wp_pt_shershen-er", "wp_pt_shershen", "wp_pt_stenka", "wp_pt_turya", "wp_pt_wisla"], red, "blacksea", "novorossiysk", SimulationDomain.Maritime, 51, 3, 44_000_000, 38_000_000, "bosporus", OrderKind.Patrol),
        Package("red-intelligence-merchant-surge", "Soviet intelligence and armed merchant auxiliary", ["wp_agi_okean_mod", "wp_agi_okean", "wp_ms_andizhan_armed", "wp_ms_mercur_decoy", "wp_ms_roro_b", "wp_vt_kazbek"], red, "southatl", "havana", SimulationDomain.Logistics, 30, 5, 15_000_000, -30_000_000, "cape", OrderKind.Patrol),
        Package("red-blacksea-amphibious-train", "Soviet Black Sea amphibious and fleet-train unit", ["wp_bdk_alligator", "wp_bdk_ivan_rogov", "wp_bdk_ropucha", "wp_vt_boris_chilikin", "wp_vt_kazbek"], red, "blacksea", "sevastopol", SimulationDomain.Logistics, 30, 5, 44_000_000, 34_500_000, "bosporus", OrderKind.Reinforce),

        // Allied and regional fleets use every remaining military vessel family in the installed catalog.
        Package("blue-atlantic-catalog-mobilization", "NATO Atlantic legacy and coastal combatant", ["rn_dd_c-class", "rn_dd_z-class", "fgs_ptg_tiger", "fgs_ss_type_205", "fgs_ss_type_206", "fr_ss_daphne", "es_ss_galerna", "es_ssk_delfin", "knm_ffg_oslo", "knm_fs_sleipner", "knm_msc_sauda", "knm_ptg_snogg", "knm_ss_kobben"], blue, "wapp", "plymouth", SimulationDomain.Maritime, 52, 4, 52_000_000, -14_000_000, "giuk", OrderKind.Escort),
        Package("blue-israeli-missile-surge", "Israeli missile and patrol combatant", ["ins_ff_blackwood", "ins_pt_saar", "ins_ptg_aliya", "ins_ptg_hetz", "ins_ptg_saar_2", "ins_ptg_saar_3"], blue, "levant", "haifa", SimulationDomain.Maritime, 24, 4, 32_000_000, 33_000_000, "med", OrderKind.Patrol),
        Package("blue-japanese-mobilization", "JMSDF mobilized escort or submarine", ["jmsdf_dd_minegumo", "jmsdf_dd_takatsuki", "jmsdf_dd_yamagumo", "jmsdf_ss_asashio", "jmsdf_ss_uzushio"], blue, "japan", "yokosuka", SimulationDomain.Maritime, 30, 5, 38_000_000, 145_000_000, "okhotsk", OrderKind.Asw),
        Package("red-plan-mobilization", "PLAN mobilized coastal or ocean combatant", ["plan_ap_qiongsha", "plan_ff_chengdu", "plan_ff_jianghu1", "plan_ff_jianghu2", "plan_k_type_037", "plan_msc_type_010", "plan_pt_shanghai2", "plan_pt_shanghai2b", "plan_ptg_type_024", "plan_ss_romeo", "plan_ssg_wuhan"], red, "southchina", "sanya", SimulationDomain.Maritime, 66, 6, 17_000_000, 115_000_000, "taiwan", OrderKind.Patrol),
        Package("red-iranian-mobilization", "Iranian mobilized fleet and irregular combatant", ["ir_aor_delvar", "ir_fab_boghammar", "ir_pf_bayandor", "ir_pt_kaivan", "ir_pt_parvin", "ir_ptg_combattante_II", "ir_ptg_kaivan"], red, "gulf", "bandar-abbas", SimulationDomain.Maritime, 28, 4, 25_000_000, 57_000_000, "arabian", OrderKind.Patrol),
    ];
}
