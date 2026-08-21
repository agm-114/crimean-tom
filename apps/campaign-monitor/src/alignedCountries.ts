// Natural Earth 4.1.0 / world-atlas@2 countries-110m is modern admin-0 geometry
// (post-1991), not a 1989 political layer. Campaign-aligned borders therefore
// map former USSR successor states onto one hostile outline and treat unified
// Germany as West Germany / FRG. East Germany is not distinguishable in this
// dataset, so a GDR OOB hit (for example Peenemunde) does not draw a separate
// border and does not recolor Germany hostile.

export type Alignment = "friendly" | "hostile";

export type AlignedCountry = {
  key: string;
  alignment: Alignment;
  isoNumericIds: string[];
  mergeInterior?: boolean;
  pattern: RegExp;
};

const USSR_SUCCESSOR_IDS = [
  "643", // Russia — stand-in core of the USSR outline
  "804", // Ukraine
  "112", // Belarus
  "498", // Moldova
  "233", // Estonia
  "428", // Latvia
  "440", // Lithuania
  "268", // Georgia
  "051", // Armenia
  "031", // Azerbaijan
  "398", // Kazakhstan
  "860", // Uzbekistan
  "795", // Turkmenistan
  "417", // Kyrgyzstan
  "762", // Tajikistan
];

export const alignedCountryCatalog: AlignedCountry[] = [
  { key: "united-states", alignment: "friendly", isoNumericIds: ["840"], pattern: /\busn_|\buss |\busns |\bus v corps|\bus south atlantic/i },
  { key: "united-kingdom", alignment: "friendly", isoNumericIds: ["826"], pattern: /\brn_|\broyal navy\b/i },
  { key: "canada", alignment: "friendly", isoNumericIds: ["124"], pattern: /\brcn_|\bhmcs /i },
  { key: "west-germany", alignment: "friendly", isoNumericIds: ["276"], pattern: /\bfgs_|\bfgs /i },
  { key: "france", alignment: "friendly", isoNumericIds: ["250"], pattern: /\bfr_|\bfrench /i },
  { key: "spain", alignment: "friendly", isoNumericIds: ["724"], pattern: /\bes_|\bspanish /i },
  { key: "portugal", alignment: "friendly", isoNumericIds: ["620"], pattern: /\bnrp_|\bportuguese /i },
  { key: "norway", alignment: "friendly", isoNumericIds: ["578"], pattern: /\bknm_|\brnon /i },
  { key: "israel", alignment: "friendly", isoNumericIds: ["376"], pattern: /\bins_|\bisraeli /i },
  { key: "india", alignment: "friendly", isoNumericIds: ["356"], pattern: /\bindian navy\b/i },
  { key: "japan", alignment: "friendly", isoNumericIds: ["392"], pattern: /\bjmsdf_|\bjds /i },
  { key: "australia", alignment: "friendly", isoNumericIds: ["036"], pattern: /\bran_|\braaf_|\bhmas /i },
  { key: "south-korea", alignment: "friendly", isoNumericIds: ["410"], pattern: /\brokaf\b|\brepublic of korea\b|\bsouth korea\b|\bchinhae\b/i },
  { key: "taiwan", alignment: "friendly", isoNumericIds: ["158"], pattern: /\brocaf\b|\brocn\b|\brepublic of china\b|\btaiwan\b|\bkaohsiung\b/i },
  {
    key: "ussr",
    alignment: "hostile",
    isoNumericIds: USSR_SUCCESSOR_IDS,
    mergeInterior: true,
    pattern: /\bsoviet\b|\bnorthern fleet\b|\badmiral zakharov\b|\bvictor iii\b|\boscar i\b|\brezkiy\b|\b8th guards\b|\bkara group\b|\bkynda\b|\bropucha\b|\bsovremenny\b|\bcharlie ii\b|\bboris chilikin\b|\bwp_rkr_|\bwp_bpk_|\bwp_ssn_|\bwp_ssgn_|\bwp_em_|\bwp_vt_|\bwp_bdk_|\bwp_skr_krivak|\bosa missile boat squadron/i,
  },
  { key: "argentina", alignment: "hostile", isoNumericIds: ["032"], pattern: /\bargentine/i },
  { key: "iran", alignment: "hostile", isoNumericIds: ["364"], pattern: /\bir_|\biranian /i },
  { key: "libya", alignment: "hostile", isoNumericIds: ["434"], pattern: /\bln_|\blibyan /i },
  { key: "pakistan", alignment: "hostile", isoNumericIds: ["586"], pattern: /\bpns_|\bpns /i },
  { key: "china", alignment: "hostile", isoNumericIds: ["156"], pattern: /\bplan_|\bplan /i },
  { key: "syria", alignment: "hostile", isoNumericIds: ["760"], pattern: /\bsyrian |\btartus\b/i },
  { key: "vietnam", alignment: "hostile", isoNumericIds: ["704"], pattern: /\bnv_|\bvietnamese /i },
  { key: "north-korea", alignment: "hostile", isoNumericIds: ["408"], pattern: /\bkpaf\b|\bkorean people's\b|\bnorth korea\b|\bwonsan\b/i },
  { key: "east-germany", alignment: "hostile", isoNumericIds: [], pattern: /\bpeenemunde\b/i },
];

export function countriesRepresentedInOrderOfBattle(campaign: {
  entities: { catalogUnitId: string; name: string }[];
  bases: { name: string }[];
}): AlignedCountry[] {
  const haystack = [
    ...campaign.entities.map(item => `${item.catalogUnitId} ${item.name}`),
    ...campaign.bases.map(item => item.name),
  ].join("\n");
  return alignedCountryCatalog.filter(country => country.pattern.test(haystack));
}
