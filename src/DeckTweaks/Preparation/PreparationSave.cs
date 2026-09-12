using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace DeckTweaks.Preparation;

[HarmonyPatch]
internal static class PreparationSave
{
    private const string Tag = "FDTP1:";

    [HarmonyPrefix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.SaveTask))]
    private static bool HasAircraft(FlightDeckTask task) => task is not PendingLaunchTask pending || pending.LaunchCount > 0;

    // Append to the native record; its loader ignores fields after the waypoint list.
    [HarmonyPostfix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.SaveTask))]
    private static void Save(IniHandler ini, string sectionName, FlightDeckTask task, int index)
    {
        if (task is not PendingLaunchTask pending || pending.LaunchCount <= 0 || TaskPreparation.Find(pending) is not { } preparation) return;
        if (ini.Data.TryGetValue(sectionName, out var section) && section.TryGetValue($"PendingLaunchTask{index - 1}", out string record))
            section[$"PendingLaunchTask{index - 1}"] = record + "," + Tag + preparation.Batch.Save();
    }

    [HarmonyPrefix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.LoadStateFromFile))]
    private static void BeforeLoad(FlightDeck __instance, out int __state) => __state = __instance._flightDeckTasksToAdd.Count;

    [HarmonyPostfix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.LoadStateFromFile))]
    private static void Load(FlightDeck __instance, IniHandler ini, string sectionName, int __state)
    {
        if (!ini.Data.TryGetValue(sectionName, out var section)) return;
        var records = section.Where(pair => pair.Key.StartsWith("PendingLaunchTask", StringComparison.Ordinal))
            .OrderBy(pair => int.TryParse(pair.Key["PendingLaunchTask".Length..], out int number) ? number : int.MaxValue)
            .Select(pair => pair.Value.Split(',')).Where(fields => fields.Length >= 14).ToList();
        foreach (PendingLaunchTask task in __instance._flightDeckTasksToAdd.Skip(__state).OfType<PendingLaunchTask>())
        {
            string[]? record = records.FirstOrDefault(fields => fields[1] == task.VehicleType._fileName
                && fields[3] == task._loadoutIndex.ToString() && fields[4] == task._squadronIndex.ToString()
                && fields[5] == task._callsignIndex.ToString() && fields[6] == task.LaunchCount.ToString());
            if (record == null) continue;
            records.Remove(record);
            string? saved = record.Skip(14).FirstOrDefault(field => field.StartsWith(Tag, StringComparison.Ordinal));
            // Old saves retain native timing; there is no accurate store state to reconstruct.
            if (saved == null || task.LaunchCount <= 0) continue;
            if (TaskPreparation.Plan(task, TaskPreparation.Selected(task)) is not { } target) continue;
            try
            {
                PreparationBatch batch = new(task.LaunchCount, target);
                batch.Load(saved[Tag.Length..], target);
                // Native inventory was already saved with these reservations deducted.
                StoreReservation reservation = StoreReservation.Excess(batch, target);
                TaskPreparation.Attach(task, batch, reservation);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Deck Tweaks] Could not restore preparation for {task.VehicleType._fileName}; using native timing. {exception}");
            }
        }
    }
}
