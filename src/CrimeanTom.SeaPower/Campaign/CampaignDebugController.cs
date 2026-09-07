using UnityEngine;

namespace CrimeanTom.SeaPower;

internal class CampaignDebugController : MonoBehaviour
{
    private static CampaignDebugController? _instance;

    private readonly Queue<Action> _nextFrame = new();

    internal static void EnsureInstalled()
    {
        if (_instance is not null)
        {
            return;
        }

        GameObject host = new("Crimean Tom Operations Debug");
        DontDestroyOnLoad(host);
        _instance = host.AddComponent<CampaignDebugController>();
    }

    internal static void RunNextFrame(Action action)
    {
        EnsureInstalled();
        _instance!._nextFrame.Enqueue(action);
    }

    private void Update()
    {
        GeneratedScenarioRuntime.RefreshFriendlyTracks();
        int count = _nextFrame.Count;
        for (int index = 0; index < count; index++)
        {
            try
            {
                _nextFrame.Dequeue().Invoke();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Crimean Tom Operations] deferred UI action failed: {exception}");
            }
        }
    }
}
