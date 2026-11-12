using System.Diagnostics;
using SeaPower;
using SeaPower.GameBridge;
using SeapowerUI;

namespace OODA;

internal class DecisionClock
{
    private const int OpeningSpeed = 5;
    private const int MaximumSpeed = 20;
    private static readonly int[] NativeSpeeds = [1, 2, 3, OpeningSpeed, 10, MaximumSpeed];
    private bool openingPauseUsed;
    private long openingPauseUntil;
    private int? requestedSpeed;
    private string? requestedReason;
    private long requestedSpeedUntil;
    private float? lastManagedSpeed;
    private string paceSource = "Cruise";
    private string paceReason = "Following the current plan.";

    internal static IReadOnlyList<int> Speeds => NativeSpeeds;
    internal bool Enabled { get; set; } = true;
    internal string Status => openingPauseUntil != 0 && GameTime.IsPaused() ? "Time paused for opening plan"
        : GameTime.IsPaused() ? "Time paused" : $"Time {GameTime.TimeCompression:0.##}×";

    internal (string Source, string Reason) Explain(bool managed)
    {
        if (GameTime.IsPaused())
        {
            if (openingPauseUntil != 0) return ("Opening plan", "OODA briefly paused to read the briefing.");
            if (PlayerTime.Available && Singleton<InputHandler>.Instance.IsInMessagePauseMode)
                return ("Mission message", "Sea Power pauses while the message is open.");
            return ("Sea Power", "The simulation is paused.");
        }
        return managed ? (paceSource, paceReason) : ("Game controls", "Automatic time control is off.");
    }

    internal static IEnumerable<int> Options(Taskforce side)
    {
        float limit = Math.Min(PlayerTime.Maximum, NormalTimeReason(side) == null ? MaximumSpeed : 1);
        return NativeSpeeds.Where(speed => speed <= limit);
    }

    internal void PauseForOpeningPlan()
    {
        if (openingPauseUsed || !Enabled || GameTime.RadioSequenceForcesNormalTime) return;
        openingPauseUsed = true;
        if (PlayerTime.TryPause()) openingPauseUntil = Stopwatch.GetTimestamp() + 4 * Stopwatch.Frequency;
    }

    internal void Resume()
    {
        if (openingPauseUntil != 0 && (!GameTime.IsPaused() || PlayerTime.TryResume())) YieldPause();
    }

    internal void YieldPause() => openingPauseUntil = 0;

    internal void Tick(Taskforce? side, bool controlling, bool hasPlan = false, ActiveTimeInterrupt? interrupt = null)
    {
        bool manage = controlling && Enabled && side != null;
        if (!manage || openingPauseUntil != 0 && Stopwatch.GetTimestamp() >= openingPauseUntil) Resume();
        if (GameTime.IsPaused() || PlayerTime.Blocked) return;
        YieldPause();
        if (manage) SetSpeed(Target(side!, hasPlan, interrupt));
        else
        {
            if (lastManagedSpeed is > 1 and { } prior && GameTime.TimeCompression == prior) SetSpeed(1);
            lastManagedSpeed = null;
            requestedSpeed = null;
            requestedReason = null;
        }
    }

    internal bool Apply(int speed, int offeredMaximum, string reason, Taskforce side, ActiveTimeInterrupt? interrupt)
    {
        if (!Enabled || GameTime.IsPaused() || PlayerTime.Blocked) return false;
        requestedSpeed = speed == offeredMaximum && offeredMaximum < MaximumSpeed ? null : speed;
        requestedReason = reason;
        requestedSpeedUntil = Stopwatch.GetTimestamp() + (speed == 1 ? 8 : 30) * Stopwatch.Frequency;
        return SetSpeed(Target(side, hasPlan: true, interrupt));
    }

    private int Target(Taskforce side, bool hasPlan, ActiveTimeInterrupt? interrupt)
    {
        if (NormalTimeReason(side) is { } limitReason)
        {
            (paceSource, paceReason) = ("Automatic limit", limitReason);
            return 1;
        }
        if (interrupt != null)
        {
            paceSource = interrupt.Custom ? "AI time rule" : "Default time rule";
            paceReason = $"{interrupt.Trigger.Label} · {interrupt.Trigger.Detail}";
            return interrupt.Rule.Speed;
        }
        if (requestedSpeed is { } desired && Stopwatch.GetTimestamp() < requestedSpeedUntil)
        {
            (paceSource, paceReason) = ("AI decision", string.IsNullOrWhiteSpace(requestedReason) ? "The AI selected this speed." : requestedReason);
            return desired;
        }
        (paceSource, paceReason) = hasPlan ? ("Cruise", "The plan is running with no active slowdown.")
            : ("Opening plan", "OODA is preparing the initial plan.");
        return hasPlan ? MaximumSpeed : OpeningSpeed;
    }

    private bool SetSpeed(int speed)
    {
        float target = PlayerTime.AtMost(speed);
        if (target > 0 && target < speed)
            (paceSource, paceReason) = ("Game speed limit", $"Sea Power limits the requested {speed}× to {target:0.##}×.");
        if (target <= 0 || GameTime.TimeCompression == target || !PlayerTime.TrySetSpeed(target)) return false;
        lastManagedSpeed = GameTime.TimeCompression;
        return true;
    }

    private static string? NormalTimeReason(Taskforce side)
    {
        if (GameTime.RadioSequenceForcesNormalTime) return "Radio traffic requires normal time.";
        if (side.PlottingTable?.Vehicles is not { } tracks) return null;
        GeoPosition[]? ownPositions = null;
        foreach (Vehicle track in tracks)
        {
            if (!PlayerPicture.IsLive(track) || track.BaseObject._taskforce == side) continue;
            MapVisualType kind = track.GetMapType();
            if (kind is not (MapVisualType.Missile or MapVisualType.Torpedo) || track.CurrentRelationship() == RelationsState.Friendly
                || track.PositionEstimate is not { } estimate) continue;

            ownPositions ??= PlayerPicture.Units(side).Select(unit => unit.getGeoPosition()).ToArray();
            if (ownPositions.Length == 0) break;
            int close = kind == MapVisualType.Missile ? 30 : 10;
            if (ownPositions.Any(position => GeoPosition.HaversineDistanceNmi(position, estimate.Item1) <= close))
                return $"A tracked {kind.ToString().ToLowerInvariant()} is within {close} nautical miles of the force.";
        }
        return null;
    }

    internal void Reset()
    {
        openingPauseUsed = false;
        openingPauseUntil = requestedSpeedUntil = 0;
        requestedSpeed = null;
        requestedReason = null;
        lastManagedSpeed = null;
        (paceSource, paceReason) = ("Opening plan", "OODA is preparing the initial plan.");
    }
}
