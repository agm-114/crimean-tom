using System.Text.Json;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal sealed class CampaignBridgeResult
{
    public CampaignBridgeResult(string responseJson, string? missionPath = null, bool closeUi = false)
    {
        ResponseJson = responseJson;
        MissionPath = missionPath;
        CloseUi = closeUi;
    }

    public string ResponseJson { get; }

    public string? MissionPath { get; }

    public bool CloseUi { get; }
}

internal static class CampaignBridge
{
    private const string ScenarioFileName = "Crimean Tom Operations - Surface Trial.ini";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static CampaignBridgeResult Handle(string message)
    {
        using JsonDocument request = JsonDocument.Parse(message);
        string requestId = request.RootElement.GetProperty("requestId").GetString()
            ?? throw new InvalidDataException("Campaign UI request id is missing.");
        string path = request.RootElement.GetProperty("path").GetString()
            ?? throw new InvalidDataException("Campaign UI request path is missing.");
        string method = request.RootElement.GetProperty("method").GetString() ?? "GET";

        if (path == "/ui/close")
        {
            return new CampaignBridgeResult(string.Empty, closeUi: true);
        }

        string missionPath = GetMissionPath();
        CampaignFile campaign = LoadCampaign(missionPath);

        if (path == "/player/status")
        {
            return Success(requestId, new { status = "ready" });
        }

        if (path == "/player/campaigns")
        {
            return Success(requestId, new[] { CreateSummary(campaign) });
        }

        if (path == $"/player/campaigns/{campaign.CampaignId}")
        {
            return Success(requestId, CreateCampaignView(campaign));
        }

        const string tacticalEntrySuffix = "/groups/player-force/tactical-entry";
        if (path == $"/player/campaigns/{campaign.CampaignId}{tacticalEntrySuffix}")
        {
            object entry = CreateTacticalEntry(campaign);
            if (StringComparer.OrdinalIgnoreCase.Equals(method, "POST"))
            {
                CampaignBridgeResult response = Success(requestId, new
                {
                    status = "launching",
                    batchId = campaign.PendingScenario?.OperationId ?? "prepared-operation",
                    entry,
                });
                return new CampaignBridgeResult(response.ResponseJson, missionPath);
            }

            return Success(requestId, entry);
        }

        const string tacticalExitSuffix = "/groups/player-force/tactical-exit";
        if (path == $"/player/campaigns/{campaign.CampaignId}{tacticalExitSuffix}")
        {
            return Success(requestId, new
            {
                campaignId = campaign.CampaignId,
                groupId = "player-force",
                groupName = "Player Surface Group",
                groupRevision = campaign.Revision,
                state = "inactive",
                message = "No tactical session is currently active.",
                canExit = false,
                batchId = (string?)null,
                campaignTimeQuanta = campaign.Turn,
                members = Array.Empty<object>(),
                unsupportedFields = Array.Empty<string>(),
            });
        }

        return Error(requestId, 404, $"Unsupported campaign request: {method} {path}");
    }

    private static object CreateSummary(CampaignFile campaign)
    {
        return new
        {
            campaignId = campaign.CampaignId,
            name = "North Atlantic Operations, 1989",
            commitPosition = campaign.Revision,
            catalogFingerprint = "sea-power-native",
            playerId = "local-player",
            coalitionId = "NATO",
            paused = true,
            lastCursor = campaign.Revision,
            lastSavedHead = (long?)campaign.Revision,
            lastSavedAt = File.GetLastWriteTimeUtc(GetCampaignPath(GetMissionPath())),
        };
    }

    private static object CreateCampaignView(CampaignFile campaign)
    {
        PendingScenarioFile? scenario = campaign.PendingScenario;
        int latitude = (int)Math.Round((scenario?.Environment.MapCenterLatitude ?? 36) * 1_000_000);
        int longitude = (int)Math.Round((scenario?.Environment.MapCenterLongitude ?? -20) * 1_000_000);
        object[] friendlyEntities = campaign.Units.Select(unit => new
        {
            entityId = unit.Id,
            name = unit.Name,
            catalogUnitId = unit.SeaPowerType,
            regionId = "north-atlantic",
            latitudeMicrodegrees = latitude,
            longitudeMicrodegrees = longitude,
            lifecycle = unit.Destroyed ? "destroyed" : $"condition {unit.Condition}%",
            fidelity = $"F{unit.Fuel} / A{unit.PrimaryAmmunition}",
            revision = campaign.Revision,
            hostEntityId = (string?)null,
        }).Cast<object>().ToArray();
        string[] memberIds = campaign.Units.Select(unit => unit.Id).ToArray();
        bool canPlay = scenario is not null && campaign.Units.Any(unit => !unit.Destroyed);

