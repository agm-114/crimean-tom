using CrimeanTom.Protocol;

namespace CrimeanTom.Companion;

internal static class CompanionWorkerHealthClassifier
{
    public static (string Health, string Eligibility) Classify(
        CapabilitiesPayload capabilities,
        OwnedSurfaceResourceStatePayload resources,
        int tacticalEntityCapacity,
        int tacticalReserveCapacity)
    {
        if (!string.Equals(capabilities.ExecutionThreadGuardSelfTest, "passed", StringComparison.Ordinal))
        {
            return ("Quarantined", "Quarantined");
        }

        if (string.IsNullOrWhiteSpace(capabilities.RuntimeFingerprint.FingerprintSha256)
            || string.IsNullOrWhiteSpace(capabilities.RuntimeFingerprint.CatalogSha256))
        {
            return ("Offline", "Disabled");
        }

        if (resources.RecoveryRequiredCount > 0)
        {
            return ("Overloaded", "DrainOnly");
        }

        var capacity = Math.Max(0, tacticalEntityCapacity);
        var reserve = Math.Clamp(tacticalReserveCapacity, 0, capacity);
        var occupied = Math.Max(
            Math.Max(0, resources.RetainedConstructedObjectCount),
            Math.Max(0, resources.ActiveObjectCount) + Math.Max(0, resources.SuspendedRetainedObjectCount));
        if (capacity > 0 && occupied > capacity - reserve)
        {
            return ("Pressured", "Eligible");
        }

        return ("Healthy", "Eligible");
    }
}
