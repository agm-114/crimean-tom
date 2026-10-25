using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NuclearOption.ScenarioEditor.Core;

namespace NuclearOption.ScenarioEditor;

public partial class MainWindow : Window
{
    private readonly EditorSettings _settings = EditorSettings.Load();
    private readonly SortedSet<string> _catalog = new(StringComparer.Ordinal);
    private IReadOnlyList<MissionFile> _missions = [];
    private MissionDocument? _document;
    private string _source = "";
    private string _workingPath = "";
    private string? _expectedOutput;
    private bool _refreshing;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        MissionsPathLabel.Text = MissionFiles.MissionsDirectory;
        _catalog.UnionWith(UnitCatalog.All.Where(u => u.IsReplacement).Select(u => u.Id));
        _ready = true;
        RefreshMissions();
        var argument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        var initial = argument ?? _settings.LastSource;
        if (initial is not null && File.Exists(initial)) OpenSource(initial);
        else StatusLabel.Text = _missions.Count > 0 ? "Choose a source mission on the left." : "No missions found. You can browse to a scenario JSON.";
        CommandBindings.Add(new(ApplicationCommands.Save, (_, _) => SaveOutput(), (_, e) => e.CanExecute = _document is not null));
        CommandBindings.Add(new(ApplicationCommands.Undo, (_, _) => Undo(), (_, e) => e.CanExecute = _document?.CanUndo == true));
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

    private void OpenSource(string path, bool resume = true)
    {
        if (!CanDiscard()) return;
        TryAction(() =>
        {
            var source = Path.GetFullPath(path);
            var output = _settings.Outputs.GetValueOrDefault(source) ?? MissionFiles.DefaultOutput(source);
            MissionFiles.ValidateOutput(source, output);
            // Validate the source even when resuming a previously edited output.
            var original = new MissionDocument(File.ReadAllText(source));
            var working = resume && File.Exists(output) ? output : source;
            var document = working == source ? original : new MissionDocument(File.ReadAllText(working));
            _source = source;
            _workingPath = working;
            _document = document;
            _refreshing = true;
            SourcePathBox.Text = source;
            OutputPathBox.Text = output;
            _refreshing = false;
            _expectedOutput = File.Exists(output) ? File.ReadAllText(output) : null;
            _catalog.UnionWith(original.Usage.Select(u => u.Type));
            _settings.LastSource = source;
            RefreshDocument();
            StatusLabel.Text = working == source ? "Source loaded. Edits will be saved to the output mission." : "Resumed the saved output. The source remains read-only.";
        });
    }

