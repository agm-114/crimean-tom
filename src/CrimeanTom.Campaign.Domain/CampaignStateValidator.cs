using CrimeanTom.Campaign.Contracts;

namespace CrimeanTom.Campaign.Domain;

public static class CampaignStateValidator
{
    public static void Validate(CampaignState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _ = CampaignState.Create(state.Metadata);
        if (state.Time.Quanta < 0 || state.Head.Value < 0 || state.CampaignRevision.Value != state.Head.Value)
        {
            throw Invalid("campaign-cursor", "Campaign time, head, or revision is invalid.");
        }

        foreach (var (key, region) in state.Regions)
        {
            if (key != region.RegionId || region.Revision.Value <= 0)
            {
                throw Invalid("region-identity", "A region key, identity, or revision is invalid.");
            }
            CampaignInvariants.RequireText(region.Name, nameof(region.Name));
            if (region.Neighbors.Any(id => id == key || !state.Regions.ContainsKey(id)))
            {
                throw Invalid("region-neighbor", "A region neighbor is missing or self-referential.");
            }
        }

        foreach (var (key, campaignBase) in state.Bases)
        {
            if (key != campaignBase.BaseId || campaignBase.Revision.Value <= 0
                || !state.Regions.ContainsKey(campaignBase.RegionId))
            {
                throw Invalid("base-identity", "A base key, revision, or region is invalid.");
            }
            CampaignInvariants.RequireId(campaignBase.CoalitionId);
            CampaignInvariants.RequireText(campaignBase.Name, nameof(campaignBase.Name));
            ValidateCoordinate(campaignBase.Coordinate);
            ValidateResources(campaignBase.Resources);
        }

        foreach (var (key, entity) in state.Entities)
        {
            if (key != entity.EntityId || entity.Revision.Value <= 0 || !state.Regions.ContainsKey(entity.RegionId)
                || !Enum.IsDefined(entity.Lifecycle) || !Enum.IsDefined(entity.Fidelity))
            {
                throw Invalid("entity-identity", "An entity key, revision, region, lifecycle, or fidelity is invalid.");
            }
            CampaignInvariants.RequireId(entity.CoalitionId);
            CampaignInvariants.RequireText(entity.Name, nameof(entity.Name));
            CampaignInvariants.RequireText(entity.CatalogUnitId, nameof(entity.CatalogUnitId));
            CampaignInvariants.RequireText(entity.ClassifiedTruthTag, nameof(entity.ClassifiedTruthTag));
            ValidateCoordinate(entity.Coordinate);
            ValidateResources(entity.Resources);
            if (entity.HomeBaseId is { } homeBaseId
                && (!state.Bases.TryGetValue(homeBaseId, out var homeBase) || homeBase.CoalitionId != entity.CoalitionId))
            {
                throw Invalid("entity-home-base", "An entity home base is missing or belongs to another coalition.");
            }
        }

        var groupedMembers = new HashSet<EntityId>();
        foreach (var (key, group) in state.Groups)
        {
            if (key != group.GroupId || group.Revision.Value <= 0 || group.Members.Count == 0)
            {
                throw Invalid("group-identity", "A group key, revision, or membership is invalid.");
            }
            CampaignInvariants.RequireId(group.CoalitionId);
            CampaignInvariants.RequireText(group.Name, nameof(group.Name));
            foreach (var memberId in group.Members)
            {
                if (!groupedMembers.Add(memberId)
                    || !state.Entities.TryGetValue(memberId, out var member)
                    || member.CoalitionId != group.CoalitionId)
                {
                    throw Invalid("group-member", "A group member is missing, duplicated, or belongs to another coalition.");
                }
            }
            if (group.ActiveOrderId is { } activeOrderId
                && (!state.Orders.TryGetValue(activeOrderId, out var activeOrder) || activeOrder.GroupId != group.GroupId))
            {
                throw Invalid("group-order", "A group's active order does not belong to that group.");
            }
        }

        foreach (var (key, order) in state.Orders)
        {
            if (key != order.OrderId || order.Revision.Value <= 0 || !state.Groups.ContainsKey(order.GroupId)
                || !state.Regions.ContainsKey(order.DestinationRegionId) || !Enum.IsDefined(order.Kind)
                || order.StartsAt.Quanta < 0
                || order.MustCompleteBy is { } deadline && deadline.Quanta < order.StartsAt.Quanta)
            {
                throw Invalid("order", "An order identity, relationship, time, kind, or revision is invalid.");
            }
        }

        foreach (var (key, estimate) in state.Intelligence)
        {
            if (key != estimate.Key || estimate.Revision.Value <= 0 || !state.Entities.ContainsKey(key.ContactEntityId)
                || !state.Regions.ContainsKey(estimate.EstimatedRegionId) || !Enum.IsDefined(estimate.Confidence)
                || estimate.ObservedAt.Quanta < 0 || estimate.ObservedAt.Quanta > state.Time.Quanta)
            {
                throw Invalid("intelligence", "An intelligence estimate is invalid.");
            }
            CampaignInvariants.RequireId(key.ObserverCoalitionId);
            ValidateCoordinate(estimate.EstimatedCoordinate);
        }

        foreach (var (key, weather) in state.Weather)
        {
            if (key != weather.RegionId || weather.Revision.Value <= 0 || !state.Regions.ContainsKey(key)
                || weather.SeaStateMilliUnits is < 0 or > 9_000)
            {
                throw Invalid("weather", "A weather record is invalid.");
            }
            _ = HeadingMilliDegrees.FromMilliDegrees(weather.WindDirection.Value);
            _ = SpeedMillimetersPerSecond.FromMillimetersPerSecond(weather.WindSpeed.Value);
            _ = DistanceMeters.FromMeters(weather.Visibility.Value);
        }

        var primaryEntities = new HashSet<EntityId>();
        foreach (var (key, assignment) in state.Assignments)
        {
            if (key != assignment.AssignmentId || assignment.Revision.Value <= 0
                || assignment.AuthorityEpoch.Value <= 0 || assignment.EffectiveAt.Quanta < 0
                || !state.Entities.ContainsKey(assignment.EntityId) || !Enum.IsDefined(assignment.Role))
            {
                throw Invalid("assignment", "An assignment identity, relationship, role, time, epoch, or revision is invalid.");
            }
            if (assignment.Role == AssignmentRole.Primary && !primaryEntities.Add(assignment.EntityId))
            {
                throw Invalid("duplicate-primary", "An entity has multiple primary assignments.");
            }
        }
    }

    private static void ValidateCoordinate(GeoCoordinate coordinate) =>
        _ = GeoCoordinate.FromMicrodegrees(coordinate.LatitudeMicrodegrees, coordinate.LongitudeMicrodegrees);

    private static void ValidateResources(IEnumerable<KeyValuePair<ResourceKind, ResourceAmount>> resources)
    {
        if (resources.Any(item => !Enum.IsDefined(item.Key) || item.Value.MilliUnits < 0))
        {
            throw Invalid("resource", "A resource kind or amount is invalid.");
        }
    }

    private static InvalidDataException Invalid(string code, string message) => new($"{code}: {message}");
}
