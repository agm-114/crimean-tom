namespace CrimeanTom.Protocol;

public readonly struct CampaignEntityRef
{
    public const string CanonicalPrefix = "unit:crimean-tom:";
    public const string StableUnitPrefix = "campaign-unit:";

    public CampaignEntityRef(Guid campaignId, Guid entityId)
    {
        CampaignId = campaignId;
        EntityId = entityId;
    }

    public Guid CampaignId { get; }

    public Guid EntityId { get; }

    public string Canonical => $"{CanonicalPrefix}{EntityId:N}";

    public string StableUnitId => $"{StableUnitPrefix}{Canonical}";

    public static bool TryParseCanonical(
        string campaignId,
        string value,
        out CampaignEntityRef entity)
    {
        entity = default;
        return Guid.TryParseExact(campaignId, "N", out var campaign)
            && value.StartsWith(CanonicalPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value[CanonicalPrefix.Length..], "N", out var entityId)
            && Assign(campaign, entityId, out entity);
    }

    public static bool TryParseStableUnitId(
        string campaignId,
        string value,
        out CampaignEntityRef entity)
    {
        entity = default;
        return value.StartsWith(StableUnitPrefix, StringComparison.Ordinal)
            && TryParseCanonical(campaignId, value[StableUnitPrefix.Length..], out entity);
    }

    public static bool TryCreate(
        string campaignId,
        string entityId,
        out CampaignEntityRef entity)
    {
        entity = default;
        return Guid.TryParseExact(campaignId, "N", out var campaign)
            && Guid.TryParseExact(entityId, "N", out var parsedEntity)
            && Assign(campaign, parsedEntity, out entity);
    }

    private static bool Assign(Guid campaignId, Guid entityId, out CampaignEntityRef entity)
    {
        entity = new CampaignEntityRef(campaignId, entityId);
        return true;
    }
}

public readonly struct CampaignContactRef
{
    public const string CanonicalPrefix = "contact:crimean-tom:";

    public CampaignContactRef(Guid campaignId, string trackId)
    {
        CampaignId = campaignId;
        TrackId = trackId;
    }

    public Guid CampaignId { get; }

    public string TrackId { get; }

    public string Canonical => $"{CanonicalPrefix}{TrackId}";

    public static bool TryParse(
        string campaignId,
        string value,
        out CampaignContactRef contact)
    {
        contact = default;
        if (!Guid.TryParseExact(campaignId, "N", out var campaign)
            || !value.StartsWith(CanonicalPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var trackId = value[CanonicalPrefix.Length..];
        if (trackId.Length is < 16 or > 64 || !trackId.All(Uri.IsHexDigit))
        {
            return false;
        }

        contact = new CampaignContactRef(campaign, trackId.ToLowerInvariant());
        return true;
    }
}
