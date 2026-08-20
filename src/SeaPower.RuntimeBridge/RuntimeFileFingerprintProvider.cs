using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using CrimeanTom.Protocol;
using CrimeanTom.LoadAcceleration;

using SeaPower;

namespace SeaPowerRuntimeBridge;

internal sealed class RuntimeFileFingerprintProvider
{
    private static readonly HashSet<string> CatalogExtensions = new(
        new[] { ".ini", ".json", ".xml" },
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CatalogRoots =
    {
        "aircraft",
        "ammunition",
        "biologic",
        "formations",
        "land_units",
        "systems",
        "vessels",
    };

    private static readonly string[] RequiredManagedAssemblies =
    {
        "Seapower-Scripts.dll",
        "Unity.Burst.dll",
        "Unity.Collections.dll",
        "Unity.Entities.dll",
        "Unity.Mathematics.dll",
        "UnityEngine.CoreModule.dll",
        "UnityEngine.PhysicsModule.dll",
        "UnityEngine.TerrainPhysicsModule.dll",
    };

    private volatile RuntimeFileFingerprintSnapshot _snapshot
        = RuntimeFileFingerprintSnapshot.Pending;
    private volatile RuntimeContentFingerprintSnapshot _contentSnapshot
        = RuntimeContentFingerprintSnapshot.Pending;
    private int _contentFingerprintStarted;
    private PersistentFileHashCache _hashCache = new(
        Path.Combine(Path.GetTempPath(), "crimean-tom-disabled-file-hash-cache.tsv"),
        enabled: false,
        refresh: false);

    private RuntimeFileFingerprintProvider()
    {
        try
        {
            var executablePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("The game executable path is unavailable.");
            var managedDirectory = Path.GetDirectoryName(typeof(GameMain).Assembly.Location)
                ?? throw new InvalidOperationException("The managed assembly directory is unavailable.");
            var pluginAssemblyPath = typeof(Plugin).Assembly.Location;
            var cacheDirectory = Path.Combine(
                Path.GetDirectoryName(pluginAssemblyPath)!,
                "cache");
            _hashCache = new PersistentFileHashCache(
                Path.Combine(cacheDirectory, "file-hashes.v1.tsv"),
                enabled: !string.Equals(
                    System.Environment.GetEnvironmentVariable("CRIMEAN_TOM_DISABLE_LOAD_CACHE"),
                    "1",
                    StringComparison.Ordinal),
                refresh: string.Equals(
                    System.Environment.GetEnvironmentVariable("CRIMEAN_TOM_REFRESH_LOAD_CACHE"),
                    "1",
                    StringComparison.Ordinal));

            _ = System.Threading.Tasks.Task.Run(() => HashFiles(
                executablePath,
                managedDirectory,
                pluginAssemblyPath));
        }
        catch (Exception exception)
        {
            _snapshot = RuntimeFileFingerprintSnapshot.Failed(Sanitize(exception));
        }
    }

    public static RuntimeFileFingerprintProvider Start() => new();

    public RuntimeFileFingerprintSnapshot GetSnapshot() => _snapshot;

    public RuntimeContentFingerprintSnapshot GetContentSnapshot() => _contentSnapshot;

    public FileHashCacheSnapshot GetLoadAccelerationSnapshot()
        => _hashCache.CaptureSnapshot();

    public void EnsureContentFingerprint(FileManager fileManager)
    {
        if (Interlocked.CompareExchange(ref _contentFingerprintStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var userPath = fileManager.specialDirectories.Length > 0
                ? NormalizePath(fileManager.specialDirectories[0].FullName)
                : string.Empty;
            var basePath = fileManager.specialDirectories.Length > 1
                ? NormalizePath(fileManager.specialDirectories[1].FullName)
                : string.Empty;
            var sources = fileManager.Directories
                .Where(directory => directory.IsChecked)
                .Select((directory, index) =>
                {
                    var fullPath = directory.DirectoryInfo.FullName;
                    var normalizedPath = NormalizePath(fullPath);
                    var isUserData = string.Equals(
                        normalizedPath,
                        userPath,
                        StringComparison.OrdinalIgnoreCase);
                    var isBaseGame = string.Equals(
                        normalizedPath,
                        basePath,
                        StringComparison.OrdinalIgnoreCase);
                    var sourceId = isUserData
                        ? "user"
                        : isBaseGame
                            ? "base"
                            : directory.IsSteam
                                ? $"steam:{directory.DirectoryInfo.Name.ToLowerInvariant()}"
                                : $"local:{directory.DirectoryInfo.Name.ToLowerInvariant()}";
                    return new RuntimeContentSourceDescriptor(
                        index,
                        sourceId,
                        directory.DirectoryInfo.Name,
                        fullPath,
                        isUserData,
                        isBaseGame,
                        directory.IsSteam);
                })
                .ToArray();

            _ = System.Threading.Tasks.Task.Run(() => HashContent(sources));
        }
        catch (Exception exception)
        {
            _contentSnapshot = RuntimeContentFingerprintSnapshot.Failed(SanitizeContent(exception));
        }
    }

    private void HashFiles(
        string executablePath,
        string managedDirectory,
        string pluginAssemblyPath)
    {
        try
        {
            var managedAssemblies = RequiredManagedAssemblies
                .Select(name => new RuntimeManagedAssemblyFingerprint(
                    name,
                    HashFile(Path.Combine(managedDirectory, name))))
                .ToArray();
            _snapshot = RuntimeFileFingerprintSnapshot.Ready(
                HashFile(executablePath),
                managedAssemblies,
                HashFile(pluginAssemblyPath));
            _hashCache.SaveFailOpen();
        }
        catch (Exception exception)
        {
            _snapshot = RuntimeFileFingerprintSnapshot.Failed(Sanitize(exception));
        }
    }

    private string HashFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("A capability fingerprint input is unavailable.");
        }
        return _hashCache.GetOrComputeSha256(path);
    }

