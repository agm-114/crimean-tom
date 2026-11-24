using SeaPower;
using Settings;

namespace Amphibs;

internal class AmphibiousProfile(string id, string name, SettingsValues defaults)
{
    internal string Id { get; } = id;
    internal string Name { get; } = name;
    internal float Speed => UnitValue(AmphibsSettings.SwimSpeed);
    internal bool WeaponsAfloat => UnitValue(AmphibsSettings.AfloatWeapons) > 0;
    internal float WaterlineOffset => Value("WaterlineOffsetMeters");
    internal float Length => UnitValue(AmphibsSettings.HullLength);
    internal float WakeScale => Value("WakeScale");

    internal float Value(string key) => WaterEffectSettings.IsPerUnit(key)
        ? UnitValue(WaterEffectSettings.ByKey[key])
        : AmphibsSettings.Value(AmphibsSettings.Wake, WaterEffectSettings.ByKey[key]);

    private float UnitValue(WaterEffectSetting field) =>
        AmphibsSettings.Value(Id, field, defaults.Get(Id, field.Key, field.Default));

    internal void Set(string key, float value)
    {
        if (!AmphibsSettings.Settings.UseDefaults)
            AmphibsSettings.Settings.Current.Set(WaterEffectSettings.IsPerUnit(key) ? Id : AmphibsSettings.Wake, key, value);
    }
}

internal static class AmphibiousCatalog
{
    private static readonly Dictionary<string, AmphibiousProfile> Profiles = new(StringComparer.OrdinalIgnoreCase);

    public static bool SupportsStockUnit(string id) =>
        AmphibsSettings.ExtendStockUnits || !AmphibiousVariants.IsDefaultVehicle(id);

    internal static bool TryLoadProfile(LandUnitParameters unit, out AmphibiousProfile profile)
    {
        string id = unit._objectIniName;
        if (Profiles.TryGetValue(id, out profile)) return true;
        string? source = AmphibiousVariants.SourceOf(id);
        if (!SupportsStockUnit(id)) return false;

        string settingsId = source ?? id;
        var defaults = new SettingsValues();
        if (!AmphibsSettings.ReadUnit(defaults, settingsId, unit.Simple._ini)) return false;
        string name = source == null ? unit.Simple.DefaultName : AmphibiousVariants.Name(unit.Simple.DefaultName);
        Profiles.Add(id, profile = new(settingsId, name, defaults));
        return true;
    }

    internal static bool TryGet(ObjectBase unit, out AmphibiousProfile profile) =>
        Profiles.TryGetValue((unit as LandUnit)?._obp?._objectIniName ?? "", out profile);
}
