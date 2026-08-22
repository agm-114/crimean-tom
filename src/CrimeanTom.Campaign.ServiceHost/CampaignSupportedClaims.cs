namespace CrimeanTom.Campaign.ServiceHost;

public static class CampaignSupportedClaims
{
    public static CampaignSupportedClaimsView Current { get; } = new(
        SchemaVersion: 1,
        GameBuild: "0.8.2-23444+s358",
        TrustModel: CampaignDurabilityPolicy.Current.DeploymentTrustModel,
        FailoverClaim: CampaignDurabilityPolicy.Current.FailoverClaim,
        ReplicaHashRule: CampaignDurabilityPolicy.Current.ReplicaHashRule,
        PackagedDeploymentProfile: CampaignDurabilityPolicy.Current.PackagedDeploymentProfile,
        PlayerProjectionTransport: CampaignDurabilityPolicy.Current.PlayerProjectionTransport,
        CompatibilityPolicy: "assignment-preparation-and-activation-fail-closed-on-pinned-fingerprint-mismatch",
        CanonicalDurabilityBoundary: CampaignDurabilityPolicy.Current.CanonicalDurabilityBoundary,
        Supported:
        [
            "loopback-authenticated-companion-and-campaign-http",
            "deterministic-campaign-kernel-and-sqlite-wal-journal",
            "coalition-filtered-projections-without-classified-truth",
            "replica-checkpoint-hash-disagreement-refuses-majority",
            "render-proxy-cannot-receive-simulation-authority",
            "observer-is-never-a-primary-activation-source",
            "online-backup-restore-with-integrity-hashes",
            "operator-pause-and-drain",
            "campaign-owned-operational-worker-spawn-and-placement-with-in-process-ticks",
            "unattended-kernel-simulation-without-sea-power-or-client",
        ],
        Unsupported:
        [
            "hot-shadow-failover",
            "untrusted-public-multiplayer",
            "signalr-canonical-commits",
            "authenticated-two-container-live-sea-power-migration",
            "hostile-client-adjudication",
            "postgresql-or-external-object-storage-requirement",
            "out-of-process-operational-tick-execution",
            "sea-power-physics-or-headless-unity-in-kernel",
        ]);
}

public sealed record CampaignSupportedClaimsView(
    int SchemaVersion,
    string GameBuild,
    string TrustModel,
    string FailoverClaim,
    string ReplicaHashRule,
    string PackagedDeploymentProfile,
    string PlayerProjectionTransport,
    string CompatibilityPolicy,
    string CanonicalDurabilityBoundary,
    IReadOnlyList<string> Supported,
    IReadOnlyList<string> Unsupported);
