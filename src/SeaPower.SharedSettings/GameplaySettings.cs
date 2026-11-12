using System.Runtime.CompilerServices;
using HarmonyLib;
using Noesis;
using SeapowerUI;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;

namespace SeaPower.SharedSettings;

internal class SettingsDraft(SettingsValues values, Action? changed = null)
{
    private static readonly Brush FieldBackground = new SolidColorBrush(Noesis.Color.FromRgb(28, 30, 33));
    private static readonly Brush FieldBorder = new SolidColorBrush(Noesis.Color.FromRgb(83, 88, 94));
    private static readonly Brush InvalidFieldBorder = new SolidColorBrush(Noesis.Color.FromRgb(206, 107, 93));
    private readonly Dictionary<string, string> invalidNumbers = new();
    internal SettingsValues Values { get; } = values;
    internal Dictionary<string, string> Errors { get; } = new();
    internal bool CanEdit => changed != null;

    internal void Set(string section, string key, double value)
    {
        if (changed == null) return;
        Values.Set(section, key, value);
        changed();
    }

    internal void Remove(string section, string key)
    {
        if (changed == null || !Values.Remove(section, key)) return;
        string field = $"{section}/{key}";
        Errors.Remove(field);
        invalidNumbers.Remove(field);
        changed();
    }

    internal T Editor<T>(T control) where T : FrameworkElement
    {
        control.IsEnabled = CanEdit;
        if (control is TextBox field)
        {
            field.Background = FieldBackground;
            field.BorderBrush = FieldBorder;
            field.BorderThickness = new Thickness(1);
        }
        return control;
    }

    internal TextBox NumberEditor(string section, string key, string label, double value, double min, double max)
    {
        string field = $"{section}/{key}";
        TextBox input = null!;
        input = Editor(Number(value, min, max, number => Set(section, key, number), error =>
        {
            input.BorderBrush = error == null ? FieldBorder : InvalidFieldBorder;
            if (error == null)
            {
                Errors.Remove(field);
                invalidNumbers.Remove(field);
            }
            else
            {
                Errors[field] = $"{label}. {error}";
                invalidNumbers[field] = input.Text;
            }
        }));
        if (invalidNumbers.TryGetValue(field, out string text)) input.Text = text;
        return input;
    }

    internal void DiscardErrors()
    {
        Errors.Clear();
        invalidNumbers.Clear();
    }
}

internal static class GameplaySettings
{
    private const string HostName = "CrimeanTomGameplaySettings";
    private static readonly ConditionalWeakTable<OptionsMenuView, List<Func<bool>>> Apply = new();
    private static readonly List<Action<OptionsMenuView, Grid, TabControl>> Pages = [];

    internal static void Register(Harmony harmony, string title, ModSettings settings, Action<StackPanel, SettingsDraft> build)
    {
        if (Pages.Count == 0)
        {
            harmony.Patch(AccessTools.Constructor(typeof(OptionsMenuView)),
                postfix: new HarmonyMethod(typeof(GameplaySettings), nameof(Attach)));
            harmony.Patch(AccessTools.Method(typeof(OptionsMenuView), "ButtonAccept_Click"),
                prefix: new HarmonyMethod(typeof(GameplaySettings), nameof(Accept)));
        }
        Pages.Add((view, host, tabs) => AddPage(view, host, tabs, title, settings, build));
    }

    private static void Attach(OptionsMenuView __instance)
    {
        __instance.Loaded += (_, _) =>
        {
            if (Apply.TryGetValue(__instance, out _)) return;
            try
            {
                if (Find<TabControl>(__instance) is not { } outer || outer.Items.Count < 3
                    || outer.Items[2] is not TabItem gameplay || gameplay.Content is not FrameworkElement native)
                    throw new InvalidOperationException("Could not find Sea Power's Gameplay tab.");

                var host = native as Grid;
                if (host?.Name != HostName)
                {
                    gameplay.Content = null;
                    host = BuildHost(native);
                    gameplay.Content = host;
                }
                var tabs = (TabControl)host.Children[0];
                foreach (var addPage in Pages) addPage(__instance, host, tabs);
                tabs.SelectedIndex = 0;
            }
            catch (Exception error) { Debug.LogError($"[Shared Settings] {error}"); }
        };
    }

    private static Grid BuildHost(FrameworkElement native)
    {
        var host = new Grid { Name = HostName };
        host.RowDefinitions.Add(new() { Height = GridLength.Auto });
        host.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var tabs = Tabs(("General", new Border()));
        tabs.Name = "ModGameplayTabs";
        host.Children.Add(tabs);
        Grid.SetRow(native, 1);
        host.Children.Add(native);
        tabs.SelectionChanged += (_, _) =>
        {
            for (int i = 1; i < host.Children.Count; i++)
                host.Children[i].Visibility = i == tabs.SelectedIndex + 1 ? Visibility.Visible : Visibility.Collapsed;
        };
        return host;
    }

    private static void AddPage(OptionsMenuView view, Grid host, TabControl tabs, string title, ModSettings settings,
        Action<StackPanel, SettingsDraft> build)
    {
        bool useDefaults = settings.UseDefaults;
        bool dirty = false;
        SettingsDraft custom = Edit(settings.Edit());
        var body = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        var editors = new StackPanel();
        var message = Text("");
        var reset = Button("Reset to mod defaults", () =>
        {
            custom = Edit(settings.Defaults.Copy());
            dirty = true;
            Refresh();
        });
        reset.Visibility = useDefaults ? Visibility.Collapsed : Visibility.Visible;
        var mode = Check("Use mod defaults", useDefaults, value =>
        {
            useDefaults = value;
            dirty = true;
            custom.DiscardErrors();
            reset.Visibility = useDefaults ? Visibility.Collapsed : Visibility.Visible;
            Refresh();
        });
        var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(reset, Dock.Right);
        actions.Children.Add(reset);
        actions.Children.Add(mode);
        body.Children.Add(actions);
        body.Children.Add(message);
        body.Children.Add(editors);
        Refresh();

        var page = Scroll(body);
        page.Visibility = Visibility.Collapsed;
        Grid.SetRow(page, 1);
        int index = 1;
        while (index < tabs.Items.Count && tabs.Items[index] is TabItem item
            && string.CompareOrdinal(item.Header?.ToString(), title) < 0) index++;
        host.Children.Insert(index + 1, page);
        var tab = new TabItem { Header = title };
        tabs.Items.Insert(index, tab);
        Apply.GetOrCreateValue(view).Add(Save);

        SettingsDraft Edit(SettingsValues values) => new(values, () => dirty = true);

        void Refresh()
        {
            message.Text = "";
            editors.Children.Clear();
            build(editors, useDefaults ? new(settings.Defaults.Copy()) : custom);
        }

        bool Save()
        {
            try
            {
                if (!useDefaults && custom.Errors.Values.FirstOrDefault() is { } error) throw new FormatException(error);
                if (!dirty) return true;
                settings.Apply(useDefaults, custom.Values);
                return true;
            }
            catch (Exception error)
            {
                message.Text = error.Message;
                tabs.SelectedItem = tab;
                page.ScrollToVerticalOffset(0);
                return false;
            }
        }
    }

    private static bool Accept(OptionsMenuView __instance) =>
        !Apply.TryGetValue(__instance, out var actions) || actions.All(save => save());
}
