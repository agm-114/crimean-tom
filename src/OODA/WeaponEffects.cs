using Newtonsoft.Json.Linq;
using SeaPower;

namespace OODA;

internal static class WeaponEffects
{
    private record Weapon(string Id, AmmunitionSimpleParameters Data, IniHandler Ini)
    {
        internal string Text(string section, string key) => Ini.readValue(section, key, "", performLogging: false);
        internal float Number(string section, string key, float fallback = 0) => Ini.readValue(section, key, fallback, performLogging: false);
        internal double Power => Number("WarheadData", "Power");
        internal AmmunitionParameters.WarheadType Warhead => AmmunitionParameters.getWarheadTypeFromIniIndex(
            Ini.readValue("WarheadData", "WarheadType", 0, performLogging: false));
    }
    private record Module(double Integrity, bool Radar, BaseSystem.ArmorType Armor);

    private static Weapon? Find(string id) => Singleton<UnitMetadataProvider>.InstanceExists()
        && Singleton<UnitMetadataProvider>.Instance.AmmunitionData.TryGetValue(id, out AmmunitionSimpleParameters data)
        && Utils.openIniFile("/ammunition", $"/{id}.ini", logErrors: false) is { } ini ? new(id, data, ini) : null;

    internal static JObject Profiles(JObject view)
    {
        var fits = (view["airDecks"]?.SelectMany(deck => deck["fits"]?.AsEnumerable() ?? []) ?? [])
            .Concat(view["airborne"]?.AsEnumerable() ?? []);
        JObject profiles = new();
        foreach (string id in fits.SelectMany(fit => fit["stores"]?.Values<string>() ?? []).OfType<string>()
            .Where(store => store.Contains('×')).Select(store => store.Substring(store.IndexOf('×') + 1).Trim()).Distinct())
        {
            if (Find(id) is not { } weapon || weapon.Power <= 0) continue;
            profiles[id] = Describe(weapon);
        }
        return profiles;
    }

    private static JObject Describe(Weapon weapon) => new()
    {
        ["id"] = weapon.Id, ["name"] = weapon.Data.DisplayedName, ["type"] = weapon.Data._type.ToString(),
        ["guidance"] = weapon.Data._guidanceType.ToString(), ["target"] = weapon.Data._targetType.ToString(),
        ["secondaryTarget"] = weapon.Data._secondonaryTargetType.ToString(), ["landAttack"] = new JArray(weapon.Data._landAttack.Select(type => type.ToString())),
        ["minRangeNm"] = Positive(weapon.Number("Guidance", "MinLaunchRange")),
        ["nominalMaxRangeNm"] = Positive(weapon.Data._type == Ammunition.Type.Projectile ? weapon.Number("Physics", "MaxRange") / 1852 : weapon.Number("Guidance", "MaxLaunchRange")),
        ["minAltitudeFeet"] = Positive(weapon.Number("Guidance", "MinLaunchAltitude")),
        ["maxAltitudeFeet"] = Positive(weapon.Number("Guidance", "MaxLaunchAltitude")),
        ["passiveRadarBands"] = new JArray(weapon.Text("Guidance", "PassiveRadarGuidanceFrequencies").Split(',').Select(band => band.Trim()).Where(band => band.Length > 0)),
        ["warhead"] = weapon.Warhead.ToString(), ["warheadPower"] = weapon.Power, ["penetration"] = weapon.Text("WarheadData", "Penetration")
    };

