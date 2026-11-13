using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;

namespace OODA;

internal class SensorSweeps
{
    private class Sweep(ObjectBase unit, SensorSystem sensor, double duration)
    {
        internal ObjectBase Unit { get; } = unit;
        internal SensorSystem Sensor { get; } = sensor;
        internal double Duration { get; } = duration;
        internal double Deadline { get; } = GameTime.missionElapsedTime + 180;
        internal int? PreviousDepth;
        internal float? OriginalAltitude;
        internal float? SweepAltitude;
        internal bool Enabled;
        internal double? EndAt;
    }

    private readonly Dictionary<ObjectBase, Sweep> active = [];
    internal bool Active => active.Count > 0;

    internal static string? Kind(SensorSystem sensor) => sensor switch
    {
        SensorSystemRadar when sensor._systemType == BaseSystem.SystemType.SensorSearchRadar => "Radar",
        SensorSystemESM => "ESM",
        _ when sensor._systemType == BaseSystem.SystemType.SensorPeriscope => "Periscope",
        _ => null
    };

    internal string Start(Taskforce side, ObjectBase unit, string kind, string? sensorId, double? duration)
    {
        if (PlayerOrders.Blocked || !PlayerPicture.CanCommand(unit, side)) return "Rejected · sensor control is unavailable";
        if (active.ContainsKey(unit)) return "Kept · this unit already has a sweep in progress";
        if (duration.HasValue && (!double.IsFinite(duration.Value) || duration.Value is < 1 or > 30))
            return "Rejected · choose an exposure of 1 to 30 simulated seconds";
        SensorSystem? sensor = unit._obp?._sensorSystems?.FirstOrDefault(item => Kind(item) == kind && !item.Inoperable.Value && item._allowTurnOn
            && (sensorId == null || item._systemName == sensorId));
        if (sensor == null) return "Rejected · no matching operable sensor";
        if (sensor.IsOn.Value) return "Kept · selected sensor is already on";
        if (unit is Submarine && sensor._isSubmarineMast && Singleton<SeaPower.Environment>.InstanceExists()
            && Singleton<SeaPower.Environment>.Instance.SeaState > 5) return "Rejected · sea state prevents mast use";
        if (unit is Submarine sub && (sub.ThreatingTorpedo != null || PlayerCombat.TorpedoInbound(sub)))
            return "Rejected · submarine has an inbound torpedo";
        if (unit is Submarine engaged && (engaged._alignmentTasks.Count > 0 || engaged._currentEngageTasks.Count > 0))
            return "Rejected · submarine is committed to a weapon engagement";
        if (unit.CurrentOrder.Value?.OrderType == Order.Type.ReturnToBase) return "Rejected · unit is recovering";
        double exposure = duration ?? (sensor is SensorSystemRadar radar && radar.ScanPeriodSeconds > 0 ? radar.ScanPeriodSeconds : 8);
        Sweep sweep = new(unit, sensor, Math.Clamp(exposure, 1, 30));
        if (unit is Submarine diving && sensor._isSubmarineMast && diving.IsBelowPeriscopeDepth.Value)
        {
            sweep.PreviousDepth = diving._currentPresetDepth;
            sweep.OriginalAltitude = diving.DesiredAltitude.Value;
            diving.setPresetDepth(1);
            sweep.SweepAltitude = diving.DesiredAltitude.Value;
        }
        else Enable(sweep);
        active.Add(unit, sweep);
        return sweep.Enabled ? $"Sweep ordered · {kind} for {sweep.Duration:0.#} simulated seconds" : $"Sweep ordered · ascend to periscope depth for {kind}";
    }

    internal void Tick(Taskforce side, Action<ObjectBase, string> report)
    {
        double now = GameTime.missionElapsedTime;
        foreach (Sweep sweep in active.Values.ToArray())
        {
            ObjectBase unit = sweep.Unit;
            if (!PlayerPicture.CanCommand(unit, side))
            {
                active.Remove(unit);
                if (unit && !unit.IsDestroyed) Restore(sweep);
                continue;
            }
            Submarine? sub = unit as Submarine;
            bool depthChanged = sub != null && sweep.SweepAltitude is float depth && Math.Abs(sub.DesiredAltitude.Value - depth) > 0.01f;
            string? outcome = depthChanged ? "Superseded · native depth order changed"
                : sub != null && (sub.ThreatingTorpedo != null || PlayerCombat.TorpedoInbound(sub)) ? "Aborted · inbound torpedo"
                : sweep.Sensor.Inoperable.Value ? "Aborted · sensor became unavailable"
                : sweep.EndAt is double end && now >= end ? "Completed · temporary sensor retracted or turned off"
                : sweep.EndAt == null && now >= sweep.Deadline ? "Blocked · sensor never became active" : null;
            if (outcome != null)
            {
                Restore(sweep);
                active.Remove(unit);
                report(unit, outcome);
                continue;
            }
            if (!sweep.Enabled && sub?.IsBelowPeriscopeDepth.Value != true) Enable(sweep);
            if (sweep.Enabled && sweep.EndAt == null && sweep.Sensor.IsOn.Value) sweep.EndAt = now + sweep.Duration;
        }
    }

    internal JArray Read() => new(active.Values.Select(sweep => new JObject { ["unitId"] = PlayerPicture.Id(sweep.Unit),
        ["sensorId"] = sweep.Sensor._systemName, ["kind"] = Kind(sweep.Sensor), ["until"] = sweep.EndAt, ["acquisitionDeadline"] = sweep.Deadline }));

    internal void CancelAll()
    {
        foreach (Sweep sweep in active.Values) if (sweep.Unit && !sweep.Unit.IsDestroyed) Restore(sweep);
        active.Clear();
    }

    private static void Enable(Sweep sweep)
    {
        sweep.Sensor.Enable(allowMessage: false);
        sweep.Enabled = true;
    }

    private static void Restore(Sweep sweep)
    {
        if (sweep.Enabled) sweep.Sensor.Disable();
        if (sweep.Unit is Submarine sub && sweep.PreviousDepth is int preset && sweep.SweepAltitude is float depth
            && Math.Abs(sub.DesiredAltitude.Value - depth) <= 0.01f)
        {
            sub.setPresetDepth(preset);
            if (sweep.OriginalAltitude is float original) sub.setDepth(-original);
        }
    }
}
