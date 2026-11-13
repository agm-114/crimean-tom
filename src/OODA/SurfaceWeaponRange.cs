using System.Globalization;
using SeaPower;

namespace OODA;

internal static class SurfaceWeaponRange
{
    internal static double? ForClass(string classId, string target = "ASuW", string folder = "vessels")
    {
        // Use the identified class's public catalog. Scenario loadouts and current enemy stocks stay hidden.
        if (!Singleton<UnitMetadataProvider>.InstanceExists()
            || Singleton<UnitMetadataProvider>.Instance.UnitData is not { } units
            || !units.TryGetValue((folder, classId), out var entry) || entry.Item2._ini is not { } vessel) return null;
        return MountedAmmunition(vessel).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ammo => AmmoRange(ammo, target)).Where(range => range is > 0 and < double.PositiveInfinity).Max();
    }

    private static IEnumerable<string> MountedAmmunition(IniHandler vessel)
    {
        int mounts = vessel.readValue("WeaponSystems", "NumberOfWeaponSystems", 0, performLogging: false);
        for (int i = 1; i <= mounts; i++)
        {
            string section = $"WeaponSystem{i}";
            yield return Utils.ResolveDateBasedAmmo(vessel, Read(vessel, section, "Ammunition"));
            string magazine = Read(vessel, section, "AssociatedMagazine");
            if (magazine.Length == 0) continue;
            int count = vessel.readValue(magazine, "NumberOfAmmunitionTypes", 0, performLogging: false);
            for (int j = 1; j <= count; j++)
                yield return Utils.ResolveDateBasedAmmo(vessel, Read(vessel, magazine, $"Ammunition{j}"));
        }
    }

    private static double? AmmoRange(string id, string target)
    {
        if (id.Length == 0 || OpenAmmo(id) is not { } ammo) return null;
        if (Read(ammo, "General", "Type").Equals("Container", StringComparison.OrdinalIgnoreCase))
        {
            string attachment = Read(ammo, "Ammunition", "Ammunition");
            if (attachment.Length == 0 || OpenAmmo(attachment) is not { } attachedAmmo) return null;
            ammo = attachedAmmo;
        }
        if (!Read(ammo, "General", "TargetType").Equals(target, StringComparison.OrdinalIgnoreCase)
            && !Read(ammo, "General", "SecondaryTargetType").Equals(target, StringComparison.OrdinalIgnoreCase)) return null;
        return ammo.readValue("General", "Type", Ammunition.Type.Unknown, performLogging: false) switch
        {
            Ammunition.Type.Projectile => ammo.readValue("Physics", "MaxRange", 10000f, performLogging: false) / 1852d,
            Ammunition.Type.Missile => ammo.readValue("Guidance", "MaxLaunchRange", 40f, performLogging: false),
            Ammunition.Type.Torpedo => ammo.doesKeyExist("Guidance", "MaxLaunchRange")
                ? ammo.readValue("Guidance", "MaxLaunchRange", 0f, performLogging: false)
                : Read(ammo, "Guidance", "SpeedSettingRanges").Split(',').Select(ParseNumber).Max(),
            _ => null
        };
    }

    private static double ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double range) ? range : 0;

    private static IniHandler? OpenAmmo(string id) => Utils.openIniFile("/ammunition", $"/{id}.ini", logErrors: false);

    private static string Read(IniHandler ini, string section, string key) =>
        ini.readValue(section, key, "", performLogging: false);
}