    internal static JObject Estimate(JObject contact, string weaponId, int hits, bool airborneShooter)
    {
        if (Find(weaponId) is not { } weapon || weapon.Power <= 0) return Error("Munition has no modeled direct warhead");
        if (weapon.Data._hasSubminution) return Error("This munition disperses submunitions. A per-impact estimate needs their footprint and pattern.");
        if ((bool?)contact["identified"] != true || (string?)contact["observedClass"] is not { Length: > 0 } classId)
            return Error("An identified class is needed for a damage estimate");
        if (!Singleton<UnitMetadataProvider>.InstanceExists() || Singleton<UnitMetadataProvider>.Instance.UnitData is not { } units)
            return Error("The unit catalog is unavailable");
        if (units.TryGetValue(("vessels", classId), out var vessel) && vessel.Item2._ini is { } vesselIni)
            return EstimateHull(classId, vesselIni, weapon, hits);
        if (!units.TryGetValue(("land_units", classId), out var entry) || entry.Item2._ini is not { } target)
            return Error("Damage estimates support catalog ground and naval units");
        float threshold = target.readValue("General", "DamagePoints", 0f, performLogging: false);
        string subtype = target.readValue("General", "LandUnitSubType", "", performLogging: false);
        BaseSystem.ArmorType armor = target.readValue("General", "ArmorType", BaseSystem.ArmorType.None, performLogging: false);
        Module[] modules = target.Data.Keys.Where(section => section == "ControlSystem" || section.StartsWith("SensorSystem", StringComparison.Ordinal)
                || section.StartsWith("WeaponSystem", StringComparison.Ordinal) || section.StartsWith("HitZone", StringComparison.Ordinal))
            .Select(section => ReadModule(target, section)).OfType<Module>().ToArray();
        bool radarKill = subtype.Equals("SAM", StringComparison.OrdinalIgnoreCase) && modules.Any(module => module.Radar);
        if (modules.Length == 0 || threshold <= 0) return Error("Public catalog has no usable target damage layout");
        bool mobile = target.readValue("General", "IsMobile", false, performLogging: false) || subtype is "MobileUnit" or "AAA";
        var warhead = weapon.Warhead;
        var penetration = weapon.Ini.readValue("WarheadData", "Penetration", AmmunitionParameters.Penetration.Minor, performLogging: false);
        bool airGun = airborneShooter && weapon.Data._type == Ammunition.Type.Projectile && mobile;
        double power = weapon.Power;
        power *= warhead switch
        {
            AmmunitionParameters.WarheadType.Fragmentation => 0.5,
            AmmunitionParameters.WarheadType.HEAT when mobile => 2.5,
            AmmunitionParameters.WarheadType.HEAT when weapon.Data._type != Ammunition.Type.Projectile && subtype is "Airbase" or "Installation" or "Port" => 1d / 3,
            AmmunitionParameters.WarheadType.Cluster => mobile || subtype is "SAM" or "AAA" ? 2 : 0.25,
            _ => 1
        };
        if (airGun) power = Math.Max(2, power * 4);
        power *= 1.1;
        return new JObject
        {
            ["targetClass"] = classId, ["weapon"] = Describe(weapon), ["damageThreshold"] = threshold,
            ["modeledModules"] = modules.Length, ["radarKillPossible"] = radarKill,
            ["directHitsTested"] = hits, ["focusedOnRadar"] = radarKill ? Simulate(true) : null, ["distributedHits"] = Simulate(false),
            ["assumptions"] = "Fresh catalog unit. Each hit damages one selected modeled collider, with native system damage variance and armor. The radar case assumes hits are placed on the radar modules. The distributed case weights catalog colliders equally. These are hit counts, not shots or sortie counts. Accuracy, blast overlap, current damage, secondary fires and difficulty bonuses are excluded."
        };

        JObject Simulate(bool focusRadar)
        {
            const int trials = 256, maxHits = 240;
            Random random = new(51871);
            int[] outcomes = new int[trials];
            int[] eligible = Enumerable.Range(0, modules.Length).Where(i => !focusRadar || modules[i].Radar).ToArray();
            for (int trial = 0; trial < trials; trial++)
            {
                double[] remaining = modules.Select(module => module.Integrity).ToArray();
                outcomes[trial] = maxHits + 1;
                double destroyedIntegrity = 0;
                int remainingRadars = modules.Count(module => module.Radar);
                for (int shot = 1; shot <= maxHits; shot++)
                {
                    int i = eligible[random.Next(eligible.Length)];
                    Module module = modules[i];
                    if (remaining[i] <= 0.01 * module.Integrity) continue;
                    double plating = airGun ? 1 : ArmorMultiplier(armor, penetration, random);
                    double multiplier = plating > 0 ? airGun ? 1 : ArmorMultiplier(module.Armor, penetration, random) : 0;
                    if (multiplier <= 0) continue;
                    remaining[i] -= Math.Max(0.5, multiplier * power * (0.5 + 0.7 * random.NextDouble()));
                    if (remaining[i] > 0.01 * module.Integrity) continue;
                    destroyedIntegrity += module.Integrity - Math.Max(0, remaining[i]);
                    if (module.Radar) remainingRadars--;
                    if (destroyedIntegrity / threshold <= 0.99 && (!radarKill || remainingRadars > 0)) continue;
                    outcomes[trial] = shot;
                    break;
                }
            }
            Array.Sort(outcomes);
            int middle = outcomes[trials / 2], upper = outcomes[(int)(0.9 * trials)];
            return new JObject { ["medianDirectHits"] = middle <= maxHits ? middle : null, ["p90DirectHits"] = upper <= maxHits ? upper : null,
                ["trialKillFractionAtHitCount"] = Math.Round(outcomes.Count(outcome => outcome <= hits) / (double)trials, 2), ["trialLimitHits"] = maxHits };
        }
    }

