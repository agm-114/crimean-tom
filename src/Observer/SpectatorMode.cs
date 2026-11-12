using System.Collections;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace Observer;

[HarmonyPatch]
internal static class SpectatorMode
{
    private static readonly Action<Taskforce, Taskforce.TfType> SetRole =
        AccessTools.MethodDelegate<Action<Taskforce, Taskforce.TfType>>(
            AccessTools.PropertySetter(typeof(Taskforce), nameof(Taskforce.Side)));
    private static Taskforce? observer;
    private static bool previousTruth;
    private static bool previousEnemyControl;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SceneCreator), "CreateTaskforces")]
    private static void CreateObserver(SceneCreator __instance, IniHandler ini)
    {
        StopWatching();
        if ((!Plugin.SpectatorEnabled && !ini.doesSectionExist("ObserverSpectator"))
            || SaveLoadManager.IsASaveFile || __instance._isLinearCampaign
            || Globals._inMuseum || Singleton<Session>.Instance.EditorMission
            || ini.doesSectionExist("CrimeanTom"))
            return;

        Taskforce combatant = Globals._playerTaskforce;
        var manager = Singleton<TaskforceManager>.Instance;
        var name = "Observer";
        while (manager.FindByName(name) != null) name += "_";
        observer = new Taskforce(Taskforce.TfType.Player, name);
        manager.addTaskforce(observer);
        foreach (Taskforce side in manager._taskForces)
            manager.Relations.Set(observer, side, manager.Relations.Get(combatant, side), sensorSharing: false);

        // Change control before spawning units so the game registers the original side with the AI.
        SetRole(combatant, Taskforce.TfType.Enemy);
        Globals._playerTaskforce = observer;
        previousTruth = DM_Sensors._showTruthForPlayer;
        previousEnemyControl = Globals.AllowControlOfEnemyUnits.Value;
        DM_Sensors._showTruthForPlayer = true;
        Globals.AllowControlOfEnemyUnits.Value = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SceneCreator), "LoadMission")]
    private static IEnumerator FinishLoading(IEnumerator __result)
    {
        try
        {
            while (__result.MoveNext()) yield return __result.Current;
        }
        finally
        {
            (__result as IDisposable)?.Dispose();
        }

        if (observer is null) yield break;
        SaveLoadManager.IsSavingAllowed = false;
        List<ObjectBase> units = Singleton<ObjectsManager>.Instance._listOfAllUnits;
        foreach (ObjectBase unit in units)
            if (!unit.IsDestroyed) unit.CreateEternalTrackForPlayer();
        Debug.Log($"[Observer] Spectator active. Tracking {units.Count} units.");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoUnload))]
    private static void StopWatching()
    {
        if (observer is null) return;
        DM_Sensors._showTruthForPlayer = previousTruth;
        Globals.AllowControlOfEnemyUnits.Value = previousEnemyControl;
        observer = null;
    }
}
