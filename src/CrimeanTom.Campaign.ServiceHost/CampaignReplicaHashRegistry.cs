using System.Collections.Concurrent;

using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;
using CrimeanTom.Campaign.Service;

namespace CrimeanTom.Campaign.ServiceHost;

internal sealed class CampaignReplicaHashRegistry
{
    internal const string MajorityRule = "never-majority; exact-match-or-unsafe-divergence-or-pending";
    internal const string UnsafeDivergence = "unsafe-divergence";
    internal const string ExactMatch = "exact-match";
    internal const string PendingInsufficientSamples = "pending-insufficient-samples";

    private readonly ConcurrentDictionary<string, ReplicaCheckpointSample> _samples = new(StringComparer.Ordinal);

    public void Observe(
        CampaignId campaignId,
        AssignmentState assignment,
        string checkpointSha256,
        long entityRevision)
    {
        if (assignment.Role is AssignmentRole.RenderProxy
            || assignment.Lifecycle == AssignmentLifecycle.Released
            || string.IsNullOrWhiteSpace(checkpointSha256))
        {
            return;
        }

        var normalized = checkpointSha256.Trim().ToLowerInvariant();
        if (!IsHexSha256(normalized))
        {
            return;
        }

        _samples[Key(campaignId, assignment.AssignmentId)] = new ReplicaCheckpointSample(
            campaignId,
            assignment.EntityId,
            assignment.AssignmentId,
            assignment.Role.ToString(),
            assignment.WorkerClientInstanceId,
            normalized,
            assignment.AuthorityEpoch.Value,
            entityRevision);
    }

    public CampaignReplicaConvergenceReport Diagnose(CampaignState state)
    {
        PruneReleasedSamples(state);
        var entities = state.Assignments.Values
            .Where(item => item.Lifecycle != AssignmentLifecycle.Released
                && item.Role is not AssignmentRole.RenderProxy)
            .Select(item => item.EntityId)
            .Distinct()
            .OrderBy(item => item.Value)
            .Select(entityId => DiagnoseEntity(state, entityId))
            .ToArray();
        return new CampaignReplicaConvergenceReport(1, MajorityRule, entities);
    }

    public void ThrowIfUnsafeDivergence(CampaignState state, IEnumerable<EntityId> entityIds)
    {
        PruneReleasedSamples(state);
        foreach (var entityId in entityIds.Distinct())
        {
            var diagnosis = DiagnoseEntity(state, entityId);
            if (string.Equals(diagnosis.Diagnosis, UnsafeDivergence, StringComparison.Ordinal))
            {
                throw new CampaignStoreConflict(
                    "replica-hash-disagreement",
                    "Live replica checkpoint hashes disagree; diagnose and resync rather than claiming a majority.");
            }
        }
    }

    private CampaignReplicaEntityDiagnosis DiagnoseEntity(CampaignState state, EntityId entityId)
    {
        var live = state.Assignments.Values
            .Where(item => item.EntityId == entityId
                && item.Lifecycle != AssignmentLifecycle.Released
                && item.Role is AssignmentRole.Primary or AssignmentRole.Shadow or AssignmentRole.Observer)
            .ToArray();
        var samples = live
            .Select(item => _samples.TryGetValue(Key(state.Metadata.CampaignId, item.AssignmentId), out var sample)
                ? sample
                : null)
            .Where(sample => sample is not null)
            .Cast<ReplicaCheckpointSample>()
            .ToArray();
        var distinct = samples.Select(item => item.CheckpointSha256)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var sameRevisionDisagreement = samples
            .GroupBy(item => item.EntityRevision)
            .Any(group => group.Select(item => item.CheckpointSha256)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any());
        var exactMatch = live.Length >= 2
            && samples.Length == live.Length
            && samples.Select(item => item.EntityRevision).Distinct().Count() == 1
            && distinct.Length == 1;
        var diagnosis = sameRevisionDisagreement
            ? UnsafeDivergence
            : exactMatch ? ExactMatch : PendingInsufficientSamples;
        return new CampaignReplicaEntityDiagnosis(
            entityId.ToString(),
            diagnosis,
            samples.Length,
            distinct);
    }

    private void PruneReleasedSamples(CampaignState state)
    {
        var liveAssignmentIds = state.Assignments.Values
            .Where(item => item.Lifecycle != AssignmentLifecycle.Released)
            .Select(item => item.AssignmentId)
            .ToHashSet();
        foreach (var pair in _samples)
        {
            if (pair.Value.CampaignId == state.Metadata.CampaignId
                && !liveAssignmentIds.Contains(pair.Value.AssignmentId))
            {
                _samples.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string Key(CampaignId campaignId, AssignmentId assignmentId) =>
        $"{campaignId}:{assignmentId}";

    private static bool IsHexSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private sealed record ReplicaCheckpointSample(
        CampaignId CampaignId,
        EntityId EntityId,
        AssignmentId AssignmentId,
        string Role,
        string WorkerClientInstanceId,
        string CheckpointSha256,
        long AuthorityEpoch,
        long EntityRevision);
}

public sealed record CampaignReplicaConvergenceReport(
    int SchemaVersion,
    string MajorityRule,
    IReadOnlyList<CampaignReplicaEntityDiagnosis> Entities);

public sealed record CampaignReplicaEntityDiagnosis(
    string EntityId,
    string Diagnosis,
    int SampleCount,
    IReadOnlyList<string> DistinctCheckpointSha256);
