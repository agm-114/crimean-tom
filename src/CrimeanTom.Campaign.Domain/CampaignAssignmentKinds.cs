namespace CrimeanTom.Campaign.Domain;

public static class CampaignAssignmentKinds
{
    public const string OperationalSimBatchPrefix = "operational:";

    public static bool IsOperationalSim(AssignmentState assignment) =>
        IsOperationalSimBatch(assignment.BatchId);

    public static bool IsOperationalSimBatch(string? batchId) =>
        !string.IsNullOrEmpty(batchId)
        && batchId.StartsWith(OperationalSimBatchPrefix, StringComparison.Ordinal);

    public static string OperationalSimBatchId(string islandId) =>
        $"{OperationalSimBatchPrefix}{islandId}";
}
