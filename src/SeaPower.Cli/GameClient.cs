using System.Text.Json;

namespace SeaPower.Cli;

internal class GameClient
{
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaPowerGameBridge");
    private string? session;

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
                        if (!document.RootElement.TryGetProperty("capabilities", out JsonElement capabilities)
                            || !capabilities.EnumerateArray().Any(value => value.GetString() == "load-scenario")
                            || !capabilities.EnumerateArray().Any(value => value.GetString() == "menu"))
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
        if (command.Request.Action == "load-scenario") return await SubmitScenarioAsync(command.Request, cancellation);
        if (command.Request.Action == "encyclopedia")
        {
            await SendAsync(command.Request, cancellation);
            while (true)
            {
                JsonElement readiness = await SendAsync(new("encyclopedia-ready"), cancellation);
                if (readiness.GetProperty("ready").GetBoolean()) break;
                if (!readiness.GetProperty("loading").GetBoolean())
                    throw new InvalidOperationException("Encyclopedia loading ended or was interrupted. Inspect the game before retrying.");
                await Task.Delay(500, cancellation);
            }
            return await SendAsync(command.Request with { Action = "encyclopedia-unit" }, cancellation);
        }
        if (command.Request.Action is "inspect" or "update" or "units" or "units-update")
            return (await DumpAsync(command, cancellation))!.Value;
        if (command.Request.Action is not ("wait-for" or "wait-sim"))
            return await SendAsync(command.Request, cancellation);

        TimeStatus? start = null;
        double previous = 0;
        bool simulation = command.Request.Action == "wait-sim";
        while (true)
        {
            JsonElement response = await SendAsync(new(simulation ? "status" : "inspect"), cancellation);
            GameReply reply = WireJson.Read(response);
            if (!simulation)
            {
                if (reply.Controls == null) throw new InvalidDataException("Bridge did not return UI controls.");
                if (reply.Controls.Any(control => control.Matches(command.Request.Text)))
                {
                    // Re-read under the baseline lock so concurrent dumps cannot save out of order.
                    if (await DumpAsync(command, cancellation) is { } dump) return dump;
                }
            }
            else
            {
                TimeStatus time = reply.Time ?? throw new InvalidDataException("Bridge did not return time state.");
                double elapsed = time.RequireElapsed();
                if (start != null && (time.MissionStartSeconds != start.MissionStartSeconds || elapsed < previous))
                    throw new InvalidOperationException("Mission changed while waiting.");
                start ??= time;
                previous = elapsed;
                double advanced = elapsed - start.RequireElapsed();
                if (advanced >= command.Seconds)
                    return JsonSerializer.SerializeToElement(new
                    {
                        ok = true, action = "wait-sim", requestedSeconds = command.Seconds, advancedSeconds = advanced, time,
                    }, WireJson.Options);
            }
            await Task.Delay(500, cancellation);
        }
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
        string current = document.RootElement.GetProperty("session").GetString() ?? "";
        if (!Guid.TryParseExact(current, "N", out _)) throw new InvalidDataException("Invalid bridge session.");
        if (session != null && session != current) throw new InvalidOperationException("Game session changed. Inspect before retrying an action.");
        session = current;
    }

    private static void DeleteOwnFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
