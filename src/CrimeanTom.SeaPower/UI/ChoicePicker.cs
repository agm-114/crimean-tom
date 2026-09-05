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
    };

    internal T Value => Control.SelectedIndex >= 0 && Control.SelectedIndex < choices.Length
        ? choices[Control.SelectedIndex].Value
        : fallback;
}