using System;
using System.ComponentModel;
using Noesis;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPower.SharedUi;

// Owns one tab and presenter; never registers a global Noesis class handler.
internal class UnitStatusPage(int kind, string title) : IDisposable
{
    private MainGameView? host;
    private UnitStatusViewModel? owner;
    private UnitStatusTabViewModel? tab;
    private UnitStatusView? view;
    private ContentControl? presenter;
    internal object? Content { get => tab?.Content; set { if (tab != null) tab.Content = value; } }

    internal void Refresh(MainGameView? currentHost, UnitStatusViewModel? currentOwner, bool available)
    {
        if (host != currentHost || owner != currentOwner)
        {
            Dispose();
            host = currentHost;
            owner = currentOwner;
            if (host == null || owner == null) return;
            tab = new(owner, (UnitStatusTab)kind, title);
            tab.PropertyChanged += OnSelection;
            owner.Tabs.Add(tab);
        }
        if (tab == null) return;
        tab.IsAvailable = available && host?.IsVisible == true;
        if (!tab.IsAvailable && tab.IsSelected) owner?.SelectTab(UnitStatusTab.Engagement);
        if (host?.IsVisible != true) return;
        if (view?.IsVisible != true)
        {
            var current = NativeUi.Find<UnitStatusView>(host);
            if (view != current) { RemovePresenter(); view = current; }
        }
        if (presenter != null || view == null) return;
        // Native headers are dynamic; content containers are hard-coded.
        if (NativeUi.Find<FlightDeckContentView>(view)?.Parent is not Grid wrapper || wrapper.Parent is not Grid panels) return;
        presenter = new ContentControl
        {
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
        tab = null;
        view = null;
        owner = null;
        host = null;
    }
}
