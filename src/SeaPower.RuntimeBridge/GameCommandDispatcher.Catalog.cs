using System.Text.Json;

using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static partial class GameCommandDispatcher
{
    private static UnitCatalogPagePayload GetUnitCatalogPage(
        string payloadJson,
        RuntimeFileFingerprintProvider fileFingerprintProvider)
    {
        var request = JsonSerializer.Deserialize<UnitCatalogPageRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new UnitCatalogPageRequestPayload();
        var offset = Math.Max(0, request.Offset);
        var limit = Math.Clamp(request.Limit, 1, 50);
        var folder = string.IsNullOrWhiteSpace(request.Folder)
            ? null
            : request.Folder.Trim().ToLowerInvariant();
        var content = fileFingerprintProvider.GetContentSnapshot();
        var provider = Singleton<UnitMetadataProvider>.Instance;
        var unitData = provider?.UnitData;
        if (content.State != RuntimeContentFingerprintSnapshot.ReadyState || unitData is null)
        {
            return new UnitCatalogPagePayload
            {
                State = unitData is null ? "metadata-unavailable" : content.State,
                CatalogSha256 = content.CatalogSha256 ?? string.Empty,
                Offset = offset,
                Limit = limit,
            };
        }

        var records = unitData
            .Where(item => folder is null || string.Equals(
                item.Key.Item1,
                folder,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Key.Item1, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Item2, StringComparer.Ordinal)
            .ToArray();

        return new UnitCatalogPagePayload
        {
            State = "ready",
            CatalogSha256 = content.CatalogSha256 ?? string.Empty,
            TotalRecords = records.Length,
            Offset = offset,
            Limit = limit,
            Records = records
                .Skip(offset)
                .Take(limit)
                .Select(item => CaptureUnitCatalogRecord(item, content))
                .ToArray(),
        };
    }

    private static UnitCatalogRecordPayload CaptureUnitCatalogRecord(
        KeyValuePair<(string, string), (int, SimpleObjectParameters)> item,
        RuntimeContentFingerprintSnapshot content)
    {
        var folder = item.Key.Item1 ?? string.Empty;
        var typeId = item.Key.Item2 ?? string.Empty;
        var metadata = item.Value.Item2;
        var source = content.FindWinningFile($"{folder}/{typeId}.ini");
        return new UnitCatalogRecordPayload
        {
            Id = $"unit:{folder.ToLowerInvariant()}/{typeId.ToLowerInvariant()}",
            RuntimeMetadataId = item.Value.Item1,
            UnitFolder = folder,
            UnitTypeId = typeId,
            ObjectType = metadata?._objectType.ToString() ?? string.Empty,
            DefaultName = metadata?.DefaultName ?? string.Empty,
            ClassName = metadata?.ClassName ?? string.Empty,
            TypeAbbreviation = metadata?._typeAbbr ?? string.Empty,
            TypeName = metadata?._typeFullName ?? string.Empty,
            DefaultNation = metadata?._defaultNation ?? string.Empty,
            SourceId = source?.SourceId ?? string.Empty,
            SourceFileSha256 = source?.Sha256 ?? string.Empty,
            LoadoutIds = metadata?._loadouts?.Keys
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray() ?? [],
            Roles = metadata?._unitRoles
                .Select(value => value.ToString())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray() ?? [],
            Variants = metadata?._variants
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => new UnitCatalogVariantPayload
                {
                    Id = value.Key,
                    Index = value.Value.Index,
                    Name = value.Value.DefaultName ?? string.Empty,
                    ClassName = value.Value.ClassName ?? string.Empty,
                    Nation = value.Value.Nation ?? string.Empty,
                    LoadoutId = value.Value.LoadoutVariant ?? string.Empty,
                    ServiceEntryYear = value.Value.ServiceEntryYear,
                    ServiceExitYear = value.Value.ServiceExitYear,
                })
                .ToArray() ?? [],
        };
    }

    private static ReferenceCatalogPagePayload GetReferenceCatalogPage(
        string payloadJson,
        RuntimeFileFingerprintProvider fileFingerprintProvider)
    {
        var request = JsonSerializer.Deserialize<ReferenceCatalogPageRequestPayload>(
            string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
            JsonOptions) ?? new ReferenceCatalogPageRequestPayload();
        var kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
        var offset = Math.Max(0, request.Offset);
        var limit = Math.Clamp(request.Limit, 1, 100);
        var content = fileFingerprintProvider.GetContentSnapshot();
        var result = new ReferenceCatalogPagePayload
        {
            State = content.State,
            CatalogSha256 = content.CatalogSha256 ?? string.Empty,
            Kind = kind,
            Offset = offset,
            Limit = limit,
        };
        if (content.State != RuntimeContentFingerprintSnapshot.ReadyState)
        {
            return result;
        }

        ReferenceCatalogRecordPayload[] records = kind switch
        {
            "ammunition" => CaptureAmmunitionCatalog(content),
            "sensors" => CaptureSensorCatalog(content),
            "signatures" => CaptureSignatureCatalog(content),
            "files" => CaptureFileCatalog(content),
            _ => [],
        };
        result.State = records.Length == 0 && kind is not "files"
            ? "metadata-unavailable-or-kind-unsupported"
            : "ready";
        result.TotalRecords = records.Length;
        result.Records = records.Skip(offset).Take(limit).ToArray();
        return result;
    }

    private static CatalogDiagnosticsPayload GetCatalogDiagnostics(
        RuntimeFileFingerprintProvider fileFingerprintProvider)
    {
        var content = fileFingerprintProvider.GetContentSnapshot();
        return new CatalogDiagnosticsPayload
        {
            State = content.State,
            CatalogSha256 = content.CatalogSha256 ?? string.Empty,
            WinningFileCount = content.CatalogFileCount,
            OverrideCount = content.Overrides.Length,
            IdenticalOverrideCount = content.Overrides.Count(item =>
                string.Equals(item.Classification, "identical-override", StringComparison.Ordinal)),
            ConflictingOverrideCount = content.Overrides.Count(item =>
                string.Equals(item.Classification, "conflicting-override", StringComparison.Ordinal)),
            Overrides = content.Overrides.Select(item => new CatalogOverrideDiagnosticPayload
            {
                VirtualPath = item.VirtualPath,
                WinningSourceId = item.WinningSourceId,
                WinningSha256 = item.WinningSha256,
                ShadowedSourceId = item.ShadowedSourceId,
                ShadowedSha256 = item.ShadowedSha256,
                Classification = item.Classification,
            }).ToArray(),
        };
    }

    private static ReferenceCatalogRecordPayload[] CaptureAmmunitionCatalog(
        RuntimeContentFingerprintSnapshot content)
    {
        var ammunitionData = Singleton<UnitMetadataProvider>.Instance?.AmmunitionData;
        if (ammunitionData is null)
        {
            return [];
        }

        return ammunitionData
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item =>
            {
                var source = content.FindWinningFile($"ammunition/{item.Key}.ini");
                return new ReferenceCatalogRecordPayload
                {
                    Id = $"ammunition:{item.Key.ToLowerInvariant()}",
                    Kind = "ammunition",
                    Name = item.Key,
                    DisplayName = item.Value?.DisplayedName ?? string.Empty,
                    Type = item.Value?._type.ToString() ?? string.Empty,
                    Subtype = item.Value?._weaponSystemType.ToString() ?? string.Empty,
                    GuidanceType = item.Value?._guidanceType.ToString() ?? string.Empty,
                    PrimaryTargetType = item.Value?._targetType.ToString() ?? string.Empty,
                    SecondaryTargetType = item.Value?._secondonaryTargetType.ToString() ?? string.Empty,
                    SourceId = source?.SourceId ?? string.Empty,
                    VirtualPath = source?.VirtualPath ?? string.Empty,
                    SourceFileSha256 = source?.Sha256 ?? string.Empty,
                };
            })
            .ToArray();
    }

    private static ReferenceCatalogRecordPayload[] CaptureSensorCatalog(
        RuntimeContentFingerprintSnapshot content)
    {
        var sensorProvider = Singleton<SensorMetadataProvider>.Instance;
        var source = content.FindWinningFile("systems/sensors.ini");
        return sensorProvider?.Sensors.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => new ReferenceCatalogRecordPayload
            {
                Id = $"sensor:{value.ToLowerInvariant()}",
                Kind = "sensor",
                Name = value,
                SourceId = source?.SourceId ?? string.Empty,
                VirtualPath = source?.VirtualPath ?? string.Empty,
                SourceFileSha256 = source?.Sha256 ?? string.Empty,
            })
            .ToArray() ?? [];
    }

    private static ReferenceCatalogRecordPayload[] CaptureSignatureCatalog(
        RuntimeContentFingerprintSnapshot content)
    {
        var sensorProvider = Singleton<SensorMetadataProvider>.Instance;
        var source = content.FindWinningFile("systems/signatures.ini");
        return sensorProvider?.SignatureMap.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => new ReferenceCatalogRecordPayload
            {
                Id = $"signature:{value.ToLowerInvariant()}",
                Kind = "signature",
                Name = value,
                Family = sensorProvider.FamilyMap.TryGetValue(value, out var family)
                    ? family ?? string.Empty
                    : string.Empty,
                Category = sensorProvider.CategoryMap.TryGetValue(value, out var category)
                    ? category ?? string.Empty
                    : string.Empty,
                SourceId = source?.SourceId ?? string.Empty,
                VirtualPath = source?.VirtualPath ?? string.Empty,
                SourceFileSha256 = source?.Sha256 ?? string.Empty,
            })
            .ToArray() ?? [];
    }

    private static ReferenceCatalogRecordPayload[] CaptureFileCatalog(
        RuntimeContentFingerprintSnapshot content) => content.Files
        .OrderBy(file => file.VirtualPath, StringComparer.Ordinal)
        .Select(file => new ReferenceCatalogRecordPayload
        {
            Id = $"asset:{file.VirtualPath}",
            Kind = "file",
            Name = Path.GetFileNameWithoutExtension(file.VirtualPath),
            Category = file.VirtualPath.Split('/')[0],
            SourceId = file.SourceId,
            VirtualPath = file.VirtualPath,
            SourceFileSha256 = file.Sha256,
        })
        .ToArray();
}
