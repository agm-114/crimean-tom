using CrimeanTom.Protocol;

using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal sealed class RuntimeInstanceConfiguration
{
    private const string InstanceIdEnvironmentVariable = "CRIMEAN_TOM_INSTANCE_ID";
    private const string PipeNameEnvironmentVariable = "CRIMEAN_TOM_GAME_PIPE_NAME";
    private const string SaveDirectoryEnvironmentVariable =
        "CRIMEAN_TOM_EXPERIMENTAL_SAVE_DIRECTORY";

    private static readonly Lazy<RuntimeInstanceConfiguration> CurrentValue = new(Load);

    private RuntimeInstanceConfiguration(
        string instanceId,
        string pipeName,
        string experimentalSaveDirectory)
    {
        InstanceId = instanceId;
        PipeName = pipeName;
        ExperimentalSaveDirectory = experimentalSaveDirectory;
    }

    public static RuntimeInstanceConfiguration Current => CurrentValue.Value;

    public string InstanceId { get; }

    public string PipeName { get; }

    public string ExperimentalSaveDirectory { get; }

    public string GetExperimentalSavePath(string fileName)
    {
        Directory.CreateDirectory(ExperimentalSaveDirectory);
        return Path.Combine(ExperimentalSaveDirectory, fileName);
    }

    private static RuntimeInstanceConfiguration Load()
    {
        var instanceId = ReadRestrictedValue(
            InstanceIdEnvironmentVariable,
            "default",
            64);
        var pipeName = ReadRestrictedValue(
            PipeNameEnvironmentVariable,
            ProtocolConstants.GamePipeName,
            200);
        var configuredSaveDirectory = Environment.GetEnvironmentVariable(
            SaveDirectoryEnvironmentVariable);
        var saveDirectory = string.IsNullOrWhiteSpace(configuredSaveDirectory)
            ? Path.Combine(Application.persistentDataPath, "Saves")
            : Path.GetFullPath(configuredSaveDirectory);
        Directory.CreateDirectory(saveDirectory);
        return new RuntimeInstanceConfiguration(instanceId, pipeName, saveDirectory);
    }

    private static string ReadRestrictedValue(
        string environmentVariable,
        string defaultValue,
        int maximumLength)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }
        value = value.Trim();
        if (value.Length > maximumLength
            || value.Any(character => !((character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character is '.' or '_' or '-')))
        {
            throw new InvalidOperationException(
                $"{environmentVariable} must contain at most {maximumLength} ASCII letters, digits, '.', '_', or '-'.");
        }
        return value;
    }
}
