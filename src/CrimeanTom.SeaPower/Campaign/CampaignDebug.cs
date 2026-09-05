using System.Globalization;

using HarmonyLib;

using SeaPower;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static partial class CampaignOperationsState
{
    internal static bool DebugAutoresolve(
        int missionIndex,
        bool playerVictory,
        out string message)
    {
        if (!IsActiveCampaign || missionIndex < 1)
        {
            message = "Open Crimean Tom Operations before using autoresolve.";
            return false;
        }

        if (HasPendingBattle)
        {
            message = "Finish the pending post-battle management phase first.";
            return false;
        }

        ClearReusableMissionState(missionIndex);
        IniHandler? stateIni = OpenCampaignState();
        if (stateIni is null)
        {
            message = "The active campaign state could not be opened.";
            return false;
        }

        int sequence = Math.Max(0, ReadInt(AutoresolveCounterKey, 0)) + 1;
        string completedAt = FormattableString.Invariant(
            $"AUTO-{Turn}-{missionIndex}-{sequence}-{(playerVictory ? "WIN" : "LOSS")}");
        string debrief = DebriefSection(missionIndex);
        stateIni.writeValue(debrief, "CompletedAt", completedAt);
        stateIni.writeValue(debrief, "MissionResult", playerVictory ? "Victory" : "Defeat");

        TaskForceRosterGenerator.RosterUnitOption[] destroyedUnits = playerVictory
            ? SelectAutoresolveDestroyedUnits(completedAt).ToArray()
            : [];
        stateIni.writeValue(debrief, "KillCount", destroyedUnits.Length);
        for (int index = 0; index < destroyedUnits.Length; index++)
        {
            TaskForceRosterGenerator.RosterUnitOption unit = destroyedUnits[index];
            string kill = CampaignStateData.KillSection(missionIndex, index + 1);
            stateIni.writeValue(kill, "Folder", "vessels");
            stateIni.writeValue(kill, "ObjectType", "Vessel");
            stateIni.writeValue(kill, "IsDestroyed", true);
            stateIni.writeValue(kill, "IniRef", unit.UnitType);
            stateIni.writeValue(kill, "VariantIndex", ParseVariantIndex(unit.VariantReference));
        }
        stateIni.saveFile(overwrite: true);

        CampaignContractOffer contract = CampaignContractOffers.Get(missionIndex);
        AccessTools.Method(AccessTools.TypeByName("SeapowerUI.ViewModels.LinearCampaignTaskForceSaveManager"),
            "ApplyCompletionRewards")?.Invoke(null,
                [stateIni, missionIndex, playerVictory ? contract.Payment : 0, playerVictory ? contract.CapReward : 0]);
        stateIni.saveFile(overwrite: true);

        Write(AutoresolveCounterKey, sequence);
        RecordBattle(missionIndex);
        if (!HasPendingBattle)
        {
            message = "Autoresolve wrote a debrief, but the campaign did not accept it.";
            return false;
        }

        message = playerVictory
            ? $"Autoresolved mission {missionIndex} as a victory with {destroyedUnits.Length} enemy hulls destroyed."
            : $"Autoresolved mission {missionIndex} as a defeat with no enemy hulls destroyed.";
        Debug.Log($"[Crimean Tom Operations] {message}");
        return true;
    }

    private static IEnumerable<TaskForceRosterGenerator.RosterUnitOption>
        SelectAutoresolveDestroyedUnits(string battleId)
    {
        TaskForceRosterGenerator.RefreshBaseCosts();
        System.Random random = new(StableHash(battleId));
        return TaskForceRosterGenerator.GetAllVesselOptions()
            .Where(option => ResolveFullPrice(option.UnitType) > 0)
            .GroupBy(option => option.UnitType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(_ => random.Next()).First())
            .OrderBy(option => ResolveFullPrice(option.UnitType))
            .ThenBy(option => option.UnitType, StringComparer.OrdinalIgnoreCase)
            .Take(2);
    }

    private static int ParseVariantIndex(string variantReference)
    {
        const string Prefix = "Variant";
        return variantReference.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                variantReference[Prefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int index)
            ? Math.Max(0, index)
            : 0;
    }
}