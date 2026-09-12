using System.Text.RegularExpressions;

namespace UNREP;

internal static class SupplyLabels
{
    internal static string Location(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unspecified";
        var magazine = Regex.Match(value, @"^WeaponMagazine[_\s]*(.*?)(?:_(\d+))?$", RegexOptions.IgnoreCase);
        if (magazine.Success)
        {
            string model = Words(magazine.Groups[1].Value);
            string number = magazine.Groups[2].Success ? " " + magazine.Groups[2].Value : "";
            return (model.Length == 0 ? "Magazine" : model + " magazine") + number;
        }
        return Regex.Replace(Words(value), @"\b(Magazine|System)\b", match => match.Value.ToLowerInvariant());
    }

    private static string Words(string value)
    {
        value = value.Replace('_', ' ');
        value = Regex.Replace(value, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        value = Regex.Replace(value, @"\bMK\s*(?=\d)", "Mk ", RegexOptions.IgnoreCase);
        return Regex.Replace(value, @"\s+", " ").Trim();
    }
}
