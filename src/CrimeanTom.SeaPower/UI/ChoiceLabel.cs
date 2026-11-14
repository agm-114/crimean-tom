namespace CrimeanTom.SeaPower;

internal static class ChoiceLabel
{
    internal static string Chance(string label, double probability) =>
        FormattableString.Invariant($"{label} ({probability * 100:0.#}% chance)");
}
