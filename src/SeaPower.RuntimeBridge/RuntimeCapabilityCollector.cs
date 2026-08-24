using System.Diagnostics;
using System.Globalization;

using CrimeanTom.Protocol;

using SeaPower;

using UnityEngine;
using UnityEngine.SceneManagement;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeCapabilityCollector
{
    public static CapabilitiesPayload Capture(
        RuntimeFileFingerprintProvider fileFingerprintProvider,
        string executionThreadGuardSelfTest)
    {
        var mutatingCommandsEnabled = RuntimeOwnedMutationAdapter.IsArmed;
        var fileIdentity = fileFingerprintProvider.GetSnapshot();
        var fileManagerAvailable = Singleton<FileManager>.InstanceExists();
        if (fileManagerAvailable)
        {
            fileFingerprintProvider.EnsureContentFingerprint(Singleton<FileManager>.Instance);
        }
        var contentIdentity = fileFingerprintProvider.GetContentSnapshot();
        var scene = SceneManager.GetActiveScene();
        var optionsManagerAvailable = Singleton<OptionsManager>.InstanceExists();
        var options = optionsManagerAvailable
            ? Singleton<OptionsManager>.Instance
            : null;

        var settings = new RuntimeSimulationSettingsPayload
        {
            OptionsManagerAvailable = optionsManagerAvailable,
            UseAssetBundles = Globals._useAssetBundles,
            UseDatalinks = Globals._useDatalinks,
            SaveLoadSensorData = SaveLoadManager.SaveLoadSensorData,
            SaveLoadFlightDeck = SaveLoadManager.SaveLoadFlightDeck,
            Difficulty = optionsManagerAvailable ? Globals._currentDificulty : null,
            PlayerAutoAttackSurface = options?.PlayerAutoAttackSurface,
            RestrictAirOpsBySeaState = options?.RestrictAirOpsBySeaState,
            AllowAssociatedSearchSensors = options?.AllowAssociatedSearchSensors,
            UseImprovedSonarModel = options?.UseImprovedSonarModel,
            AswSimulation = options?.AswSimulation.ToString(),
            FlightDeckTimingsMode = options?.FlightDeckTimingsMode.ToString(),
            FloodingCheckSeconds = Globals._shipFloodingCheckTime,
            FireCheckSeconds = Globals._shipFireCheckTime,
        };

        var hooks = RuntimeDeterministicHookFingerprintProvider.Capture();
        var uiAutomationSeams = RuntimeUiAutomationSeamFingerprintProvider.Capture();
        var verifiedUiAutomationCommands = uiAutomationSeams
            .Where(seam => string.Equals(seam.Status, "verified", StringComparison.Ordinal))
            .SelectMany(seam => seam.Commands)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(command => Array.IndexOf(GameCommandTypes.UiAutomationCommands, command))
            .ToArray();
        var blockers = GetAuthorityBlockers(
            fileIdentity,
            contentIdentity,
            optionsManagerAvailable,
            hooks,
            mutatingCommandsEnabled,
            executionThreadGuardSelfTest);
        var gameVersion = Application.version ?? string.Empty;
        var pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";
        var loadAcceleration = fileFingerprintProvider.GetLoadAccelerationSnapshot();

        var runtimeFingerprint = new RuntimeCapabilityFingerprintPayload
        {
            SchemaVersion = 5,
            FileIdentityState = fileIdentity.State,
            FileIdentityError = fileIdentity.Error,
            GameVersion = gameVersion,
            PluginVersion = pluginVersion,
            ProcessId = Process.GetCurrentProcess().Id,
            ActiveScene = scene.name ?? string.Empty,
            SceneLoaded = scene.isLoaded,
            GameExecutableSha256 = fileIdentity.GameExecutableSha256,
            ManagedAssemblies = fileIdentity.ManagedAssemblies
                .Select(assembly => new RuntimeAssemblyFingerprintPayload
                {
                    Name = assembly.Name,
                    Sha256 = assembly.Sha256,
                })
                .ToArray(),
            PluginAssemblySha256 = fileIdentity.PluginAssemblySha256,
            ContentIdentityState = contentIdentity.State,
            ContentIdentityError = contentIdentity.Error,
            CatalogSha256 = contentIdentity.CatalogSha256,
            ModSetSha256 = contentIdentity.ModSetSha256,
            CatalogFileCount = contentIdentity.CatalogFileCount,
            ActiveContentSources = contentIdentity.Sources
                .Select(source => new RuntimeContentSourcePayload
                {
                    LoadOrder = source.LoadOrder,
                    SourceId = source.SourceId,
                    DirectoryName = source.DirectoryName,
                    IsUserData = source.IsUserData,
                    IsBaseGame = source.IsBaseGame,
                    IsSteamWorkshop = source.IsSteamWorkshop,
                    WinningCatalogFileCount = source.WinningCatalogFileCount,
                })
                .ToArray(),
            SimulationSettings = settings,
            Timing = new RuntimeTimingPayload
            {
                EngineFixedDeltaTime = Time.fixedDeltaTime,
                GameFixedDeltaTime = GameTime.fixedDeltaTime,
                TimeScale = Time.timeScale,
                TimeCompression = GameTime.TimeCompression,
                InternalTimeDilation = GameTime.TimeDilation,
            },
            DeterministicHooks = hooks,
            UiAutomationSeams = uiAutomationSeams,
            LoadAcceleration = new RuntimeLoadAccelerationPayload
            {
                FileHashCacheState = loadAcceleration.State,
                FileHashCacheEntryCount = loadAcceleration.EntryCount,
                FileHashCacheHitCount = loadAcceleration.HitCount,
                FileHashCacheMissCount = loadAcceleration.MissCount,
            },
            AuthorityEligible = blockers.Count == 0,
            AuthorityBlockers = blockers.ToArray(),
        };
        runtimeFingerprint.FingerprintSha256 = ComputeFingerprint(runtimeFingerprint);

        var supportedExperimentalCommands = new List<string>();
        if (RuntimeDeterministicHookFingerprintProvider.IsVerified("logical-time"))
        {
            supportedExperimentalCommands.Add(GameCommandTypes.SetExperimentalPauseState);
            supportedExperimentalCommands.Add(GameCommandTypes.SetExperimentalTimeCompression);
        }

        return new CapabilitiesPayload
        {
            ProtocolVersion = ProtocolConstants.Version,
            SupportedCommands = GameCommandTypes.AllCommands,
            SupportedUiAutomationCommands = verifiedUiAutomationCommands,
            UiAutomationEnabled = verifiedUiAutomationCommands.Length > 0,
            ExperimentalRuntimeMode = "ControlledExperimental",
            SupportedExperimentalCommands = supportedExperimentalCommands.ToArray(),
            MutatingCommandsEnabled = mutatingCommandsEnabled,
            MutationMode = mutatingCommandsEnabled
                ? "owned-mutations-armed"
                : "disabled",
            SupportedMutationCommands = mutatingCommandsEnabled
                ? GameCommandTypes.MutationCommands
                : [],
            OwnedSurfaceMaterializationProfiles =
                RuntimeOwnedMutationAdapter.SupportedProfiles(),
            OwnedLandMaterializationProfiles =
                RuntimeOwnedLandMutationAdapter.SupportedProfiles(),
            PlayerTaskforceId = RuntimeOwnedMutationAdapter.GetPlayerTaskforceId(),
            MutationModeExpiresAtUnixMilliseconds =
                RuntimeOwnedMutationAdapter.ExpiresAtUnixMilliseconds,
            CommandCancellationSupported = true,
            SupportedPriorities = CommandPriorities.Supported,
            ExecutionThreadGuardSelfTest = executionThreadGuardSelfTest,
            RuntimeFingerprint = runtimeFingerprint,
        };
    }

    private static List<string> GetAuthorityBlockers(
        RuntimeFileFingerprintSnapshot fileIdentity,
        RuntimeContentFingerprintSnapshot contentIdentity,
        bool optionsManagerAvailable,
        IEnumerable<DeterministicHookCapabilityPayload> hooks,
        bool mutatingCommandsEnabled,
        string executionThreadGuardSelfTest)
    {
        var blockers = new List<string>();
        if (!string.Equals(
            fileIdentity.State,
            RuntimeFileFingerprintSnapshot.ReadyState,
            StringComparison.Ordinal))
        {
            blockers.Add($"file-identity-{fileIdentity.State}");
        }

        if (!optionsManagerAvailable)
        {
            blockers.Add("options-manager-unavailable");
        }

        if (!string.Equals(
            contentIdentity.State,
            RuntimeContentFingerprintSnapshot.ReadyState,
            StringComparison.Ordinal))
        {
            blockers.Add($"content-identity-{contentIdentity.State}");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(contentIdentity.CatalogSha256))
            {
                blockers.Add("catalog-fingerprint-unavailable");
            }
            if (string.IsNullOrWhiteSpace(contentIdentity.ModSetSha256))
            {
                blockers.Add("mod-set-fingerprint-unavailable");
            }
        }
        blockers.AddRange(hooks
            .Where(hook => !string.Equals(hook.Status, "verified", StringComparison.Ordinal))
            .Select(hook => $"deterministic-hook-{hook.Name}-{hook.Status}"));

        if (!mutatingCommandsEnabled)
        {
            blockers.Add("mutating-commands-disabled");
        }

        if (!string.Equals(
            executionThreadGuardSelfTest,
            ExecutionThreadGuardSelfTest.Passed,
            StringComparison.Ordinal))
        {
            blockers.Add(string.Equals(
                executionThreadGuardSelfTest,
                ExecutionThreadGuardSelfTest.Pending,
                StringComparison.Ordinal)
                    ? "execution-thread-guard-self-test-pending"
                    : "execution-thread-guard-self-test-failed");
        }

        return blockers;
    }

    private static string ComputeFingerprint(RuntimeCapabilityFingerprintPayload fingerprint)
    {
        var settings = fingerprint.SimulationSettings;
        var fields = new List<KeyValuePair<string, string?>>
        {
            Field("schema-version", fingerprint.SchemaVersion),
            Field("protocol-version", ProtocolConstants.Version),
            Field("game-version", fingerprint.GameVersion),
            Field("plugin-version", fingerprint.PluginVersion),
            Field("file-identity-state", fingerprint.FileIdentityState),
            Field("game-executable-sha256", fingerprint.GameExecutableSha256),
            Field("plugin-assembly-sha256", fingerprint.PluginAssemblySha256),
            Field("content-identity-state", fingerprint.ContentIdentityState),
            Field("catalog-sha256", fingerprint.CatalogSha256),
            Field("mod-set-sha256", fingerprint.ModSetSha256),
            Field("catalog-file-count", fingerprint.CatalogFileCount),
            Field("options-manager-available", settings.OptionsManagerAvailable),
            Field("use-asset-bundles", settings.UseAssetBundles),
            Field("use-datalinks", settings.UseDatalinks),
            Field("save-load-sensor-data", settings.SaveLoadSensorData),
            Field("save-load-flight-deck", settings.SaveLoadFlightDeck),
            Field("difficulty", settings.Difficulty),
            Field("player-auto-attack-surface", settings.PlayerAutoAttackSurface),
            Field("restrict-air-ops-by-sea-state", settings.RestrictAirOpsBySeaState),
            Field("allow-associated-search-sensors", settings.AllowAssociatedSearchSensors),
            Field("use-improved-sonar-model", settings.UseImprovedSonarModel),
            Field("asw-simulation", settings.AswSimulation),
            Field("flight-deck-timings-mode", settings.FlightDeckTimingsMode),
            Field("flooding-check-seconds", settings.FloodingCheckSeconds),
            Field("fire-check-seconds", settings.FireCheckSeconds),
        };

        foreach (var assembly in fingerprint.ManagedAssemblies.OrderBy(
            assembly => assembly.Name,
            StringComparer.Ordinal))
        {
            fields.Add(Field($"managed-assembly.{assembly.Name}.sha256", assembly.Sha256));
        }

        foreach (var source in fingerprint.ActiveContentSources.OrderBy(
            source => source.LoadOrder))
        {
            fields.Add(Field($"content-source.{source.LoadOrder}.id", source.SourceId));
            fields.Add(Field(
                $"content-source.{source.LoadOrder}.directory",
                source.DirectoryName));
            fields.Add(Field(
                $"content-source.{source.LoadOrder}.winning-catalog-files",
                source.WinningCatalogFileCount));
        }

        foreach (var hook in fingerprint.DeterministicHooks.OrderBy(
            hook => hook.Name,
            StringComparer.Ordinal))
        {
            fields.Add(Field($"hook.{hook.Name}.status", hook.Status));
            fields.Add(Field(
                $"hook.{hook.Name}.method-fingerprint-sha256",
                hook.MethodFingerprintSha256));
        }

        foreach (var seam in fingerprint.UiAutomationSeams.OrderBy(
            seam => seam.Name,
            StringComparer.Ordinal))
        {
            fields.Add(Field($"ui-automation.{seam.Name}.status", seam.Status));
            fields.Add(Field(
                $"ui-automation.{seam.Name}.method-fingerprint-sha256",
                seam.MethodFingerprintSha256));
        }

        return CapabilityFingerprint.ComputeSha256(fields);
    }

    private static KeyValuePair<string, string?> Field(string name, object? value)
    {
        return new KeyValuePair<string, string?>(name, Canonical(value));
    }

    private static string? Canonical(object? value)
    {
        return value switch
        {
            null => null,
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }
}
