using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using UnityEngine;
using SeaPower.SharedSettings;
using static SeaPower.SharedUi.NativeUi;

namespace Amphibs;

public class WaterEffectsPanel : MonoBehaviour
{
    private MainGameView? host;
    private readonly HostedWindow window = new();
    private AmphibiousProfile? current;
    private SettingsValues? displayed;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        if (host != MainGameView.Instance) { window.Dispose(); host = MainGameView.Instance; current = null; }
        if (!AmphibsSettings.ShowTuningPanel) { window.Dispose(); current = null; return; }
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
        AmphibiousProfile? profile = unit && AmphibiousCatalog.TryGet(unit, out var found) ? found : null;
        if (profile == current && displayed == AmphibsSettings.Settings.Current) return;
        window.Dispose();
        current = profile;
        displayed = AmphibsSettings.Settings.Current;
        if (profile != null && host?.IsVisible == true) window.Open(host, () => Build(profile));
    }

    private SeapowerUI.Window Build(AmphibiousProfile profile)
    {
        var content = new DockPanel { Margin = new Thickness(8) };
        var footer = new StackPanel();
        var message = Text("Wake coefficients affect all vehicles. Box corrections affect this type.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var baseline = WaterEffectSettings.All.ToDictionary(setting => setting.Key, setting => profile.Value(setting.Key));
        var restore = new Dictionary<string, Action<float>>();
        buttons.Children.Add(Button("Save", () =>
        {
            try
            {
                var settings = AmphibsSettings.Settings;
                if (settings.UseDefaults) return;
                settings.Apply(false, settings.Current);
                displayed = settings.Current;
                foreach (var setting in WaterEffectSettings.All) baseline[setting.Key] = profile.Value(setting.Key);
                message.Text = "Saved to local Amphibs settings.";
            }
            catch (Exception error) { message.Text = "Save failed: " + error.Message; }
        }, primary: true));
        buttons.Children.Add(Button("Revert", () =>
        {
            foreach (var entry in restore) entry.Value(baseline[entry.Key]);
            message.Text = "Restored values from when opened or last saved.";
        }));
        footer.Children.Add(message);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        var rows = new StackPanel();
        string[] order = ["Vehicle box", "General", "Surface footprint", "Displacement", "Ripples", "Surface foam", "Bow spray", "Propwash", "Hull foam trail", "Stern foam trail"];
        foreach (string group in order)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            rows.Children.Add(section);
            var settings = WaterEffectSettings.All.Where(setting => setting.Group == group).ToArray();
            if (!settings.Any(setting => setting.IsToggle)) section.Children.Add(Text(group));
            var controls = new StackPanel { Margin = new Thickness(16, 5, 0, 0) };
            foreach (var setting in settings.OrderByDescending(setting => setting.IsToggle))
            {
                if (setting.IsToggle)
                {
                    var toggle = new CheckBox
                    {
                        Content = setting.Label,
                        IsChecked = profile.Value(setting.Key) > 0,
                        Name = setting.Key,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    toggle.Checked += (_, _) => Changed(1);
                    toggle.Unchecked += (_, _) => Changed(0);
                    restore.Add(setting.Key, value => toggle.IsChecked = value > 0);
                    section.Children.Add(toggle);
                    controls.IsEnabled = toggle.IsChecked == true;
                    void Changed(float value)
                    {
                        profile.Set(setting.Key, value);
                        controls.IsEnabled = value > 0;
                        message.Text = "Unsaved changes.";
                    }
                    continue;
                }
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                var label = Text($"{setting.Label}: {profile.Value(setting.Key):0.###}");
                var slider = new Slider
                {
                    Minimum = setting.Min,
                    Maximum = setting.Max,
                    Value = profile.Value(setting.Key),
                    SmallChange = 0.01f,
                    LargeChange = 0.1f,
                    Height = 22,
                    Name = setting.Key
                };
                slider.ValueChanged += (_, _) =>
                {
                    profile.Set(setting.Key, (float)Math.Round(slider.Value, 3));
                    label.Text = $"{setting.Label}: {profile.Value(setting.Key):0.###}";
                    message.Text = WaterEffectSettings.IsPerUnit(setting.Key) ? "Unsaved vehicle box correction." : "Unsaved coefficients. Applies to all amphibious vehicles.";
                };
                restore.Add(setting.Key, value => slider.Value = value);
                row.Children.Add(label);
                row.Children.Add(slider);
                controls.Children.Add(row);
            }
            section.Children.Add(controls);
        }
        content.Children.Add(Scroll(rows));
        var panel = Window("AmphibsWaterEffects", "Water effects: " + profile.Name, content, 440, 680, 360, 300);
        panel.X = 450;
        return panel;
    }

    private void OnDestroy() => window.Dispose();
}
