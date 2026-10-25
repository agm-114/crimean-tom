using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NuclearOption.ScenarioEditor.Core;

namespace NuclearOption.ScenarioEditor;

public partial class MainWindow : Window
{
    private readonly EditorSettings _settings = EditorSettings.Load();
    private readonly SortedSet<string> _catalog = new(StringComparer.Ordinal);
    private readonly OverrideFile _overrideFile = new();
    private readonly Stack<string> _undo = new();
    private readonly DispatcherTimer _overrideTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private OverridePlan _overrides = new();
    private string _savedDocument = "";
    private IReadOnlyList<UnitUsage> _sourceUsage = [];
    private IReadOnlyList<UnitUsage> _usage = [];
    private IReadOnlyList<MissionFile> _missions = [];
    private MissionDocument? _document;
    private MissionDocument? _sourceDocument;
    private string _source = "";
    private string _workingPath = "";
    private string? _expectedOutput;
    private string? _editingSource;
    private bool _refreshing;
    private bool _ready;
    private bool _retrySync;

    public MainWindow()
    {
        InitializeComponent();
        UnitMode.SelectedValue = _settings.UnitMode;
        MissionsPathLabel.Text = MissionFiles.MissionsDirectory;
        _catalog.UnionWith(UnitCatalog.All.Where(u => u.IsReplacement).Select(u => u.Id));
        TryAction(() => SynchronizeOverrides());
        _ready = true;
        RefreshMissions();
        var argument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        var restarting = int.TryParse(Environment.GetEnvironmentVariable("DOTNET_WATCH_ITERATION"), out var iteration) && iteration > 1;
        var initial = restarting ? _settings.LastSource ?? argument : argument ?? _settings.LastSource;
        if (initial is not null && File.Exists(initial)) OpenSource(initial);
        else StatusLabel.Text = _missions.Count > 0 ? "Choose a source mission on the left." : "No missions found. You can browse to a scenario JSON.";
        CommandBindings.Add(new(ApplicationCommands.Save, (_, _) => SaveOutput(), (_, e) => e.CanExecute = _document is not null));
        CommandBindings.Add(new(ApplicationCommands.Undo, (_, _) => Undo(), (_, e) => e.CanExecute = _undo.Count > 0));
        _overrideTimer.Tick += (_, _) => CheckOverrides();
        _overrideTimer.Start();
        Closed += (_, _) => _overrideTimer.Stop();
    }

    private void RefreshMissions()
    {
        try
        {
            _missions = MissionFiles.Discover(MissionFiles.MissionsDirectory);
            FilterMissions();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusLabel.Text = $"Could not scan the missions folder: {ex.Message}";
        }
    }

