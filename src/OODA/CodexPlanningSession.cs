using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OODA;

internal class CodexSessionUnavailableException(Exception inner) : Exception(inner.Message, inner);

internal class CodexPlanningSession(DecisionRole role, DecisionPrompt opening) : IDisposable
{
    private const int MaxTurns = 4;
    private const long MaxPriorInputTokens = 150_000;
    private const string CurrentSnapshotRule = "Use the newest player-view snapshot as the current state. Earlier snapshots and decisions are historical. An earlier proposed order is not evidence it was applied.";
    private readonly string briefing = opening.BriefingText;
    private readonly object gate = new();
    private Process? process;
    private string? directory;
    private string? threadId;
    private int rpcId, turnsSinceCompaction;
    private bool disposed, busy;

    internal bool Matches(DecisionPrompt prompt) => briefing == prompt.BriefingText;
    internal long LastInputTokens { get; private set; }
    internal bool NeedsCompaction => turnsSinceCompaction >= MaxTurns || LastInputTokens >= MaxPriorInputTokens;

    internal async Task<JObject> Decide(DecisionPrompt prompt, CancellationToken cancellation, Action onStarted)
    {
        Begin();
        bool started = false;
        try
        {
            await Task.Yield();
            cancellation.ThrowIfCancellationRequested();
            Process server = GetServer();
            using CancellationTokenRegistration stop = cancellation.Register(() => CodexDecisions.Kill(server));
            if (threadId == null) threadId = await StartThread(prompt).ConfigureAwait(false);
            onStarted();
            string snapshot = $"{DecisionPrompt.SnapshotGuide}\n{prompt.CompactSnapshot(role).ToString(Formatting.None)}";
            string input = turnsSinceCompaction == 0 && briefing.Length > 0 ? $"{briefing.TrimStart()}\n\n{snapshot}" : snapshot;
            int bytes = Encoding.UTF8.GetByteCount(input) + (turnsSinceCompaction == 0 ? Encoding.UTF8.GetByteCount(Instructions(prompt)) : 0);
            started = true;
            JObject result = await Call("turn/start", new()
            {
                ["threadId"] = threadId,
                ["input"] = new JArray(new JObject { ["type"] = "text", ["text"] = input, ["text_elements"] = new JArray() }),
                ["effort"] = CodexDecisions.PlanningEffort,
                ["outputSchema"] = DecisionSchema.For(role)
            }).ConfigureAwait(false);
            string turnId = (string?)result["turn"]?["id"] ?? throw new InvalidOperationException("Codex did not start a turn.");
            JObject decision = await ReadDecision(turnId, bytes).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            turnsSinceCompaction++;
            return decision;
        }
        catch (Exception error)
        {
            Dispose();
            cancellation.ThrowIfCancellationRequested();
            if (!started) throw new CodexSessionUnavailableException(error);
            throw;
        }
        finally { End(); }
    }