    private static JObject EstimateHull(string classId, IniHandler target, Weapon weapon, int hits)
    {
        double displacement = target.readValue("Physics", "Displacement", 0f, performLogging: false);
        double integrity = target.readValue("Physics", "IntegrityOverride", 0f, performLogging: false);
        string type = target.readValue("General", "UnitType", "", performLogging: false);
        bool submarine = type.Equals("Submarine", StringComparison.OrdinalIgnoreCase);
        BaseSystem.ArmorType armor = target.readValue("General", "ArmorType", BaseSystem.ArmorType.None, performLogging: false);
        if (integrity <= 0)
        {
            if (displacement <= 0) return Error("The public catalog does not give a hull displacement");
            integrity = Math.Pow(displacement, 1d / 3) * 2.5 * (submarine || classId.StartsWith("civ_", StringComparison.Ordinal) ? 0.5 : 1);
            integrity *= armor switch { BaseSystem.ArmorType.Minor => 1.2, BaseSystem.ArmorType.Moderate => 1.25, BaseSystem.ArmorType.Heavy => 1.3, _ => 1 };
        }
        var warhead = weapon.Warhead;
        if (warhead == AmmunitionParameters.WarheadType.Cluster || weapon.Data._type == Ammunition.Type.Projectile)
            return Error("The naval estimate covers non-cluster missiles, bombs, and torpedoes. Naval gunfire uses a separate hull rule.");
        double limit = integrity * 5;
        double power = 1.1 * weapon.Power * (warhead == AmmunitionParameters.WarheadType.Fragmentation ? 0.5 : warhead == AmmunitionParameters.WarheadType.HEAT ? 1d / 3 : 1);
        double hullDamage = power * (submarine ? weapon.Data._type == Ammunition.Type.Torpedo ? 1.5 : 1 : weapon.Data._type == Ammunition.Type.Torpedo ? 3.5 : 2);
        int structuralHits = (int)Math.Floor(limit / hullDamage) + 1;
        return new JObject { ["targetClass"] = classId, ["weapon"] = Describe(weapon), ["directHitsTested"] = hits,
            ["structuralLimit"] = Math.Round(limit, 1), ["structuralDamagePerEffectiveHit"] = Math.Round(hullDamage, 1),
            ["structuralThresholdHits"] = structuralHits, ["crossesStructuralThresholdAtHitCount"] = hits >= structuralHits,
            ["assumptions"] = "Fresh catalog hull, using the native structural-limit rule for effective hull impacts. This omits flooding, fire, magazines, dud and interception rates, mount or difficulty modifiers, and the torpedo depth bonus. Ships can sink earlier from local damage. No estimate of hit probability or current target damage." };
    }

    private static Module? ReadModule(IniHandler target, string section)
    {
        string collider = target.readValue(section, "Collider", "", performLogging: false);
        bool radar = target.readValue(section, "Type", "", performLogging: false).Equals("Radar", StringComparison.OrdinalIgnoreCase);
        if (collider.Length == 0 && !radar) return null;
        float integrity = target.readValue(section, "Integrity", 10f, performLogging: false);
        if (integrity < 1) return null;
        return new(integrity, radar, target.readValue(section, "ArmorType", BaseSystem.ArmorType.None, performLogging: false));
    }

    private static double ArmorMultiplier(BaseSystem.ArmorType armor, AmmunitionParameters.Penetration penetration, Random random)
    {
        if (armor == BaseSystem.ArmorType.None) return 1;
        if (Globals._damageValues?._penVsArmorMatrix.TryGetValue((int)armor, out var row) != true || (int)penetration >= row.Length) return 0;
        var range = row[(int)penetration];
        return random.NextDouble() < range.y ? range.x + (range.y - range.x) * random.NextDouble() : 0;
    }

    private static double? Positive(double number) => number > 0 && double.IsFinite(number) ? Math.Round(number, 2) : null;
    private static JObject Error(string message) => new() { ["error"] = message };
}
