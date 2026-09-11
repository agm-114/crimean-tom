using Noesis;

namespace SeaPower.GameBridge;

internal class UiControls
{
    private readonly Dictionary<int, (FrameworkElement Element, object? Context)> elements = new();
    private string snapshot = "";
    private int nextId;
    private readonly List<WeakReference<FrameworkElement>> windows = [];
    private bool initialized;

    private void Initialize() => EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
        new RoutedEventHandler((sender, args) =>
        {
            if (Equals(sender, args.Source) && sender is FrameworkElement element
                && (element.GetType().Name == "Window" || element is ToolTip || element is ComboBoxItem)
                && !windows.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, element)))
                windows.Add(new(element));
        }));

    internal object Execute(GameCommand command)
    {
        if (command.Action == "scenario-ready") return new { ready = ScenarioControls.Ready };
        if (command.Action == "encyclopedia-ready") return new { ready = EncyclopediaControls.Ready, loading = MenuControls.Loading };
        if (command.Action == "menu" && command.Text.Length == 0) return MenuControls.Execute(command);
        if (command.Action is "load-scenario" or "cancel-scenario" or "menu" or "encyclopedia" or "encyclopedia-unit")
        {
            snapshot = "";
            return command.Action switch
            {
                "load-scenario" => ScenarioControls.Load(command), "cancel-scenario" => ScenarioControls.Cancel(command),
                "menu" => MenuControls.Execute(command), "encyclopedia" => EncyclopediaControls.Open(command),
                _ => EncyclopediaControls.Select(command),
            };
        }
        if (command.Action == "status") return new { time = TimeControls.Read() };
        if (command.Action == "inspect") return Inspect();
        if (command.Action == "units") return UnitSnapshot.Read();
        if (command.Action is "pause" or "resume" or "set-speed")
        {
            snapshot = "";
            return TimeControls.Execute(command);
        }
        if (snapshot.Length == 0 || command.Snapshot != snapshot || !elements.TryGetValue(command.Id, out var control))
            throw new InvalidOperationException("Stale control ID. Inspect the UI again.");
        FrameworkElement element = control.Element;
        if (!ReferenceEquals(element.DataContext, control.Context))
            throw new InvalidOperationException("Control data changed. Inspect the UI again.");
        if (command.Action == "details")
        {
            if (element.View == null) throw new InvalidOperationException("Control is detached. Inspect again.");
            return UiDetails.Read(element, command.Id, snapshot, command.Depth);
        }
        if (!element.IsVisible || !element.IsEnabled || element.View == null)
            throw new InvalidOperationException("Control is hidden, disabled or detached.");
        // Consume before dispatch, including actions that throw after changing UI state.
        snapshot = "";
        switch (command.Action)
        {
            case "click" when element is ButtonBase button:
                string label = Label(button);
                if (!command.Confirm && new[] { "save", "load", "play", "launch", "quit", "exit", "start", "delete" }
                    .Any(word => (button.Name + " " + label).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                    throw new InvalidOperationException("This action requires Confirm=true.");
                UIElement target = button.CommandTarget ?? button;
                bool canExecute = button.Command is RoutedCommand routedCheck
                    ? routedCheck.CanExecute(button.CommandParameter, target)
                    : button.Command?.CanExecute(button.CommandParameter) ?? true;
                if (!canExecute) throw new InvalidOperationException("Command is disabled.");
                if (button is ToggleButton toggle) toggle.IsChecked = toggle.IsChecked != true;
                if (button.Command is RoutedCommand routed)
                    routed.Execute(button.CommandParameter, target);
                else if (button.Command is { } action)
                    action.Execute(button.CommandParameter);
                else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                break;
            case "select" when element is Selector selector:
                if (command.Index < 0 || command.Index >= selector.Items.Count) throw new ArgumentOutOfRangeException(nameof(command.Index));
                selector.SelectedIndex = command.Index;
                break;
            case "select" when element is TreeViewItem item:
                item.IsSelected = true;
                break;
            case "expand" when element is TreeViewItem item:
                item.IsExpanded = true;
                break;
            case "open" when element is ComboBox combo:
                combo.IsDropDownOpen = true;
                break;
            case "set-text" when element is TextBox text:
                if (text.IsReadOnly) throw new InvalidOperationException("Text is read-only.");
                text.Text = command.Text;
                break;
            default: throw new ArgumentException("Unsupported action for this control.");
        }
        return new { ok = true, action = command.Action };
    }

    private object Inspect()
    {
        NoesisView[] views = UnityEngine.Object.FindObjectsByType<NoesisView>(UnityEngine.FindObjectsSortMode.None);
        if (!initialized && views.Any(view => view.Content != null))
        {
            Initialize();
            initialized = true;
        }
        Dictionary<FrameworkElement, int> previousIds = elements.ToDictionary(entry => entry.Value.Element, entry => entry.Key);
        elements.Clear();
        snapshot = Guid.NewGuid().ToString("N");
        List<object> rows = [];
        HashSet<DependencyObject> visited = [];
        Dictionary<int, int> siblingOrder = [];
        bool truncated = false;
        windows.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in windows.ToArray())
            if (reference.TryGetTarget(out var window) && window.IsVisible && window.View != null) Walk(window, 0);
        foreach (NoesisView view in views)
            if (view.isActiveAndEnabled && view.Content != null) Walk(view.Content, 0);
        return new { snapshot, identityVersion = 1, truncated, time = TimeControls.Read(), controls = rows };

        void Walk(DependencyObject node, int parent)
        {
            if (visited.Contains(node)) return;
            if (visited.Count >= 12000) { truncated = true; return; }
            visited.Add(node);
            if (node is UIElement { IsVisible: false }) return;
            if (node is FrameworkElement element)
            {
                bool actionable = element is ButtonBase or Selector or TextBox or TreeViewItem;
                string label = Label(element);
                if (actionable || element is TextBlock && label.Length > 0 || element.Name.Length > 0)
                {
                    int id = previousIds.TryGetValue(element, out int existing) ? existing : ++nextId;
                    int order = siblingOrder.TryGetValue(parent, out int next) ? next : 0;
                    siblingOrder[parent] = order + 1;
                    elements[id] = (element, element.DataContext);
                    object? options = element is Selector selector ? selector.Items.Cast<object>().Select((item, index) =>
                        new { index, text = ItemLabel(item, selector.DisplayMemberPath) }).ToArray() : null;
                    rows.Add(new { id, parent, order, type = element.GetType().Name, name = element.Name, text = label,
                        enabled = element.IsEnabled, selected = element is Selector selected ? selected.SelectedIndex : (int?)null,
                        expanded = element is TreeViewItem tree ? tree.IsExpanded : (bool?)null, options });
                    parent = id;
                }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), parent);
        }
    }

    private static string ItemLabel(object? item, string path = "")
    {
        if (item == null) return "";
        if (item is FrameworkElement element) return Label(element);
        foreach (string property in new[] { path, "DisplayName", "Name", "Title" }.Where(name => name.Length > 0).Distinct())
            if (item.GetType().GetProperty(property)?.GetValue(item) is string text && text.Length > 0) return text;
        return item.ToString() ?? "";
    }

    private static string Label(FrameworkElement element) => element switch
    {
        TextBlock text => text.Text ?? "",
        TextBox text => text.Text ?? "",
        ContentControl { Content: string text } => text,
        HeaderedItemsControl { Header: string text } => text,
        _ => string.Join(" ", TextChildren(element).Take(8)),
    };

    private static IEnumerable<string> TextChildren(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is UIElement { IsVisible: false }) continue;
            if (child is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)) yield return text.Text;
            else foreach (string value in TextChildren(child)) yield return value;
        }
    }
}
