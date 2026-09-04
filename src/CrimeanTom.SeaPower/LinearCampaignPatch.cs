using HarmonyLib;

using UnityEngine;

namespace CrimeanTom.SeaPower;

[HarmonyPatch]
internal static class LinearCampaignPatch
{
    private static System.Reflection.MethodBase TargetMethod()
    {
        Type linearCampaign = AccessTools.TypeByName("SeaPower.LinearCampaign")
            ?? throw new TypeLoadException("SeaPower.LinearCampaign was not found");
        return AccessTools.Method(linearCampaign, "LoadLinearCampaign", [typeof(string)])
            ?? throw new MissingMethodException(linearCampaign.FullName, "LoadLinearCampaign");
    }

    private const string CampaignMarker = "[CrimeanTomOperations]";

    private static bool Prefix([HarmonyArgument(0)] string filePath)
    {
        if (!IsOperationsCampaign(filePath))
        {
            return true;
        }

        CampaignUiBehaviour? campaignUi = CampaignUiBehaviour.Instance;
        if (campaignUi is null)
        {
            Debug.LogError("[Crimean Tom Operations] campaign headquarters is unavailable");
            return false;
        }

        Debug.Log("[Crimean Tom Operations] opening campaign headquarters from Sea Power Campaigns");
        campaignUi.OpenFromCampaignMenu();
        return false;
    }

    private static bool IsOperationsCampaign(string filePath)
    {
        try
        {
            return File.Exists(filePath)
                && File.ReadAllText(filePath).Contains(CampaignMarker, StringComparison.Ordinal);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Crimean Tom Operations] could not inspect campaign file {filePath}: {exception.Message}");
            return false;
        }
    }
}