    private void HashContent(RuntimeContentSourceDescriptor[] sources)
    {
        try
        {
            var winningFiles = new Dictionary<string, RuntimeCatalogFile>(
                StringComparer.OrdinalIgnoreCase);
            var winningCounts = sources.ToDictionary(
                source => source.SourceId,
                _ => 0,
                StringComparer.Ordinal);
            var overrides = new List<RuntimeCatalogOverride>();

            foreach (var source in sources.OrderBy(source => source.LoadOrder))
            {
                foreach (var catalogRoot in CatalogRoots)
                {
                    var physicalRoot = Path.Combine(source.RootPath, catalogRoot);
                    if (!Directory.Exists(physicalRoot))
                    {
                        continue;
                    }

                    foreach (var filePath in Directory.GetFiles(
                        physicalRoot,
                        "*",
                        SearchOption.AllDirectories))
                    {
                        if (!CatalogExtensions.Contains(Path.GetExtension(filePath)))
                        {
                            continue;
                        }

                        var relativePath = NormalizeVirtualPath(
                            Path.GetRelativePath(source.RootPath, filePath));
                        if (winningFiles.TryGetValue(relativePath, out var winningFile))
                        {
                            var shadowedSha256 = HashFile(filePath);
                            overrides.Add(new RuntimeCatalogOverride(
                                relativePath,
                                winningFile.SourceId,
                                winningFile.Sha256,
                                source.SourceId,
                                shadowedSha256,
                                string.Equals(
                                    winningFile.Sha256,
                                    shadowedSha256,
                                    StringComparison.Ordinal)
                                    ? "identical-override"
                                    : "conflicting-override"));
                            continue;
                        }

                        winningFiles.Add(relativePath, new RuntimeCatalogFile(
                            relativePath,
                            source.SourceId,
                            HashFile(filePath)));
                        winningCounts[source.SourceId]++;
                    }
                }
            }

            var catalogFields = winningFiles.Values
                .OrderBy(file => file.VirtualPath, StringComparer.Ordinal)
                .SelectMany(file => new[]
                {
                    new KeyValuePair<string, string?>(
                        $"file.{file.VirtualPath}.source",
                        file.SourceId),
                    new KeyValuePair<string, string?>(
                        $"file.{file.VirtualPath}.sha256",
                        file.Sha256),
                });
            var catalogSha256 = CapabilityFingerprint.ComputeSha256(catalogFields);

            var modSetFields = sources
                .OrderBy(source => source.LoadOrder)
                .SelectMany(source => new[]
                {
                    new KeyValuePair<string, string?>(
                        $"source.{source.LoadOrder}.id",
                        source.SourceId),
                    new KeyValuePair<string, string?>(
                        $"source.{source.LoadOrder}.directory",
                        source.DirectoryName),
                    new KeyValuePair<string, string?>(
                        $"source.{source.LoadOrder}.info-sha256",
                        HashOptionalFile(Path.Combine(source.RootPath, "_info.ini"))),
                });
            var modSetSha256 = CapabilityFingerprint.ComputeSha256(modSetFields);

            _contentSnapshot = RuntimeContentFingerprintSnapshot.Ready(
                catalogSha256,
                modSetSha256,
                winningFiles.Count,
                winningFiles.Values.ToArray(),
                overrides
                    .OrderBy(item => item.VirtualPath, StringComparer.Ordinal)
                    .ThenBy(item => item.ShadowedSourceId, StringComparer.Ordinal)
                    .ToArray(),
                sources.Select(source => new RuntimeContentSourceFingerprint(
                    source.LoadOrder,
                    source.SourceId,
                    source.DirectoryName,
                    source.IsUserData,
                    source.IsBaseGame,
                    source.IsSteamWorkshop,
                    winningCounts[source.SourceId])).ToArray());
            _hashCache.SaveFailOpen();
        }
        catch (Exception exception)
        {
            _contentSnapshot = RuntimeContentFingerprintSnapshot.Failed(SanitizeContent(exception));
        }
    }