    private void FilterMissions()
    {
        var selected = (MissionList.SelectedItem as MissionFile)?.Path;
        MissionList.ItemsSource = _missions.Where(m => m.Name.Contains(MissionSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        MissionList.SelectedItem = MissionList.Items.OfType<MissionFile>().FirstOrDefault(m => m.Path == selected);
    }

    private void OpenSource(string path)
    {
        if (!CanDiscard()) return;
        TryAction(() =>
        {
            SynchronizeOverrides();
            var source = Path.GetFullPath(path);
            var output = _settings.Outputs.GetValueOrDefault(source) ?? MissionFiles.DefaultOutput(source);
            MissionFiles.ValidateOutput(source, output);
            var sourceJson = File.ReadAllText(source);
            var original = new MissionDocument(sourceJson);
            var result = _overrides.Apply(sourceJson);
            var outputJson = File.Exists(output) ? File.ReadAllText(output) : null;
            _source = source;
            _workingPath = source;
            _document = result;
            _sourceDocument = original;
            _sourceUsage = original.Usage;
            _savedDocument = outputJson is null ? original.Serialize() : new MissionDocument(outputJson).Serialize();
            _undo.Clear();
            _editingSource = null;
            _refreshing = true;
            SourcePathBox.Text = source;
            OutputPathBox.Text = output;
            _refreshing = false;
            _expectedOutput = outputJson;
            _catalog.UnionWith(original.Usage.Select(u => u.Type));
            _settings.LastSource = source;
            RememberSettings();
            RefreshDocument();
            ShowRebuildStatus(result, "Source loaded and overrides applied");
        });
    }

    private void RefreshDocument()
    {
        if (_document is null) return;
        _refreshing = true;
        var from = FromType.Text;
        var to = ToType.Text;
        _usage = _document.Usage;
        _catalog.UnionWith(_usage.Select(u => u.Type));
        var available = _usage.Where(u => IsAvailableSource(u.Type)).Select(u => u.Type);
        FromType.ItemsSource = UnitCatalog.Options(available);
        ToType.ItemsSource = UnitCatalog.Options(_catalog.Where(MatchesMode));
        var fromId = UnitCatalog.ResolveId(from);
        FromType.Text = IsAvailableSource(fromId) || IsEditingSource(fromId) ? from : "";
        ToType.Text = to;
        UsageGrid.ItemsSource = _usage.Where(u => u.MatchesCategory(CurrentMode)).ToList();
        FilterFactories();
        var faction = ProductionFaction.SelectedItem as string ?? "All factions";
        var factions = new[] { "All factions" }.Concat((_sourceDocument?.Factions ?? []).Concat(_document.Factions)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)).ToList();
        ProductionFaction.ItemsSource = factions;
        ProductionFaction.SelectedItem = factions.Contains(faction) ? faction : "All factions";
        RefreshProduction();
        ChangesGrid.ItemsSource = _document.Edits.ToList();
        UndoButton.IsEnabled = _undo.Count > 0;
        SaveButton.IsEnabled = EditorTabs.IsEnabled = true;
        Title = $"{(DocumentDirty ? "* " : "")}Nuclear Option | {Path.GetFileNameWithoutExtension(_source)}";
        DocumentInfo.Text = $"{_usage.Sum(u => u.Ground):N0} ground units · {_document.Factories.Count:N0} production buildings · " +
            $"{_document.Edits.Count:N0} overridden fields · Working from {Path.GetFileName(_workingPath)}";
        _refreshing = false;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_document is null || _refreshing) return;
        var before = UnitCatalog.ResolveId(FromType.Text);
        var after = UnitCatalog.ResolveId(ToType.Text);
        var usage = _usage.FirstOrDefault(u => u.Type == before) ??
            (IsEditingSource(before) ? _sourceUsage.FirstOrDefault(u => u.Type == before) : null);
        ReplacementPreview.Text = usage is null || string.IsNullOrWhiteSpace(after) ? "Choose the original type and its replacement." :
            before == after ? "Choose a different replacement type." :
            $"{usage.Total} fields will change: {usage.Ground} ground, {usage.Aircraft} aircraft, {usage.Ships} ships, {usage.Factories} factories, {usage.Reserves} reserve entries.";
    }

