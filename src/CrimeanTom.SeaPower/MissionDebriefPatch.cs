using HarmonyLib;

using SeaPower;

using SeapowerUI.ViewModels;

using UnityEngine;

namespace CrimeanTom.SeaPower;

[HarmonyPatch(typeof(MissionManager), nameof(MissionManager.ExitToMainMenu))]
internal static class MissionDebriefPatch
{
    private static void Postfix()
    {
        try
        {
            object? currentWindow = Singleton<MenuSystemViewModel>.Instance.CurrentWindow;
            object? dataContext = currentWindow?
                .GetType()
                .GetProperty("DataContext")?
                .GetValue(currentWindow);
            MissionDebriefViewModel? debrief = dataContext as MissionDebriefViewModel;

            if (debrief is not null)
            {
                AfterActionExporter.TryWrite(debrief);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Crimean Tom Operations] after-action export failed: {exception}");
        }
    }
}