        return new
        {
            campaignId = campaign.CampaignId,
            name = "North Atlantic Operations, 1989",
            scenarioId = scenario?.OperationId ?? "headquarters",
            saveSchemaVersion = 1,
            algorithmVersion = 19,
            catalogFingerprint = "sea-power-native",
            playerId = "local-player",
            coalitionId = "NATO",
            timeQuanta = campaign.Turn - 1,
            tempo = new
            {
                simulatedSecondsPerQuantum = 86_400,
                advanceQuanta = 1,
                wallIntervalMilliseconds = 0,
                targetMultiplier = 0,
            },
            head = campaign.Revision,
            paused = true,
            lastCursor = campaign.Revision,
            lastSavedHead = (long?)campaign.Revision,
            lastSavedAt = File.GetLastWriteTimeUtc(GetCampaignPath(GetMissionPath())),
            trustBoundary = canPlay ? "operation prepared" : "headquarters",
            scenarioEntityCount = campaign.Units.Count + (scenario?.EnemyVessels.Count ?? 0),
            friendlyEntities,
            friendlyGroups = new[]
            {
                new
                {
                    groupId = "player-force",
                    name = "Player Surface Group",
                    memberEntityIds = memberIds,
                    currentRegionId = "north-atlantic",
                    activeOrderId = (string?)null,
                    activeOrderKind = (string?)null,
                    activeOrderDestinationRegionId = (string?)null,
                    playerControlled = true,
                    orderStatus = canPlay ? "operation ready" : "awaiting orders",
                    canMove = false,
                    revision = campaign.Revision,
                },
            },
            friendlyBases = Array.Empty<object>(),
            contacts = scenario is null
                ? Array.Empty<object>()
                : new object[]
                {
                    new
                    {
                        trackId = "hostile-surface-contact",
                        estimatedRegionId = "north-atlantic",
                        estimatedLatitudeMicrodegrees = latitude,
                        estimatedLongitudeMicrodegrees = longitude + 400_000,
                        confidence = "confirmed",
                        uncertaintyRadiusMeters = 5_000,
                        classification = "hostile surface group",
                        observedAtQuanta = campaign.Turn - 1,
                        revision = campaign.Revision,
                    },
                },
            objectives = scenario is null
                ? Array.Empty<object>()
                : new object[]
                {
                    new
                    {
                        objectiveId = scenario.OperationId,
                        kind = "Defeat hostile surface group",
                        regionId = "north-atlantic",
                        regionName = "North Atlantic",
                        priorityMilliUnits = 1_000,
                        assignedGroupCount = 1,
                        playerAssignedGroupCount = 1,
                    },
                },
        };
    }

    private static object CreateTacticalEntry(CampaignFile campaign)
    {
        bool canEnter = campaign.PendingScenario is not null && campaign.Units.Any(unit => !unit.Destroyed);
        return new
        {
            campaignId = campaign.CampaignId,
            groupId = "player-force",
            groupName = "Player Surface Group",
            groupRevision = campaign.Revision,
            state = canEnter ? "ready" : "blocked",
            message = canEnter
                ? "The generated operation is ready to play in Sea Power."
                : "Prepare the next operation before launching Sea Power.",
            workerConnected = true,
            canEnter,
            requiredMemberCount = campaign.Units.Count,
            supportedMemberCount = campaign.Units.Count(unit => !unit.Destroyed),
            availableTacticalCapacity = campaign.Units.Count(unit => !unit.Destroyed),
            workerClientInstanceId = "in-game",
            playerTaskforceId = "Taskforce1",
            members = campaign.Units.Select(unit => new
            {
                entityId = unit.Id,
                name = unit.Name,
                catalogUnitId = unit.SeaPowerType,
                status = unit.Destroyed ? "destroyed" : "ready",
                message = $"Condition {unit.Condition}%, fuel {unit.Fuel}%, ammunition {unit.PrimaryAmmunition}",
                hostEntityId = (string?)null,
            }).ToArray(),
        };
    }

    private static CampaignBridgeResult Success(string requestId, object body)
    {
        return new CampaignBridgeResult(JsonSerializer.Serialize(new
        {
            requestId,
            ok = true,
            status = 200,
            body,
        }, JsonOptions));
    }

    private static CampaignBridgeResult Error(string requestId, int status, string error)
    {
        return new CampaignBridgeResult(JsonSerializer.Serialize(new
        {
            requestId,
            ok = false,
            status,
            error,
        }, JsonOptions));
    }

    private static CampaignFile LoadCampaign(string missionPath)
    {
        string campaignPath = GetCampaignPath(missionPath);
        return JsonSerializer.Deserialize<CampaignFile>(File.ReadAllText(campaignPath), JsonOptions)
            ?? throw new InvalidDataException("The campaign save was empty.");
    }

    private static string GetCampaignPath(string missionPath)
    {
        Dictionary<string, string> settings = AfterActionExporter.ReadSection(
            missionPath,
            AfterActionExporter.SectionName);
        if (!settings.TryGetValue("AfterActionPath", out string? afterActionPath)
            || string.IsNullOrWhiteSpace(afterActionPath))
        {
            throw new InvalidDataException("The installed operation has no campaign link.");
        }

        return Path.Combine(Path.GetDirectoryName(afterActionPath)!, "campaign.json");
    }

    private static string GetMissionPath()
    {
        string missionPath = Path.Combine(
            Application.streamingAssetsPath,
            "user",
            "missions",
            "user_missions",
            ScenarioFileName);
        if (!File.Exists(missionPath))
        {
            throw new FileNotFoundException("The prepared Crimean Tom operation is not installed.", missionPath);
        }

        return missionPath;
    }

    private sealed class CampaignFile
    {
        public string CampaignId { get; set; } = string.Empty;

        public long Revision { get; set; }

        public int Turn { get; set; }

        public List<CampaignUnitFile> Units { get; set; } = new();

        public PendingScenarioFile? PendingScenario { get; set; }
    }

    private sealed class CampaignUnitFile
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string SeaPowerType { get; set; } = string.Empty;

        public int Condition { get; set; }

        public int Fuel { get; set; }

        public int PrimaryAmmunition { get; set; }

        public bool Destroyed { get; set; }
    }

    private sealed class PendingScenarioFile
    {
        public string OperationId { get; set; } = string.Empty;

        public ScenarioEnvironmentFile Environment { get; set; } = new();

        public List<object> EnemyVessels { get; set; } = new();
    }

    private sealed class ScenarioEnvironmentFile
    {
        public double MapCenterLatitude { get; set; }

        public double MapCenterLongitude { get; set; }
    }
}
