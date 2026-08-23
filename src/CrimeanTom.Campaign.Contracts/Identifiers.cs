using System.Globalization;

namespace CrimeanTom.Campaign.Contracts;

public interface ICampaignId
{
    Guid Value { get; }
}

public readonly record struct CampaignId(Guid Value) : ICampaignId, IComparable<CampaignId>
{
    public int CompareTo(CampaignId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct CoalitionId(Guid Value) : ICampaignId, IComparable<CoalitionId>
{
    public int CompareTo(CoalitionId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct PlayerId(Guid Value) : ICampaignId, IComparable<PlayerId>
{
    public int CompareTo(PlayerId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct EntityId(Guid Value) : ICampaignId, IComparable<EntityId>
{
    public int CompareTo(EntityId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct GroupId(Guid Value) : ICampaignId, IComparable<GroupId>
{
    public int CompareTo(GroupId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct BaseId(Guid Value) : ICampaignId, IComparable<BaseId>
{
    public int CompareTo(BaseId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct RegionId(Guid Value) : ICampaignId, IComparable<RegionId>
{
    public int CompareTo(RegionId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct OrderId(Guid Value) : ICampaignId, IComparable<OrderId>
{
    public int CompareTo(OrderId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct StrategicObjectiveId(Guid Value) : ICampaignId, IComparable<StrategicObjectiveId>
{
    public int CompareTo(StrategicObjectiveId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct AssignmentId(Guid Value) : ICampaignId, IComparable<AssignmentId>
{
    public int CompareTo(AssignmentId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct ScheduledActivityId(Guid Value) : ICampaignId, IComparable<ScheduledActivityId>
{
    public int CompareTo(ScheduledActivityId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct CommandId(Guid Value) : ICampaignId, IComparable<CommandId>
{
    public int CompareTo(CommandId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct EventId(Guid Value) : ICampaignId, IComparable<EventId>
{
    public int CompareTo(EventId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct CoordinatorId(Guid Value) : ICampaignId, IComparable<CoordinatorId>
{
    public int CompareTo(CoordinatorId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct IdempotencyKey(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct WriterEpoch(long Value)
{
    public static readonly WriterEpoch None = new(0);
}

public readonly record struct AuthorityEpoch(long Value)
{
    public static readonly AuthorityEpoch None = new(0);
}

public readonly record struct EntityRevision(long Value)
{
    public static readonly EntityRevision None = new(0);
    public EntityRevision Next() => new(checked(Value + 1));
}

public readonly record struct CommitPosition(long Value)
{
    public static readonly CommitPosition Origin = new(0);
    public CommitPosition Next() => new(checked(Value + 1));
}
