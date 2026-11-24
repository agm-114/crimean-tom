using SeaPower;
using UI;
using SeapowerUI;
using UnityEngine;

namespace UNREP;

public class SuppliesController : MonoBehaviour
{
    private readonly UnitStatusPage page = new(0x5552, "Supplies");
    private readonly UnitStatusPage unrepPage = new(0x5553, "UNREP");
    private readonly UnitStatusPage vertrepPage = new(0x5554, "VERTREP");
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        SupplyTransfers.Update();
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() as Vessel : null;
        bool available = SupplyTransfers.Available(unit);
        var host = MainGameView.Instance;
        var status = Globals._mainGameViewModel?.UnitStatus;
        var policy = available ? SupplyPolicy.For(unit!) : null;
        page.Refresh(host, status, available);
        unrepPage.Refresh(host, status, policy?.Unrep == true);
        vertrepPage.Refresh(host, status, policy?.Vertrep == true);
        if (available)
        {
            page.SetContent(unit!, ship => { var view = new SuppliesView(ship); return (view.Content, view.Refresh); });
            if (policy!.Unrep)
                unrepPage.SetContent(unit!, ship => { var view = new TransferView(ship); return (view.Content, view.Refresh); });
            if (policy.Vertrep)
                vertrepPage.SetContent(unit!, ship => { var view = new VertrepView(ship); return (view.Content, view.Refresh); });
        }
        page.RefreshContent();
        unrepPage.RefreshContent();
        vertrepPage.RefreshContent();
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
