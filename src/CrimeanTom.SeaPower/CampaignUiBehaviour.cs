using SeaPower;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using Vuplex.WebView;

namespace CrimeanTom.SeaPower;

internal sealed class CampaignUiBehaviour : MonoBehaviour
{
    internal static CampaignUiBehaviour? Instance { get; private set; }

    private GameObject? _canvasObject;
    private GameObject? _eventSystemObject;
    private CanvasWebViewPrefab? _webViewPrefab;

    private void Awake()
    {
        Instance = this;
    }

    private void Update()
    {
        if (_canvasObject is not null
            && (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F10)))
        {
            _ = CloseAsync();
        }
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    internal void OpenFromCampaignMenu()
    {
        if (_canvasObject is null)
        {
            Open();
        }
    }

    private void Open()
    {
        string uiPath = Path.Combine(
            Application.streamingAssetsPath,
            "CrimeanTomOperations",
            "ui",
            "index.html");
        if (!File.Exists(uiPath))
        {
            Debug.LogError($"[Crimean Tom Operations] campaign UI was not found at {uiPath}");
            return;
        }

        _canvasObject = new GameObject(
            "Crimean Tom Campaign UI",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster));
        DontDestroyOnLoad(_canvasObject);

        if (EventSystem.current is null)
        {
            _eventSystemObject = new GameObject(
                "Crimean Tom Campaign Event System",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            DontDestroyOnLoad(_eventSystemObject);
        }

        Canvas canvas = _canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;

        CanvasScaler scaler = _canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        _webViewPrefab = CanvasWebViewPrefab.Instantiate();
        _webViewPrefab.name = "Crimean Tom Campaign WebView";
        _webViewPrefab.InitialUrl = new Uri(uiPath).AbsoluteUri;
        _webViewPrefab.LogConsoleMessages = true;
        _webViewPrefab.transform.SetParent(_canvasObject.transform, worldPositionStays: false);

        RectTransform rect = _webViewPrefab.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        _webViewPrefab.Initialized += (_, _) =>
        {
            if (_webViewPrefab?.WebView is not null)
            {
                _webViewPrefab.WebView.MessageEmitted += OnMessageEmitted;
            }
        };

        Debug.Log("[Crimean Tom Operations] opened campaign headquarters");
    }

    private async System.Threading.Tasks.Task CloseAsync()
    {
        if (_canvasObject is not null)
        {
            Destroy(_canvasObject);
        }

        _canvasObject = null;
        _webViewPrefab = null;

        if (_eventSystemObject is not null)
        {
            Destroy(_eventSystemObject);
            _eventSystemObject = null;
        }

        await StandaloneWebView.TerminateBrowserProcess();
    }

    private async void OnMessageEmitted(object sender, Vuplex.WebView.EventArgs<string> eventArgs)
    {
        try
        {
            CampaignBridgeResult result = CampaignBridge.Handle(eventArgs.Value);
            if (result.CloseUi)
            {
                await CloseAsync();
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.MissionPath))
            {
                await CloseAsync();
                Globals.currentMissionFilePath = result.MissionPath;
                Debug.Log($"[Crimean Tom Operations] launching prepared operation {result.MissionPath}");
                MissionManager.DoLoad();
                return;
            }

            _webViewPrefab?.WebView.PostMessage(result.ResponseJson);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[Crimean Tom Operations] campaign UI request failed: {exception}");
        }
    }
}
