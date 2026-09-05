using System.Collections;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace CrimeanTom.SeaPower;

[HarmonyPatch(typeof(SceneCreator), "LoadMission")]
internal static class GeneratedScenarioRuntimePatch
{
    private static void Postfix(ref IEnumerator __result)
    {
        __result = FinishLoad(__result);
    }

    private static IEnumerator FinishLoad(IEnumerator original)
    {
        while (original.MoveNext())
        {
            yield return original.Current;
        }

        GeneratedScenarioRuntime.Apply();
    }
}

[HarmonyPatch(typeof(MissionManager), nameof(MissionManager.DoUnload))]
internal static class GeneratedScenarioUnloadPatch
{
    private static void Prefix()
    {
        GeneratedScenarioRuntime.RestoreDebugState();
    }
}

internal static class GeneratedScenarioRuntime
{
    private const string SettingsSection = "CrimeanTom";

    private static bool _spectatorTruthActive;
    private static bool _previousTruthValue;

    internal static void RestoreDebugState()
    {
        if (!_spectatorTruthActive)
        {
            return;
        }

        DM_Sensors._showTruthForPlayer = _previousTruthValue;
        _spectatorTruthActive = false;
    }

    internal static void Apply()
    {
        if (!ScenarioDebugGenerator.IsLauncherPath(Globals.currentMissionFilePath) ||
            !Singleton<SceneCreator>.Instance.MissionIni.doesSectionExist(SettingsSection))
        {
            return;
        }

        IniHandler ini = Singleton<SceneCreator>.Instance.MissionIni;
        bool spectator = string.Equals(
            ini.readValue(SettingsSection, "Mode", "Command"),
            "Spectator",
            StringComparison.OrdinalIgnoreCase);
        string[] alliedReferences = ini
            .readValue(SettingsSection, "AlliedAIUnits", string.Empty)
            .Split([','], StringSplitOptions.RemoveEmptyEntries);
        if (alliedReferences.Length == 0)
        {
            return;
        }

        Taskforce ally = new(Taskforce.TfType.Ally)
        {
            _nameInMissionFile = "CrimeanTomAlly",
            _roe = MissionManager.ROE.Unrestricted,
        };
        Singleton<TaskforceManager>.Instance.addTaskforce(ally);

        List<ObjectBase> unitsToTransfer = [];
        foreach (string reference in alliedReferences)
        {
            if (!Singleton<SceneCreator>.Instance.ObjectsByPvKey.TryGetValue(
                    reference.Trim(),
                    out ObjectBase unit) ||
                unit.IsDestroyed)
            {
                continue;
            }

            unitsToTransfer.Add(unit);
        }

        UnitFormation[] formationsToMove = unitsToTransfer
            .Select(unit => unit.Formation)
            .Where(formation => formation is not null)
            .Cast<UnitFormation>()
            .Distinct()
            .ToArray();
        Dictionary<ObjectBase, ObjectBase> homeBases = unitsToTransfer
            .Where(unit => unit._homeBase is not null)
            .ToDictionary(unit => unit, unit => unit._homeBase);

        int transferred = 0;
        foreach (ObjectBase unit in unitsToTransfer)
        {
            if (unit._taskforce == ally)
            {
                continue;
            }

            unit.TransferToTaskforce(
                ally,
                clearWaypoints: false,
                detachFromFormation: false,
                transferAircraft: true);
            unit.CreateEternalTrackForPlayer();
            transferred++;
        }

        foreach (UnitFormation formation in formationsToMove.Where(formation =>
                     formation.Stations
                         .Where(station => station.UnitObject is not null)
                         .All(station => station.UnitObject._taskforce == ally)))
        {
            formation._taskforce.Formations.Remove(formation);
            formation._taskforce = ally;
            if (!ally.Formations.Contains(formation))
            {
                ally.Formations.Add(formation);
            }
        }

        foreach ((ObjectBase aircraft, ObjectBase homeBase) in homeBases)
        {
            if (!aircraft.IsDestroyed && aircraft._taskforce == ally && homeBase._taskforce == ally)
            {
                aircraft.Rebase(homeBase);
            }
        }

        if (spectator)
        {
            EnableTruthForPlayer();
        }

        Debug.Log(
            $"[Crimean Tom Operations] configured generated scenario mode " +
            $"{(spectator ? "Spectator" : "Command")}; transferred {transferred} " +
            $"units to allied AI; truth vision {spectator}");
    }

    private static void EnableTruthForPlayer()
    {
        _previousTruthValue = DM_Sensors._showTruthForPlayer;
        _spectatorTruthActive = true;
        DM_Sensors._showTruthForPlayer = true;
        foreach (ObjectBase unit in Singleton<ObjectsManager>.Instance._listOfAllObjects
                     .Where(candidate => candidate is not null && candidate.isUnit())
                     .ToArray())
        {
            unit.CreateEternalTrackForPlayer();
        }
    }
}