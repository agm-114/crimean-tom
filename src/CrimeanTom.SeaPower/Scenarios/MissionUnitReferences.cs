using System.Text.RegularExpressions;

namespace CrimeanTom.SeaPower;

internal static class MissionUnitReferences
{
    private const string Vessel = "Vessel";
    private const string Submarine = "Submarine";
    private const string HullPattern = $@"(?:{Vessel}|{Submarine})\d+";
    private static readonly Regex HullTokens = new(@"\bTaskforce[12]" + HullPattern + @"\b", RegexOptions.IgnoreCase);

    internal static string FormatHull(string taskforce, int number, bool isSubmarine = false) =>
        $"{taskforce}{(isSubmarine ? Submarine : Vessel)}{number}";

    // Number in source order, before tactical grouping reorders units. Each kind starts at one.
    internal static IEnumerable<(T Unit, string Reference)> NumberHulls<T>(
        string taskforce, IEnumerable<T> units, Func<T, bool> isSubmarine)
    {
        int vessels = 0, submarines = 0;
        foreach (T unit in units)
        {
            bool submarine = isSubmarine(unit);
            yield return (unit, FormatHull(taskforce, submarine ? ++submarines : ++vessels, submarine));
        }
    }

    internal static Regex HullNamesFor(string taskforce) =>
        new("^" + Regex.Escape(taskforce) + HullPattern + "$", RegexOptions.IgnoreCase);

    internal static Regex HullSectionsFor(string taskforce) =>
        new(@"^\[(?<reference>" + Regex.Escape(taskforce) + HullPattern + @")\]\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // Native aircraft section lookup was case-sensitive; keep that distinct from hull lookup.
    internal static Regex AirNamesFor(string taskforce) =>
        new("^" + Regex.Escape(taskforce) + @"(?:Aircraft|Helicopter)\d+$");

    internal static string RemapHulls(string value, IReadOnlyDictionary<string, string> mapping) =>
        HullTokens.Replace(value, match => mapping.TryGetValue(match.Value, out string? replacement)
            ? replacement : match.Value);

    internal static IEnumerable<string> FindHulls(string value) =>
        HullTokens.Matches(value).Cast<Match>().Select(match => match.Value);
}