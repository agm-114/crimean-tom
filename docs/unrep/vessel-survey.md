# UNREP vessel survey

The Sea Power 0.9.1 build 261112 catalog has 294 base definitions in
`StreamingAssets/original/vessels`. The survey read the native unit type,
displacement, forward-speed field and role tags for each definition. It followed
`#!copy` inheritance and counted hull variants with their base definition.
Tonnages below use the game's displacement field.

| Group | Definitions | Displacement (tonnes) | UNREP tuning |
| --- | ---: | ---: | --- |
| Replenishment ships tagged RAS | 7 | 904–53,000 | 2× initial and maximum food, water and spares unless a sourced value is available. |
| Carrier, landing and transport ships | 22 | 540–100,000 | 1.5× initial and maximum food, water and spares, food and spare-parts use, and water production. |
| Other powered surface units | 204 | 1–253,000 | Displacement defaults with sourced bunker, aviation fuel, and water exceptions. |
| Surface definitions with zero listed forward speed | 5 | 0.1–0.3 | No unit overrides. Four rafts or pods and one sea mine. |
| Submarines | 56 | 455–22,500 | Outside UNREP's surface inventory. |

RAS takes priority for the dual-role Delvar. The carrier group includes CVS but
excludes the Ro-Ro merchant that carries a CVL role tag by itself. Leaf and
Narwal inherit their displacement from the medium tanker and stern trawler
respectively. Each tuned definition has one file in
[unit_extensions](../../src/UNREP/unit_extensions) named after the stock INI with an
`_unrep.ini` suffix.

The multipliers are gameplay allowances based on ship role. Standard fuel burn
still uses the displacement formula. The bunker overrides below use a half-full
starting load, consistent with the default reserve formula.

| Ship or class | Value used | Config and source notes |
| --- | ---: | --- |
| Sacramento | 16,700 t marine fuel cargo | [GAO-based estimate](../../src/UNREP/unit_extensions/usn_aoe_sacramento_unrep.ini) |
| T2 | 15,200 t fuel cargo, 112 t drinking water | [Deadweight-based fuel estimate and tank records](../../src/UNREP/unit_extensions/usn_ao_t2_unrep.ini) |
| Leaf | 10,323 t marine fuel cargo | [Brambleleaf's July 1982 load as a proxy](../../src/UNREP/unit_extensions/rn_ao_leaf_unrep.ini) |
| Boris Chilikin | 10,300 t marine fuel cargo, 1,000 t drinking water, 220 t provisions | [Project 1559V cargo breakdown](../../src/UNREP/unit_extensions/wp_vt_boris_chilikin_unrep.ini) |
| Kazbek | 10,310 t fuel cargo | [Nominal tanker payload used as an estimate](../../src/UNREP/unit_extensions/wp_vt_kazbek_unrep.ini) |
| Iowa | 7,200 t bunker fuel | [Museum bulletin and volume conversion](../../src/UNREP/unit_extensions/usn_bb_iowa_unrep.ini) |
| Gearing | 761 t bunker fuel | [U.S. Navy wartime class data as a proxy](../../src/UNREP/unit_extensions/usn_dd_gearing_unrep.ini) |
| Sumner FRAM II | 512 t bunker fuel | [U.S. Navy wartime class data as a proxy](../../src/UNREP/unit_extensions/ara_dd_sumner_fram2_unrep.ini) |
| Brooklyn / General Belgrano | 2,013 t bunker fuel | [U.S. Navy wartime class data, including Phoenix, as a proxy](../../src/UNREP/unit_extensions/ara_cl_belgrano_unrep.ini) |

Available Delvar data gives total deadweight. The Kilauea reference gives
ordnance mass and describes its fuel cargo as limited. The game tracks
ammunition by items or points, so those figures remain source notes.

The Sacramento, Leaf and Boris Chilikin configs also include separate aviation
fuel cargo. Sourced flight-deck tanks cover [Nimitz](../../src/UNREP/unit_extensions/usn_cvn_nimitz_unrep.ini),
[Enterprise](../../src/UNREP/unit_extensions/usn_cvn_enterprise_unrep.ini),
[Kitty Hawk](../../src/UNREP/unit_extensions/usn_cv_kitty_hawk_unrep.ini),
[Tarawa](../../src/UNREP/unit_extensions/usn_lha_tarawa_unrep.ini) and [Austin](../../src/UNREP/unit_extensions/usn_lpd_austin_unrep.ini),
[America](../../src/UNREP/unit_extensions/usn_cv_america_79_unrep.ini), and
[Forrestal using Saratoga as a class proxy](../../src/UNREP/unit_extensions/usn_cv_forrestal_75_unrep.ini).

The *Ship Design Data Book* gives Royal Navy AVCAT stowage in cubic metres and
fresh water stowage in tons. The profiles interpret the water figures as long
tons, consistent with the handbook's imperial gallons per ton convention.
Capacity is listed below in metric tonnes. The ship's own tanks start half
full. Leaf starts with its water cargo full and half of its own water stowage.

| Ship or class | Aviation fuel (t) | Fresh water (t) | Config |
| --- | ---: | ---: | --- |
| Invincible | 560 | 356 | [Invincible](../../src/UNREP/unit_extensions/rn_cvs_invincible_unrep.ini) |
| Hermes | 1,160 | 457 | [Stock Centaur definition](../../src/UNREP/unit_extensions/rn_cv_centaur_unrep.ini) |
| Fearless | 56 | 366 | [Fearless](../../src/UNREP/unit_extensions/rn_lpd_fearless_unrep.ini) |
| Bristol | 48 | 132 | [Bristol](../../src/UNREP/unit_extensions/rn_ddg_bristol_unrep.ini) |
| County | 28.8 | 122 | [County](../../src/UNREP/unit_extensions/rn_ddg_county_batch1_unrep.ini) |
| Sheffield / Type 42 | 24 | 81 | [Sheffield](../../src/UNREP/unit_extensions/rn_ddg_sheffield_unrep.ini) |
| Leander | 9.6 | 81 | [Leander](../../src/UNREP/unit_extensions/rn_ff_leander_unrep.ini) |
| Amazon / Type 21 | 24 | 41 | [Amazon](../../src/UNREP/unit_extensions/rn_ff_amazon_unrep.ini) |
| Broadsword / Type 22 | 24 | 71 | [Broadsword](../../src/UNREP/unit_extensions/rn_ff_broadsword_unrep.ini) |
| Round Table | — | 762 | [Round Table](../../src/UNREP/unit_extensions/rn_lsl_roundtable_unrep.ini) |
| Leaf | 4,524 | 1,138 including water cargo | [Leaf](../../src/UNREP/unit_extensions/rn_ao_leaf_unrep.ini) |

The [Iowa config](../../src/UNREP/unit_extensions/usn_bb_iowa_unrep.ini) also
uses Missouri's published fresh water capacity, rounded to 905 tonnes.
