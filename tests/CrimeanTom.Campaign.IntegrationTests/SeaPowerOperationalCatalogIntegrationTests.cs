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

    /// <summary>
    /// Magazine stock decides when a platform stops shooting, so the server has
    /// to count rounds the way Sea Power counts them rather than share one
    /// fungible pool. This covers each rule the game applies: a magazine's
    /// declared count, one magazine shared by two mounts, a launcher sized by
    /// containers times attachments, a gun mount with no magazine, a hardpoint
    /// station, and a station carrying an attachment group.
    /// </summary>
    [Fact]
    public void Magazine_rounds_follow_the_native_loadout_rule()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimean-tom-magazines-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "vessels"));
            Directory.CreateDirectory(Path.Combine(root, "aircraft"));
            Directory.CreateDirectory(Path.Combine(root, "ammunition"));
            Directory.CreateDirectory(Path.Combine(root, "systems"));

            // Two torpedo mounts share one magazine, a box launcher holds one
            // container of eight rounds, and a gun mount carries rounds the
            // catalog never enumerates.
            File.WriteAllText(Path.Combine(root, "vessels", "test_ship.ini"), """
                [WeaponSystems]
                NumberOfWeaponSystems=5

                [WeaponSystem1]
                Type=Missile
                SystemName=BoxLauncher
                NumberOfContainers=1
                Ammunition=test_sam

                [WeaponSystem2]
                Type=Torpedo
                AssociatedMagazine=TorpedoMagazine

                [WeaponSystem3]
                Type=Torpedo
                AssociatedMagazine=TorpedoMagazine

                [WeaponSystem4]
                Type=Gun
                SystemName=DeckGun
                Ammunition=test_shell

                [WeaponSystem5]
                Type=Missile
                SystemName=TwinArm
                NumberOfContainers=2
                Ammunition=test_ssm

                [TorpedoMagazine]
                NumberOfAmmunitionTypes=1
                Ammunition1=test_torpedo
                Ammunition1_Count=22        // number of torpedoes in magazine
                """);
            File.WriteAllText(Path.Combine(root, "systems", "weapons.ini"), """
                [BoxLauncher]
                NumberOfAttachments=8

                [TwinArm]
                NumberOfAttachments=2
                """);

            // One bare station, and one station naming an attachment group that
            // holds a whole rack.
            File.WriteAllText(Path.Combine(root, "aircraft", "test_air.ini"), """
                [WeaponSystems]
                NumberOfWeaponSystems=1

                [WeaponSystem1]
                Type=Hardpoint
                NumberOfStations=2
                BuoyPositions=0,0,0|1,1,1|2,2,2

                [WeaponSystem1Default]
                Station1=test_torpedo
                Station2=test_shell|Buoy
                """);

            foreach (var (id, target) in new[]
            {
                ("test_sam", "AAW"),
                ("test_ssm", "ASuW"),
                ("test_torpedo", "ASW"),
                ("test_shell", "ASuW"),
            })
            {
                File.WriteAllText(Path.Combine(root, "ammunition", $"{id}.ini"), $"""
                    [General]
                    Type=Missile
                    TargetType={target}

                    [Guidance]
                    GuidanceType=3
                    MaxLaunchRange=10
                    """);
            }

            var catalog = new SeaPowerOperationalCatalog(root);
            var ship = catalog.Resolve("test_ship")
                .ToDictionary(item => item.AmmunitionId, item => item.MagazineRounds);

            Assert.Equal(8, ship["test_sam"]);
            Assert.Equal(4, ship["test_ssm"]);
            // Counted once even though two mounts feed from it.
            Assert.Equal(22, ship["test_torpedo"]);
            // The gun stays a usable capability, but the catalog declares no
            // countable stock, so the server must not invent one.
            Assert.Equal(0, ship["test_shell"]);

            var aircraft = catalog.Resolve("test_air")
                .ToDictionary(item => item.AmmunitionId, item => item.MagazineRounds);

            Assert.Equal(1, aircraft["test_torpedo"]);
            Assert.Equal(3, aircraft["test_shell"]);
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
