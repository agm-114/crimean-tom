using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SeaPower;
using Debug = UnityEngine.Debug;
using GameEnvironment = SeaPower.Environment;
using Task = System.Threading.Tasks.Task;

namespace Quickload;

internal class StartupArchives
{
    private const int FingerprintLength = 64;
    private const long MaxCacheBytes = 96 * 1024 * 1024;
    private static readonly string directory = StartupPrefetch.DirectoryPath;
    private readonly Layer[] roots;
    private readonly string context;
    private readonly string segmentContext;
    private readonly Task<InputIndex> inputs;
    private readonly Lazy<Task<bool>> unchanged;
    private readonly StartupPrefetch.Job? prefetch;
    private readonly string bundleDirectory;
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> writes = [];
    internal string Profile { get; }
    internal string ReferenceDisplayContext { get; }

    private StartupArchives(Layer[] roots, string context, string profile)
    {
        this.roots = roots;
        this.context = context;
        segmentContext = Digest(context);
        Profile = profile;
        ReferenceDisplayContext = JsonConvert.SerializeObject(new
        {
            NatoNames = OptionsManager.NatoWeaponNamesEnabled,
            Culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            GunElevation = Globals._gunAADirectFireMaxElevationDeg,
        });
        bundleDirectory = Path.Combine(UnityEngine.Application.streamingAssetsPath, "Quickload", "CacheBundles");
        prefetch = StartupPrefetch.Take(profile);
        if (prefetch != null)
            Debug.Log($"[Quickload] Prefetched {prefetch.HashCount:N0} input hashes and {prefetch.ArchiveCount} archives during startup.");
        inputs = Task.Run(() =>
        {
            try { return IndexInputs(prefetch); }
            finally { prefetch?.ReleaseHashes(); }
        });
        unchanged = new(VerifyInputs);
    }

    internal static StartupArchives? Start()
    {
        if (!File.Exists(Path.Combine(directory, "cache-unit-loadouts.enabled"))) return null;
        var roots = Singleton<FileManager>.Instance.Directories.Where(layer => layer.IsChecked)
            .Select(layer => new Layer(layer.DirectoryInfo.FullName, layer.MountPoint, (int)layer.Kind, layer.Name,
                layer.ReplacedFolders.ToArray(), layer.steamId.m_PublishedFileId)).ToArray();
        int year = Utils.DateBasedAmmoYearOverride > 0 ? Utils.DateBasedAmmoYearOverride
            : Singleton<GameEnvironment>.InstanceExists() ? Singleton<GameEnvironment>.Instance.Year : int.MaxValue;
        string context = JsonConvert.SerializeObject(new
        {
            Game = typeof(SimpleObjectParameters).Assembly.ManifestModule.ModuleVersionId,
            Language = Singleton<LanguageResourceHandler>.Instance.Language,
            Year = year,
            LifeRafts = Globals._vesselLifeRafts,
        });
        return new(roots, context, Digest(JsonConvert.SerializeObject(roots.Where(layer => !layer.Tooling).Select(layer => layer.Identity))));
    }

    internal static IEnumerable<string> ActiveRoots() => Singleton<FileManager>.InstanceExists()
        ? Singleton<FileManager>.Instance.Directories.Where(layer => layer.IsChecked).Select(layer => layer.DirectoryInfo.FullName)
        : [];

    internal bool Includes(string path) => FindLayer(path) is { Tooling: false };