    private void ReplaceClick(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        if (_document is null) return;
        var before = UnitCatalog.ResolveId(FromType.Text);
        var after = UnitCatalog.ResolveId(ToType.Text);
        if (!IsAvailableSource(before) && !IsEditingSource(before))
            throw new ArgumentException("Choose a unit remaining in the current mission. To change an existing substitution, select Edit in Overrides.");
        if (!MatchesMode(before) || UnitCatalog.Get(after).Category is not "Unknown" && !MatchesMode(after))
            throw new ArgumentException("Choose source and replacement units in the current mode.");
        ChangeOverrides(plan => plan.SetSubstitution(before, after));
    });

    private void SaveOutput() => TryAction(() =>
    {
        if (_document is null) return;
        SynchronizeOverrides();
        var output = Path.GetFullPath(OutputPathBox.Text);
        MissionFiles.ValidateOutput(_source, output);
        var actual = File.Exists(output) ? File.ReadAllText(output) : null;
        if (actual != _expectedOutput && MessageBox.Show(this,
                "The target already exists or changed outside this editor. Save the current document over it? A backup will be created.",
                "Output changed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var backup = MissionFiles.Save(_source, output, _document);
        _expectedOutput = _document.Serialize();
        _savedDocument = _expectedOutput;
        _workingPath = output;
        _settings.LastSource = _source;
        _settings.Outputs[_source] = output;
        RefreshDocument();
        RefreshMissions();
        StatusLabel.Text = $"Saved output: {output}" + (backup.Length > 0 ? " · Previous output backed up." : "");
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusLabel.Text += " · Output saved, but the editor could not remember its path.";
        }
    });

    private void FilterFactories()
    {
        if (_document is null) return;
        var search = FactorySearch.Text;
        FactoryGrid.ItemsSource = _document.Factories.Where(f =>
            $"{f.Name} {f.Faction} {f.Building} {f.BuildingName} {f.Product} {f.ProductName}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void RefreshProduction()
    {
        if (_sourceDocument is null || _document is null) return;
        var faction = ProductionFaction.SelectedItem as string;
        ProductionGrid.ItemsSource = UnitProduction.Compare(_sourceDocument, _document, faction == "All factions" ? null : faction)
            .Where(row => row.HasFinalProduction && (HideUnchangedProduction.IsChecked != true || row.IsChanged)).ToList();
    }

    private void ProductionFactionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_refreshing) RefreshProduction();
    }

    private void ProductionFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_ready && !_refreshing) RefreshProduction();
    }

    private bool DocumentDirty => _document is not null && _document.Serialize() != _savedDocument;
    private bool CanDiscard() => !DocumentDirty || MessageBox.Show(this,
        "The mission output has not been exported. Continue? Your overrides are already saved and the preview can be rebuilt.",
        "Unexported output", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            StatusLabel.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Scenario editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Undo() => TryAction(() =>
    {
        SynchronizeOverrides();
        if (!_undo.TryPeek(out var previous)) return;
        ApplyPlan(OverridePlan.Parse(previous), false);
        _undo.Pop();
        RefreshDocument();
        StatusLabel.Text = "Undid the last override edit. JSON saved and preview rebuilt.";
    });

    private void RefreshOverrides()
    {
        SubstitutionRules.ItemsSource = _overrides.Substitutions.ToList();
        OverridesPathLabel.Text = _overrideFile.Path + " · Saves automatically";
    }

    private bool SynchronizeOverrides()
    {
        if (!_overrideFile.HasChanged && !_retrySync) return false;
        _retrySync = true;
        var plan = _overrideFile.Load();
        var result = _document is null ? null : plan.Apply(File.ReadAllText(_source));
        _overrides = plan;
        _undo.Clear();
        _editingSource = null;
        _retrySync = false;
        RefreshOverrides();
        if (result is not null)
        {
            _document = result;
            _workingPath = _source;
            RefreshDocument();
            ShowRebuildStatus(result, "External overrides reloaded");
        }
        return true;
    }

    private void CheckOverrides()
    {
        try { SynchronizeOverrides(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            _retrySync = true;
            StatusLabel.Text = $"Could not reload overrides: {ex.Message} Retrying automatically.";
        }
    }

    private void ApplyPlan(OverridePlan plan, bool remember = true)
    {
        if (_document is null) return;
        var result = plan.Apply(File.ReadAllText(_source));
        _overrideFile.Save(plan);
        if (remember) _undo.Push(_overrides.Serialize());
        _overrides = plan;
        _document = result;
        _editingSource = null;
        _workingPath = _source;
        RefreshOverrides();
        RefreshDocument();
        ShowRebuildStatus(result, "Overrides saved automatically");
    }

    private void ShowRebuildStatus(MissionDocument document, string message) => StatusLabel.Text =
        $"{message}. Preview rebuilt with {document.Edits.Count} field changes. Save output to export.";

    private void ChangeOverrides(Action<OverridePlan> edit) => TryAction(() =>
    {
        if (SynchronizeOverrides()) return;
        var plan = OverridePlan.Parse(_overrides.Serialize());
        edit(plan);
        if (plan.Serialize() != _overrides.Serialize()) ApplyPlan(plan);
    });

    private void RemoveSubstitutionClick(object sender, RoutedEventArgs e)
    {
        if (SubstitutionRules.SelectedItem is UnitSubstitution rule) ChangeOverrides(p => p.Substitutions.Remove(rule));
    }

    private void ToggleSubstitutionClick(object sender, RoutedEventArgs e)
    {
        if (SubstitutionRules.SelectedItem is UnitSubstitution rule)
            ChangeOverrides(p => p.Substitutions[p.Substitutions.IndexOf(rule)] = rule with { Disabled = !rule.Disabled });
    }

    private void EditSubstitutionClick(object sender, RoutedEventArgs e)
    {
        if (SubstitutionRules.SelectedItem is not UnitSubstitution rule) return;
        var category = UnitCatalog.Get(rule.From).Category;
        if (category is "Vehicle" or "Aircraft" or "Ship") UnitMode.SelectedValue = category;
        EditorTabs.SelectedIndex = 0;
        _editingSource = rule.From;
        FromType.Text = UnitCatalog.Name(rule.From);
        ToType.Text = UnitCatalog.Name(rule.To);
    }

    private void FactoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FactoryGrid.SelectedItem is not Factory factory) return;
        var category = UnitCatalog.Get(factory.Product).Category;
        if (category is not ("Vehicle" or "Aircraft" or "Ship")) return;
        UnitMode.SelectedValue = category;
        EditorTabs.SelectedIndex = 0;
        SelectSource(factory.Product);
        ToType.Text = "";
    }

    private void ChooseOutputClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var dialog = new OpenFolderDialog { Title = "Choose the output mission folder. Its name becomes the JSON filename." };
        if (Directory.Exists(Path.GetDirectoryName(OutputPathBox.Text))) dialog.InitialDirectory = Path.GetDirectoryName(OutputPathBox.Text)!;
        if (dialog.ShowDialog(this) != true) return;
        OutputPathBox.Text = Path.Combine(dialog.FolderName, Path.GetFileName(dialog.FolderName) + ".json");
    }

    private void BrowseSourceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open a source mission", Filter = "Mission JSON (*.json)|*.json" };
        if (Directory.Exists(MissionFiles.MissionsDirectory)) dialog.InitialDirectory = MissionFiles.MissionsDirectory;
        if (dialog.ShowDialog(this) == true) OpenSource(dialog.FileName);
    }

    private void OpenSelectedClick(object sender, RoutedEventArgs e)
    {
        if (MissionList.SelectedItem is MissionFile mission) OpenSource(mission.Path);
    }

    private void MissionDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedClick(sender, e);
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshMissions();
    private void SaveClick(object sender, RoutedEventArgs e) => SaveOutput();
    private void UndoClick(object sender, RoutedEventArgs e) => Undo();
    private void ReloadClick(object sender, RoutedEventArgs e) { if (_source.Length > 0) OpenSource(_source); }
    private void FactorySearchChanged(object sender, TextChangedEventArgs e) { if (_ready) FilterFactories(); }
    private void MissionSearchChanged(object sender, TextChangedEventArgs e) { if (_ready) FilterMissions(); }
    private void ReplacementChanged(object sender, TextChangedEventArgs e) { if (_ready) UpdatePreview(); }
    private void WindowClosing(object? sender, CancelEventArgs e) => e.Cancel = !CanDiscard();
    private void WindowActivated(object? sender, EventArgs e) { if (_ready) { RefreshMissions(); CheckOverrides(); } }
    private void OutputPathChanged(object sender, TextChangedEventArgs e) { if (!_refreshing) _expectedOutput = null; }

    private void UsageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && UsageGrid.SelectedItem is UnitUsage usage) SelectSource(usage.Type);
    }

    private string CurrentMode => UnitMode.SelectedValue as string ?? "Vehicle";

    private bool IsAvailableSource(string id) => _usage.Any(u => u.Type == id && u.MatchesCategory(CurrentMode)) &&
        !_overrides.Substitutions.Any(r => r.From == id);

    private bool IsEditingSource(string id) => _editingSource == id && _overrides.Substitutions.Any(r => r.From == id);

    private void SelectSource(string id)
    {
        if (!IsAvailableSource(id))
        {
            StatusLabel.Text = "This unit already has a substitution. Use Edit in Overrides to adjust it.";
            return;
        }
        _editingSource = null;
        FromType.Text = UnitCatalog.Name(id);
    }

    private bool MatchesMode(string id) => UnitCatalog.Get(id).Category == CurrentMode ||
        _sourceUsage.Concat(_usage).Any(u => u.Type == id && u.MatchesCategory(CurrentMode));

    private void UnitModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshing) return;
        _editingSource = null;
        _refreshing = true;
        FromType.Text = ToType.Text = "";
        _refreshing = false;
        RefreshDocument();
        _settings.UnitMode = CurrentMode;
        RememberSettings();
    }

    private void RememberSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusLabel.Text = "The editor could not remember its current source or mode.";
        }
    }
}
