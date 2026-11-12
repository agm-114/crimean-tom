using Newtonsoft.Json.Linq;

namespace OODA;

internal enum TimeEventKind { RadarContact, TrackAppeared, TrackChanged, TrackLost, UnitStatusChanged, UnitDamaged, MissileHit }

internal record TimeEvent(TimeEventKind Kind, string SubjectId, string Label, string Category, string Posture, string Detail,
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

internal class TimeInterrupts
{
    private static readonly TimeRule[] Defaults =
    [
        new(TimeEventKind.RadarContact, null, null, null, 5, 5),
        new(TimeEventKind.TrackChanged, null, null, "Hostile", 5, 6),
        new(TimeEventKind.UnitDamaged, null, null, null, 1, 10),
        new(TimeEventKind.MissileHit, null, null, null, 1, 12)
    ];
    private TimeRule[] rules = [];
    private readonly Dictionary<TimeRule, double> active = new();
    private readonly Queue<TimeEvent> recent = new();

    internal int? Speed => active.Count == 0 ? null : active.Keys.Min(rule => rule.Speed);

    internal bool Replace(TimeRule[] next)
    {
        if (rules.SequenceEqual(next)) return false;
        rules = next;
        return true;
    }

    internal void Expire(double now)
    {
        foreach (TimeRule rule in active.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
            active.Remove(rule);
    }

    internal TimeRule? Observe(TimeEvent item, double now, bool managed)
    {
        if (recent.Count == 16) recent.Dequeue();
        recent.Enqueue(item);
        if (!managed) return null;
        TimeRule? rule = rules.Where(rule => rule.Matches(item))
            .OrderBy(rule => rule.SubjectId == null).ThenBy(rule => rule.Speed).ThenByDescending(rule => rule.HoldSeconds)
            .FirstOrDefault() ?? Defaults.FirstOrDefault(rule => rule.Matches(item));
        if (rule == null) return null;
        bool alreadyActive = active.ContainsKey(rule);
        active[rule] = now + rule.HoldSeconds;
        return alreadyActive ? null : rule;
    }

    internal JObject Describe() => new()
    {
        ["rules"] = new JArray(rules.Select(rule => rule.Describe())),
        ["defaults"] = new JArray(Defaults.Select(rule => rule.Describe())),
        ["active"] = new JArray(active.Keys.Select(rule => rule.Describe())),
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