    internal Task<InputIndex> Inputs => inputs;
    internal bool ExportRequested { get; } = File.Exists(Path.Combine(directory, "export-bundles.enabled"));
    internal async Task ExportBundles()
    {
        if (!ExportRequested) return;
        try
        {
            int observed;
            do
            {
                var pending = writes.ToArray();
                observed = pending.Length;
                await Task.WhenAll(pending).ConfigureAwait(false);
            } while (observed != writes.Count);
            var index = await inputs.ConfigureAwait(false);
            if (!await unchanged.Value.ConfigureAwait(false)) return;
            string output = Path.Combine(directory, "CacheBundles");
            foreach (var layer in roots.Where(layer => layer.BundleEligible && !layer.Tooling))
            {
                if (!index.Layers.TryGetValue(layer.Id, out string hash)) continue;
                var from = Path.Combine(directory, "segments", layer.Id);
                if (!Directory.Exists(from)) continue;
                var to = BundleLayer(output, layer, hash);
                foreach (string file in new[] { "inis.bin.gz", $"units-{index.SharedUnitInputs}.json.gz",
                    $"weapon-reference-{Digest(index.WeaponInputs + ReferenceDisplayContext)}.json.gz", "unit-reference-calculations.json.gz" })
                    Copy(Path.Combine(from, file), Path.Combine(to, file));
                if (Directory.Exists(to)) File.WriteAllText(Path.Combine(to, "content.sha256"), hash);
            }
            if (IsVanilla)
            {
                var target = VanillaProfile(output);
                foreach (string file in new[] { "resolved-sensors.json.gz", "unit-worker-output.json.gz", "ini-warmup.bin.gz", "encyclopedia.json.gz" })
                    Copy(Path.Combine(directory, Profile, file), Path.Combine(target, file), index.Full);
                PointValueProfiles.ExportVanilla(target);
            }
            Debug.Log($"[Quickload] Cache bundles exported to {output}.");
        }
        catch (Exception error) when (IsCacheError(error))
        {
            Debug.LogWarning($"[Quickload] Could not export cache bundles: {error.Message}");
        }

        static void Copy(string source, string target, string? expected = null)
        {
            if (!File.Exists(source)) return;
            if (expected != null)
            {
                using var compressed = File.OpenRead(source);
                using var archive = new GZipStream(compressed, CompressionMode.Decompress);
                using var reader = new BinaryReader(archive);
                if (Encoding.ASCII.GetString(reader.ReadBytes(FingerprintLength)) != expected) return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".tmp";
            try
            {
                File.Copy(source, temporary, overwrite: true);
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { File.Delete(temporary); }
        }
    }

    private async Task<bool> VerifyInputs() =>
        (await inputs.ConfigureAwait(false)).Full == (await Task.Run(() => IndexInputs()).ConfigureAwait(false)).Full;

    internal void ReleasePrefetch() => prefetch?.ReleaseArchives();

    internal string? LayerFor(string path)
    {
        return FindLayer(path)?.Id;
    }

    internal string PortablePath(string path)
    {
        string normalized = Normalize(path);
        var layer = FindLayer(normalized);
        return layer == null ? normalized : layer.Id + "/" + normalized.Substring(layer.Prefix.Length);
    }

    internal string LocalPath(string path)
    {
        if (path.Length < 66 || path[64] != '/') return path;
        var layer = roots.FirstOrDefault(root => root.Id == path.Substring(0, 64));
        return layer == null ? path : layer.Prefix + path.Substring(65);
    }

    private Layer? FindLayer(string path) => roots.FirstOrDefault(layer =>
        Normalize(path).StartsWith(layer.Prefix, StringComparison.OrdinalIgnoreCase));

    internal IEnumerable<string> SegmentLayers => roots.Where(root => !root.Tooling).Select(root => root.Id).Distinct();

    internal Task<T?> ReadSegment<T>(string file, int format, Func<Stream, T?> read) where T : class =>
        Read(file, format, read, segment: true);

    internal Task<T?> Read<T>(string file, int format, Func<Stream, T?> read, bool segment = false, bool sensorIndependent = false) where T : class => Task.Run(async () =>
    {
        try
        {
            string? key = sensorIndependent ? (await inputs.ConfigureAwait(false)).SensorIndependent : null;
            if (sensorIndependent && key == null) return null;
            string path = ArchivePath(file, segment, key);
            string tag = key ?? (segment ? segmentContext : (await inputs.ConfigureAwait(false)).Full);
            T? cached = ReadArchive(path, tag, format, read);
            if (cached != null) return cached;
            var index = await inputs.ConfigureAwait(false);
            if (segment)
            {
                string id = file.Split('/')[0];
                if (!index.Layers.TryGetValue(id, out string layerHash)) return null;
                var layer = roots.FirstOrDefault(root => root.Id == id);
                if (layer == null || !layer.BundleEligible) return null;
                string directory = BundleLayer(bundleDirectory, layer, layerHash);
                string stamp = Path.Combine(directory, "content.sha256");
                if (!File.Exists(stamp) || File.ReadAllText(stamp) != layerHash) return null;
                cached = ReadArchive(Path.Combine(directory, Path.GetFileName(file)), tag, format, read);
                if (cached != null) Debug.Log($"[Quickload] Restored {Path.GetFileName(file)} from packaged {layer.BundleName} chunks.");
                return cached;
            }
            if (key != null || !IsVanilla) return null;
            string bundled = Path.Combine(VanillaBundle, file);
            cached = ReadArchive(bundled, tag, format, read);
            if (cached != null) Debug.Log($"[Quickload] Restored {file} from the packaged vanilla snapshot.");
            return cached;
        }
        catch (Exception error) when (IsCacheError(error))
        {
            Debug.LogWarning($"[Quickload] Ignoring {file}: {error.Message}");
            return null;
        }
    });

    private T? ReadArchive<T>(string path, string tag, int format, Func<Stream, T?> read) where T : class
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxCacheBytes) return null;
        using var archive = prefetch?.TakeArchive(path) is { } warm
            ? (Stream)new MemoryStream(warm.Data, writable: false)
            : new GZipStream(new MemoryStream(StartupPrefetch.ReadFile(path), writable: false), CompressionMode.Decompress);
        using var reader = new BinaryReader(archive, Encoding.UTF8, leaveOpen: true);
        var header = reader.ReadBytes(FingerprintLength);
        return header.Length == FingerprintLength && reader.ReadInt32() == format &&
            Encoding.ASCII.GetString(header) == tag ? read(archive) : null;
    }

