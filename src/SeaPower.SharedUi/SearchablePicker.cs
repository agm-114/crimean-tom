using Noesis;

namespace SeaPower.SharedUi;

public class SearchablePicker<T>
{
    private readonly TextBox search;
    private readonly Func<string, IEnumerable<T>> find;
    private readonly Func<T, string> label;
    private readonly int limit;
    private readonly TextBlock notice = NativeUi.Text("");
    private readonly ChoicePicker<T> picker;
    private bool refreshing;
    public StackPanel Results { get; }
    public bool HasSelection => picker.TryGetValue(out _);
    public bool TryGetValue(out T value) => picker.TryGetValue(out value);
    public event Action? SelectionChanged;

    public SearchablePicker(TextBox search, string name, Func<string, IEnumerable<T>> find,
        Func<T, string> label, int limit, Func<T, string>? tooltip = null)
    {
        if (limit < 1 || limit == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(limit));
        this.search = search;
        this.find = find;
        this.label = label;
        this.limit = limit;
        picker = new([], tooltip: tooltip);
        picker.Control.Name = name;
        picker.Control.SelectionChanged += (_, _) => { if (!refreshing) SelectionChanged?.Invoke(); };
        Results = NativeUi.Stack(picker.Control, notice);
        search.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        var matches = find(search.Text.Trim()).Take(limit + 1).ToArray();
        refreshing = true;
        try { picker.Replace(matches.Take(limit), label, preserveSelection: false); }
        finally { refreshing = false; }
        picker.Control.IsEnabled = HasSelection;
        notice.Text = matches.Length == 0 ? "No matching entries" : matches.Length > limit ? "Refine the search to see more matches." : "";
        notice.Visibility = notice.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SelectionChanged?.Invoke();
    }
}
