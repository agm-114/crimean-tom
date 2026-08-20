using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

using CrimeanTom.Protocol;

namespace CrimeanTom.Protocol.Tests;

public sealed class CompanionControlIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task RealHostAuthenticatesAndBridgesReadStateAndTypedUiAutomation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "crimean-tom-control.ps1");
        var runDirectory = CreateTemporaryDirectory();
        var controlUrl = $"http://127.0.0.1:{GetUnusedLoopbackPort()}";
        var gamePipeName = $"CrimeanTom.Tests.{Guid.NewGuid():N}";
        var probeSavePath = Path.Combine(runDirectory, "_CrimeanTomPhase1Probe.sav");
        var pidPath = Path.Combine(runDirectory, "companion.pid");
        var tokenPath = Path.Combine(runDirectory, "control.token");
        var stdoutPath = Path.Combine(runDirectory, "companion.stdout.log");
        var stderrPath = Path.Combine(runDirectory, "companion.stderr.log");
        const string staleMarker = "stale-artifact-marker";

        await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString(), testTimeout.Token);
        await File.WriteAllTextAsync(tokenPath, $"{staleMarker}-token", testTimeout.Token);
        await File.WriteAllTextAsync(stdoutPath, $"{staleMarker}-stdout", testTimeout.Token);
        await File.WriteAllTextAsync(stderrPath, $"{staleMarker}-stderr", testTimeout.Token);

        try
        {
            var start = await RunControlScriptAsync(
                scriptPath,
                "start",
                runDirectory,
                controlUrl,
                gamePipeName,
                skipBuild: true,
                testTimeout.Token);
            Assert.True(start.ExitCode == 0, start.FormatFailure("start"));

            var companionProcessId = int.Parse(
                (await File.ReadAllTextAsync(pidPath, testTimeout.Token)).Trim());
            Assert.NotEqual(Environment.ProcessId, companionProcessId);
            Assert.NotEqual(
                $"{staleMarker}-token",
                (await File.ReadAllTextAsync(tokenPath, testTimeout.Token)).Trim());

            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(controlUrl),
                Timeout = TimeSpan.FromSeconds(8),
            };

            using var missingTokenResponse = await client.GetAsync("/health", testTimeout.Token);
            await AssertUnauthorizedAsync(missingTokenResponse, testTimeout.Token);

            using var wrongTokenRequest = new HttpRequestMessage(HttpMethod.Get, "/game/ping");
            wrongTokenRequest.Headers.TryAddWithoutValidation(
                ProtocolConstants.ControlTokenHeader,
                "wrong-token");
            using var wrongTokenResponse = await client.SendAsync(
                wrongTokenRequest,
                testTimeout.Token);
            await AssertUnauthorizedAsync(wrongTokenResponse, testTimeout.Token);

            var token = (await File.ReadAllTextAsync(tokenPath, testTimeout.Token)).Trim();
            AssertAnonymousPipeAccessDenied(gamePipeName);
            var gamePeer = RunGamePeerAsync(gamePipeName, probeSavePath, testTimeout.Token);
            await WaitForConnectedGameAsync(client, token, testTimeout.Token);

            using var ping = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/ping",
                testTimeout.Token);
            Assert.True(ping.RootElement.GetProperty("isMainThread").GetBoolean());

            using var capabilities = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/capabilities",
                testTimeout.Token);
            var supportedCommands = capabilities.RootElement
                .GetProperty("supportedCommands")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray();
            Assert.Equal(GameCommandTypes.AllCommands, supportedCommands);
            Assert.True(capabilities.RootElement.GetProperty("uiAutomationEnabled").GetBoolean());
            Assert.Equal(
                GameCommandTypes.OpenTaskForceBuilder,
                capabilities.RootElement
                    .GetProperty("supportedUiAutomationCommands")[0]
                    .GetString());

            using var scene = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/scene",
                testTimeout.Token);
            Assert.Equal("IntegrationScenario", scene.RootElement.GetProperty("sceneName").GetString());

            using var world = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/world",
                testTimeout.Token);
            Assert.Equal("loaded-scene", world.RootElement.GetProperty("identityScope").GetString());
            Assert.Equal(
                "unit:42",
                world.RootElement.GetProperty("units")[0].GetProperty("id").GetString());
            Assert.Equal(
                "taskforce:Blue",
                world.RootElement.GetProperty("taskforces")[0].GetProperty("id").GetString());
            Assert.Equal(
                "integration-canonical-sha256",
                world.RootElement.GetProperty("canonicalSha256").GetString());
            Assert.Equal(
                "player-track:7001",
                world.RootElement.GetProperty("contacts")[0].GetProperty("id").GetString());
            Assert.Equal(
                "estimated",
                world.RootElement.GetProperty("contacts")[0].GetProperty("positionState").GetString());

            using var catalog = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/catalog/units?offset=0&limit=5&folder=vessels",
                testTimeout.Token);
            Assert.Equal("ready", catalog.RootElement.GetProperty("state").GetString());
            Assert.Equal(
                "unit:vessels/usn_ff_knox",
                catalog.RootElement.GetProperty("records")[0].GetProperty("id").GetString());
            Assert.Equal(
                "base",
                catalog.RootElement.GetProperty("records")[0].GetProperty("sourceId").GetString());

            using var ammunitionCatalog = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/catalog/references?kind=ammunition&offset=0&limit=5",
                testTimeout.Token);
            Assert.Equal(
                "ammunition:rim-7m",
                ammunitionCatalog.RootElement.GetProperty("records")[0].GetProperty("id").GetString());

            using var catalogDiagnostics = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/catalog/diagnostics",
                testTimeout.Token);
            Assert.Equal("ready", catalogDiagnostics.RootElement.GetProperty("state").GetString());
            Assert.Equal(
                "conflicting-override",
                catalogDiagnostics.RootElement
                    .GetProperty("overrides")[0]
                    .GetProperty("classification")
                    .GetString());

            using var campaign = await GetAuthorizedJsonAsync(
                client,
                token,
                "/game/campaign",
                testTimeout.Token);
            Assert.True(campaign.RootElement.GetProperty("isTaskForceModeCampaign").GetBoolean());
            Assert.True(campaign.RootElement.GetProperty("taskForceBuilderEnabled").GetBoolean());
            Assert.False(campaign.RootElement.GetProperty("taskForceBuilderLoaded").GetBoolean());

            using var startPortOperations = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/automation/port-operations/start",
                testTimeout.Token);
            Assert.Equal(
                "port-operations-campaign-load-requested",
                startPortOperations.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                "campaigns/crimean-tom-port-operations/campaign.ini",
                startPortOperations.RootElement.GetProperty("campaignPath").GetString());

            using var openBuilder = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/automation/task-force-builder",
                testTimeout.Token);
            Assert.Equal("completed", openBuilder.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "task-force-builder-loaded-for-selected-mission",
                openBuilder.RootElement.GetProperty("postcondition").GetString());
            Assert.True(
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("taskForceBuilderLoaded")
                    .GetBoolean());
            Assert.Equal(
                12,
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("availableRosterVariantCount")
                    .GetInt32());
            Assert.True(
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("hasShipRosterUnits")
                    .GetBoolean());
            Assert.True(
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("hasSubmarineRosterUnits")
                    .GetBoolean());
            Assert.True(
                openBuilder.RootElement
                    .GetProperty("campaign")
                    .GetProperty("hasAirRosterUnits")
                    .GetBoolean());
            var purchasedUnit = openBuilder.RootElement
                .GetProperty("campaign")
                .GetProperty("purchasedUnits")[0];
            Assert.Equal("port-unit:1", purchasedUnit.GetProperty("instanceId").GetString());
            Assert.Equal("usn_ff_knox_84", purchasedUnit.GetProperty("unitType").GetString());
            Assert.Equal("Default", purchasedUnit.GetProperty("selectedLoadoutVariant").GetString());
            Assert.Equal(1, purchasedUnit.GetProperty("quantity").GetInt32());
            Assert.Equal(
                "ready",
                openBuilder.RootElement.GetProperty("campaign")
                    .GetProperty("nativeMaintenanceState").GetString());
            Assert.True(
                openBuilder.RootElement.GetProperty("campaign")
                    .GetProperty("nativeRearmAvailable").GetBoolean());

            using var addMaintenanceUnit = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/automation/port-operations/draft/units",
                JsonSerializer.Serialize(new AddMaintenanceDraftUnitRequestPayload
                {
                    UnitType = "usn_ffg_oliver_hazard_perry",
                    VariantReference = "Variant17",
                    Quantity = 1,
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "maintenance-unit-staged-uncommitted",
                addMaintenanceUnit.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                "usn_ffg_oliver_hazard_perry",
                addMaintenanceUnit.RootElement
                    .GetProperty("campaign")
                    .GetProperty("pendingUnits")[0]
                    .GetProperty("unitType")
                    .GetString());

            using var startScenario = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/automation/scenario/start",
                JsonSerializer.Serialize(new StartScenarioRequestPayload
                {
                    ScenarioId = "scenario:missions/intro/integration",
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "scenario-load-requested",
                startScenario.RootElement.GetProperty("postcondition").GetString());

            using var continueBriefing = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/automation/briefing/continue",
                testTimeout.Token);
            Assert.Equal(
                "mission-running-unpaused",
                continueBriefing.RootElement.GetProperty("postcondition").GetString());

            using var pause = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/experimental/pause",
                JsonSerializer.Serialize(new ExperimentalPauseRequestPayload
                {
                    IsPaused = true,
                }, JsonOptions),
                testTimeout.Token);
            Assert.True(pause.RootElement.GetProperty("isPaused").GetBoolean());

            using var controlledTicks = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/experimental/ticks/run",
                JsonSerializer.Serialize(new ControlledTickExperimentRequestPayload
                {
                    TickCount = 30,
                    StepSeconds = 1d / 30d,
                    Seed = 1042,
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "controlled-ticks-running",
                controlledTicks.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(30, controlledTicks.RootElement.GetProperty("requestedTicks").GetInt32());

            using var launchProbeWeapon = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/game/experimental/save-load/launch",
                JsonSerializer.Serialize(new ExperimentalWeaponLaunchRequestPayload
                {
                    WeaponType = "Missile",
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "serializable-active-weapon-launched",
                launchProbeWeapon.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                "integration_missile",
                launchProbeWeapon.RootElement.GetProperty("ammunitionId").GetString());

            using var saveProbe = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/experimental/save-load/save",
                testTimeout.Token);
            Assert.Equal("completed", saveProbe.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "active-weapon-save-materialized",
                saveProbe.RootElement.GetProperty("postcondition").GetString());
            Assert.Equal(
                64,
                saveProbe.RootElement.GetProperty("saveFileSha256").GetString()!.Length);

            using var loadProbe = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/experimental/save-load/load",
                testTimeout.Token);
            Assert.Equal("scheduled", loadProbe.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "native-mission-reload-scheduled",
                loadProbe.RootElement.GetProperty("postcondition").GetString());

            using var projection = await GetAuthorizedJsonAsync(
                client,
                token,
                "/port-operations/projection",
                testTimeout.Token);
            Assert.Equal(
                "external-campaign-service",
                projection.RootElement.GetProperty("authority").GetString());
            Assert.Equal(1042, projection.RootElement.GetProperty("campaignRevision").GetInt64());

            using var importedDraft = await PostAuthorizedJsonWithBodyAsync(
                client,
                token,
                "/port-operations/drafts/import",
                JsonSerializer.Serialize(new
                {
                    projectionId = "port-projection:sevastopol:blue-fleet:1042",
                    expectedCampaignRevision = 1042,
                    idempotencyKey = "integration-import-1042",
                }, JsonOptions),
                testTimeout.Token);
            Assert.Equal(
                "valid-uncommitted",
                importedDraft.RootElement.GetProperty("status").GetString());
            Assert.True(importedDraft.RootElement.GetProperty("committable").GetBoolean());
            Assert.False(importedDraft.RootElement.GetProperty("committed").GetBoolean());
            Assert.Equal(
                "external-campaign-service",
                importedDraft.RootElement.GetProperty("authority").GetString());
            Assert.Equal(
                "ready",
                importedDraft.RootElement.GetProperty("nativeMaintenanceState").GetString());

            using var exitTactical = await PostAuthorizedJsonAsync(
                client,
                token,
                "/game/automation/tactical/exit",
                testTimeout.Token);
            Assert.Equal("scheduled", exitTactical.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(
                "native-tactical-exit-scheduled",
                exitTactical.RootElement.GetProperty("postcondition").GetString());

            await gamePeer;
        }
        finally
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stop = await RunControlScriptAsync(
                scriptPath,
                "stop",
                runDirectory,
                controlUrl,
                gamePipeName,
                skipBuild: true,
                cleanupTimeout.Token);
            Assert.True(stop.ExitCode == 0, stop.FormatFailure("stop"));
            Assert.False(File.Exists(pidPath));
            Assert.False(File.Exists(tokenPath));
            Assert.DoesNotContain(
                staleMarker,
                await ReadAllTextWhenUnlockedAsync(stdoutPath, cleanupTimeout.Token));
            Assert.DoesNotContain(
                staleMarker,
                await ReadAllTextWhenUnlockedAsync(stderrPath, cleanupTimeout.Token));
            Directory.Delete(runDirectory, recursive: true);
        }
    }

    private static async Task<string> ReadAllTextWhenUnlockedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await File.ReadAllTextAsync(path, cancellationToken);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private static void AssertAnonymousPipeAccessDenied(string pipeName)
    {
        if (!ImpersonateAnonymousToken(GetCurrentThread()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not impersonate the Windows anonymous token for the pipe ACL probe.");
        }

        try
        {
            using var anonymousClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            var exception = Record.Exception(() => anonymousClient.Connect(1_000));
            Assert.True(
                exception is UnauthorizedAccessException or IOException,
                $"Anonymous pipe access was not rejected by the kernel ACL: {exception}");
        }
        finally
        {
            if (!RevertToSelf())
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not restore the test process identity after the pipe ACL probe.");
            }
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateAnonymousToken(IntPtr threadHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    private static async Task RunGamePeerAsync(
        string pipeName,
        string probeSavePath,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);

        var hello = new WireEnvelope
        {
            Kind = MessageKinds.Hello,
            MessageId = Guid.NewGuid().ToString("N"),
            MessageType = "game",
            PayloadJson = JsonSerializer.Serialize(new GameHelloPayload
            {
                ClientInstanceId = Guid.NewGuid().ToString("N"),
                PluginVersion = "integration",
                ProcessId = Environment.ProcessId,
                GameVersion = "integration",
                InitialScene = "IntegrationScenario",
            }, JsonOptions),
        };
        await WriteEnvelopeAsync(pipe, hello, cancellationToken);

        var accepted = await ReadEnvelopeAsync(pipe, cancellationToken);
        Assert.Equal(MessageKinds.HelloAccepted, accepted.Kind);
        var handshake = JsonSerializer.Deserialize<HandshakeResponsePayload>(
            accepted.PayloadJson,
            JsonOptions)!;
        Assert.True(handshake.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(handshake.SessionId));

        var builderLoaded = false;
        var draftPending = false;
        for (var commandIndex = 0; commandIndex < GameCommandTypes.AllCommands.Length + 1; commandIndex++)
        {
            var command = await ReadEnvelopeAsync(pipe, cancellationToken);
            Assert.Equal(MessageKinds.Command, command.Kind);
            Assert.Equal(handshake.SessionId, command.SessionId);
            if (command.MessageType == GameCommandTypes.OpenTaskForceBuilder)
            {
                builderLoaded = true;
            }
            if (command.MessageType == GameCommandTypes.AddMaintenanceDraftUnit)
            {
                draftPending = true;
            }
            if (command.MessageType == GameCommandTypes.SaveExperimentalProbe)
            {
                await File.WriteAllTextAsync(
                    probeSavePath,
                    "[File]\nSaveFile=true\n[Taskforce1Weapon1]\nType=integration_weapon\n",
                    cancellationToken);
            }

            var resultJson = command.MessageType switch
            {
                GameCommandTypes.Ping => JsonSerializer.Serialize(new PongPayload
                {
                    PluginVersion = "integration",
                    ProcessId = Environment.ProcessId,
                    InitializedMainThreadId = 1,
                    ExecutingThreadId = 1,
                    IsMainThread = true,
                    RespondedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }, JsonOptions),
                GameCommandTypes.GetCapabilities => JsonSerializer.Serialize(new CapabilitiesPayload
                {
                    ProtocolVersion = ProtocolConstants.Version,
                    SupportedCommands = GameCommandTypes.AllCommands,
                    SupportedUiAutomationCommands = GameCommandTypes.UiAutomationCommands,
                    UiAutomationEnabled = true,
                    ExperimentalRuntimeMode = "ControlledExperimental",
                    SupportedExperimentalCommands = GameCommandTypes.ExperimentalCommands,
                    MutatingCommandsEnabled = false,
                    CommandCancellationSupported = true,
                    SupportedPriorities = CommandPriorities.Supported,
                    ExecutionThreadGuardSelfTest = "passed",
                }, JsonOptions),
                GameCommandTypes.GetSceneState => JsonSerializer.Serialize(new SceneStatePayload
                {
                    SceneName = "IntegrationScenario",
                    SceneLoaded = true,
                    GameMainAvailable = true,
                    ObjectsManagerAvailable = true,
                    TaskforceManagerAvailable = true,
                    ObjectCount = 1,
                    TaskforceCount = 1,
                    InitializedMainThreadId = 1,
                    ExecutingThreadId = 1,
                    IsMainThread = true,
                }, JsonOptions),
                GameCommandTypes.GetWorldSnapshot => JsonSerializer.Serialize(CreateWorldSnapshot(), JsonOptions),
                GameCommandTypes.GetUnitCatalogPage => JsonSerializer.Serialize(
                    CreateUnitCatalogPage(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.GetReferenceCatalogPage => JsonSerializer.Serialize(
                    CreateReferenceCatalogPage(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.GetCatalogDiagnostics => JsonSerializer.Serialize(
                    new CatalogDiagnosticsPayload
                    {
                        State = "ready",
                        CatalogSha256 = "integration-catalog-sha256",
                        WinningFileCount = 1,
                        OverrideCount = 1,
                        ConflictingOverrideCount = 1,
                        Overrides =
                        [
                            new CatalogOverrideDiagnosticPayload
                            {
                                VirtualPath = "vessels/usn_ff_knox.ini",
                                WinningSourceId = "user",
                                WinningSha256 = new string('a', 64),
                                ShadowedSourceId = "base",
                                ShadowedSha256 = new string('b', 64),
                                Classification = "conflicting-override",
                            },
                        ],
                    },
                    JsonOptions),
                GameCommandTypes.GetCampaignManagementState => JsonSerializer.Serialize(
                    CreateCampaignManagementState(builderLoaded, draftPending),
                    JsonOptions),
                GameCommandTypes.OpenTaskForceBuilder => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.OpenTaskForceBuilder,
                        Outcome = "completed",
                        Postcondition = "task-force-builder-loaded-for-selected-mission",
                        Campaign = CreateCampaignManagementState(builderLoaded: true),
                    },
                    JsonOptions),
                GameCommandTypes.StartPortOperations => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.StartPortOperations,
                        Outcome = "completed",
                        Postcondition = "port-operations-campaign-load-requested",
                        CampaignPath = "campaigns/crimean-tom-port-operations/campaign.ini",
                    },
                    JsonOptions),
                GameCommandTypes.AddMaintenanceDraftUnit => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.AddMaintenanceDraftUnit,
                        Outcome = "completed",
                        Postcondition = "maintenance-unit-staged-uncommitted",
                        Campaign = CreateCampaignManagementState(
                            builderLoaded: true,
                            draftPending: true),
                    },
                    JsonOptions),
                GameCommandTypes.StartScenario => JsonSerializer.Serialize(
                    CreateScenarioActionResult(
                        command.PayloadJson,
                        GameCommandTypes.StartScenario,
                        "scenario-load-requested",
                        briefingPaused: true),
                    JsonOptions),
                GameCommandTypes.ContinueMissionBriefing => JsonSerializer.Serialize(
                    CreateScenarioActionResult(
                        command.PayloadJson,
                        GameCommandTypes.ContinueMissionBriefing,
                        "mission-running-unpaused",
                        briefingPaused: false),
                    JsonOptions),
                GameCommandTypes.ExitTacticalSession => JsonSerializer.Serialize(
                    new UiAutomationActionResultPayload
                    {
                        Action = GameCommandTypes.ExitTacticalSession,
                        Outcome = "scheduled",
                        Postcondition = "native-tactical-exit-scheduled",
                    },
                    JsonOptions),
                GameCommandTypes.SetExperimentalPauseState => JsonSerializer.Serialize(
                    CreateExperimentalPauseResult(command.PayloadJson),
                    JsonOptions),
                GameCommandTypes.RunControlledTickExperiment => JsonSerializer.Serialize(
                    new ControlledTickExperimentPayload
                    {
                        State = "running",
                        Postcondition = "controlled-ticks-running",
                        HooksInstalled = true,
                        RequestedTicks = 30,
                        StepSeconds = 1d / 30d,
                        Seed = 1042,
                        SeededSources = ["Globals._rnd"],
                    },
                    JsonOptions),
                GameCommandTypes.LaunchExperimentalProbeWeapon => JsonSerializer.Serialize(
                    new ExperimentalWeaponLaunchResultPayload
                    {
                        Action = GameCommandTypes.LaunchExperimentalProbeWeapon,
                        Outcome = "completed",
                        Postcondition = "serializable-active-weapon-launched",
                        UnitId = "unit:42",
                        WeaponType = "Missile",
                        AmmunitionId = "integration_missile",
                        ActiveWeaponId = "weapon:43",
                        SerializableActiveWeaponCount = 1,
                    },
                    JsonOptions),
                GameCommandTypes.SaveExperimentalProbe => JsonSerializer.Serialize(
                    new ExperimentalSaveLoadProbeResultPayload
                    {
                        Action = GameCommandTypes.SaveExperimentalProbe,
                        Outcome = "scheduled",
                        Postcondition = "native-save-write-scheduled",
                        SaveFileName = Path.GetFileName(probeSavePath),
                        SaveFilePath = probeSavePath,
                        PreTransitionWeaponCount = 1,
                        PreTransitionWeaponsSha256 = "integration-weapons-sha256",
                        PreTransitionCanonicalSha256 = "integration-canonical-sha256",
                    },
                    JsonOptions),
                GameCommandTypes.LoadExperimentalProbe => JsonSerializer.Serialize(
                    new ExperimentalSaveLoadProbeResultPayload
                    {
                        Action = GameCommandTypes.LoadExperimentalProbe,
                        Outcome = "scheduled",
                        Postcondition = "native-mission-reload-scheduled",
                        SaveFileName = Path.GetFileName(probeSavePath),
                        SaveFilePath = probeSavePath,
                        SaveFileSha256 = new string('a', 64),
                        PreTransitionWeaponCount = 1,
                        PreTransitionWeaponsSha256 = "integration-weapons-sha256",
                        PreTransitionCanonicalSha256 = "integration-canonical-sha256",
                    },
                    JsonOptions),
                _ => throw new InvalidOperationException($"Unexpected command '{command.MessageType}'."),
            };

            await WriteEnvelopeAsync(pipe, new WireEnvelope
            {
                Kind = MessageKinds.Response,
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = command.MessageId,
                SessionId = handshake.SessionId,
                MessageType = command.MessageType,
                PayloadJson = JsonSerializer.Serialize(new CommandResponsePayload
                {
                    Success = true,
                    ResultJson = resultJson,
                }, JsonOptions),
                Priority = command.Priority,
            }, cancellationToken);
        }
    }

    private static WorldSnapshotPayload CreateWorldSnapshot()
    {
        return new WorldSnapshotPayload
        {
            SceneName = "IntegrationScenario",
            CapturedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CanonicalSha256 = "integration-canonical-sha256",
            Taskforces =
            [
                new TaskforceSnapshotPayload
                {
                    Id = "taskforce:Blue",
                    MissionName = "Blue",
                    Side = "Player",
                    Nation = "us",
                    UnitIds = ["unit:42"],
                },
            ],
            Units =
            [
                new UnitSnapshotPayload
                {
                    Id = "unit:42",
                    GameUniqueId = 42,
                    TaskforceId = "taskforce:Blue",
                    Name = "USS Integration",
                    ClassName = "Test Ship",
                    Type = "Vessel",
                    Nation = "us",
                    Latitude = 35,
                    Longitude = 20,
                    HeadingDegrees = 90,
                    SpeedKnots = 18,
                },
            ],
            Contacts =
            [
                new ContactSnapshotPayload
                {
                    Id = "player-track:7001",
                    TrackNumber = 7001,
                    Relationship = "Hostile",
                    PositionState = "estimated",
                    Latitude = 35.5,
                    Longitude = 20.5,
                    PositionMajorStdDevMeters = 1200,
                    PositionMinorStdDevMeters = 350,
                    DetectingSensors = "PassiveSonar",
                    ObservationSha256 = "integration-contact-sha256",
                    TrackSources =
                    [
                        new ContactTrackSourcePayload
                        {
                            SensorType = "PassiveSonar",
                            SensorDisplayName = "AN/SQS-26",
                            DetectionKind = "Subsurface",
                            Confidence = 0.75,
                            ObservationCount = 3,
                        },
                    ],
                },
            ],
        };
    }

    private static CampaignManagementStatePayload CreateCampaignManagementState(
        bool builderLoaded,
        bool draftPending = false)
    {
        return new CampaignManagementStatePayload
        {
            SceneName = "LinearCampaign",
            LinearCampaignViewAvailable = true,
            LinearCampaignMapAvailable = true,
            IsTaskForceModeCampaign = true,
            HasSelectedCampaignEvent = true,
            SelectedCampaignEventName = "Port Operations",
            TaskForceBuilderVisible = true,
            TaskForceBuilderEnabled = true,
            TaskForceBuilderTargetMissionIndex = builderLoaded ? 1 : -1,
            TaskForceBuilderLoaded = builderLoaded,
            TaskForceName = "Integration Task Force",
            AvailablePoints = 500,
            PointCap = 500,
            PurchasedUnitCount = 1,
            VisibleTaskForceUnitCount = 1,
            AvailableRosterVariantCount = builderLoaded ? 12 : 0,
            HasShipRosterUnits = builderLoaded,
            HasSubmarineRosterUnits = builderLoaded,
            HasAirRosterUnits = builderLoaded,
            PurchasedUnits = builderLoaded
                ?
                [
                    new CampaignMaintenanceUnitPayload
                    {
                        InstanceId = "port-unit:1",
                        UnitFolder = "vessels",
                        UnitType = "usn_ff_knox_84",
                        Category = "Ship",
                        VariantReference = "Variant13",
                        DisplayName = "USS Integration",
                        Nation = "us",
                        SelectedLoadoutVariant = "Default",
                        Quantity = 1,
                        Cost = 120,
                    },
                ]
                : [],
            PendingUnitCount = draftPending ? 1 : 0,
            PendingPurchaseCost = draftPending ? 135 : 0,
            HasPendingTransactions = draftPending,
            PendingUnits = draftPending
                ?
                [
                    new CampaignMaintenanceUnitPayload
                    {
                        InstanceId = "pending-port-unit:1",
                        UnitFolder = "vessels",
                        UnitType = "usn_ffg_oliver_hazard_perry",
                        Category = "Ship",
                        VariantReference = "Variant17",
                        DisplayName = "USS Integration Reinforcement",
                        Nation = "us",
                        SelectedLoadoutVariant = "Default",
                        Quantity = 1,
                        Cost = 135,
                    },
                ]
                : [],
            NativeMaintenanceState = "ready",
            NativeRearmAvailable = true,
            NativeMaintenanceUnits =
            [
                new CampaignNativeMaintenanceStatePayload
                {
                    InstanceId = "port-unit:1",
                    UnitType = "usn_ff_knox_84",
                    DisplayName = "USS Integration",
                    DamageSummary = "No Damage",
                    RepairStatus = "not-needed",
                    HasRearmedBadge = true,
                    Ammunition =
                    [
                        new CampaignNativeAmmunitionStatePayload
                        {
                            Reference = "usn_rim66e_sm1mr",
                            NativeSummary = "SM-1MR: 20/20",
                        },
                    ],
                },
            ],
        };
    }

    private static UnitCatalogPagePayload CreateUnitCatalogPage(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<UnitCatalogPageRequestPayload>(
            payloadJson,
            JsonOptions)!;
        Assert.Equal(0, request.Offset);
        Assert.Equal(5, request.Limit);
        Assert.Equal("vessels", request.Folder);
        return new UnitCatalogPagePayload
        {
            State = "ready",
            CatalogSha256 = new string('a', 64),
            TotalRecords = 1,
            Offset = request.Offset,
            Limit = request.Limit,
            Records =
            [
                new UnitCatalogRecordPayload
                {
                    Id = "unit:vessels/usn_ff_knox",
                    RuntimeMetadataId = 42,
                    UnitFolder = "vessels",
                    UnitTypeId = "usn_ff_knox",
                    ObjectType = "Vessel",
                    DefaultName = "USS Integration",
                    ClassName = "Knox",
                    DefaultNation = "us",
                    SourceId = "base",
                    SourceFileSha256 = new string('b', 64),
                    Variants =
                    [
                        new UnitCatalogVariantPayload
                        {
                            Id = "Variant1",
                            Index = 1,
                            Name = "USS Integration",
                            Nation = "us",
                            ServiceEntryYear = 1969,
                            ServiceExitYear = 1999,
                        },
                    ],
                },
            ],
        };
    }

    private static ReferenceCatalogPagePayload CreateReferenceCatalogPage(string payloadJson)
    {
        var request = JsonSerializer.Deserialize<ReferenceCatalogPageRequestPayload>(
            payloadJson,
            JsonOptions)!;
        Assert.Equal("ammunition", request.Kind);
        Assert.Equal(5, request.Limit);
        return new ReferenceCatalogPagePayload
        {
            State = "ready",
            CatalogSha256 = new string('a', 64),
            Kind = request.Kind,
            TotalRecords = 1,
            Offset = request.Offset,
            Limit = request.Limit,
            Records =
            [
                new ReferenceCatalogRecordPayload
                {
                    Id = "ammunition:rim-7m",
                    Kind = "ammunition",
                    Name = "rim-7m",
                    DisplayName = "RIM-7M Sea Sparrow",
                    Type = "Missile",
                    GuidanceType = "SemiActiveRadarHoming",
                    SourceId = "base",
                    VirtualPath = "ammunition/rim-7m.ini",
                    SourceFileSha256 = new string('c', 64),
                },
            ],
        };
    }

    private static UiAutomationActionResultPayload CreateScenarioActionResult(
        string payloadJson,
        string action,
        string postcondition,
        bool briefingPaused)
    {
        if (string.Equals(action, GameCommandTypes.StartScenario, StringComparison.Ordinal))
        {
            var request = JsonSerializer.Deserialize<StartScenarioRequestPayload>(
                payloadJson,
                JsonOptions)!;
            Assert.Equal("scenario:missions/intro/integration", request.ScenarioId);
        }

        return new UiAutomationActionResultPayload
        {
            Action = action,
            Outcome = "completed",
            Postcondition = postcondition,
            Scenario = new ScenarioAutomationStatePayload
            {
                SceneName = "IntegrationScenario",
                CurrentWindowType = "MissionBriefView",
                ScenarioId = "scenario:missions/intro/integration",
                ScenarioPath = "missions/intro/integration.ini",
                BriefingVisible = true,
                BriefingPaused = briefingPaused,
                BriefingCanContinue = briefingPaused,
                GameMainAvailable = true,
            },
        };
    }

    private static ExperimentalPauseResultPayload CreateExperimentalPauseResult(
        string payloadJson)
    {
        var request = JsonSerializer.Deserialize<ExperimentalPauseRequestPayload>(
            payloadJson,
            JsonOptions)!;
        return new ExperimentalPauseResultPayload
        {
            RequestedPaused = request.IsPaused,
            IsPaused = request.IsPaused,
            SimulationTimeSeconds = 10,
            TimeCompression = 1,
            Postcondition = request.IsPaused
                ? "tactical-time-paused"
                : "tactical-time-running",
        };
    }

    private static async Task WaitForConnectedGameAsync(
        HttpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var health = await GetAuthorizedJsonAsync(
                client,
                token,
                "/health",
                cancellationToken);
            if (health.RootElement.GetProperty("gameConnected").GetBoolean())
            {
                Assert.Equal(
                    "connected",
                    health.RootElement.GetProperty("connectionState").GetString());
                Assert.True(health.RootElement.GetProperty("sessionGeneration").GetInt64() >= 1);
                return;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task<JsonDocument> GetAuthorizedJsonAsync(
        HttpClient client,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(ProtocolConstants.ControlTokenHeader, token);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static async Task<JsonDocument> PostAuthorizedJsonAsync(
        HttpClient client,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation(ProtocolConstants.ControlTokenHeader, token);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static async Task<JsonDocument> PostAuthorizedJsonWithBodyAsync(
        HttpClient client,
        string token,
        string path,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ProtocolConstants.ControlTokenHeader, token);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static async Task AssertUnauthorizedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        Assert.Equal(
            "invalid-control-token",
            document.RootElement.GetProperty("error").GetString());
    }

    private static async Task<WireEnvelope> ReadEnvelopeAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var json = await PipeFrameCodec.ReadAsync(
            pipe,
            ProtocolConstants.MaximumFrameBytes,
            cancellationToken);
        Assert.NotNull(json);
        return JsonSerializer.Deserialize<WireEnvelope>(json, JsonOptions)!;
    }

    private static Task WriteEnvelopeAsync(
        Stream pipe,
        WireEnvelope envelope,
        CancellationToken cancellationToken)
    {
        return PipeFrameCodec.WriteAsync(
            pipe,
            JsonSerializer.Serialize(envelope, JsonOptions),
            ProtocolConstants.MaximumFrameBytes,
            cancellationToken);
    }

    private static async Task<ScriptResult> RunControlScriptAsync(
        string scriptPath,
        string action,
        string runDirectory,
        string controlUrl,
        string gamePipeName,
        bool skipBuild,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(action);
        startInfo.ArgumentList.Add("-RunDirectory");
        startInfo.ArgumentList.Add(runDirectory);
        startInfo.ArgumentList.Add("-ControlUrl");
        startInfo.ArgumentList.Add(controlUrl);
        startInfo.ArgumentList.Add("-GamePipeName");
        startInfo.ArgumentList.Add(gamePipeName);
        if (skipBuild)
        {
            startInfo.ArgumentList.Add("-SkipBuild");
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ScriptResult(process.ExitCode);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))
                && File.Exists(Path.Combine(directory.FullName, "scripts", "crimean-tom-control.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Crimean Tom repository root.");
    }

    private static int GetUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "CrimeanTom.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed record ScriptResult(int ExitCode)
    {
        public string FormatFailure(string action)
        {
            return $"Companion {action} failed with exit code {ExitCode}.";
        }
    }
}
