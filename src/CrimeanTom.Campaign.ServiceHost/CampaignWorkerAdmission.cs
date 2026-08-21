using CrimeanTom.Campaign.Contracts;
using CrimeanTom.Campaign.Domain;

namespace CrimeanTom.Campaign.ServiceHost;

internal readonly record struct CampaignWorkerAdmission(
    int OccupiedTacticalEntityCount,
    int PromotionCapacityLimit,
    int PromotionHeadroom,
    int MigrationOverlapHeadroom)
{
    public bool CanAdmitPromotion(int entityCount) =>
        entityCount >= 0 && entityCount <= PromotionHeadroom;

    public bool CanAdmitMigrationOverlap(int entityCount) =>
        entityCount > 0 && entityCount <= MigrationOverlapHeadroom;
}

internal static class CampaignWorkerAdmissionPolicy
{
    public static CampaignWorkerAdmission Calculate(
        CampaignState state,
        CampaignSessionSnapshot worker,
        int campaignSafeCapacity)
    {
        var assignedCount = state.Assignments.Values
            .Where(assignment => assignment.Lifecycle != AssignmentLifecycle.Released
                && string.Equals(
                    assignment.WorkerClientInstanceId,
                    worker.ClientInstanceId,
                    StringComparison.Ordinal))
            .Select(assignment => assignment.EntityId)
            .Distinct()
            .Count();
        return Calculate(worker, campaignSafeCapacity, Math.Max(
            worker.OwnedMaterializedEntityCount,
            assignedCount));
    }

    public static CampaignWorkerAdmission Calculate(
        CampaignSessionSnapshot worker,
        int campaignSafeCapacity,
        int occupiedTacticalEntityCount)
    {
        var capacity = Math.Max(0, worker.TacticalEntityCapacity);
        var reserve = Math.Clamp(worker.TacticalReserveCapacity, 0, capacity);
        var occupied = Math.Max(0, occupiedTacticalEntityCount);
        var promotionCapacityLimit = Math.Min(
            Math.Max(0, campaignSafeCapacity),
            capacity - reserve);
        var promotionHeadroom = Math.Max(0, promotionCapacityLimit - occupied);
        var migrationOverlapHeadroom = Math.Min(
            reserve,
            Math.Max(0, capacity - occupied));
        return new CampaignWorkerAdmission(
            occupied,
            promotionCapacityLimit,
            promotionHeadroom,
            migrationOverlapHeadroom);
    }
}
