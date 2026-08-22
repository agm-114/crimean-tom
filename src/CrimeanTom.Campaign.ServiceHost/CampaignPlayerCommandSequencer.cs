using System.Collections.Concurrent;

using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal sealed class CampaignPlayerCommandSequencer
{
    private const int MaximumCommandsPerCampaign = 4_096;
    private readonly ConcurrentDictionary<string, CommandLog> _logs = new(StringComparer.Ordinal);

    public PlayerCommandResult Submit(
        string campaignId,
        long campaignTimeQuanta,
        PlayerSessionView session,
        PlayerCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PredictionId) || request.PredictionId.Length > 128)
        {
            throw new CampaignStoreConflict(
                "invalid-prediction-id",
                "A player command requires a prediction ID of at most 128 characters.");
        }
        if (!string.Equals(session.CoalitionId, request.CoalitionId, StringComparison.Ordinal))
        {
            throw new CampaignStoreConflict("player-command-scope", "Player commands must stay inside the session coalition.");
        }

        var log = _logs.GetOrAdd(campaignId, _ => new CommandLog());
        lock (log.Gate)
        {
            var predictionKey = new PlayerPredictionKey(session.PlayerId, request.PredictionId);
            if (log.ByPredictionId.TryGetValue(predictionKey, out var existing))
            {
                return existing;
            }
            if (log.ByPredictionId.Count >= MaximumCommandsPerCampaign)
            {
                throw new CampaignStoreConflict(
                    "player-command-capacity",
                    "The bounded player command window is full for this campaign.");
            }

            if (request.Kind is not ("acknowledge" or "observe" or "transfer-control"))
            {
                var rejected = new PlayerCommandResult(
                    request.PredictionId,
                    Accepted: false,
                    Order: 0,
                    campaignTimeQuanta,
                    "unsupported-player-command",
                    "Only acknowledge, observe, and transfer-control envelopes are accepted; they never commit canonical outcomes.");
                log.ByPredictionId[predictionKey] = rejected;
                return rejected;
            }

            log.Sequence += 1;
            var accepted = new PlayerCommandResult(
                request.PredictionId,
                Accepted: true,
                log.Sequence,
                campaignTimeQuanta,
                "accepted",
                "Queued for filtered presentation; canonical campaign outcomes remain journal-committed events.");
            log.ByPredictionId[predictionKey] = accepted;
            log.Ordered.Add(accepted);
            return accepted;
        }
    }

    public IReadOnlyList<PlayerCommandResult> List(string campaignId)
    {
        if (!_logs.TryGetValue(campaignId, out var log))
        {
            return [];
        }
        lock (log.Gate)
        {
            return log.Ordered.ToArray();
        }
    }

    private sealed class CommandLog
    {
        public object Gate { get; } = new();
        public int Sequence { get; set; }
        public List<PlayerCommandResult> Ordered { get; } = [];
        public Dictionary<PlayerPredictionKey, PlayerCommandResult> ByPredictionId { get; } = [];
    }

    private readonly record struct PlayerPredictionKey(string PlayerId, string PredictionId);
}

public sealed record PlayerCommandRequest(
    string SessionId,
    string CoalitionId,
    string PredictionId,
    string Kind,
    int Generation);

public sealed record PlayerCommandResult(
    string PredictionId,
    bool Accepted,
    int Order,
    long EffectiveTickQuanta,
    string Status,
    string Detail);
