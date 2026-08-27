using CrimeanTom.LoadAcceleration;

namespace CrimeanTom.Campaign.IntegrationTests;

public sealed class SeaPowerOperationalCatalogIntegrationTests
{
    [Fact]
    public void Native_ammunition_mechanics_are_loaded_without_filename_inference()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimean-tom-catalog-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "vessels"));
            Directory.CreateDirectory(Path.Combine(root, "ammunition"));
            File.WriteAllText(Path.Combine(root, "vessels", "test_ship.ini"), """
                [WeaponSystems]
                NumberOfWeaponSystems=1

                [WeaponSystem1]
                Type=Launcher
                Ammunition=opaque_weapon_id
                """);
            File.WriteAllText(Path.Combine(root, "ammunition", "opaque_weapon_id.ini"), """
                [General]
                Type=Torpedo
                TargetType=ASW
                SecondaryTargetType=ASuW

                [Guidance]
                GuidanceType=10
                MidCourseCorrection=2
                MinLaunchRange=0.5
                SpeedSettings=28,40,55
                SpeedSettingRanges=27,20,14
                SeekerActiveRange=2
                SeekerPassiveRange=1
                LaunchReliability=95

                [WarheadData]
                KillProbability=0.72
                Power=68
                """);

            var catalog = new SeaPowerOperationalCatalog(root);
            var weapons = catalog.Resolve("test_ship");

            Assert.Equal(2, weapons.Count);
            Assert.All(weapons, weapon =>
            {
                Assert.Equal("opaque_weapon_id", weapon.AmmunitionId);
                Assert.Equal("Torpedo", weapon.OrdnanceType);
                Assert.Equal(926, weapon.MinimumRangeMeters);
                Assert.Equal(50_004, weapon.MaximumRangeMeters);
                Assert.Equal(10, weapon.GuidanceType);
                Assert.Equal(2, weapon.MidCourseCorrection);
                Assert.Equal(28, weapon.SpeedMetersPerSecond);
                Assert.Equal(3_704, weapon.SeekerRangeMeters);
                Assert.Equal(950, weapon.LaunchReliabilityMilliUnits);
                Assert.Equal(720, weapon.KillProbabilityMilliUnits);
                Assert.Equal(68, weapon.WarheadPower);
            });
            Assert.Contains(weapons, weapon => weapon.TargetType == "Surface");
            Assert.Contains(weapons, weapon => weapon.TargetType == "Subsurface");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
