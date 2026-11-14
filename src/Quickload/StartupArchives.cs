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
    private static readonly string directory = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SeaPowerQuickload");
    private readonly Layer[] roots;
    private readonly string context;
    private readonly string segmentContext;
    private readonly Task<InputIndex> inputs;
    private readonly Lazy<Task<bool>> unchanged;
    internal string Profile { get; }

    private StartupArchives(Layer[] roots, string context, string profile)
    {
        this.roots = roots;
        this.context = context;
        segmentContext = Digest(context);
        Profile = profile;
        inputs = Task.Run(IndexInputs);
        unchanged = new(VerifyInputs);
    }

    internal static StartupArchives? Start()
    {
        if (!File.Exists(Path.Combine(directory, "cache-unit-loadouts.enabled"))) return null;
        var roots = Singleton<FileManager>.Instance.Directories.Where(layer => layer.IsChecked)
            .Select(layer => new Layer(layer.DirectoryInfo.FullName, layer.MountPoint, (int)layer.Kind, layer.Name,
                layer.ReplacedFolders.ToArray())).ToArray();
        int year = Utils.DateBasedAmmoYearOverride > 0 ? Utils.DateBasedAmmoYearOverride
            : Singleton<GameEnvironment>.InstanceExists() ? Singleton<GameEnvironment>.Instance.Year : int.MaxValue;
        string context = JsonConvert.SerializeObject(new
        {
            Game = typeof(SimpleObjectParameters).Assembly.ManifestModule.ModuleVersionId,
            Language = Singleton<LanguageResourceHandler>.Instance.Language,
            Year = year,
            LifeRafts = Globals._vesselLifeRafts,
        });
        return new(roots, context, Digest(JsonConvert.SerializeObject(roots)));
    }

    internal bool Includes(string path) => roots.Any(layer => path.StartsWith(layer.Prefix, StringComparison.OrdinalIgnoreCase));

    internal Task<InputIndex> Inputs => inputs;

    private async Task<bool> VerifyInputs() =>
        (await inputs.ConfigureAwait(false)).Full == (await Task.Run(IndexInputs).ConfigureAwait(false)).Full;

    internal string? LayerFor(string path)
    {
        string normalized = Normalize(path);
        return roots.FirstOrDefault(layer => normalized.StartsWith(layer.Prefix, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    internal IEnumerable<string> SegmentLayers => roots.Select(root => root.Id).Distinct();

    internal Task<T?> ReadSegment<T>(string file, int format, Func<Stream, T?> read) where T : class =>
        Read(file, format, read, segment: true);

    internal Task<T?> Read<T>(string file, int format, Func<Stream, T?> read, bool segment = false, bool sensorIndependent = false) where T : class => Task.Run(async () =>
    {
        try
        {
            string? key = sensorIndependent ? (await inputs.ConfigureAwait(false)).SensorIndependent : null;
            if (sensorIndependent && key == null) return null;
            string path = ArchivePath(file, segment, key);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxCacheBytes) return null;
            using var input = File.OpenRead(path);
            using var archive = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(archive, Encoding.UTF8, leaveOpen: true);
            var header = reader.ReadBytes(FingerprintLength);
            if (header.Length != FingerprintLength || reader.ReadInt32() != format)
                return null;
            string tag = key ?? (segment ? segmentContext : (await inputs.ConfigureAwait(false)).Full);
            return Encoding.ASCII.GetString(header) == tag ? read(archive) : null;
        }
        catch (Exception error) when (IsCacheError(error))
        {
            Debug.LogWarning($"[Quickload] Ignoring {file}: {error.Message}");
            return null;
        }
    });

    internal void SaveSegment<T>(string file, int format, Dictionary<string, T> snapshot,
        Func<Stream, Dictionary<string, T>?> read, Action<Stream, Dictionary<string, T>> write) => _ = Task.Run(async () =>
    {
        var previous = await ReadSegment(file, format, read).ConfigureAwait(false);
        if (previous != null)
            foreach (var (key, value) in previous) snapshot.TryAdd(key, value);
        Save(file, format, snapshot, write, segment: true);
    });

    internal void Save<T>(string file, int format, T snapshot, Action<Stream, T> write, bool segment = false, bool sensorIndependent = false) => _ = Task.Run(async () =>
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

    private InputIndex IndexInputs()
    {
        var files = new Dictionary<string, Input>(StringComparer.OrdinalIgnoreCase);
        var sensorFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shared = new List<string> { context };
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
            string layer = JsonConvert.SerializeObject(root);
            full.AppendData(Encoding.UTF8.GetBytes(layer + "\0"));
            bool layerAdded = false;
            byte[]? manifestHash = null;
            if (root.Replaces.Length > 0) AddLayer();
            if (root.Replaces.Any(IsSharedPath)) shared.Add(layer);
            foreach (string path in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                string relative = Normalize(Path.GetRelativePath(root.Path, path));
                string virtualPath = "/" + (root.Mount.Trim('/') is { Length: > 0 } mount ? mount + "/" : "") + relative;
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not (".ini" or ".json" or ".dll")) continue;
                bool library = extension == ".dll" || IsSharedPath(virtualPath);
                byte[] bytes = File.ReadAllBytes(path);
                byte[] fileHash = hash.ComputeHash(bytes);
                full.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
                full.AppendData(fileHash);
                string digest = BitConverter.ToString(fileHash);
                string text = extension == ".ini" ? ReadText(bytes) : "";
                bool sensorFile = IsSensorPath(virtualPath);
                if (sensorFile) sensorFiles[Normalize(path)] = Digest(layer + "\0" + digest);
                bool directives = text.Contains("#!alias", StringComparison.Ordinal) ||
                    text.Contains("#!copy", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("#!extend", StringComparison.Ordinal) || text.Contains("#!prepend", StringComparison.Ordinal);
                // Files that inherit from or modify sensor sources need the full dependency path.
                if (sensorFile && (directives || text.Contains("#!repeat", StringComparison.Ordinal)) ||
                    directives && Normalize(text).Contains("systems/sensors", StringComparison.OrdinalIgnoreCase)) sensorIsolation = false;
                if (string.Equals(relative, "_info.ini", StringComparison.OrdinalIgnoreCase))
                {
                    manifestHash = fileHash;
                    if (layerAdded) withoutSensors.AppendData(manifestHash);
                }
                else if (!sensorFile)
                {
                    AddLayer();
                    withoutSensors.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
                    withoutSensors.AppendData(fileHash);
                }
                if (extension != ".ini" && !library) continue;
                bool hasIncludes = text.Contains("#!alias", StringComparison.Ordinal) ||
                    text.Contains("#!copy", StringComparison.OrdinalIgnoreCase) || text.Contains("#!repeat", StringComparison.OrdinalIgnoreCase);
                bool modifiesLibrary = (text.Contains("#!extend", StringComparison.Ordinal) ||
                    text.Contains("#!prepend", StringComparison.Ordinal)) && ModifiesSharedInput(text);
                files[Normalize(path)] = new(digest, hasIncludes);
                if (library || modifiesLibrary)
                {
                    shared.Add(layer + virtualPath + ":" + digest);
                    if (hasIncludes) CollectSharedAliases(text, aliases, ref uncertain);
                }
                else if (hasIncludes && text.Contains("#!copy", StringComparison.OrdinalIgnoreCase))
                {
                    // Copy source names can change how the game resolves keys in shared INIs.
                    shared.Add(layer + virtualPath + ":" + digest);
                }
            }

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
                shared.Add(path + ":" + input.Hash);
            }
            uncertain |= !resolved;
        }
        string complete = BitConverter.ToString(full.GetHashAndReset()).Replace("-", "");
        string unitInputs = uncertain ? complete : Digest(string.Join("\0", shared));
        return new(files, sensorFiles, unitInputs, complete, sensorIsolation
            ? BitConverter.ToString(withoutSensors.GetHashAndReset()).Replace("-", "") : null);
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

    private static string ReadText(byte[] bytes)
    {
        // The parser accepts directives anywhere in an alias file and also accepts UTF-16 input.
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static string Normalize(string path) => path.Replace('\\', '/');

    internal record Input(string Hash, bool HasIncludes);
    internal record InputIndex(Dictionary<string, Input> Files, Dictionary<string, string> SensorFiles,
        string SharedUnitInputs, string Full, string? SensorIndependent);

    private static bool IsCacheError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException;

    private record Layer(string Path, string Mount, int Kind, string? Name, string[] Replaces)
    {
        [JsonIgnore] internal string Prefix { get; } = Path.Replace('\\', '/').TrimEnd('/') + "/";
        [JsonIgnore] internal string Id { get; } = Digest(Normalize(Path).TrimEnd('/') + "/");
    }
}
