using System;
using System.ComponentModel;
using Noesis;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPower.SharedUi;

// Owns one tab and presenter; never registers a global Noesis class handler.
public class UnitStatusPage(int kind, string title, UnitStatusTab? insertAfter = null, string? presenterName = null) : IDisposable
{
    private MainGameView? host;
    private UnitStatusViewModel? owner;
    private UnitStatusTabViewModel? tab;
    private UnitStatusView? view;
    private ContentControl? presenter;
    private object? contentKey;
    private Action? refreshContent;

    public bool IsShowing(object key) => tab?.Content != null && Equals(contentKey, key);

    public bool SetContent<TKey>(TKey key, Func<TKey, (FrameworkElement Content, Action Refresh)> create) where TKey : notnull
    {
        if (tab?.IsAvailable != true) { ClearContent(); return false; }
        if (IsShowing(key)) return false;
        var (content, refresh) = create(key);
        tab.Content = content;
        contentKey = key;
        refreshContent = refresh;
        return true;
    }

    public void RefreshContent() => refreshContent?.Invoke();

    private void ClearContent()
    {
        if (tab?.Content != null) tab.Content = null;
        contentKey = null;
        refreshContent = null;
    }

    public void Refresh(MainGameView? currentHost, UnitStatusViewModel? currentOwner, bool available)
    {
        if (host != currentHost || owner != currentOwner)
        {
            Dispose();
            host = currentHost;
            owner = currentOwner;
            if (host == null || owner == null) return;
            tab = new(owner, (UnitStatusTab)kind, title);
            tab.PropertyChanged += OnSelection;
            int index = insertAfter is { } anchor && owner.Tabs.FirstOrDefault(item => item.Kind == anchor) is { } existing
                ? owner.Tabs.IndexOf(existing) : -1;
            if (index < 0) owner.Tabs.Add(tab);
            else owner.Tabs.Insert(index + 1, tab);
        }
        if (tab == null) return;
        tab.IsAvailable = available && host?.IsVisible == true;
        if (!tab.IsAvailable)
        {
            if (tab.IsSelected) owner?.SelectTab(UnitStatusTab.Engagement);
            ClearContent();
        }
        // Noesis can retain old views across window recreation and scene changes.
        // IsVisible on a cached view alone does not establish membership in this host.
        var current = host?.IsVisible == true
            ? NativeUi.Find<UnitStatusView>(host, candidate => candidate.IsVisible && Equals(candidate.DataContext, owner))
            : null;
        if (view != current) { RemovePresenter(); view = current; }
        if (presenter != null && presenter.Parent == null) RemovePresenter();
        if (presenter != null || view == null) return;
        // Native headers are dynamic; content containers are hard-coded.
        if (NativeUi.Find<FlightDeckContentView>(view)?.Parent is not Grid wrapper || wrapper.Parent is not Grid panels) return;
        presenter = new ContentControl
        {
            Name = presenterName ?? "UnitStatusTabContent" + kind,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Visibility = tab.IsSelected ? Visibility.Visible : Visibility.Collapsed,
        };
        NativeUi.Bind(presenter, ContentControl.ContentProperty, nameof(tab.Content), tab);
        panels.Children.Add(presenter);
    }

    private void OnSelection(object sender, PropertyChangedEventArgs args)
    {
        if (presenter != null && args.PropertyName == nameof(UnitStatusTabViewModel.IsSelected))
            presenter.Visibility = tab?.IsSelected == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemovePresenter()
    {
        // Release the visual content before another presenter binds the same tab.
        if (presenter != null) presenter.Content = null;
        if (presenter?.Parent is Panel parent) parent.Children.Remove(presenter);
        presenter = null;
    }

    public void Dispose()
    {
        if (tab != null)
        {
            if (tab.IsSelected) owner?.SelectTab(UnitStatusTab.Engagement);
            tab.PropertyChanged -= OnSelection;
            owner?.Tabs.Remove(tab);
        }
        RemovePresenter();
        ClearContent();
        tab = null;
        view = null;
        owner = null;
        host = null;
    }
}
