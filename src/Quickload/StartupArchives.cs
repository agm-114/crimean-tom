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
    private readonly Task<string> fingerprint;

    private StartupArchives(Layer[] roots, string context)
    {
        this.roots = roots;
        this.context = context;
        fingerprint = Task.Run(Fingerprint);
    }

    internal static StartupArchives? Start()
    {
        if (!File.Exists(Path.Combine(directory, "cache-unit-loadouts.enabled"))) return null;
        var roots = Singleton<FileManager>.Instance.Directories.Where(layer => layer.IsChecked)
            .Select(layer => new Layer(layer.DirectoryInfo.FullName, layer.MountPoint, (int)layer.Kind,
                layer.ReplacedFolders.ToArray())).ToArray();
        int year = Utils.DateBasedAmmoYearOverride > 0 ? Utils.DateBasedAmmoYearOverride
            : Singleton<GameEnvironment>.InstanceExists() ? Singleton<GameEnvironment>.Instance.Year : int.MaxValue;
        string context = JsonConvert.SerializeObject(new
        {
            Game = typeof(SimpleObjectParameters).Assembly.ManifestModule.ModuleVersionId,
            Language = Singleton<LanguageResourceHandler>.Instance.Language,
            Year = year,
            Roots = roots,
            LifeRafts = Globals._vesselLifeRafts,
        });
        return new(roots, context);
    }

    internal bool Includes(string path) => roots.Any(layer => path.StartsWith(layer.Prefix, StringComparison.OrdinalIgnoreCase));

    internal Task<T?> Read<T>(string file, int format, Func<Stream, T?> read) where T : class => Task.Run(async () =>
    {
        string path = Path.Combine(directory, file);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxCacheBytes) return null;
            using var input = File.OpenRead(path);
            using var archive = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(archive, Encoding.UTF8, leaveOpen: true);
            var header = reader.ReadBytes(FingerprintLength);
            if (header.Length != FingerprintLength || reader.ReadInt32() != format)
                return null;
            var snapshot = read(archive);
            return Encoding.ASCII.GetString(header) == await fingerprint.ConfigureAwait(false) ? snapshot : null;
        }
        catch (Exception error) when (IsCacheError(error))
        {
            Debug.LogWarning($"[Quickload] Ignoring {file}: {error.Message}");
            return null;
        }
    });

    internal void Save<T>(string file, int format, T snapshot, Action<Stream, T> write) => _ = Task.Run(async () =>
    {
        string path = Path.Combine(directory, file);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string tag = await fingerprint.ConfigureAwait(false);
            if (Fingerprint() != tag) return;
            using (var output = File.Create(temporary))
            using (var archive = new GZipStream(output, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(archive, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes(tag));
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
            try { File.Delete(temporary); }
            catch (Exception error) when (IsCacheError(error)) { }
        }
    });

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

    private string Fingerprint()
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fileHash = SHA256.Create();
        void Append(string value) => digest.AppendData(Encoding.UTF8.GetBytes(value + "\0"));
        Append(context);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path)) continue;
            foreach (string path in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".ini" or ".json" or ".dll")
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                Append(Path.GetRelativePath(root.Path, path));
                using var input = File.OpenRead(path);
                digest.AppendData(fileHash.ComputeHash(input));
            }
        }
        return BitConverter.ToString(digest.GetHashAndReset()).Replace("-", "");
    }

    private static bool IsCacheError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException;

    private record Layer(string Path, string Mount, int Kind, string[] Replaces)
    {
        [JsonIgnore] internal string Prefix { get; } = Path.Replace('\\', '/').TrimEnd('/') + "/";
    }
}
