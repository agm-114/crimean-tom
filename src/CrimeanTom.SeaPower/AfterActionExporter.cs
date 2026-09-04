using System.Text.Json;

using SeaPower;

using SeapowerUI.ViewModels;

using UnityEngine;

namespace CrimeanTom.SeaPower;

internal static class AfterActionExporter
{
    internal const string SectionName = "CrimeanTomOperations";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void TryWrite(MissionDebriefViewModel debrief)
    {
        string missionPath = Globals.currentMissionFilePath;
        if (string.IsNullOrWhiteSpace(missionPath) || !File.Exists(missionPath))
        {
            return;
        }

        Dictionary<string, string> settings = ReadSection(missionPath, SectionName);
        if (!settings.TryGetValue("AfterActionPath", out string? reportPath)
            || string.IsNullOrWhiteSpace(reportPath))
        {
            return;
        }

        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException("The campaign after-action template was not found.", reportPath);
        }

        AfterActionFile report = JsonSerializer.Deserialize<AfterActionFile>(
            File.ReadAllText(reportPath),
            JsonOptions) ?? throw new InvalidDataException("The campaign after-action template was empty.");

        if (settings.TryGetValue("OperationId", out string? operationId)
            && !StringComparer.Ordinal.Equals(operationId, report.OperationId))
        {
            throw new InvalidDataException("The scenario and after-action template have different operation ids.");
        }

        int updatedUnits = 0;
        for (int index = 1; ; index++)
        {
            if (!settings.TryGetValue($"PlayerUnit{index}Id", out string? unitId))
            {
                break;
            }

            if (!settings.TryGetValue($"PlayerUnit{index}Type", out string? seaPowerType))
            {
                throw new InvalidDataException($"PlayerUnit{index}Type is missing from the scenario.");
            }

            UnitRegister? status = debrief.PlayerTaskForceStatus.FirstOrDefault(
                unit => StringComparer.Ordinal.Equals(unit.IniRef, seaPowerType));
            AfterActionUnit? unitReport = report.Units.FirstOrDefault(
                unit => StringComparer.Ordinal.Equals(unit.UnitId, unitId));

            if (status is null || unitReport is null)
            {
                throw new InvalidDataException($"Could not match campaign unit '{unitId}' to the Sea Power debrief.");
            }

            unitReport.Destroyed = status.IsDestroyed;
            unitReport.Condition = status.IsDestroyed ? 0 : status.IsDamaged ? 50 : 100;
            updatedUnits++;
        }

        if (updatedUnits == 0)
        {
            throw new InvalidDataException("The scenario contains no campaign unit mappings.");
        }

        report.Outcome = debrief.MissionResult.ToString();
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        Debug.Log($"[Crimean Tom Operations] wrote after-action report for {report.OperationId} to {reportPath}");
    }

    internal static Dictionary<string, string> ReadSection(string path, string sectionName)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        bool inSection = false;

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = StringComparer.OrdinalIgnoreCase.Equals(
                    line.Substring(1, line.Length - 2),
                    sectionName);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }

        return values;
    }

    private sealed class AfterActionFile
    {
        public string CampaignId { get; set; } = string.Empty;

        public long CampaignRevision { get; set; }

        public string OperationId { get; set; } = string.Empty;

        public List<AfterActionUnit> Units { get; set; } = new();

        public string Outcome { get; set; } = "Pending";
    }

    private sealed class AfterActionUnit
    {
        public string UnitId { get; set; } = string.Empty;

        public int Condition { get; set; }

        public int Fuel { get; set; }

        public int PrimaryAmmunition { get; set; }

        public bool Destroyed { get; set; }
    }
}
