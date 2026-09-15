using System.Text.Json;
using SeaPower.Cli;

if (args.Any(argument => argument is "--help" or "-h"))
{
    Console.WriteLine(CliCommand.Help);
    return 0;
}

using CancellationTokenSource cancelled = new();
Console.CancelKeyPress += (_, input) => { input.Cancel = true; cancelled.Cancel(); };
CliCommand? command = null;
string format = "yaml";
try
{
    format = SnapshotData.OutputFormat(args);
    command = CliCommand.Parse(args);
    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelled.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));
    JsonElement result;
    if (command.Request.Action == "presets") result = JsonSerializer.SerializeToElement(QueryPresets.All, SnapshotQuery.JsonOptions);
    else if (command.Request.Action == "unity-explorer") result = await new UnityExplorerInstall(command).ExecuteAsync(timeout.Token);
    else if (command.Request.Action is "game-status" or "logs" or "launch") result = await new GameDiagnostics(command).ExecuteAsync(timeout.Token);
    else if (command.Input is { } path)
    {
        result = command.Query.Apply(SnapshotData.Parse(await File.ReadAllTextAsync(path, timeout.Token), path));
    }
    else result = await new GameClient().ExecuteAsync(command, timeout.Token);
    Console.WriteLine(SnapshotData.Format(result, format));
    return command.Request.Action == "workshop-update" && !result.GetProperty("ok").GetBoolean() ? 1 : 0;
}
catch (Exception exception)
{
    string error = exception is OperationCanceledException
        ? cancelled.IsCancellationRequested ? "Cancelled. Accepted game actions are not undone."
            : $"Timed out after {command?.TimeoutSeconds} wall-clock seconds. Inspect before retrying an action."
        : exception.Message;
    if (command?.Request.Action == "launch" && exception is OperationCanceledException)
        error += " Launch verification did not finish. Sea Power may still be running; check game-status and logs before retrying.";
    if (command?.Request.Action == "workshop-update")
        error += " Use workshop-status before retrying; accepted Steam uploads continue after the CLI exits.";
    Console.Error.WriteLine(SnapshotData.Format(JsonSerializer.SerializeToElement(new { error }), format));
    return cancelled.IsCancellationRequested ? 130 : 1;
}
