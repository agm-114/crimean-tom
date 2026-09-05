using System.Reflection;
using HarmonyLib;
using Noesis;
using SeapowerUI;
using UnityEngine;

namespace CrimeanTom.SeaPower;

[HarmonyPatch]
internal static class CampaignDebugHotkeyPatch
{
    private static int _lastHandledFrame = -1;

    private static MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SteamManager"),
        "Update");

    private static void Postfix()
    {
        if (Input.GetKeyDown(KeyCode.F8))
        {
            TryShowAutoresolve("Unity input");
        }
    }

    internal static void TryShowAutoresolve(string source)
    {
        try
        {
            int frame = Time.frameCount;
            if (_lastHandledFrame == frame)
            {
                return;
            }
            _lastHandledFrame = frame;

            Debug.Log(
                $"[Crimean Tom Operations] autoresolve hotkey received from {source}; " +
                $"active campaign {CampaignOperationsState.IsActiveCampaign}");
            if (CampaignOperationsState.IsActiveCampaign)
            {
                CampaignAutoresolveWindow.Show();
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Crimean Tom Operations] failed to open autoresolve: {exception}");
        }
    }
}

[HarmonyPatch(typeof(LinearCampaignView), MethodType.Constructor)]
internal static class CampaignDebugViewHotkeyPatch
{
    private static void Postfix(LinearCampaignView __instance)
    {
        __instance.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.F8)
        {
            return;
        }

        eventArgs.Handled = true;
        CampaignDebugHotkeyPatch.TryShowAutoresolve("campaign view");
    }
}