    private void RefreshDocument()
    {
        if (_document is null) return;
        _refreshing = true;
        var from = FromType.Text;
        var to = ToType.Text;
        var product = FactoryProduct.Text;
        var usage = _document.Usage;
        _catalog.UnionWith(usage.Select(u => u.Type));
        FromType.ItemsSource = UnitCatalog.Options(usage.Select(u => u.Type));
        ToType.ItemsSource = UnitCatalog.Options(_catalog);
        FactoryProduct.ItemsSource = UnitCatalog.Options(_catalog.Where(id => UnitCatalog.Get(id).Category != "Ship"));
        FromType.Text = from;
        ToType.Text = to;
        FactoryProduct.Text = product;
        UsageGrid.ItemsSource = usage;
        FilterFactories();
        ChangesGrid.ItemsSource = _document.Edits.ToList();
        UndoButton.IsEnabled = _document.CanUndo;
        SaveButton.IsEnabled = EditorTabs.IsEnabled = true;
        Title = $"{(_document.IsDirty ? "* " : "")}Nuclear Option | {Path.GetFileNameWithoutExtension(_source)}";
        DocumentInfo.Text = $"{usage.Sum(u => u.Ground):N0} ground units · {_document.Factories.Count:N0} production buildings · " +
            $"{_document.Edits.Count:N0} edits this session · Working from {Path.GetFileName(_workingPath)}";
        _refreshing = false;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_document is null || _refreshing) return;
        var before = UnitCatalog.ResolveId(FromType.Text);
        var after = UnitCatalog.ResolveId(ToType.Text);
        var usage = _document.Usage.FirstOrDefault(u => u.Type == before);
        ReplacementPreview.Text = usage is null || string.IsNullOrWhiteSpace(after) ? "Choose the original type and its replacement." :
            before == after ? "Choose a different replacement type." :
            $"{usage.Total} fields will change: {usage.Ground} ground, {usage.Aircraft} aircraft, {usage.Ships} ships, {usage.Factories} factories, {usage.Reserves} reserve entries.";
    }

    private void ReplaceClick(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        if (_document is null) return;
        var before = UnitCatalog.ResolveId(FromType.Text);
        var after = UnitCatalog.ResolveId(ToType.Text);
        var count = _document.ReplaceGlobally(before, after);
        RefreshDocument();
        StatusLabel.Text = count == 0 ? "No matching fields changed." : $"Replaced {UnitCatalog.Name(before)} with {UnitCatalog.Name(after)} in {count} fields. Save output to write the mission.";
    });

    private void ApplyFactoriesClick(object sender, RoutedEventArgs e) => TryAction(() =>
    {
        if (_document is null) return;
        var selected = FactoryGrid.SelectedItems.OfType<Factory>().ToList();
        if (selected.Count == 0) throw new ArgumentException("Select at least one factory first.");
        double? seconds = null;
        if (!string.IsNullOrWhiteSpace(FactorySeconds.Text))
        {
            if (!double.TryParse(FactorySeconds.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value))
                throw new ArgumentException("Enter a valid production time in seconds.");
            seconds = value;
        }
        string? product = DisableProduction.IsChecked == true ? "" : string.IsNullOrWhiteSpace(FactoryProduct.Text) ? null : UnitCatalog.ResolveId(FactoryProduct.Text);
        var count = _document.SetFactories(selected, product, seconds);
        RefreshDocument();
        StatusLabel.Text = $"Changed {count} settings across {selected.Count} selected production buildings.";
    });

    private void SaveOutput() => TryAction(() =>
    {
        if (_document is null) return;
        var output = Path.GetFullPath(OutputPathBox.Text);
        MissionFiles.ValidateOutput(_source, output);
        var actual = File.Exists(output) ? File.ReadAllText(output) : null;
        if (actual != _expectedOutput && MessageBox.Show(this,
                "The target already exists or changed outside this editor. Save the current document over it? A backup will be created.",
                "Output changed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var backup = MissionFiles.Save(_source, output, _document);
        _expectedOutput = _document.Serialize();
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

    private bool CanDiscard() => _document?.IsDirty != true || MessageBox.Show(this,
        "There are unsaved changes. Discard them?", "Unsaved output", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            StatusLabel.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Scenario editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Undo()
    {
        _document?.Undo();
        RefreshDocument();
        StatusLabel.Text = "Undid the last operation.";
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
    private void ReloadClick(object sender, RoutedEventArgs e) { if (_source.Length > 0) OpenSource(_source, false); }
    private void SelectFactoriesClick(object sender, RoutedEventArgs e) => FactoryGrid.SelectAll();
    private void FactorySelectionChanged(object sender, SelectionChangedEventArgs e) => FactorySelectionLabel.Text = $"{FactoryGrid.SelectedItems.Count} factories selected";
    private void FactorySearchChanged(object sender, TextChangedEventArgs e) { if (_ready) FilterFactories(); }
    private void MissionSearchChanged(object sender, TextChangedEventArgs e) { if (_ready) FilterMissions(); }
    private void ReplacementChanged(object sender, TextChangedEventArgs e) { if (_ready) UpdatePreview(); }
    private void WindowClosing(object? sender, CancelEventArgs e) => e.Cancel = !CanDiscard();
    private void WindowActivated(object? sender, EventArgs e) { if (_ready) RefreshMissions(); }
    private void OutputPathChanged(object sender, TextChangedEventArgs e) { if (!_refreshing) _expectedOutput = null; }

    private void UsageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && UsageGrid.SelectedItem is UnitUsage usage) FromType.Text = usage.Display;
    }
}
