using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeaPower.GameBridge;
using SeapowerUI;

namespace OODA;

[HarmonyPatch]
internal static class MissionCommunications
{
    internal record Message(double Time, string Kind, string Title, string Body, string? From = null);

    private static readonly List<Message> Received = [];
    private static readonly Regex Formatting = new(@"\[/?(?:b|i|u|s|size|color|font)(?:=[^\]\r\n]*)?\]|</?(?:b|i|u|s|size|color|font)(?:=[^>\r\n]*)?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static IReadOnlyList<Message> Messages => Received;
    internal static void Reset() => Received.Clear();
    internal static JArray Snapshot() => new(Received.Select(message => new JObject
    {
        ["elapsedSeconds"] = message.Time, ["kind"] = message.Kind, ["title"] = message.Title,
        ["from"] = message.From, ["body"] = message.Body
    }));

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RichMissionMessages), nameof(RichMissionMessages.CreateMissionPopupModal))]
    private static void CapturePopup(MissionPopupData messageData, Noesis.UIElement context, Modal __result)
    {
        if (context == null || context != MainGameView.Instance) return;
        bool delivered = false;
        __result.Loaded += (_, _) =>
        {
            if (delivered || context != MainGameView.Instance || PlayerPicture.Side == null) return;
            delivered = true;
            string title = string.IsNullOrWhiteSpace(messageData.NavalMessageSubject) ? messageData.Header : messageData.NavalMessageSubject;
            Add("Mission popup", title, messageData.Body, messageData.NavalMessageFrom);
        };
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
