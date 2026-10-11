using SeaPower;

namespace CrimeanTom.SeaPower;

internal static partial class TaskForceNationRules
{
    internal const string ShowRareNationsKey = "CrimeanTomShowRareCommanderNations";
    internal const string SavedDiscountKey = "CrimeanTomCommanderDiscount";

    private static bool? _showRareNationsForSession;

    internal static string? CommanderSelection(IDictionary<string, string> data)
    {
        data.TryGetValue("TaskForceCommanderNationOverride", out string selection);
        if (string.IsNullOrWhiteSpace(selection)) data.TryGetValue("TaskForceCommanderNation", out selection);
        return selection;
    }

    internal static bool IsOurCampaign(IniHandler? campaignIni) =>
        campaignIni is not null &&
        (IsOurCampaignPath(campaignIni.getFilePath()) ||
         IsOurCampaignPath(campaignIni.readValue("File", "Base", string.Empty)));

    internal static bool IsOurCampaignPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Replace('\\', '/').Contains("/" + CampaignOperationsState.FolderName + "/", StringComparison.OrdinalIgnoreCase);

    internal static bool ShowRareNations(IniHandler campaignIni)
    {
        string savedValue = campaignIni.readValue("PersistentData", ShowRareNationsKey, string.Empty);
        return bool.TryParse(savedValue, out bool saved)
            ? saved
            : _showRareNationsForSession ?? false;
    }

    internal static void SetShowRareNationsForSession(bool showRareNations) =>
        _showRareNationsForSession = showRareNations;
}
