using Noesis;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;

namespace UNREP;

public class SuppliesController : MonoBehaviour
{
    private MainGameView? host;
    private UnitStatusViewModel? status;
    private UnitStatusTabViewModel? tab;
    private UnitStatusView? view;
    private ContentControl? presenter;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        if (host != MainGameView.Instance) { Detach(); host = MainGameView.Instance; }
        if (host == null || !host.IsVisible) { Hide(); return; }
        if (status != Globals._mainGameViewModel?.UnitStatus)
        {
            Detach();
            status = Globals._mainGameViewModel?.UnitStatus;
            if (status == null) return;
            tab = new(status, (UnitStatusTab)0x5552, "Supplies")
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(10),
                    Children = { Text("Supplies"), Text("Replenishment controls are not implemented yet.") },
                },
            };
            tab.PropertyChanged += OnTabChanged;
            status.Tabs.Add(tab);
        }
        if (tab == null) return;
        if (view?.IsVisible != true)
        {
            var current = FindView<UnitStatusView>(host);
            if (view != current) { RemovePresenter(); view = current; }
        }
        AttachContent();
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
        tab.IsAvailable = unit is Vessel && !unit.IsDestroyed && unit.IsPlayerUnit && unit.AcceptsOrdersFromPlayer;
        if (!tab.IsAvailable) Hide();
    }

    private void AttachContent()
    {
        if (presenter != null || view == null || tab == null) return;
        // Native tab headers are dynamic, but their content containers are not.
        if (FindView<FlightDeckContentView>(view)?.Parent is not Grid wrapper || wrapper.Parent is not Grid panels) return;
        presenter = new ContentControl
        {
            Name = "SuppliesContent", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Visibility = tab.IsSelected ? Visibility.Visible : Visibility.Collapsed,
        };
        presenter.SetBinding(ContentControl.ContentProperty, new Binding(nameof(tab.Content)) { Source = tab });
        panels.Children.Add(presenter);
    }

    private static T? FindView<T>(DependencyObject root) where T : FrameworkElement
    {
        if (root is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindView<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void OnTabChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (presenter != null && args.PropertyName == nameof(UnitStatusTabViewModel.IsSelected))
            presenter.Visibility = tab?.IsSelected == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Hide()
    {
        if (tab == null) return;
        tab.IsAvailable = false;
        if (tab.IsSelected) status?.SelectTab(UnitStatusTab.Engagement);
    }

    private void RemovePresenter()
    {
        if (presenter?.Parent is Panel panels) panels.Children.Remove(presenter);
        presenter = null;
    }

    private void Detach()
    {
        Hide();
        RemovePresenter();
        view = null;
        if (tab != null) { tab.PropertyChanged -= OnTabChanged; status?.Tabs.Remove(tab); }
        tab = null;
        status = null;
    }

    private void OnDestroy() => Detach();
}
