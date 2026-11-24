using AnchorChain;
using HarmonyLib;
using SeaPower;
using SeaPower.GameBridge;
using SeapowerUI.ViewModels;
using UnityEngine;

namespace OODA;

[ACPlugin("crimean-tom.ooda", "OODA", "0.1.2")]
[HarmonyPatch]
public class Plugin : IAnchorChainMod
{
    internal const string DrillSection = "OODADrill";
    private static ReinforcementDrill? drill;

    public void TriggerEntryPoint()
    {
        new Harmony("crimean-tom.ooda").PatchAll(typeof(Plugin).Assembly);
        GameObject host = new("OODA Decisions");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<DecisionWindow>();
        Debug.Log("[OODA] Loaded.");
    }

    [HarmonyPostfix]
    [HarmonyAfter("crimean-tom.observer")]
    [HarmonyPatch(typeof(SceneCreator), "CreateTaskforces")]
    private static void ConfigureDrill(SceneCreator __instance, IniHandler ini)
    {
        StopDrill();
        if (!ini.doesSectionExist(DrillSection)) return;

        Taskforce? blue = Singleton<TaskforceManager>.Instance.FindByName(__instance.PlayerSideName);
        Taskforce red = Globals._enemyTaskforce;
        if (blue is null || !blue.IsAI || !red.IsAI || blue == red)
        {
            Debug.LogWarning("[OODA] Enable Observer to run the reinforcement drill.");
            return;
        }
        var traffic = DrillTraffic.Write(ini, __instance.GeoCenterPosition);
        var spawns = new ReinforcementSpawns(__instance.GeoCenterPosition, new ReinforcementCatalog());
        spawns.WriteStartingShips(ini, blue._nameInMissionFile);
        spawns.WriteStartingShips(ini, red._nameInMissionFile);
        drill = new ReinforcementDrill(__instance, blue, red, spawns, traffic);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SceneCreator), "LoadCivilianRoutes")]
    private static void AttachCivilianTraffic(IniHandler ini)
    {
        drill?.RegisterCivilianTraffic(ini);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TaskforceManager), nameof(TaskforceManager.OnUpdate))]
    private static void UpdateDrill() => drill?.Update();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.IsDestroyed), MethodType.Setter)]
    private static void CountLoss(ObjectBase __instance, bool value)
    {
        if (value) drill?.RecordLoss(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ObjectsManager), nameof(ObjectsManager.AddObjectToItsTaskforce))]
    private static void CountCarrierAircraft(ObjectBase obj) => drill?.RecordCarrierAircraft(obj);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PatrolZoneDrawing), "Render")]
    private static void HideLineCapArea(PatrolZoneDrawing __instance, ref bool ____areaShown, out bool __state)
    {
        __state = ____areaShown;
        if (PlayerFlightDeck.IsLineCap(__instance.Zone)) ____areaShown = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PatrolZoneDrawing), "Render")]
    private static void RestoreCapArea(ref bool ____areaShown, bool __state) => ____areaShown = __state;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PatrolZoneDrawing), nameof(PatrolZoneDrawing.Update))]
    private static void HideLineCapCorners(PatrolZoneDrawing __instance, PatrolZoneLayer ____layer)
    {
        if (!PlayerFlightDeck.IsLineCap(__instance.Zone)) return;
        foreach (PatrolZoneVertexDrawing corner in __instance.Vertices) ____layer.Remove(corner);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoUnload))]
    private static void StopDrill()
    {
        drill?.Finish();
        drill = null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoUnload))]
    private static void EndDecisionSession()
    {
        MissionCommunications.Reset();
        DecisionWindow.EndMission();
    }
}
