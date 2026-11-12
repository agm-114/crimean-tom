using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;
using SeaPower.RollingReplay;
using SeapowerUI;

namespace OODA;

[HarmonyPatch]
internal static class MissionCommunications
{
    internal record Message(double Time, string Kind, string Title, string Body, string? From = null);
    internal record Popup(Modal Window, MainGameView Host, string Title, string Button, bool PausedRunningGame);

    private static readonly List<Message> Received = [];
    private static readonly List<Popup> Pending = [];
    private static readonly Regex Formatting = new(@"\[/?(?:b|i|u|s|size|color|font)(?:=[^\]\r\n]*)?\]|</?(?:b|i|u|s|size|color|font)(?:=[^>\r\n]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static IReadOnlyList<Message> Messages => Received;
    internal static void Reset()
    {
        Received.Clear();
        Pending.Clear();
    }
    internal static JArray Snapshot() => new(Received.Select(message => new JObject
    {
        ["elapsedSeconds"] = message.Time, ["kind"] = message.Kind, ["title"] = message.Title,
        ["from"] = message.From, ["body"] = message.Body
    }));

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RichMissionMessages), nameof(RichMissionMessages.CreateMissionPopupModal))]
    private static void CapturePopup(MissionPopupData messageData, Noesis.UIElement context, bool pauseOnLoad, Modal __result)
    {
        if (context is not MainGameView host || host != MainGameView.Instance) return;
        bool pausedRunningGame = pauseOnLoad && PlayerTime.Available && !GameTime.IsPaused();
        bool delivered = false;
        string title = Clean(string.IsNullOrWhiteSpace(messageData.NavalMessageSubject) ? messageData.Header : messageData.NavalMessageSubject);
        var popup = new Popup(__result, host, title, Clean(messageData.ButtonText), pausedRunningGame);
        __result.Loaded += (_, _) =>
        {
            if (delivered || context != MainGameView.Instance || PlayerPicture.Side == null) return;
            delivered = true;
            Add("Mission popup", title, messageData.Body, messageData.NavalMessageFrom);
            Pending.Add(popup);
        };
        __result.Unloaded += (_, _) => Pending.Remove(popup);
    }

    internal static Popup? Acknowledge()
    {
        if (Pending.Count == 0 || !PlayerTime.Available || RollingReplayManager.IsActive
            || Singleton<InputHandler>.Instance.menuOpen) return null;

        Popup popup = Pending[Pending.Count - 1];
        Modal window = popup.Window;
        if (popup.Host != MainGameView.Instance || !window.IsVisible || !window.IsEnabled || window.View == null
            || !WindowHostCommands.CloseWindowCommand.CanExecute(window, popup.Host)
            || window.ActionCommand is not { } action || !action.CanExecute(null)) return null;

        action.Execute(null);
        Pending.Remove(popup);
        if (popup.PausedRunningGame && Pending.Count == 0) PlayerTime.TryResume();
        return popup;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RadioSubtitlePresentation), nameof(RadioSubtitlePresentation.Show))]
    private static void CaptureRadio(RadioMessageData message)
    {
        if (PlayerPicture.Side != null) Add("Radio", message.SpeakerName, message.Text);
    }

    private static void Add(string kind, string? title, string? body, string? from = null)
    {
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(title)) return;
        Received.Add(new(GameTime.missionElapsedTime, kind, Clean(title), Clean(body), string.IsNullOrWhiteSpace(from) ? null : Clean(from)));
    }

    private static string Clean(string? text) => Formatting.Replace(text ?? "", "").Trim();
}