    internal async Task Compact()
    {
        Begin();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
            Process server = GetServer();
            using CancellationTokenRegistration stop = timeout.Token.Register(() => CodexDecisions.Kill(server));
            string? compactedTurn = null;
            JObject? completedTurn = null;
            await Call("thread/compact/start", new() { ["threadId"] = threadId }, Observe).ConfigureAwait(false);
            while (completedTurn == null) Observe(await Read().ConfigureAwait(false));
            timeout.Token.ThrowIfCancellationRequested();
            if ((string?)completedTurn["status"] != "completed" || compactedTurn == null || compactedTurn != (string?)completedTurn["id"])
                throw new InvalidOperationException("Codex did not finish compacting the thread.");
            turnsSinceCompaction = 0;
            LastInputTokens = 0;

            void Observe(JObject message)
            {
                if (message["params"] is not JObject data || (string?)data["threadId"] != threadId) return;
                if ((string?)message["method"] == "item/completed" && (string?)data["item"]?["type"] == "contextCompaction")
                    compactedTurn = (string?)data["turnId"];
                if ((string?)message["method"] == "turn/completed") completedTurn = data["turn"] as JObject;
            }
        }
        finally { End(); }
    }

    private void Begin()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CodexPlanningSession));
            if (busy) throw new InvalidOperationException("Codex thread already has an active turn.");
            busy = true;
        }
    }

    private void End()
    {
        lock (gate)
        {
            busy = false;
            if (disposed) Close();
        }
    }

    private Process GetServer()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CodexPlanningSession));
            if (process != null) return process;
            directory = CodexDecisions.CreateDirectory();
            ProcessStartInfo start = CodexDecisions.StartInfo(directory, ["-c", "model_provider=\"openai\"", "-c", "forced_login_method=\"chatgpt\"",
                "-c", "web_search=\"disabled\"", "app-server", "--listen", "stdio://"],
                ["hooks", "multi_agent", "multi_agent_v2", "browser_use", "computer_use", "in_app_browser", "skill_search"]);
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex app-server.");
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return process;
        }
    }

    private async Task<string> StartThread(DecisionPrompt prompt)
    {
        await Call("initialize", new() { ["clientInfo"] = new JObject { ["name"] = "ooda", ["version"] = "0.1.0" } }).ConfigureAwait(false);
        await Send(new() { ["method"] = "initialized", ["params"] = new JObject() }).ConfigureAwait(false);
        JObject config = await Call("config/read", new() { ["cwd"] = directory }).ConfigureAwait(false);
        // An empty override merges with the user's config. Disable each server explicitly.
        JObject servers = new((config["config"]?["mcp_servers"] as JObject ?? new JObject()).Properties()
            .Select(server => new JProperty(server.Name, new JObject { ["enabled"] = false })));
        JObject result = await Call("thread/start", new()
        {
            ["model"] = CodexDecisions.Model, ["cwd"] = directory, ["approvalPolicy"] = "never", ["sandbox"] = "read-only",
            ["ephemeral"] = true,
            ["developerInstructions"] = Instructions(prompt),
            ["config"] = new JObject { ["mcp_servers"] = servers, ["model_reasoning_effort"] = CodexDecisions.PlanningEffort }
        }).ConfigureAwait(false);
        return (string?)result["thread"]?["id"] ?? throw new InvalidOperationException("Codex did not start a planning session.");
    }

    private static string Instructions(DecisionPrompt prompt) => $"{prompt.Instructions}\n\n{CurrentSnapshotRule}";

    private async Task<JObject> ReadDecision(string turnId, int payloadBytes)
    {
        string? answer = null;
        JObject? usage = null;
        while (true)
        {
            JObject message = await Read().ConfigureAwait(false);
            if (message["params"] is not JObject data || (string?)data["threadId"] != threadId) continue;
            bool currentTurn = (string?)data["turnId"] == turnId;
            switch ((string?)message["method"])
            {
                case "item/completed" when currentTurn && ((string?)data["item"]?["type"] == "agentMessage"):
                    answer = (string?)data["item"]?["text"];
                    break;
                case "thread/tokenUsage/updated" when currentTurn:
                    usage = data["tokenUsage"]?["last"] as JObject;
                    break;
                case "turn/completed" when ((string?)data["turn"]?["id"] == turnId):
                    if ((long?)usage?["inputTokens"] is { } input && (long?)usage?["outputTokens"] is { } output)
                    {
                        LastInputTokens = input;
                        CodexDecisions.LogUsage(role, input, (long?)usage["cachedInputTokens"], output,
                            (long?)usage["reasoningOutputTokens"], payloadBytes, "session");
                    }
                    if ((string?)data["turn"]?["status"] != "completed" || answer == null)
                        throw new InvalidOperationException("Codex ended the planning turn without a decision.");
                    return JObject.Parse(answer);
            }
        }
    }

    private async Task<JObject> Call(string method, JObject parameters, Action<JObject>? observe = null)
    {
        int id = ++rpcId;
        await Send(new() { ["id"] = id, ["method"] = method, ["params"] = parameters }).ConfigureAwait(false);
        while (true)
        {
            JObject message = await Read().ConfigureAwait(false);
            if (message["id"] is not JValue { Type: JTokenType.Integer } response || response.Value<int>() != id)
            {
                observe?.Invoke(message);
                continue;
            }
            if (message["result"] is JObject result) return result;
            throw new InvalidOperationException($"Codex {method} failed: {(string?)message["error"]?["message"] ?? "no result"}");
        }
    }

    private async Task<JObject> Read()
    {
        while (true)
        {
            string line = await process!.StandardOutput.ReadLineAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Codex app-server closed the connection.");
            JObject message = JObject.Parse(line);
            if (message["id"] is not { } id || message["method"] == null) return message;
            await Send(new() { ["id"] = id.DeepClone(), ["error"] = new JObject
                { ["code"] = -32601, ["message"] = "OODA does not allow Codex to call external tools." } }).ConfigureAwait(false);
        }
    }

    private async Task Send(JObject message)
    {
        StreamWriter input = process!.StandardInput;
        await input.WriteLineAsync(message.ToString(Formatting.None)).ConfigureAwait(false);
        await input.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            if (process != null) CodexDecisions.Kill(process);
            if (!busy) Close();
        }
    }

    private void Close()
    {
        Process? server = process;
        string? cwd = directory;
        process = null;
        directory = null;
        if (cwd == null) return;
        _ = Task.Run(() =>
        {
            using (server)
            {
                try
                {
                    server?.WaitForExit(5_000);
                    Directory.Delete(cwd);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidOperationException) { }
            }
        });
    }
}
