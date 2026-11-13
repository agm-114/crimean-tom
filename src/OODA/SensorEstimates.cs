using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal static class SensorEstimates
{
    internal static JArray Read(IEnumerable<ObjectBase> units) => new(units.Where(unit => unit._obp?._sensorSystems != null)
        .Select(unit => new { Unit = unit, Sensors = unit._obp._sensorSystems.Where(sensor => sensor is SensorSystemRadar or SensorSystemSonar
            || SensorSweeps.Kind(sensor) != null).Where(sensor => !sensor.Inoperable.Value).ToArray() })
        .Where(entry => entry.Sensors.Length > 0).Select(entry => new JObject
        {
            ["unitId"] = PlayerPicture.Id(entry.Unit),
            ["sensors"] = new JArray(entry.Sensors.Select(sensor => new JObject
            {
                ["id"] = sensor._systemName, ["kind"] = sensor is SensorSystemRadar ? "Radar" : sensor is SensorSystemSonar ? "Sonar" : SensorSweeps.Kind(sensor),
                ["mast"] = sensor._isSubmarineMast, ["on"] = sensor.IsOn.Value, ["sweep"] = sensor._allowTurnOn ? SensorSweeps.Kind(sensor) : null,
                ["sweepSeconds"] = sensor is SensorSystemRadar radar && radar.ScanPeriodSeconds > 0 ? Math.Round(radar.ScanPeriodSeconds, 1) : null
            }))
        }));

    internal static JObject EmitterProfiles(JObject view)
    {
        JObject result = new();
        if (!Singleton<UnitMetadataProvider>.InstanceExists() || Singleton<UnitMetadataProvider>.Instance.UnitData is not { } units) return result;
        foreach (string classId in (view["contacts"]?.OfType<JObject>() ?? []).Where(track => (bool?)track["identified"] == true
            && (string?)track["posture"] == "Hostile").Select(track => (string?)track["observedClass"]).OfType<string>().Distinct())
        {
            if (!units.TryGetValue(("land_units", classId), out var entry) || entry.Item2._ini is not { } ini) continue;
            HashSet<string> missileSensors = ini.Data.Keys.Where(section => IsSystem(section, "WeaponSystem", "Missile"))
                .SelectMany(section => ini.readValue(section, "AssociatedSensors", "", performLogging: false).Split(','))
                .Select(sensor => sensor.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            JArray radars = new(ini.Data.Keys.Where(section => IsSystem(section, "SensorSystem", "Radar"))
                .Select(section => (Section: section, Radar: ReadRadar(ini.readValue(section, "SystemName", "", performLogging: false))))
                .Where(entry => entry.Radar != null)
                .GroupBy(entry => entry.Radar!._systemName)
                .Select(group =>
                {
                    RadarParameters radar = group.First().Radar!;
                    return new JObject { ["id"] = group.Key, ["role"] = radar._role.ToString(),
                        ["band"] = radar._frequency == Globals.Frequency.Undefined ? null : radar._frequency.ToString().Replace('_', '-'),
                        ["supportsMissileFire"] = group.Any(entry => missileSensors.Contains(entry.Section)) };
                }));
            if (radars.HasValues) result[classId] = radars;

            bool IsSystem(string section, string prefix, string type) => section.StartsWith(prefix, StringComparison.Ordinal)
                && ini.readValue(section, "Type", "", performLogging: false).Equals(type, StringComparison.OrdinalIgnoreCase);
        }
        return result;

        static RadarParameters? ReadRadar(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            try { return new(name); }
            catch (KeyNotFoundException) { return null; }
        }
    }

    internal static JObject Estimate(ObjectBase observer, string? sensorId, string targetKind, float? targetHeightMeters,
        float? rcsSquareMeters, float? observerHeightMeters, float? observerSpeedKnots)
    {
        if (targetKind is not ("SmallAir" or "LargeAir" or "SmallSurface" or "LargeSurface" or "Land" or "Submarine"))
            return new() { ["error"] = "Select a target kind from the detection tool guide" };
        if (!Valid(targetHeightMeters, 0, 30000) || !Valid(observerHeightMeters, 0, 30000)
            || !Valid(rcsSquareMeters, 0.001f, 1000000) || !Valid(observerSpeedKnots, 0, 2000))
            return new() { ["error"] = "Height, speed, or radar cross section is outside the planning range" };
        if (observer._obp?._sensorSystems is not { } available)
            return new() { ["error"] = "Observer has no available sensors" };
        float altitude = targetHeightMeters ?? (targetKind.EndsWith("Air", StringComparison.Ordinal) ? 3000 : targetKind == "Land" ? 5 : 15);
        float speed = observerSpeedKnots ?? Math.Abs(observer._velocityInKnots);
        float rcs = rcsSquareMeters ?? (targetKind switch { "SmallAir" => 5, "LargeAir" => 100, "SmallSurface" => 1000, "LargeSurface" => 10000, _ => 1 });
        JArray results = [];
        foreach (SensorSystem sensor in available.Where(sensor => !sensor.Inoperable.Value
            && (sensorId == null || sensor._systemName == sensorId)).Take(32))
        {
            if (sensor is SensorSystemRadar radar && radar._radar?._rp is { } rp && targetKind != "Submarine")
            {
                bool air = targetKind.EndsWith("Air", StringComparison.Ordinal);
                if (air && rp._role == RadarParameters.Role.Surface || !air && rp._role == RadarParameters.Role.Air
                    || targetKind == "Land" && !rp._canDetectLandTargets) continue;
                SensorDataRadarComponent data = new() { _maxRange = rp._maxRange, _gain = rp._gain, _gainFactor = rp._gainFactor,
                    _peakPower = rp._peakPower, _frequency = rp._frequency };
                double nominal = PointValueSensorScorer.RadarDetectionRangeNmi(in data, rcs);
                float antenna = observerHeightMeters.HasValue ? Utils.heightInMeterToUnity(observerHeightMeters.Value) : sensor.SensorPosition.y;
                double horizon = RadarCalculator.getRadarHorizon(antenna, Utils.heightInMeterToUnity(altitude)) / Utils.heightInMeterToUnity(1852);
                results.Add(new JObject { ["id"] = sensor._systemName, ["kind"] = "Radar", ["on"] = sensor.IsOn.Value,
                    ["band"] = rp._frequency.ToString(), ["nominalNm"] = Round(nominal), ["horizonNm"] = Round(horizon),
                    ["clearLineOfSightNm"] = Round(Math.Min(nominal, horizon)), ["radarCrossSectionM2"] = rcs,
                    ["minimumTargetAltitudeMeters"] = rp._minAltitude / Utils.heightInMeterToUnity(1),
                    ["minimumRangeNm"] = Round(rp._minRange / Utils.heightInMeterToUnity(1852)) });
            }
            else if (sensor is SensorSystemSonar sonarSystem && targetKind is "Submarine" or "SmallSurface" or "LargeSurface")
            {
                var sonar = PointValueSensorScorer.EstimatePassiveSonarRanges(sensor._systemName, null, speed, observer is Submarine);
                results.Add(new JObject { ["id"] = sensor._systemName, ["kind"] = "Sonar", ["on"] = sensor.IsOn.Value,
                    ["activeNominalNm"] = sonarSystem._sonar?._sp is { _activeRangeInKm: > 0 } sp ? Round(sp._activeRangeInKm / 1.852) : null,
                    ["quietAboveNm"] = Round(sonar.QuietAbove), ["quietBelowNm"] = Round(sonar.QuietBelow),
                    ["loudAboveNm"] = Round(sonar.LoudAbove), ["loudBelowNm"] = Round(sonar.LoudBelow) });
            }
            else if (SensorSweeps.Kind(sensor) == "Periscope" && targetKind != "Submarine")
            {
                float eye = observerHeightMeters.HasValue ? Utils.heightInMeterToUnity(observerHeightMeters.Value) : sensor.SensorPosition.y;
                double horizon = RadarCalculator.getRadarHorizon(eye, Utils.heightInMeterToUnity(altitude), visual: true) / Utils.heightInMeterToUnity(1852);
                results.Add(new JObject { ["id"] = sensor._systemName, ["kind"] = "Visual", ["on"] = sensor.IsOn.Value,
                    ["horizonNm"] = Round(horizon) });
            }
        }
        return new() { ["targetKind"] = targetKind, ["targetHeightMeters"] = altitude, ["observerSpeedKnots"] = speed, ["sensors"] = results,
            ["assumptions"] = "Encyclopedia nominal radar and passive sonar models, with native radar or visual horizon. Target and observer heights are above sea level. Weather, terrain masking, jamming, aspect, and current propagation can reduce detection. Passive ESM needs an emitter and does not visually identify contacts." };

        static bool Valid(float? number, float min, float max) => !number.HasValue || float.IsFinite(number.Value) && number.Value >= min && number.Value <= max;
        static double? Round(double number) => double.IsFinite(number) && number >= 0 ? Math.Round(number, 1) : null;
    }
}