    internal bool IsVanilla => roots.All(root => root.Kind is LayerKind.Base or LayerKind.User || root.Tooling);
    internal string VanillaBundle => VanillaProfile(bundleDirectory);
    private string VanillaProfile(string parent) => Path.Combine(parent, "vanilla", "profiles", Profile.Substring(0, 16));
    private static string BundleLayer(string parent, Layer layer, string hash) => Path.Combine(parent, layer.BundleName, hash.Substring(0, 16));

    internal void SaveSegment<T>(string file, int format, Dictionary<string, T> snapshot,
        Func<Stream, Dictionary<string, T>?> read, Action<Stream, Dictionary<string, T>> write) => Track(Task.Run(async () =>
    {
        var previous = await ReadSegment(file, format, read).ConfigureAwait(false);
        if (previous != null)
            foreach (var (key, value) in previous) snapshot.TryAdd(key, value);
        await WriteArchive(file, format, snapshot, write, segment: true).ConfigureAwait(false);
    }));

    internal void Track(Task task) => writes.Add(task);

    internal void Save<T>(string file, int format, T snapshot, Action<Stream, T> write, bool sensorIndependent = false) =>
        Track(WriteArchive(file, format, snapshot, write, segment: false, sensorIndependent));

    private Task WriteArchive<T>(string file, int format, T snapshot, Action<Stream, T> write, bool segment, bool sensorIndependent = false) =>
        Task.Run(async () =>
    {
        string? temporary = null;
        try
        {
            var index = await inputs.ConfigureAwait(false);
            string? key = sensorIndependent ? index.SensorIndependent : null;
            if (sensorIndependent && key == null) return;
            string path = ArchivePath(file, segment, key);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            if (!await unchanged.Value.ConfigureAwait(false)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = File.Create(temporary))
            using (var archive = new GZipStream(output, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes(key ?? (segment ? segmentContext : index.Full)));
                writer.Write(format);
                write(archive, snapshot);
            }
            if (new FileInfo(temporary).Length > MaxCacheBytes) return;
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            Debug.Log($"[Quickload] Saved {file} ({new FileInfo(path).Length:N0} bytes).");
        }
        catch (Exception error) when (IsCacheError(error))
        {
            Debug.LogWarning($"[Quickload] Could not save {file}: {error.Message}");
        }
        finally
        {
            try { if (temporary != null) File.Delete(temporary); }
            catch (Exception error) when (IsCacheError(error)) { }
        }
    });

