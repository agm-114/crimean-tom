using System.Text.Json;

namespace SeaPower.Cli;

internal class GameClient
{
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaPowerGameBridge");
    private string? session;
    private string? requiredCapability;

    internal async Task<JsonElement> LoadScenarioAsync(string path, DateTime launchUtc, CancellationToken cancellation)
    {
        string sessionPath = Path.Combine(directory, "session.json");
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(sessionPath) && File.GetLastWriteTimeUtc(sessionPath) >= launchUtc)
            {
                JsonDocument? document = null;
                try { document = JsonDocument.Parse(await File.ReadAllTextAsync(sessionPath, cancellation)); }
                catch (IOException) { }
                catch (JsonException) { }
                if (document != null)
                {
                    using (document)
                    {
                        if (!HasCapability(document.RootElement, "load-scenario") || !HasCapability(document.RootElement, "menu"))
                            throw new InvalidOperationException("The running bridge lacks scenario loading. Deploy the updated game bridge and restart.");
                        session = document.RootElement.GetProperty("session").GetString();
                    }
                    break;
                }
            }
            await Task.Delay(500, cancellation);
        }
        while (true)
        {
            JsonElement readiness = await SendAsync(new("scenario-ready"), cancellation);
            if (readiness.GetProperty("ready").GetBoolean()) break;
            await Task.Delay(500, cancellation);
        }
        // Never retry an uncertain load: the game may already be changing scenes.
        return await SubmitScenarioAsync(new("load-scenario") { Text = path, Confirm = true }, cancellation);
    }

    internal async Task<JsonElement> ExecuteAsync(CliCommand command, CancellationToken cancellation)
    {
        requiredCapability = command.Request.Action switch
        {
            "workshop-create" or "double-click" => command.Request.Action,
            "workshop-update" when command.Request.ContentOnly => "workshop-content-only",
            "workshop-update" when command.Request.PreviewDirectory.Length > 0 => "workshop-preview-images",
            "workshop-update" or "workshop-status" => "workshop-update",
            "player-view" or "player-waypoint" or "player-standing-orders" or "ooda-status" => command.Request.Action, _ => null,
        };
        if (command.Request.Action is "workshop-update" or "workshop-create") return await UploadWorkshopAsync(command.Request, cancellation);
        if (command.Request.Action == "load-scenario") return await SubmitScenarioAsync(command.Request, cancellation);
        if (command.Request.Action == "encyclopedia") return await OpenEncyclopediaAsync(command.Request, cancellation);
        if (command.Request.Action == "advance-sim") return await AdvanceSimulationAsync(command, cancellation);
        if (command.Request.Action == "wait-sim")
        {
            TimeStatus start = await ReadTimeAsync(new("status"), cancellation);
            TimeStatus time = await WaitForSimulationAsync(start, command.Seconds, null, cancellation);
            return SimulationResult(command, start, time);
        }
        if (command.Request.Action is "inspect" or "update" or "units" or "units-update")
            return (await DumpAsync(command, cancellation))!.Value;
        if (command.Request.Action != "wait-for")
            return await SendAsync(command.Request, cancellation);

        while (true)
        {
            GameReply reply = WireJson.Read(await SendAsync(new("inspect"), cancellation));
            if (reply.Controls == null) throw new InvalidDataException("Bridge did not return UI controls.");
            if (reply.Controls.Any(control => control.Matches(command.Request.Text)))
            {
                // Re-read under the baseline lock so concurrent dumps cannot save out of order.
                if (await DumpAsync(command, cancellation) is { } dump) return dump;
            }
            await Task.Delay(500, cancellation);
        }
    }

    private async Task<JsonElement> AdvanceSimulationAsync(CliCommand command, CancellationToken cancellation)
    {
        float speed = command.Request.Speed;
        TimeStatus before = await ReadTimeAsync(new("status"), cancellation);
        before.RequireElapsed();
        TimeStatus? start = null;
        try
        {
            start = await ReadTimeAsync(new("set-speed") { Speed = speed }, cancellation);
            await WaitForSimulationAsync(start, command.Seconds, speed, cancellation);
            TimeStatus paused = await ReadTimeAsync(new("pause"), cancellation);
            return SimulationResult(command, start, paused);
        }
        catch
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(3));
            try
            {
                TimeStatus time = await ReadTimeAsync(new("status"), cleanup.Token);
                TimeStatus reference = start ?? before;
                if ((start != null || before.Paused || before.Speed != speed)
                    && time.Available && !time.Blocked && !time.Paused && time.Speed == speed
                    && time.MissionStartSeconds == reference.MissionStartSeconds && time.ElapsedSeconds >= reference.ElapsedSeconds)
                    await ReadTimeAsync(new("pause"), cleanup.Token);
            }
            catch (Exception) { } // Preserve the original error if the game can no longer be paused.
            throw;
        }
    }

    private async Task<TimeStatus> WaitForSimulationAsync(TimeStatus start, int seconds, float? speed, CancellationToken cancellation)
    {
        double initial = start.RequireElapsed();
        double previous = initial;
        while (true)
        {
            TimeStatus time = await ReadTimeAsync(new("status"), cancellation);
            double elapsed = time.RequireElapsed();
            if (time.MissionStartSeconds != start.MissionStartSeconds || elapsed < previous)
                throw new InvalidOperationException("Mission changed while waiting.");
            if (speed.HasValue && (time.Blocked || time.Paused || time.Speed != speed.Value))
                throw new InvalidOperationException("Simulation advance was interrupted by a pause, speed change, or open menu.");
            if (elapsed - initial >= seconds) return time;
            previous = elapsed;
            await Task.Delay(speed.HasValue ? 100 : 500, cancellation);
        }
    }

    private async Task<TimeStatus> ReadTimeAsync(GameRequest request, CancellationToken cancellation) =>
        WireJson.Read(await SendAsync(request, cancellation)).Time
        ?? throw new InvalidDataException("Bridge did not return time state.");

    private static JsonElement SimulationResult(CliCommand command, TimeStatus start, TimeStatus time) =>
        JsonSerializer.SerializeToElement(new
        {
            ok = true, action = command.Request.Action, requestedSeconds = command.Seconds,
            advancedSeconds = time.RequireElapsed() - start.RequireElapsed(), time,
        }, WireJson.Options);

    private async Task<JsonElement> UploadWorkshopAsync(GameRequest request, CancellationToken cancellation)
    {
        JsonElement result = await SendAsync(request, cancellation);
        string operation = result.GetProperty("operation").GetString()!;
        while (result.GetProperty("state").GetString() is "checking" or "creating" or "adding-dependencies" or "uploading")
        {
            await Task.Delay(500, cancellation);
            result = await SendAsync(new("workshop-status") { Text = operation }, cancellation);
        }
        if (result.GetProperty("state").GetString() == "failed")
            throw new InvalidOperationException($"{result.GetProperty("failure").GetString()} Target: {result.GetProperty("url").GetString()}. Operation: {operation}.");
        return result;
    }

    private async Task<JsonElement> OpenEncyclopediaAsync(GameRequest request, CancellationToken cancellation)
    {
        await SendAsync(request, cancellation);
        while (true)
        {
            JsonElement readiness = await SendAsync(new("encyclopedia-ready"), cancellation);
            if (readiness.GetProperty("ready").GetBoolean()) break;
            if (!readiness.GetProperty("loading").GetBoolean())
                throw new InvalidOperationException("Encyclopedia loading ended or was interrupted. Inspect the game before retrying.");
            await Task.Delay(500, cancellation);
        }
        return await SendAsync(request with { Action = "encyclopedia-unit" }, cancellation);
    }

    private async Task<JsonElement> SubmitScenarioAsync(GameRequest command, CancellationToken cancellation)
    {
        JsonElement menu = await SendAsync(new("menu"), cancellation);
        if (menu.GetProperty("currentWindow").GetString() == "MainMenuView" && menu.GetProperty("idle").GetBoolean())
            await SendAsync(new("menu") { Text = "scenarios" }, cancellation);
        // Let the Scenarios view attach before native mod hooks open their setup windows.
        return await SendAsync(command, cancellation);
    }

    private async Task<JsonElement?> DumpAsync(CliCommand command, CancellationToken cancellation)
    {
        string action = command.Request.Action;
        string collection = action is "units" or "units-update" ? "units" : "controls";
        await using SnapshotCache cache = await SnapshotCache.OpenAsync(collection, cancellation);
        JsonElement response = await SendAsync(new(collection == "units" ? "units" : "inspect"), cancellation);
        if (action == "wait-for" && WireJson.Read(response).Controls?.Any(control => control.Matches(command.Request.Text)) != true)
            return null;
        return await cache.UpdateAsync(response, session!, action is "update" or "units-update", command.Query, cancellation);
    }

    private async Task<JsonElement> SendAsync(GameRequest command, CancellationToken cancellation)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        string temporary = Path.Combine(directory, id + ".request.tmp");
        string pending = Path.Combine(directory, "request.json");
        string response = Path.Combine(directory, id + ".json");
        try
        {
            // Wait for the single request slot; never resend an action once published.
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await CheckSessionAsync(token);
                GameRequest request = command with { Session = session!, Request = id, Expires = DateTime.UtcNow.AddSeconds(10) };
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request, WireJson.Options);
                if (bytes.Length > 16384) throw new ArgumentException("Request exceeds the bridge's 16 KB limit.");
                await File.WriteAllBytesAsync(temporary, bytes, token);
                try { File.Move(temporary, pending); break; }
                catch (IOException) when (File.Exists(pending)) { await Task.Delay(100, token); }
            }

            while (!File.Exists(response))
            {
                await CheckSessionAsync(token);
                await Task.Delay(100, token);
            }
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(response, token), WireJson.DocumentOptions);
            JsonElement result = document.RootElement.Clone();
            if (result.TryGetProperty("error", out JsonElement error)) throw new InvalidOperationException(error.GetString());
            return result;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The game bridge did not respond within 12 seconds. Inspect before retrying an action.");
        }
        finally
        {
            DeleteOwnFile(temporary);
            DeleteOwnFile(response);
        }
    }

    private async Task CheckSessionAsync(CancellationToken cancellation)
    {
        string path = Path.Combine(directory, "session.json");
        if (!File.Exists(path)) throw new InvalidOperationException("Game bridge is not running or enabled.");
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellation));
        if (requiredCapability != null && !HasCapability(document.RootElement, requiredCapability))
            throw new InvalidOperationException($"The running bridge lacks {requiredCapability}. Deploy the updated bridge and restart the game when ready.");
        string current = document.RootElement.GetProperty("session").GetString() ?? "";
        if (!Guid.TryParseExact(current, "N", out _)) throw new InvalidDataException("Invalid bridge session.");
        if (session != null && session != current) throw new InvalidOperationException("Game session changed. Inspect before retrying an action.");
        session = current;
    }

    private static bool HasCapability(JsonElement session, string capability) =>
        session.TryGetProperty("capabilities", out var capabilities)
        && capabilities.EnumerateArray().Any(value => value.GetString() == capability);

    private static void DeleteOwnFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
