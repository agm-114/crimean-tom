using System.Diagnostics;
using SeaPower;
using SeaPower.GameBridge;
using SeapowerUI;

namespace OODA;

internal class DecisionClock
{
    private const int OpeningSpeed = 5;
    private const int CruiseSpeed = 10;
    private const int MaximumSpeed = 20;
    private static readonly int[] NativeSpeeds = [1, 2, 3, OpeningSpeed, CruiseSpeed, MaximumSpeed];
    private bool openingPauseUsed;
    private long openingPauseUntil;
    private int? requestedSpeed;
    private long requestedSpeedUntil;
    private float? lastManagedSpeed;

    internal static IReadOnlyList<int> Speeds => NativeSpeeds;
    internal bool Enabled { get; set; } = true;
    internal string Status => openingPauseUntil != 0 && GameTime.IsPaused() ? "Time paused for opening plan"
        : GameTime.IsPaused() ? "Time paused" : $"Time {GameTime.TimeCompression:0.##}×";

    internal static IEnumerable<int> Options(Taskforce side)
    {
        float limit = Math.Min(PlayerTime.Maximum, ContactLimit(side));
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

    internal void Tick(Taskforce? side, bool controlling, bool hasPlan = false, bool assessingAttack = false, int? interruptSpeed = null)
    {
        bool manage = controlling && Enabled && side != null;
        if (!manage || openingPauseUntil != 0 && Stopwatch.GetTimestamp() >= openingPauseUntil) Resume();
        if (GameTime.IsPaused() || PlayerTime.Blocked) return;
        YieldPause();
        if (manage) SetSpeed(assessingAttack ? 1 : Target(side!, hasPlan, interruptSpeed));
        else if (lastManagedSpeed is { } prior)
        {
            if (prior > 1 && GameTime.TimeCompression == prior) SetSpeed(1);
            lastManagedSpeed = null;
            requestedSpeed = null;
        }
    }

    internal bool Apply(int speed, int offeredMaximum, Taskforce side, bool assessingAttack, int? interruptSpeed)
    {
        if (!Enabled || GameTime.IsPaused() || PlayerTime.Blocked) return false;
        // Let automatic pacing recover as soon as a temporary contact limit clears.
        requestedSpeed = speed == offeredMaximum && offeredMaximum < CruiseSpeed ? null : speed;
        requestedSpeedUntil = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
        return SetSpeed(assessingAttack ? 1 : Target(side, hasPlan: true, interruptSpeed));
    }

    private int Target(Taskforce side, bool hasPlan, int? interruptSpeed)
    {
        int cruise = hasPlan ? CruiseSpeed : OpeningSpeed;
        int desired = Stopwatch.GetTimestamp() < requestedSpeedUntil ? requestedSpeed ?? cruise : cruise;
        return Math.Min(interruptSpeed ?? desired, ContactLimit(side));
    }

    private bool SetSpeed(int speed)
    {
        float target = PlayerTime.AtMost(speed);
        if (target <= 0 || GameTime.TimeCompression == target || !PlayerTime.TrySetSpeed(target)) return false;
        lastManagedSpeed = GameTime.TimeCompression;
        return true;
    }

    private static int ContactLimit(Taskforce side)
    {
        if (GameTime.RadioSequenceForcesNormalTime) return 1;
        if (side.PlottingTable?.Vehicles is not { } tracks) return MaximumSpeed;
        int limit = MaximumSpeed;
        GeoPosition[]? ownPositions = null;
        foreach (Vehicle track in tracks)
        {
            if (!track.BaseObject || track.UnitTaskforce.Value == side || !track.HasLiveTrack || !track.IsVisible()) continue;
            MapVisualType kind = track.GetMapType();
            bool weapon = kind is MapVisualType.Missile or MapVisualType.Torpedo;
            if (!weapon && track.CurrentRelationship() != RelationsState.Hostile) continue;
            if (track.PositionEstimate is not { } estimate)
            {
                if (weapon) return 1;
                limit = OpeningSpeed;
                continue;
            }

            ownPositions ??= PlayerPicture.Units(side).Select(unit => unit.getGeoPosition()).ToArray();
            if (ownPositions.Length == 0) break;
            (int close, int caution) = kind switch
            {
                MapVisualType.Missile => (30, 65),
                MapVisualType.Torpedo or MapVisualType.Submarine => (10, 25),
                MapVisualType.Air or MapVisualType.Helicopter => (35, 80),
                _ => (20, 45)
            };
            double distance = ownPositions.Min(position => GeoPosition.HaversineDistanceNmi(position, estimate.Item1));
            if (distance <= close) return 1;
            if (distance <= caution) limit = OpeningSpeed;
        }
        return limit;
    }

    internal void Reset()
    {
        openingPauseUsed = false;
        openingPauseUntil = requestedSpeedUntil = 0;
        requestedSpeed = null;
        lastManagedSpeed = null;
    }
}