    private string? HashOptionalFile(string path)
    {
        return File.Exists(path) ? HashFile(path) : null;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeVirtualPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var result = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
        {
            result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    private static string Sanitize(Exception exception)
    {
        return $"{exception.GetType().Name}: file identity could not be computed";
    }

    private static string SanitizeContent(Exception exception)
    {
        return $"{exception.GetType().Name}: content identity could not be computed";
    }
}

internal sealed record RuntimeContentSourceDescriptor(
    int LoadOrder,
    string SourceId,
    string DirectoryName,
    string RootPath,
    bool IsUserData,
    bool IsBaseGame,
    bool IsSteamWorkshop);

internal sealed record RuntimeCatalogFile(
    string VirtualPath,
    string SourceId,
    string Sha256);

internal sealed record RuntimeCatalogOverride(
    string VirtualPath,
    string WinningSourceId,
    string WinningSha256,
    string ShadowedSourceId,
    string ShadowedSha256,
    string Classification);

internal sealed class RuntimeFileFingerprintSnapshot
{
    public const string PendingState = "pending";
    public const string ReadyState = "ready";
    public const string FailedState = "failed";

    private RuntimeFileFingerprintSnapshot(
        string state,
        string? error,
        string? gameExecutableSha256,
        RuntimeManagedAssemblyFingerprint[] managedAssemblies,
        string? pluginAssemblySha256)
    {
        State = state;
        Error = error;
        GameExecutableSha256 = gameExecutableSha256;
        ManagedAssemblies = managedAssemblies;
        PluginAssemblySha256 = pluginAssemblySha256;
    }

    public static RuntimeFileFingerprintSnapshot Pending { get; } = new(
        PendingState,
        null,
        null,
        Array.Empty<RuntimeManagedAssemblyFingerprint>(),
        null);

    public string State { get; }

    public string? Error { get; }

    public string? GameExecutableSha256 { get; }

    public RuntimeManagedAssemblyFingerprint[] ManagedAssemblies { get; }

    public string? PluginAssemblySha256 { get; }

    public static RuntimeFileFingerprintSnapshot Ready(
        string gameExecutableSha256,
        RuntimeManagedAssemblyFingerprint[] managedAssemblies,
        string pluginAssemblySha256) => new(
            ReadyState,
        null,
        gameExecutableSha256,
        managedAssemblies,
        pluginAssemblySha256);

    public static RuntimeFileFingerprintSnapshot Failed(string error) => new(
        FailedState,
        error,
        null,
        Array.Empty<RuntimeManagedAssemblyFingerprint>(),
        null);
}

internal sealed class RuntimeManagedAssemblyFingerprint
{
    public RuntimeManagedAssemblyFingerprint(string name, string sha256)
    {
        Name = name;
        Sha256 = sha256;
    }

    public string Name { get; }

    public string Sha256 { get; }
}

internal sealed class RuntimeContentFingerprintSnapshot
{
    public const string PendingState = "pending";
    public const string ReadyState = "ready";
    public const string FailedState = "failed";

    private RuntimeContentFingerprintSnapshot(
        string state,
        string? error,
        string? catalogSha256,
        string? modSetSha256,
        int catalogFileCount,
        RuntimeCatalogFile[] files,
        RuntimeCatalogOverride[] overrides,
        RuntimeContentSourceFingerprint[] sources)
    {
        State = state;
        Error = error;
        CatalogSha256 = catalogSha256;
        ModSetSha256 = modSetSha256;
        CatalogFileCount = catalogFileCount;
        Files = files;
        Overrides = overrides;
        Sources = sources;
    }

    public static RuntimeContentFingerprintSnapshot Pending { get; } = new(
        PendingState,
        null,
        null,
        null,
        0,
        Array.Empty<RuntimeCatalogFile>(),
        Array.Empty<RuntimeCatalogOverride>(),
        Array.Empty<RuntimeContentSourceFingerprint>());

    public string State { get; }

    public string? Error { get; }

    public string? CatalogSha256 { get; }

    public string? ModSetSha256 { get; }

    public int CatalogFileCount { get; }

    public RuntimeCatalogFile[] Files { get; }

    public RuntimeCatalogOverride[] Overrides { get; }

    public RuntimeContentSourceFingerprint[] Sources { get; }

    public static RuntimeContentFingerprintSnapshot Ready(
        string catalogSha256,
        string modSetSha256,
        int catalogFileCount,
        RuntimeCatalogFile[] files,
        RuntimeCatalogOverride[] overrides,
        RuntimeContentSourceFingerprint[] sources) => new(
            ReadyState,
            null,
            catalogSha256,
            modSetSha256,
            catalogFileCount,
            files,
            overrides,
            sources);

    public static RuntimeContentFingerprintSnapshot Failed(string error) => new(
        FailedState,
        error,
        null,
        null,
        0,
        Array.Empty<RuntimeCatalogFile>(),
        Array.Empty<RuntimeCatalogOverride>(),
        Array.Empty<RuntimeContentSourceFingerprint>());

    public RuntimeCatalogFile? FindWinningFile(string virtualPath) => Files.FirstOrDefault(
        file => string.Equals(file.VirtualPath, virtualPath, StringComparison.OrdinalIgnoreCase));
}

internal sealed record RuntimeContentSourceFingerprint(
    int LoadOrder,
    string SourceId,
    string DirectoryName,
    bool IsUserData,
    bool IsBaseGame,
    bool IsSteamWorkshop,
    int WinningCatalogFileCount);
