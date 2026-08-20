using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using CrimeanTom.Protocol;

namespace CrimeanTom.Companion;

public sealed class PortOperationsProjectionStore
{
    private const string CurrentForceStartMarker = "; BEGIN CRIMEAN TOM GENERATED CURRENT FORCE";
    private const string CurrentForceEndMarker = "; END CRIMEAN TOM GENERATED CURRENT FORCE";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _projectionPath;
    private readonly string _rosterPath;
    private readonly string _campaignPath;

    public PortOperationsProjectionStore(IConfiguration configuration)
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        _projectionPath = Path.GetFullPath(configuration["PortOperationsProjectionFile"]
            ?? Path.Combine(
                repositoryRoot,
                "content",
                "campaigns",
                "crimean-tom-port-operations",
                "port-state.projection.json"));
        _rosterPath = Path.GetFullPath(configuration["PortOperationsRosterFile"]
            ?? Path.Combine(
                repositoryRoot,
                "content",
                "campaigns",
                "crimean-tom-port-operations",
                "player_task_force_roster.generated.ini"));
        _campaignPath = Path.GetFullPath(configuration["PortOperationsCampaignFile"]
            ?? Path.Combine(
                repositoryRoot,
                "content",
                "campaigns",
                "crimean-tom-port-operations",
                "campaign.ini"));
    }

    public PortOperationsProjection Load()
    {
        var projection = JsonSerializer.Deserialize<PortOperationsProjection>(
            File.ReadAllText(_projectionPath),
            JsonOptions) ?? throw new InvalidDataException("Port projection is empty.");
        ValidateProjection(projection);
        return projection;
    }

    public async Task<PortProjectionMaterializationResult> MaterializeAsync(
        CancellationToken cancellationToken)
    {
        var projection = Load();
        var roster = GenerateRoster(projection);
        var campaign = ReplaceGeneratedCurrentForce(
            File.ReadAllText(_campaignPath),
            GenerateCurrentForce(projection));
        Directory.CreateDirectory(Path.GetDirectoryName(_rosterPath)!);
        var temporaryPath = $"{_rosterPath}.{Guid.NewGuid():N}.tmp";
        var campaignTemporaryPath = $"{_campaignPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                roster,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await File.WriteAllTextAsync(
                campaignTemporaryPath,
                campaign,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, _rosterPath, overwrite: true);
            File.Move(campaignTemporaryPath, _campaignPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            if (File.Exists(campaignTemporaryPath))
            {
                File.Delete(campaignTemporaryPath);
            }
        }

        return new PortProjectionMaterializationResult
        {
            ProjectionId = projection.ProjectionId,
            CampaignRevision = projection.CampaignRevision,
            RosterFile = Path.GetFileName(_rosterPath),
            RosterSha256 = ComputeSha256(roster),
            CampaignFile = Path.GetFileName(_campaignPath),
            CampaignSha256 = ComputeSha256(campaign),
            RequiresGameRestart = true,
            Authority = "external-campaign-service",
        };
    }

    public PortMaintenanceDraftValidation ValidateDraft(
        PortMaintenanceDraftImportRequest request,
        CampaignManagementStatePayload nativeDraft)
    {
        var projection = Load();
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!string.Equals(request.ProjectionId, projection.ProjectionId, StringComparison.Ordinal))
        {
            errors.Add("projection-id-mismatch");
        }
        if (request.ExpectedCampaignRevision != projection.CampaignRevision)
        {
            errors.Add("campaign-revision-mismatch");
        }
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length is < 8 or > 128)
        {
            errors.Add("invalid-idempotency-key");
        }
        if (!nativeDraft.IsTaskForceModeCampaign || !nativeDraft.TaskForceBuilderLoaded)
        {
            errors.Add("native-maintenance-draft-not-loaded");
        }
        var draftUnits = nativeDraft.PurchasedUnits
            .Concat(nativeDraft.PendingUnits)
            .ToArray();

        var inventory = projection.EligibleUnits
            .Concat(projection.Reinforcements)
            .GroupBy(item => item.UnitType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    Maximum = group.Sum(item => item.MaximumQuantity),
                    Variants = group.SelectMany(item => item.Variants)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                },
                StringComparer.OrdinalIgnoreCase);
        foreach (var group in draftUnits.GroupBy(
                     item => item.UnitType,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!inventory.TryGetValue(group.Key, out var available))
            {
                errors.Add($"unit-unavailable:{group.Key}");
                continue;
            }
            if (group.Sum(item => Math.Max(0, item.Quantity)) > available.Maximum)
            {
                errors.Add($"quantity-exceeds-availability:{group.Key}");
            }
            foreach (var item in group)
            {
                if (!string.IsNullOrWhiteSpace(item.VariantReference)
                    && !available.Variants.Contains(item.VariantReference))
                {
                    errors.Add($"variant-unavailable:{group.Key}:{item.VariantReference}");
                }
            }
        }

        var totalQuantity = draftUnits.Sum(item => Math.Max(0, item.Quantity));
        var totalCost = draftUnits.Sum(item =>
            Math.Max(0, item.Quantity) * Math.Max(0, item.Cost));
        if (totalQuantity > projection.PortCapacity)
        {
            errors.Add("port-capacity-exceeded");
        }
        if (totalCost > projection.PointBudget)
        {
            errors.Add("point-budget-exceeded");
        }
        if (nativeDraft.PointCap != 0 && nativeDraft.PointCap != projection.PointBudget)
        {
            errors.Add("native-point-cap-does-not-match-projection");
        }

        if (!string.Equals(nativeDraft.NativeMaintenanceState, "ready", StringComparison.Ordinal))
        {
            warnings.Add($"native-maintenance-state:{nativeDraft.NativeMaintenanceState}");
        }
        foreach (var unit in nativeDraft.NativeMaintenanceUnits.Where(unit =>
                     !string.Equals(
                         unit.DamageSummary,
                         "No Damage",
                         StringComparison.OrdinalIgnoreCase)
                     && !unit.CanRepair))
        {
            warnings.Add($"native-repair-unavailable:{unit.InstanceId}");
        }
        if (!nativeDraft.NativeRearmAvailable)
        {
            warnings.Add("native-rearm-unavailable-for-selected-mission");
        }
        var normalized = draftUnits
            .OrderBy(item => item.UnitType, StringComparer.Ordinal)
            .ThenBy(item => item.VariantReference, StringComparer.Ordinal)
            .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();
        var draftSlots = normalized
            .Select((item, index) => new
            {
                item.InstanceId,
                Slot = $"draft-unit:{index}",
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.InstanceId))
            .ToDictionary(item => item.InstanceId, item => item.Slot, StringComparer.Ordinal);
        var projectedUnitIds = projection.EligibleUnits
            .Select(item => item.ExternalUnitId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var assignmentTarget in normalized
                     .SelectMany(item => item.AircraftAssignments.Keys)
                     .Distinct(StringComparer.Ordinal))
        {
            if (!draftSlots.ContainsKey(assignmentTarget)
                && !projectedUnitIds.Contains(assignmentTarget))
            {
                errors.Add("aircraft-assignment-target-not-in-projection");
            }
        }
        var transactionDocument = JsonSerializer.Serialize(new
        {
            projection.ProjectionId,
            projection.CampaignRevision,
            request.IdempotencyKey,
            Units = normalized.Select((item, index) => new
            {
                Slot = $"draft-unit:{index}",
                item.UnitFolder,
                item.UnitType,
                item.Category,
                item.VariantReference,
                item.SelectedLoadoutVariant,
                item.Quantity,
                item.Cost,
                AircraftAssignments = item.AircraftAssignments
                    .OrderBy(assignment => assignment.Key, StringComparer.Ordinal)
                    .Select(assignment => new
                    {
                        Target = draftSlots.TryGetValue(assignment.Key, out var slot)
                            ? slot
                            : $"external-unit:{assignment.Key}",
                        assignment.Value,
                    }),
            }),
        }, JsonOptions);

        return new PortMaintenanceDraftValidation
        {
            ProjectionId = projection.ProjectionId,
            ExpectedCampaignRevision = request.ExpectedCampaignRevision,
            IdempotencyKey = request.IdempotencyKey,
            Status = errors.Count == 0 ? "valid-uncommitted" : "rejected-uncommitted",
            Committable = errors.Count == 0,
            Committed = false,
            Authority = "external-campaign-service",
            TotalCost = totalCost,
            RemainingBudget = projection.PointBudget - totalCost,
            TotalQuantity = totalQuantity,
            PortCapacity = projection.PortCapacity,
            NativeMaintenanceState = nativeDraft.NativeMaintenanceState,
            NativeRearmAvailable = nativeDraft.NativeRearmAvailable,
            NativeDamagedUnitCount = nativeDraft.NativeDamagedUnitCount,
            NativeRepairCandidateCount = nativeDraft.NativeRepairCandidateCount,
            NativeMaintenanceUnits = nativeDraft.NativeMaintenanceUnits,
            TransactionSha256 = ComputeSha256(transactionDocument),
            Errors = errors.ToArray(),
            Warnings = warnings.ToArray(),
            DraftUnits = normalized,
        };
    }

    private static string GenerateRoster(PortOperationsProjection projection)
    {
        var lines = new List<string>
        {
            "; Generated from port-state.projection.json. Do not treat this file as authority.",
            $"; ProjectionId={projection.ProjectionId}",
            $"; CampaignRevision={projection.CampaignRevision}",
            "; A controlled Sea Power restart is required after this projection changes.",
            string.Empty,
        };
        foreach (var section in projection.EligibleUnits
                     .Concat(projection.Reinforcements)
                     .GroupBy(item => item.RosterSection, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            lines.Add($"[{section.Key}]");
            foreach (var unit in section
                         .GroupBy(item => item.UnitType, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                lines.Add($"{unit.Key}={string.Join(',', unit.SelectMany(item => item.Variants).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))}");
            }
            lines.Add(string.Empty);
        }
        lines.Add("[LoadoutPrices]");
        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static string GenerateCurrentForce(PortOperationsProjection projection)
    {
        var lines = new List<string>
        {
            CurrentForceStartMarker,
            "; Generated from the external port projection. Native state is disposable.",
            $"; ProjectionId={projection.ProjectionId}",
            $"; CampaignRevision={projection.CampaignRevision}",
            "[ProjectedCurrentTaskForce]",
            "TaskForceName=Crimean Tom Port Detachment",
            $"NumberOfUnits={projection.EligibleUnits.Length}",
            $"AvailablePoints={projection.PointBudget}",
            $"CapPoints={projection.PointBudget}",
            string.Empty,
        };
        for (var index = 0; index < projection.EligibleUnits.Length; index++)
        {
            var unit = projection.EligibleUnits[index];
            var category = string.Equals(
                unit.RosterSection,
                "AllowedSubmarines",
                StringComparison.OrdinalIgnoreCase)
                    ? "Submarine"
                    : string.Equals(
                        unit.RosterSection,
                        "AllowedAircraft",
                        StringComparison.OrdinalIgnoreCase)
                        ? "Aircraft"
                        : string.Equals(
                            unit.RosterSection,
                            "AllowedHelicopters",
                            StringComparison.OrdinalIgnoreCase)
                            ? "Helicopter"
                            : "Ship";
            lines.Add($"[ProjectedCurrentTaskForce_Unit{index + 1}]");
            lines.Add($"Type={unit.UnitType}");
            lines.Add($"Folder={unit.UnitFolder}");
            lines.Add($"Category={category}");
            lines.Add($"InstanceId={unit.ExternalUnitId}");
            lines.Add($"VariantReference={unit.Variants.FirstOrDefault() ?? "Default"}");
            lines.Add("LoadoutVariant=Default");
            lines.Add($"Cost={unit.PointCost}");
            lines.Add($"CostPerUnit={unit.PointCost}");
            lines.Add($"BaseCost={unit.PointCost}");
            lines.Add($"BaseCostPerUnit={unit.PointCost}");
            lines.Add("QuantityPurchased=1");
            lines.Add("IsFlagshipEligible=False");
            lines.Add("CrewSkillSurvivalCredits=0");
            lines.Add("CrewSkill=Trained");
            lines.Add(string.Empty);
        }
        lines.Add(CurrentForceEndMarker);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ReplaceGeneratedCurrentForce(string campaign, string generated)
    {
        var start = campaign.IndexOf(CurrentForceStartMarker, StringComparison.Ordinal);
        var end = campaign.IndexOf(CurrentForceEndMarker, StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidDataException("Campaign generated-current-force markers are missing.");
        }
        end += CurrentForceEndMarker.Length;
        return campaign[..start] + generated + campaign[end..];
    }

    private static void ValidateProjection(PortOperationsProjection projection)
    {
        if (projection.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported port projection schema {projection.SchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(projection.ProjectionId)
            || projection.CampaignRevision < 0
            || projection.PointBudget < 0
            || projection.PortCapacity < 1)
        {
            throw new InvalidDataException("Port projection identity, revision, budget, or capacity is invalid.");
        }
        var units = projection.EligibleUnits.Concat(projection.Reinforcements).ToArray();
        if (units.Any(item => string.IsNullOrWhiteSpace(item.ExternalUnitId)
            || string.IsNullOrWhiteSpace(item.UnitType)
            || string.IsNullOrWhiteSpace(item.RosterSection)
            || item.MaximumQuantity < 1
            || item.Variants.Length == 0))
        {
            throw new InvalidDataException("A projected port unit is incomplete.");
        }
        if (units.GroupBy(item => item.ExternalUnitId, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Projected port unit identities must be unique.");
        }
    }

    private static string FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrimeanTom.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Crimean Tom repository root was not found.");
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class PortOperationsProjection
{
    public int SchemaVersion { get; set; } = 1;
    public string ProjectionId { get; set; } = string.Empty;
    public long CampaignRevision { get; set; }
    public string PortId { get; set; } = string.Empty;
    public string ForceId { get; set; } = string.Empty;
    public int PointBudget { get; set; }
    public int PortCapacity { get; set; }
    public string Authority { get; set; } = "external-campaign-service";
    public PortProjectedUnit[] EligibleUnits { get; set; } = [];
    public PortProjectedUnit[] Reinforcements { get; set; } = [];
}

public sealed class PortProjectedUnit
{
    public string ExternalUnitId { get; set; } = string.Empty;
    public string UnitFolder { get; set; } = string.Empty;
    public string UnitType { get; set; } = string.Empty;
    public string RosterSection { get; set; } = string.Empty;
    public string Nation { get; set; } = string.Empty;
    public string[] Variants { get; set; } = [];
    public int MaximumQuantity { get; set; } = 1;
    public int PointCost { get; set; }
    public string RepairState { get; set; } = string.Empty;
    public string RearmState { get; set; } = string.Empty;
}

public sealed class PortMaintenanceDraftImportRequest
{
    public string ProjectionId { get; set; } = string.Empty;
    public long ExpectedCampaignRevision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class PortMaintenanceDraftValidation
{
    public int SchemaVersion { get; set; } = 1;
    public string ProjectionId { get; set; } = string.Empty;
    public long ExpectedCampaignRevision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Committable { get; set; }
    public bool Committed { get; set; }
    public string Authority { get; set; } = string.Empty;
    public int TotalCost { get; set; }
    public int RemainingBudget { get; set; }
    public int TotalQuantity { get; set; }
    public int PortCapacity { get; set; }
    public string TransactionSha256 { get; set; } = string.Empty;
    public string[] Errors { get; set; } = [];
    public string[] Warnings { get; set; } = [];
    public CampaignMaintenanceUnitPayload[] DraftUnits { get; set; } = [];
    public string NativeMaintenanceState { get; set; } = string.Empty;
    public bool NativeRearmAvailable { get; set; }
    public int NativeDamagedUnitCount { get; set; }
    public int NativeRepairCandidateCount { get; set; }
    public CampaignNativeMaintenanceStatePayload[] NativeMaintenanceUnits { get; set; } = [];
}

public sealed class PortProjectionMaterializationResult
{
    public int SchemaVersion { get; set; } = 1;
    public string ProjectionId { get; set; } = string.Empty;
    public long CampaignRevision { get; set; }
    public string RosterFile { get; set; } = string.Empty;
    public string RosterSha256 { get; set; } = string.Empty;
    public string CampaignFile { get; set; } = string.Empty;
    public string CampaignSha256 { get; set; } = string.Empty;
    public bool RequiresGameRestart { get; set; }
    public string Authority { get; set; } = string.Empty;
}
