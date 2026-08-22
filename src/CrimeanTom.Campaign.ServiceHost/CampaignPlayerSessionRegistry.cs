using System.Collections.Concurrent;
using System.Security.Cryptography;

using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal sealed class CampaignPlayerSessionRegistry
{
    private readonly ConcurrentDictionary<string, PlayerSession> _bySession = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byReconnectToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RateWindow> _issueWindows = new(StringComparer.Ordinal);

    public PlayerSessionView Issue(string campaignId, string playerId, string coalitionId)
    {
        RequireToken(campaignId, "campaignId");
        RequireToken(playerId, "playerId");
        RequireToken(coalitionId, "coalitionId");
        EnforceIssueRate(playerId);
        foreach (var prior in _bySession.Values
            .Where(item => string.Equals(item.CampaignId, campaignId, StringComparison.Ordinal)
                && string.Equals(item.PlayerId, playerId, StringComparison.Ordinal))
            .ToArray())
        {
            _bySession.TryRemove(prior.SessionId, out _);
            _byReconnectToken.TryRemove(prior.ReconnectToken, out _);
        }

        var session = new PlayerSession(
            Guid.NewGuid().ToString("N"),
            campaignId,
            playerId,
            coalitionId,
            CreateToken(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Generation: 1);
        _bySession[session.SessionId] = session;
        _byReconnectToken[session.ReconnectToken] = session.SessionId;
        return ToView(session);
    }

    public PlayerSessionView Reconnect(string reconnectToken)
    {
        if (string.IsNullOrWhiteSpace(reconnectToken)
            || !_byReconnectToken.TryRemove(reconnectToken, out var sessionId)
            || !_bySession.TryGetValue(sessionId, out var current))
        {
            throw new CampaignStoreConflict("player-session-unknown", "The reconnect token is unknown or already replaced.");
        }

        var replacement = current with
        {
            ReconnectToken = CreateToken(),
            LastSeenAt = DateTimeOffset.UtcNow,
            Generation = current.Generation + 1,
        };
        _bySession[sessionId] = replacement;
        _byReconnectToken[replacement.ReconnectToken] = sessionId;
        return ToView(replacement);
    }

    public PlayerSessionView Require(string sessionId, string campaignId, string coalitionId)
    {
        if (!_bySession.TryGetValue(sessionId, out var session)
            || !string.Equals(session.CampaignId, campaignId, StringComparison.Ordinal)
            || !string.Equals(session.CoalitionId, coalitionId, StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict("player-session-mismatch", "The player session does not match the campaign coalition.");
        }

        var updated = session with { LastSeenAt = DateTimeOffset.UtcNow };
        _bySession[sessionId] = updated;
        return ToView(updated);
    }

    private void EnforceIssueRate(string playerId)
    {
        var now = DateTimeOffset.UtcNow;
        var window = _issueWindows.AddOrUpdate(
            playerId,
            _ => new RateWindow(now, 1),
            (_, existing) => now - existing.StartedAt > TimeSpan.FromMinutes(1)
                ? new RateWindow(now, 1)
                : existing with { Count = existing.Count + 1 });
        if (window.Count > 30)
        {
            throw new CampaignStoreConflict("player-session-rate-limited", "Player session issuance is rate limited.");
        }
    }

    private static void RequireToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CampaignStoreConflict("invalid-player-session", $"A {name} is required.");
        }
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static PlayerSessionView ToView(PlayerSession session) => new(
        session.SessionId,
        session.CampaignId,
        session.PlayerId,
        session.CoalitionId,
        session.ReconnectToken,
        session.Generation,
        session.LastSeenAt);

    private sealed record PlayerSession(
        string SessionId,
        string CampaignId,
        string PlayerId,
        string CoalitionId,
        string ReconnectToken,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastSeenAt,
        int Generation);

    private sealed record RateWindow(DateTimeOffset StartedAt, int Count);
}

public sealed record PlayerSessionView(
    string SessionId,
    string CampaignId,
    string PlayerId,
    string CoalitionId,
    string ReconnectToken,
    int Generation,
    DateTimeOffset LastSeenAt);

public sealed record PlayerSessionIssueRequest(string CampaignId, string PlayerId, string CoalitionId);

public sealed record PlayerSessionReconnectRequest(string ReconnectToken);
