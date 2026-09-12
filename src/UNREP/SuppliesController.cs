using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using UnityEngine;

namespace UNREP;

public class SuppliesController : MonoBehaviour
{
    private readonly UnitStatusPage page = new(0x5552, "Supplies");
    private readonly UnitStatusPage unrepPage = new(0x5553, "UNREP");
    private readonly UnitStatusPage vertrepPage = new(0x5554, "VERTREP");
    private Vessel? selected;
    private SuppliesView? view;
    private TransferView? unrepView;
    private VertrepView? vertrepView;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        SupplyTransfers.Update();
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() as Vessel : null;
        bool available = SupplyTransfers.Available(unit);
        page.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, available);
        var policy = available ? SupplyPolicy.For(unit!) : null;
        unrepPage.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, policy?.Unrep == true);
        vertrepPage.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, policy?.Vertrep == true);
        if (!available)
        {
            selected = null; view = null; unrepView = null; vertrepView = null;
            page.Content = unrepPage.Content = vertrepPage.Content = null;
            return;
        }
        if (unit != selected || page.Content == null)
        {
            selected = unit;
            view = new SuppliesView(unit!);
            page.Content = view.Content;
            unrepView = null;
            vertrepView = null;
        }
        if (policy!.Unrep && (unrepView == null || unrepPage.Content == null)) unrepView = new(unit!);
        if (policy.Vertrep && (vertrepView == null || vertrepPage.Content == null)) vertrepView = new(unit!);
        unrepPage.Content = policy.Unrep ? unrepView?.Content : null;
        vertrepPage.Content = policy.Vertrep ? vertrepView?.Content : null;
        view?.Refresh();
        if (policy.Unrep) unrepView?.Refresh();
        if (policy.Vertrep) vertrepView?.Refresh();
    }

    private void OnDestroy()
    {
        page.Dispose();
        unrepPage.Dispose();
        vertrepPage.Dispose();
        foreach (var job in SupplyTransfers.Jobs) job.Release();
        SupplyTransfers.Jobs.Clear();
        VertrepOperation.Clear();
        SupplySpeedLimits.Update();
    }
}
