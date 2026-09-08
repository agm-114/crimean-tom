using Noesis;
using static CrimeanTom.SeaPower.NativeUi;

namespace CrimeanTom.SeaPower;

internal class ScenarioActivityControls
{
    private readonly ChoicePicker<NearbyPresence> friendlyNaval = Presence(NearbyPresence.On);
    private readonly ChoicePicker<NearbyPresence> friendlyCoast = Presence(NearbyPresence.On);
    private readonly ChoicePicker<NearbyPresence> enemyNaval = Presence();
    private readonly ChoicePicker<NearbyPresence> enemyCoast = Presence(NearbyPresence.On);
    private readonly ChoicePicker<NearbySize> friendlySize = Size();
    private readonly ChoicePicker<NearbySize> enemySize = Size();
    private readonly ChoicePicker<NearbyPresence> friendlyAirfields = Presence(NearbyPresence.On);
    private readonly ChoicePicker<NearbyPresence> enemyAirfields = Presence(NearbyPresence.On);
    private readonly ChoicePicker<int> friendlyAircraft = AircraftChance();
    private readonly ChoicePicker<int> enemyAircraft = AircraftChance();
    private readonly CheckBox encounters = new() { Content = "Allow nearby patrols to encounter each other", IsChecked = true, Foreground = TextBrush };
    internal ScenarioActivitySettings Value => new(new(friendlyNaval.Value, friendlyCoast.Value, friendlySize.Value,
            friendlyAirfields.Value, friendlyAircraft.Value),
        new(enemyNaval.Value, enemyCoast.Value, enemySize.Value, enemyAirfields.Value, enemyAircraft.Value), encounters.IsChecked == true);

    internal void AddTo(StackPanel content)
    {
        content.Children.Add(CreateText("Nearby forces, not under player control", 18, TextBrush));
        foreach (var field in new (string Label, FrameworkElement Control)[]
        {
            ("Friendly naval activity", friendlyNaval.Control), ("Friendly coastal sites", friendlyCoast.Control),
            ("Friendly naval/coastal allowance", friendlySize.Control), ("Enemy naval activity", enemyNaval.Control),
            ("Enemy coastal sites", enemyCoast.Control), ("Enemy naval/coastal allowance", enemySize.Control),
            ("Friendly airfields", friendlyAirfields.Control), ("Friendly independent aircraft chance", friendlyAircraft.Control),
            ("Enemy airfields", enemyAirfields.Control), ("Enemy independent aircraft chance", enemyAircraft.Control),
        })
        {
            content.Children.Add(CreateText(field.Label, 14, MutedTextBrush));
            content.Children.Add(field.Control);
        }
        content.Children.Add(encounters);
    }

    private static ChoicePicker<NearbyPresence> Presence(NearbyPresence initial = NearbyPresence.Off) => new(
        [(ChoiceLabel.Chance("Off", 1 - ScenarioRandomization.PresenceChance), NearbyPresence.Off),
         (ChoiceLabel.Chance("On, where placement permits", ScenarioRandomization.PresenceChance), NearbyPresence.On),
         ("Random", NearbyPresence.Random)], (int)initial);
    private static ChoicePicker<NearbySize> Size() => new(
        [.. new[] { "Small, 10%, minimum 8 pts", "Medium, 25%, minimum 16 pts", "Large, 40%, minimum 24 pts" }
            .Select((label, index) => (ChoiceLabel.Chance(label, ScenarioRandomization.Chance(ScenarioRandomization.NearbySizeWeights, index)), (NearbySize)index)),
         ("Random", NearbySize.Random)]);
    private static ChoicePicker<int> AircraftChance() => new(
        [.. new[] { 0, 35, 50, 75, 100 }.Select(chance => ($"{chance}%", chance))], 1);
}
