using Noesis;

namespace CrimeanTom.SeaPower;

// Keep native UI binding on strings while mapping each label directly to its typed value.
internal class ChoicePicker<T>(
    (string Label, T Value)[] choices, int selectedIndex = 0, T fallback = default!)
{
    internal ComboBox Control { get; } = new()
    {
        ItemsSource = choices.Select(choice => choice.Label).ToArray(),
        SelectedIndex = selectedIndex,
        MinWidth = 330f,
        ToolTip = choices.Any(choice => choice.Label == "Random")
            ? "Chances apply when Random is selected. Choosing an entry directly overrides the roll." : null,
    };

    internal T Value => Control.SelectedIndex >= 0 && Control.SelectedIndex < choices.Length
        ? choices[Control.SelectedIndex].Value
        : fallback;

    internal void UpdateLabels(Func<T, string> label)
    {
        int selected = Math.Max(0, Control.SelectedIndex);
        Control.ItemsSource = choices.Select(choice => label(choice.Value)).ToArray();
        Control.SelectedIndex = selected;
    }
}

internal static class ChoiceLabel
{
    internal static string Chance(string label, double probability) =>
        FormattableString.Invariant($"{label} ({probability * 100:0.#}% chance)");
}
