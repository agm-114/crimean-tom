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
    /// Detection range decides what a platform can even shoot at, and Sea Power
    /// declares it per radar rather than per ship class. This covers the rules
    /// that make one hull see further than another: the declared kilometre
    /// range, the role that says whether a set searches for aircraft or ships,
    /// land-detection, and the exclusion of sensors whose range the catalog
    /// does not actually state.
    /// </summary>
    [Fact]
    public void Radar_horizons_come_from_the_units_own_sensors()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crimean-tom-sensors-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "vessels"));
            Directory.CreateDirectory(Path.Combine(root, "ammunition"));
            Directory.CreateDirectory(Path.Combine(root, "systems"));
            File.WriteAllText(Path.Combine(root, "vessels", "test_ship.ini"), """
                [SensorSystems]
                NumberOfSensorSystems=4

                [SensorSystem1]
                SystemName=Illuminator

                [SensorSystem2]
                SystemName=SearchRadar

                [SensorSystem3]
                SystemName=HullSonar

                [SensorSystem4]
                SystemName=Periscope
                """);
            File.WriteAllText(Path.Combine(root, "systems", "sensors.ini"), """
                [Illuminator]
                Kind=Radar
                Role=Air
                MinRange=0.2
                MaxRange=37

                [SearchRadar]
                Kind=Radar
                Role=AirAndSurface
                CanDetectLandTargets=True
                HasDataLink=True
                MinRange=9.3
                MaxRange=407.4         // km, unlike weapon ranges in nmi
                TargetChannels=6

                [HullSonar]
                Kind=Sonar
                ActiveRange=2000       // a nominal bound, not a detection range

                [Periscope]
                Kind=Visual
                MaxRangeMultiplier=2.2
                """);

            var sensors = new SeaPowerOperationalCatalog(root).ResolveSensors("test_ship");

            // The sonar and the optics carry no declared detection range, so
            // the catalog reports neither rather than inventing one.
            Assert.Equal(["SensorSystem1", "SensorSystem2"], sensors.Select(item => item.SensorId));

            var illuminator = sensors[0];
            Assert.Equal(37_000, illuminator.MaximumRangeMeters);
            Assert.Equal(200, illuminator.MinimumRangeMeters);
            Assert.True(illuminator.DetectsAir);
            Assert.False(illuminator.DetectsSurface);
            Assert.False(illuminator.HasDataLink);

            var search = sensors[1];
            Assert.Equal(407_400, search.MaximumRangeMeters);
            Assert.True(search.DetectsAir);
            Assert.True(search.DetectsSurface);
            Assert.True(search.DetectsLand);
            Assert.Equal(6, search.TargetChannels);
            Assert.True(search.HasDataLink);
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
                AssociatedSensors=SensorSystem1

                [WeaponSystem2]
                Type=Torpedo
                SystemName=TorpedoTube
                AssociatedMagazine=TorpedoMagazine

                [WeaponSystem3]
                Type=Torpedo
                SystemName=TorpedoTube
                AssociatedMagazine=TorpedoMagazine

                [WeaponSystem4]
                Type=Gun
                SystemName=DeckGun
                Ammunition=test_shell
                ReloadTime=45              // overrides the shared definition

                [WeaponSystem5]
                Type=Missile
                SystemName=TwinArm
                NumberOfContainers=2
                Ammunition=test_ssm
                AssociatedSensors=SensorSystem1,SensorSystem2

                [SensorSystem1]
                SystemName=Illuminator

                [SensorSystem2]
                SystemName=SearchRadar

                [TorpedoMagazine]
                NumberOfAmmunitionTypes=1
                Ammunition1=test_torpedo
                Ammunition1_Count=22        // number of torpedoes in magazine
                """);
            File.WriteAllText(Path.Combine(root, "systems", "weapons.ini"), """
                [BoxLauncher]
                NumberOfAttachments=8
                ReloadTime=1800

                [TwinArm]
                NumberOfAttachments=2
                ReloadTime=8
                SalvoFireAmount=4

                [TorpedoTube]
                ReloadTime=30

                [DeckGun]
                ReloadTime=60
                FireRate=120
                """);
            File.WriteAllText(Path.Combine(root, "systems", "sensors.ini"), """
                [Illuminator]
                TargetChannels=1
                WeaponChannels=2       // missiles guided simultaneously

                [SearchRadar]
                TargetChannels=6
                WeaponChannels=1
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

            // Firing cadence comes from the weapon system, not the ammunition,
            // and a unit may override it. A gun sustains a rate of fire instead
            // of reloading as a unit, so one engagement is a second of fire.
            // Mounts stay separate because each reloads on its own.
            var mounts = catalog.Resolve("test_ship")
                .DistinctBy(item => item.AmmunitionId)
                .ToDictionary(item => item.AmmunitionId, item => item.Mounts);

            var sam = Assert.Single(mounts["test_sam"]);
            Assert.Equal("WeaponSystem1", sam.MountId);
            Assert.Equal(1_800, sam.ReloadSeconds);

            // A twin-arm launcher is one mount that cycles both arms, so its two
            // containers raise capacity but not the number of mounts.
            var ssm = Assert.Single(mounts["test_ssm"]);
            Assert.Equal(8, ssm.ReloadSeconds);
            Assert.Equal(4, ssm.SalvoRounds);

            // Two mounts share the torpedo magazine but each cycles separately,
            // so the pair can launch twice in quick succession and then both
            // wait. Collapsing them into a count could not express that.
            Assert.Equal(
                ["WeaponSystem2", "WeaponSystem3"],
                mounts["test_torpedo"].Select(item => item.MountId));
            Assert.All(mounts["test_torpedo"], item => Assert.Equal(30, item.ReloadSeconds));

            var gun = Assert.Single(mounts["test_shell"]);
            // 120 rounds per minute is two rounds of sustained fire a second.
            Assert.Equal(2, gun.SalvoRounds);
            // The unit's own section overrides the shared system definition.
            Assert.Equal(45, gun.ReloadSeconds);

            // Guidance capacity comes from the mount's own fire-control
            // sensors, summed across them, not from the launcher or missile.
            Assert.Equal(2, sam.GuidanceChannels);
            Assert.Equal(3, ssm.GuidanceChannels);
            // A mount with no associated fire-control sensor declares none.
            Assert.All(mounts["test_torpedo"], item => Assert.Equal(0, item.GuidanceChannels));
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
