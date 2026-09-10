using Newtonsoft.Json;
using UnityEngine;

namespace SeaPower.GameBridge;

public class GameBridge : MonoBehaviour
{
    private readonly UiControls controls = new();
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly string directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SeaPowerGameBridge");
    private float nextPoll;

    private void Awake()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonConvert.SerializeObject(new { session, capabilities = new[] { "load-scenario" } }));
        Debug.Log("[Sea Power Game Bridge] Enabled local command queue. Development use only.");
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextPoll) return;
        nextPoll = Time.realtimeSinceStartup + 0.1f;
        string request = Path.Combine(directory, "request.json");
        if (!File.Exists(request)) return;
        GameCommand? command = null;
        bool consumed = false;
        try
        {
            if (new FileInfo(request).Length > 16384) throw new ArgumentException("Request too large");
            command = JsonConvert.DeserializeObject<GameCommand>(File.ReadAllText(request));
            File.Delete(request);
            consumed = true;
            if (command == null || !Guid.TryParseExact(command.Request, "N", out _)) return;
            if (command.Session != session || command.Expires < DateTime.UtcNow)
                throw new InvalidOperationException("Expired request or stale game session. Inspect again.");
            Reply(command, controls.Execute(command));
        }
        catch (Exception exception)
        {
            if (!consumed) File.Delete(request);
            if (command != null && Guid.TryParseExact(command.Request, "N", out _))
                Reply(command, new { error = exception.Message });
            else Debug.LogWarning($"[Sea Power Game Bridge] Rejected request: {exception.Message}");
        }
    }

    private void Reply(GameCommand command, object value)
    {
        string path = Path.Combine(directory, command.Request + ".json");
        File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(value));
        File.Move(path + ".tmp", path);
    }

    private void OnDestroy()
    {
        string path = Path.Combine(directory, "session.json");
        if (File.Exists(path) && File.ReadAllText(path).Contains(session)) File.Delete(path);
    }
}

internal class GameCommand
{
    public string Session { get; set; } = "";
    public string Request { get; set; } = "";
    public DateTime Expires { get; set; }
    public string Action { get; set; } = "inspect";
    public string Snapshot { get; set; } = "";
    public int Id { get; set; }
    public int Index { get; set; } = -1;
    public string Text { get; set; } = "";
    public bool Confirm { get; set; }
    public float Speed { get; set; } = 1;
}
