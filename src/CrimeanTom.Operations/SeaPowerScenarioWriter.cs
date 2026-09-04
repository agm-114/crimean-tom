using System.Globalization;
using System.Text;

namespace CrimeanTom.Operations;

public sealed class SeaPowerScenarioWriter
{
    public string Render(ScenarioPackage scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        EnsureSingleLine(scenario.Title, nameof(scenario.Title));
        EnsureSingleLine(scenario.Description, nameof(scenario.Description));
        EnsureSingleLine(scenario.Environment.Clouds, nameof(scenario.Environment.Clouds));
        EnsureSingleLine(scenario.Environment.Weather, nameof(scenario.Environment.Weather));

        StringBuilder text = new();
        AddSection(text, "Language_en");
        AddValue(text, "Name", scenario.Title);
        AddValue(text, "Description", scenario.Description);
        AddBlankLine(text);

        AddSection(text, "Environment");
        AddValue(text, "Date", FormattableString.Invariant($"{scenario.Environment.Date.Year},{scenario.Environment.Date.Month},{scenario.Environment.Date.Day}"));
        AddValue(text, "Time", FormattableString.Invariant($"{scenario.Environment.Time.Hour},{scenario.Environment.Time.Minute}"));
        AddValue(text, "ConvertTimeToLocal", "False");
        AddValue(text, "SeaState", scenario.Environment.SeaState.ToString(CultureInfo.InvariantCulture));
        AddValue(text, "Clouds", scenario.Environment.Clouds);
        AddValue(text, "WindDirection", "N");
        AddValue(text, "Weather", scenario.Environment.Weather);
        AddValue(text, "MapCenterLatitude", FormatNumber(scenario.Environment.MapCenterLatitude));
        AddValue(text, "MapCenterLongitude", FormatNumber(scenario.Environment.MapCenterLongitude));
        AddValue(text, "LoadBackgroundData", "False");
        AddBlankLine(text);

        AddSection(text, "Mission");
        AddValue(text, "Difficulty", "3");
        AddValue(text, "PlayerTaskforce", "Taskforce1");
        AddValue(text, "EnemyTaskforce", "Taskforce2");
        AddValue(text, "NumberOfTaskforce1Vessels", scenario.PlayerVessels.Count.ToString(CultureInfo.InvariantCulture));
        AddValue(text, "NumberOfTaskforce2Vessels", scenario.EnemyVessels.Count.ToString(CultureInfo.InvariantCulture));
        AddValue(text, "NumberOfTriggers", "0");

        AddVessels(text, 1, scenario.PlayerVessels);
        AddVessels(text, 2, scenario.EnemyVessels);
        return text.ToString();
    }

    public void Write(string path, ScenarioPackage scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, Render(scenario), new UTF8Encoding(false));
    }

    private static void AddVessels(StringBuilder text, int taskforce, IReadOnlyList<ScenarioVessel> vessels)
    {
        for (int index = 0; index < vessels.Count; index++)
        {
            ScenarioVessel vessel = vessels[index];
            EnsureSingleLine(vessel.SeaPowerType, nameof(vessel.SeaPowerType));
            AddBlankLine(text);
            AddSection(text, $"Taskforce{taskforce}Vessel{index + 1}");
            AddValue(text, "Type", vessel.SeaPowerType);
            AddValue(text, "VariantReference", "Default");
            AddValue(text, "LoadoutVariant", "Default");
            AddValue(text, "MissionType", "Patrol");
            AddValue(text, "WeaponStatus", "Free");
            AddValue(text, "RadarsActive", vessel.RadarsActive ? "True" : "False");
            AddValue(text, "CrewSkill", "Trained");
            AddValue(text, "RelativePositionInNM", FormatPosition(vessel.EastPositionNm, vessel.NorthPositionNm));
            AddValue(text, "Telegraph", vessel.Telegraph.ToString(CultureInfo.InvariantCulture));
            AddValue(text, "Heading", vessel.Heading.ToString(CultureInfo.InvariantCulture));
            AddValue(text, "Waypoints", FormatPosition(vessel.EastWaypointNm, vessel.NorthWaypointNm));
        }
    }

    private static string FormatPosition(double east, double north)
    {
        return $"{FormatNumber(east)},0,{FormatNumber(north)}";
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static void EnsureSingleLine(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Sea Power INI values must fit on one line.", parameterName);
        }
    }

    private static void AddSection(StringBuilder text, string name)
    {
        text.Append('[').Append(name).Append("]\r\n");
    }

    private static void AddValue(StringBuilder text, string name, string value)
    {
        text.Append(name).Append('=').Append(value).Append("\r\n");
    }

    private static void AddBlankLine(StringBuilder text)
    {
        text.Append("\r\n");
    }
}