    private string ArchivePath(string file, bool segment, string? sensorIndependent) =>
        Path.Combine(directory, sensorIndependent == null ? segment ? "segments" : Profile : "sensor-inputs/" + sensorIndependent, file);

    internal static T? ReadJson<T>(Stream stream, JsonSerializer serializer) where T : class
    {
        using var text = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        using var reader = new JsonTextReader(text) { MaxDepth = 96 };
        var snapshot = serializer.Deserialize<T>(reader) ?? throw new JsonException("Missing snapshot.");
        if (reader.Read()) throw new JsonException("Unexpected data after the snapshot.");
        return snapshot;
    }

    internal static void WriteJson<T>(Stream stream, T snapshot, JsonSerializer serializer)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8, 4096, leaveOpen: true);
        serializer.Serialize(writer, snapshot);
    }

    internal static string Digest(string value)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
    }

    private InputIndex IndexInputs(StartupPrefetch.Job? prefetched = null)
    {
        var files = new Dictionary<string, Input>(StringComparer.OrdinalIgnoreCase);
        var sensorFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var layers = new Dictionary<string, string>(StringComparer.Ordinal);
        var shared = new List<string> { context };
        var weaponInputs = new List<string> { context };
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool uncertain = false;
        bool sensorIsolation = true;
        using var hash = SHA256.Create();
        using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var withoutSensors = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        full.AppendData(Encoding.UTF8.GetBytes(context + Profile));
        withoutSensors.AppendData(Encoding.UTF8.GetBytes(context));
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path)) continue;
            string layer = root.Identity;
            bool tooling = root.Tooling;
            using var layerContent = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (!tooling) full.AppendData(Encoding.UTF8.GetBytes(layer + "\0"));
            bool layerAdded = false;
            byte[]? manifestHash = null;
            if (root.Replaces.Length > 0) AddLayer();
            if (root.Replaces.Any(IsSharedPath)) shared.Add(layer);
            if (root.Replaces.Any(IsSharedPath)) weaponInputs.Add(layer);
            foreach (string path in ContentPaths(root).Where(StartupPrefetch.IsInput)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                string relative = Normalize(Path.GetRelativePath(root.Path, path));
                string virtualPath = "/" + (root.Mount.Trim('/') is { Length: > 0 } mount ? mount + "/" : "") + relative;
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (tooling && (extension == ".dll" || relative == "_info.ini")) continue;
                bool library = extension == ".dll" || IsSharedPath(virtualPath);
                byte[] fileHash;
                string text;
                if (prefetched?.TakeHash(path) is { } warm)
                {
                    fileHash = warm.Hash;
                    text = warm.Text;
                }
                else
                {
                    byte[] bytes = StartupPrefetch.ReadFile(path);
                    fileHash = hash.ComputeHash(bytes);
                    text = StartupPrefetch.InputText(path, bytes);
                }
                byte[] relativeKey = Encoding.UTF8.GetBytes(relative + "\0");
                full.AppendData(relativeKey);
                full.AppendData(fileHash);
                layerContent.AppendData(relativeKey);
                layerContent.AppendData(fileHash);
                string digest = BitConverter.ToString(fileHash);
                bool sensorFile = IsSensorPath(virtualPath);
                if (sensorFile) sensorFiles[PortablePath(path)] = Digest(layer + "\0" + digest);
                bool hasDirective = text.Contains("#!", StringComparison.Ordinal);
                bool alias = hasDirective && text.Contains("#!alias", StringComparison.Ordinal);
                bool copy = hasDirective && text.Contains("#!copy", StringComparison.OrdinalIgnoreCase);
                bool changesSection = hasDirective && (text.Contains("#!extend", StringComparison.Ordinal) ||
                    text.Contains("#!prepend", StringComparison.Ordinal));
                bool directives = alias || copy || changesSection;
                // Files that inherit from or modify sensor sources need the full dependency path.
                if (sensorFile && (directives || hasDirective && text.Contains("#!repeat", StringComparison.Ordinal)) ||
                    directives && Normalize(text).Contains("systems/sensors", StringComparison.OrdinalIgnoreCase)) sensorIsolation = false;
                if (string.Equals(relative, "_info.ini", StringComparison.OrdinalIgnoreCase))
                {
                    manifestHash = fileHash;
                    if (layerAdded) withoutSensors.AppendData(manifestHash);
                }
                else if (!sensorFile)
                {
                    AddLayer();
                    withoutSensors.AppendData(relativeKey);
                    withoutSensors.AppendData(fileHash);
                }
                if (extension != ".ini" && !library) continue;
                bool hasIncludes = alias || copy || hasDirective && text.Contains("#!repeat", StringComparison.OrdinalIgnoreCase);
                bool modifiesLibrary = changesSection && ModifiesSharedInput(text);
                files[Normalize(path)] = new(digest, hasIncludes);
                if (library || modifiesLibrary)
                {
                    shared.Add(layer + virtualPath + ":" + digest);
                    if (hasIncludes) CollectSharedAliases(text, aliases, ref uncertain);
                }
                if (library || modifiesLibrary || hasIncludes)
                    weaponInputs.Add(layer + virtualPath + ":" + digest);
                if (virtualPath.StartsWith("/ammunition/", StringComparison.OrdinalIgnoreCase) && hasIncludes)
                    CollectSharedAliases(text, aliases, ref uncertain);
                else if (copy)
                {
                    // Copy source names can change how the game resolves keys in shared INIs.
                    shared.Add(layer + virtualPath + ":" + digest);
                }
            }

            layers[root.Id] = BitConverter.ToString(layerContent.GetHashAndReset()).Replace("-", "");
            void AddLayer()
            {
                if (layerAdded) return;
                withoutSensors.AppendData(Encoding.UTF8.GetBytes(layer + "\0"));
                if (manifestHash != null) withoutSensors.AppendData(manifestHash);
                layerAdded = true;
            }
        }
        // Library INIs with file directives can pull inputs from any folder.
        foreach (string alias in aliases.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            // Shared libraries generally inherit within their own folders, already included above.
            if (IsSharedPath(alias)) continue;
            bool resolved = false;
            foreach (var root in roots.Where(root => string.IsNullOrEmpty(root.Mount)))
            {
                string path = Normalize(Path.Combine(root.Path, alias));
                if (!files.TryGetValue(path, out var input)) continue;
                resolved = true;
                uncertain |= input.HasIncludes;
                string dependency = PortablePath(path) + ":" + input.Hash;
                shared.Add(dependency);
                weaponInputs.Add(dependency);
            }
            uncertain |= !resolved;
        }
        string complete = BitConverter.ToString(full.GetHashAndReset()).Replace("-", "");
        string unitInputs = uncertain ? complete : Digest(string.Join("\0", shared));
        return new(files, sensorFiles, layers, unitInputs, uncertain ? complete : Digest(string.Join("\0", weaponInputs)), complete, sensorIsolation
            ? BitConverter.ToString(withoutSensors.GetHashAndReset()).Replace("-", "") : null);
    }

    private static IEnumerable<string> ContentPaths(Layer layer)
    {
        foreach (string file in Directory.EnumerateFiles(layer.Path)) yield return file;
        foreach (string folder in Directory.EnumerateDirectories(layer.Path))
        {
            if (layer.Tooling && Path.GetFileName(folder).Equals("CacheBundles", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) yield return file;
        }
    }

    private static bool IsSensorPath(string path) => path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
        (path.Equals("/systems/sensors.ini", StringComparison.OrdinalIgnoreCase) ||
        (path.StartsWith("/systems/sensors/", StringComparison.OrdinalIgnoreCase) && path.IndexOf('/', "/systems/sensors/".Length) < 0));

    private static bool ModifiesSharedInput(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal)) break;
            string directive = line.StartsWith("#!extend", StringComparison.Ordinal) ? "#!extend"
                : line.StartsWith("#!prepend", StringComparison.Ordinal) ? "#!prepend" : "";
            if (directive.Length == 0) continue;
            foreach (string target in line.Replace(directive, "").Split(','))
            {
                if (IsSharedPath(target.Trim())) return true;
            }
        }
        return false;
    }

    private static void CollectSharedAliases(string text, HashSet<string> aliases, ref bool uncertain)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith("#!alias", StringComparison.Ordinal))
                aliases.Add(Normalize(line.Replace("#!alias", "").Trim()).TrimStart('/'));
            if (line.StartsWith("#!copy", StringComparison.OrdinalIgnoreCase))
                foreach (string target in line.Substring(6).Split(','))
                    if (target.Trim().Length > 0) aliases.Add(Normalize(target.Trim()).TrimStart('/'));
            // An alias can be created by a repeat template.
            if (line.StartsWith("#!repeat", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("#!alias", StringComparison.Ordinal)) uncertain = true;
        }
    }

    private static bool IsSharedPath(string path)
    {
        string folder = Normalize(path).TrimStart('/').Split('/')[0].ToLowerInvariant();
        return folder is "ammunition" or "systems" || folder.StartsWith("language_", StringComparison.Ordinal);
    }

    internal static string Normalize(string path) => path.Replace('\\', '/');

    internal record Input(string Hash, bool HasIncludes);
    internal record InputIndex(Dictionary<string, Input> Files, Dictionary<string, string> SensorFiles, Dictionary<string, string> Layers,
        string SharedUnitInputs, string WeaponInputs, string Full, string? SensorIndependent);

    private static bool IsCacheError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException;

    private class Layer
    {
        internal string Path { get; }
        internal string Prefix { get; }
        internal string Mount { get; }
        internal string[] Replaces { get; }
        internal string Identity { get; }
        internal string Id { get; }
        internal ulong Workshop { get; }
        internal LayerKind Kind { get; }
        internal bool Tooling { get; }
        internal bool BundleEligible => Kind == LayerKind.Base || Workshop != 0;
        internal string BundleName => Workshop == 0 ? "vanilla" : $"workshop-{Workshop}";

        internal Layer(string path, string mount, int kind, string? name, string[] replaces, ulong workshop)
        {
            Path = path;
            Prefix = Normalize(path).TrimEnd('/') + "/";
            Mount = mount;
            Replaces = replaces;
            Workshop = workshop;
            Kind = (LayerKind)kind;
            string folder = System.IO.Path.GetFileName(path.TrimEnd('/', '\\')).ToLowerInvariant();
            Tooling = workshop == 0 && Kind == LayerKind.Mod && mount.Length == 0 && replaces.Length == 0 &&
                folder is "quickload" or "seapowerkeel" or "seapowergamebridge";
            Identity = JsonConvert.SerializeObject(new
            {
                Source = workshop != 0 ? $"workshop:{workshop}" : Kind is LayerKind.Base or LayerKind.User or LayerKind.Mod
                    ? folder : Normalize(path),
                Mount = mount, Kind = kind, Name = name, Replaces = replaces,
            });
            Id = Digest(Identity);
        }
    }
}
