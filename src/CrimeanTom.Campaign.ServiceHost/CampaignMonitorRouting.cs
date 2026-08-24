using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

public static class CampaignMonitorRouting
{
    public static string? ResolveActiveCampaignId(
        IReadOnlyList<CampaignSummary> campaigns,
        IReadOnlyList<CampaignSessionSnapshot> sessions)
    {
        if (campaigns.Count == 0)
        {
            return null;
        }

        var connectedAtByCampaign = sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.CampaignId))
            .GroupBy(session => session.CampaignId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Max(session => session.ConnectedAt),
                StringComparer.Ordinal);

        return campaigns
            .OrderBy(campaign => connectedAtByCampaign.ContainsKey(campaign.CampaignId) ? 0 : 1)
            .ThenByDescending(campaign => connectedAtByCampaign.GetValueOrDefault(campaign.CampaignId))
            .ThenByDescending(campaign => campaign.CommitPosition)
            .ThenBy(campaign => campaign.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(campaign => campaign.CampaignId, StringComparer.Ordinal)
            .Select(campaign => campaign.CampaignId)
            .First();
    }

    public static bool MatchesActiveCampaign(string? requestedCampaignId, string activeCampaignId) =>
        !string.IsNullOrWhiteSpace(requestedCampaignId)
        && string.Equals(requestedCampaignId, activeCampaignId, StringComparison.OrdinalIgnoreCase);

    public static bool IsMonitorEntryPath(string? requestPath) =>
        requestPath is "/monitor" or "/monitor/" or "/monitor/index.html";

    public static bool IsExistingMonitorAsset(string webRoot, string? requestPath)
    {
        if (string.IsNullOrEmpty(requestPath) || IsMonitorEntryPath(requestPath))
        {
            return false;
        }

        var relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(webRoot);
        var physical = Path.GetFullPath(Path.Combine(webRoot, relative));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return physical.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && File.Exists(physical);
    }

    public static bool IsSameOriginScheduleMutation(HttpRequest request, Uri serviceUri)
    {
        if (!HttpMethods.IsPost(request.Method)
            || !request.Path.StartsWithSegments("/control/campaigns", out var remaining))
        {
            return false;
        }

        var segments = remaining.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        if (segments.Length != 2
            || !Guid.TryParseExact(segments[0], "N", out _)
            || !segments[1].Equals("schedule", StringComparison.Ordinal))
        {
            return false;
        }

        var expectedOrigin = serviceUri.GetLeftPart(UriPartial.Authority);
        return request.Headers.Origin.Count == 1
            && string.Equals(request.Headers.Origin[0], expectedOrigin, StringComparison.OrdinalIgnoreCase)
            && request.Headers["Sec-Fetch-Site"].Count == 1
            && string.Equals(request.Headers["Sec-Fetch-Site"][0], "same-origin", StringComparison.OrdinalIgnoreCase)
            && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string MonitorLocation(string campaignId) => $"/monitor/?campaign={campaignId}";
}
