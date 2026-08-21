namespace CrimeanTom.Campaign.ServiceHost;

public static class CampaignDurabilityPolicy
{
    public static CampaignDurabilityPolicyView Current { get; } = new(
        SchemaVersion: 1,
        DisplayPolicy: "display-first-provisional",
        CanonicalDurabilityBoundary: "campaign-journal-commit",
        TacticalOutcomeCommit: "typed-event-or-final-checkpoint",
        CrashRecovery: "discard-local-only-state-and-resume-from-last-committed-checkpoint",
        MaximumRollbackQuanta: 120,
        OrderedStages:
        [
            "native-runtime-effect",
            "companion-durable-proposal",
            "campaign-journal-commit",
        ],
        ProvisionalEffectFamilies:
        [
            "launch",
            "impact",
            "damage",
            "destruction",
            "ui",
            "audio",
            "visual",
        ],
        ContinuouslyProposedEffectFamilies:
        [
            "ordered-movement",
        ],
        CheckpointCommittedEffectFamilies:
        [
            "fuel",
            "stores",
            "damage",
            "destruction",
        ],
        PresentationTimestampCoverage: "native-world-and-player-event-observation; exact-ui-audio-visual-emission-unavailable",
        FailoverClaim: "cold-checkpoint-or-operational; hot-shadow-unclaimed",
        ReplicaHashRule: CampaignReplicaHashRegistry.MajorityRule,
        DeploymentTrustModel: "trusted-private-multiplayer; a-player-process-holding-full-opposing-truth-is-trusted",
        PlayerProjectionTransport: "http-coalition-filtered; signalr-not-canonical-commit",
        PackagedDeploymentProfile: "sqlite-wal-local-blobs; no-docker-no-external-db");
}

public sealed record CampaignDurabilityPolicyView(
    int SchemaVersion,
    string DisplayPolicy,
    string CanonicalDurabilityBoundary,
    string TacticalOutcomeCommit,
    string CrashRecovery,
    long MaximumRollbackQuanta,
    IReadOnlyList<string> OrderedStages,
    IReadOnlyList<string> ProvisionalEffectFamilies,
    IReadOnlyList<string> ContinuouslyProposedEffectFamilies,
    IReadOnlyList<string> CheckpointCommittedEffectFamilies,
    string PresentationTimestampCoverage,
    string FailoverClaim,
    string ReplicaHashRule,
    string DeploymentTrustModel,
    string PlayerProjectionTransport,
    string PackagedDeploymentProfile);
