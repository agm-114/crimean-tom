using Newtonsoft.Json.Linq;

namespace OODA;

internal enum TimeEventKind { RadarContact, TrackAppeared, TrackChanged, TrackLost, HostileNearby, SurfaceAttack, AirRaidWarning, UnitStatusChanged, UnitDamaged, MissileHit }

internal record TimeEvent(TimeEventKind Kind, string? SubjectId, string Label, string Category, string Posture, string Detail,
    string? MissileId = null)
{
    internal JObject Describe() => new()
    {
        ["event"] = Kind.ToString(), ["subjectId"] = SubjectId, ["label"] = Label, ["category"] = Category,
        ["posture"] = Posture, ["detail"] = Detail, ["missileId"] = MissileId
    };
}

internal record TimeRule(TimeEventKind Kind, string? SubjectId, string? Category, string? Posture, int Speed, int HoldSeconds)
{
    internal bool Matches(TimeEvent item) => Kind == item.Kind
        && (SubjectId == null || SubjectId == item.SubjectId || SubjectId == item.MissileId)
        && (Category == null || Category == item.Category) && (Posture == null || Posture == item.Posture);

    internal JObject Describe() => new()
    {
        ["event"] = Kind.ToString(), ["subjectId"] = SubjectId, ["category"] = Category,
        ["posture"] = Posture, ["speed"] = Speed, ["holdSeconds"] = HoldSeconds
    };
}

internal record ActiveTimeInterrupt(TimeRule Rule, TimeEvent Trigger, bool Custom, double EndsAt);

internal class TimeInterrupts
{
    private static readonly TimeRule[] Defaults =
    [
        new(TimeEventKind.RadarContact, null, null, null, 5, 5),
        new(TimeEventKind.TrackChanged, null, null, "Hostile", 5, 6),
        new(TimeEventKind.HostileNearby, null, null, null, 5, 8),
        new(TimeEventKind.SurfaceAttack, null, null, null, 1, 8),
        new(TimeEventKind.AirRaidWarning, null, null, null, 2, 10),
        new(TimeEventKind.UnitDamaged, null, null, null, 3, 8),
        new(TimeEventKind.MissileHit, null, null, null, 1, 12)
    ];
    private TimeRule[] rules = [];
    private readonly Dictionary<TimeRule, ActiveTimeInterrupt> active = new();
    private readonly Queue<TimeEvent> recent = new();

    internal ActiveTimeInterrupt? Current => active.Values.OrderBy(item => item.Rule.Speed).ThenByDescending(item => item.EndsAt).FirstOrDefault();

    internal bool Replace(TimeRule[] next)
    {
        if (rules.SequenceEqual(next)) return false;
        rules = next;
        return true;
    }

    internal void Expire(double now)
    {
        foreach (TimeRule rule in active.Where(entry => entry.Value.EndsAt <= now).Select(entry => entry.Key).ToArray())
            active.Remove(rule);
    }

    internal TimeRule? Observe(TimeEvent item, double now, bool managed)
    {
        if (recent.Count == 16) recent.Dequeue();
        recent.Enqueue(item);
        if (!managed) return null;
        TimeRule? custom = rules.Where(rule => rule.Matches(item))
            .OrderBy(rule => rule.SubjectId == null).ThenBy(rule => rule.Speed).ThenByDescending(rule => rule.HoldSeconds)
            .FirstOrDefault();
        TimeRule? rule = custom ?? Defaults.FirstOrDefault(rule => rule.Matches(item));
        return rule != null && active.TryAdd(rule, new(rule, item, custom != null, now + rule.HoldSeconds)) ? rule : null;
    }

    internal JObject Describe() => new()
    {
        ["rules"] = new JArray(rules.Select(rule => rule.Describe())),
        ["defaults"] = new JArray(Defaults.Select(rule => rule.Describe())),
        ["active"] = new JArray(active.Values.Select(item =>
        {
            JObject rule = item.Rule.Describe();
            rule["trigger"] = item.Trigger.Describe();
            rule["custom"] = item.Custom;
            return rule;
        })),
        ["recentEvents"] = new JArray(recent.Select(item => item.Describe()))
    };

    internal void CancelActive() => active.Clear();

    internal void Reset()
    {
        rules = [];
        active.Clear();
        recent.Clear();
    }
}
