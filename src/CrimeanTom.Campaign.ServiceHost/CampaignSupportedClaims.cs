namespace CrimeanTom.Campaign.ServiceHost;

public static class CampaignSupportedClaims
{
    public static CampaignSupportedClaimsView Current { get; } = new(
        SchemaVersion: 14,
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
            "bearer-authenticated-loopback-companion-and-campaign-control-writes",
            "process-lifetime-browser-cookie-scoped-to-player-api-without-operator-authority",
            "http-content-type-cannot-bypass-bearer-authentication",
            "local-player-create-pause-resume-save-and-restart-restore",
            "full-4096-player-scenario-coalition-filtered-map-and-friendly-hierarchy",
            "durable-coalition-scoped-player-transit-and-hold-orders-with-idempotency-revision-and-restart-recovery",
            "player-selected-supported-group-tactical-entry-with-preflight-idempotency-and-atomic-activation",
            "live-atomic-two-ship-player-entry-with-campaign-display-identities",
            "live-campaign-owned-los-angeles-submarine-entry-with-parallel-campaign-advance",
            "live-atomic-two-helicopter-player-entry-at-altitude-with-parallel-campaign-advance",
            "deterministic-campaign-kernel-and-sqlite-wal-journal",
            "deterministic-two-coalition-strategic-and-operational-campaign-driver",
            "fixed-installation-complexes-remain-local-without-movement-orders",
            "coalition-intelligence-and-weapon-capability-aware-bounded-strategic-replanning",
            "durable-role-and-phase-coordinated-operational-packages",
            "coalition-filtered-projections-without-classified-truth",
            "render-proxy-cannot-receive-simulation-authority",
            "online-backup-restore-with-integrity-hashes",
            "operator-pause-and-drain",
            "heartbeat-renewed-tactical-ownership-while-unrelated-campaign-state-advances",
            "owned-surface-periodic-state-sync-during-unpaused-play",
            "coalition-map-projection-and-selected-visual-proxies-without-simulation-authority",
            "fresh-player-session-explicit-coalition-bound-friendly-projection",
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
            "campaign-owned-operational-worker-spawn-and-placement",
            "sea-power-physics-or-headless-unity-in-kernel",
            "existing-campaign-schema-or-algorithm-upgrade",
            "bounded-long-running-campaign-performance-envelope",
            "campaign-owned-hostile-tactical-loss-committed-to-journal",
            "player-patrol-escort-engage-withdraw-taskforce-policy-objective-progress-and-campaign-end-state",
            "complete-player-campaign-to-tactical-loop",
            "human-equivalent-adaptive-strategic-ai-deception-or-learned-opponent-response",
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
