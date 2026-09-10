using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SeaPower;
using UnityEngine;

namespace SeaPower.StartupTweaks;

internal static class CatalogueStore
{
    private static string directory = "";
    internal static void Initialize()
    {
        string context = string.Join("\n", new[] { "1", Application.version, Singleton<LanguageResourceHandler>.Instance.Language }
            .Concat(Singleton<FileManager>.Instance.Directories.Where(item => item.IsChecked).Select(item => item.DirectoryInfo.FullName)));
        using var hash = SHA256.Create();
        string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(context))).Replace("-", "");
        directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SeaPowerStartupTweaks", "catalogues", key);
    }
    internal static T? Read<T>(string name) where T : class
    {
        try
        {
            string path = Path.Combine(directory, name + ".json");
            return File.Exists(path) && new FileInfo(path).Length <= 16 * 1024 * 1024
                ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StartupTweaks.Log($"Catalogue cache unavailable: {error.Message}");
            return null;
        }
    }
    internal static void Write<T>(string name, T value)
    {
        string path = Path.Combine(directory, name + ".json");
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonConvert.SerializeObject(value));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            StartupTweaks.Log($"Could not save catalogue: {error.Message}");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
