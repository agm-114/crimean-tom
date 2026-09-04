using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrimeanTom.Operations;

public sealed class CampaignStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public CampaignState Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<CampaignState>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Campaign file '{path}' was empty.");
    }

    public void Save(string path, CampaignState campaign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(campaign);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Campaign path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        string temporaryPath = $"{fullPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, campaign, JsonOptions);
                stream.Flush(true);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void SaveAfterAction(string path, AfterActionReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, JsonOptions));
    }

    public AfterActionReport LoadAfterAction(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AfterActionReport>(stream, JsonOptions)
            ?? throw new InvalidDataException($"After-action file '{path}' was empty.");
    }
}
