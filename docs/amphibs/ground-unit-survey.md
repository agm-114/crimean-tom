# Ground-unit survey

Scanned the installed Sea Power 0.8.2 loose `land_units/*.ini` definitions on
2026-09-11, excluding `_variants.ini` companions. There were 187 definitions,
including 44 with `IsMobile=True`. The installed Workshop directory contained
no additional loose ground-unit definitions. Packed assets and unsubscribed mods
are outside this scan.

## Variant profiles

`amphibious-variants.ini` reserves separate IDs and points at original units.
The mod reads Candidate profiles at startup and registers separate counterparts
with the original models, weapons, sensors and national liveries. Conditional
profiles remain excluded. Build output and deployment include this INI.

| Source ID | Decision | Notes |
| --- | --- | --- |
| `usa_apc_aav7` | Candidate | Ship-to-shore vehicle; first implementation target. |
| `wp_sa-8_launcher` | Candidate | Amphibious Osa launcher vehicle, not a whole SAM-site assembly. |
| `wp_sa-13_launcher` | Candidate | Strela-10 on a modified MT-LB chassis. |
| `usa_apc_m113` | Candidate | Early/light configuration; added armor can invalidate buoyancy. |
| `wp_lt_pt-76` | Candidate | Amphibious light tank with water jets. |
| `pla_lt_type_63` | Candidate | Original Type 63 tank, not the faster Type 63A. |
| `pla_apc_type63` | Candidate | Amphibious APC, unrelated to the Type 63 SPAAG. Verify exact production configuration before release. |
| `jsdf_apc_type73` | Conditional | Requires fitted flotation equipment. |
| `usa_aaa_vulcan` | Conditional | M163 needs its flotation sponsons and swimming preparation. |
| `usa_ifv_bradley` | Conditional | Early swim-capable configuration and erected barrier; not all Bradleys. |

Dimensions and mass are copied from the local catalogue, not independently measured.
Water speeds are initial calm-water tuning: AAV7 7 kn, Osa 4.3 kn, Strela-10
3 kn, M113 3 kn, PT-76 5.5 kn, Type 63 tank 6.5 kn and Type 63 APC 3.2 kn.
Conditional entries deliberately omit a speed until their configuration is resolved.
No draft, sea-state limit or preparation duration has been invented.

`WaterRole` is a conservative gameplay classification. Swimming across sheltered
water does not qualify a vehicle for open-ocean launch. `WeaponsAfloat=False` is
a gameplay restriction, not a claim about historical firing capability. Missing
weapon-policy keys default to false. WaterRole does not yet enforce launch limits.

## Other mobile definitions

The remaining 34 mobile definitions are not approved for swimming:

- Tanks: `iqa_mbt_t-55_70`, `jsdf_mbt_type74`, `usa_mbt_abrams`, `usa_mbt_m60`,
  `wp_mbt_t-55`, `wp_mbt_t-55m`, `wp_mbt_t-72a`. Fording or snorkelling is not swimming.
- Other tracked vehicles: `jsdf_apc_type60`, `pla_spaag_type63`, `usa_aaa_m42`,
  `usa_mlrs_m270`, `usa_spa_m109a2`, `usa_spa_m110a1`, `wp_spa_2s3`, `wp_zsu-57`.
  Do not infer amphibious capability from tracks, a related chassis or a shared name.
- Wheeled vehicles: the three `civ_car_pickup_1983_*` technicals, `jsdf_sam_type81`,
  `usa_car_hemtt`, all three `usa_car_hmmwv*` entries, `usa_car_m923`, the three
  `wp_aaa_ural_*` technicals, `wp_car_ural`, `wp_car_ural_command`, `wp_mlrs_bm-21`.
- Mobile radar definitions: `wp_p-10_radar`, `wp_p-15_radar`, `wp_p-18_radar`,
  `wp_p-19_radar`. Mobile radar equipment is not necessarily a swimming carrier.

The other 143 definitions have no explicit mobile flag. These include sites,
emplacements, ports, buildings and other equipment. No amphibious profile was
created for them. A SAM system name alone is not enough to qualify a whole site.

## Sources and limitations

- [BAE Systems AAV](https://www.baesystems.com/en-us/product/assault-amphibious-vehicle)
  describes the two water jets and 7-knot cruising speed. Its AAV7A1 specification
  is a starting reference, not proof of the exact model represented by the game.
- [Osa](https://weaponsystems.net/system/1064-9K33%2BOsa) lists amphibious operation
  up to 8 km/h; [Strela-10](https://weaponsystems.net/system/54-9K35%20Strela-10)
  describes the MT-LB chassis and 5–6 km/h afloat.
- [Army Historical Foundation M113 history](https://armyhistory.org/m113-armored-personnel-carrier/)
  confirms track propulsion afloat. [FNSS specifications](https://www.fnss.com.tr/en/products/m113-fov-capability-sustainment-program/m113-fov-technical-specifications)
  give 5.8 km/h for their configuration, used only as a tuning reference.
- [PT-76](https://en.wikipedia.org/wiki/PT-76) lists 10.2 km/h swimming.
  [MCIA Vietnam handbook](https://info.publicintelligence.net/MCIA-VietnamHandbook.pdf)
  gives the Type 63 tank 12 km/h afloat. The
  [Type 63 APC overview](https://www.theworldwars.net/weapons/entry.php?b=land&m=zsd-63)
  gives 6 km/h; exact production-version eligibility still needs checking.
- [Japan MOD Type 73 note](https://www.mod.go.jp/pco/niigata/HP/01-data/mokei/mokei/mokei-20.pdf)
  describes flotation equipment. [M163 overview](https://weaponsystems.net/system/67-M163%20Vulcan)
  explains its buoyancy sponsons.
- [Armor, May–June 1999](https://www.benning.army.mil/Armor/eARMOR/content/issues/1999/MAY_JUN/ArmorMayJune1999web.pdf)
  discusses Bradley swim preparation and the 1994 swimming moratorium.

Before gameplay release, verify waterline and model collision geometry, shoreline
movement, preparation requirements, weapon operation afloat and save/load behavior.
Only then wire eligible profiles into Boat Ops launch/recovery.
