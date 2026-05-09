using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class TransparentPetContextMenu : MonoBehaviour
{
    public TransparentWindowController windowController;
    public TransparentPetFreeCamera freeCamera;
    public TransparentPetRuntimeControls runtimeControls;
    public TransparentPetKawaiiActionController actionController;
    public TransparentPetPlacementController placementController;
    public TransparentPetVoiceRuntimeLauncher voiceLauncher;
    public TransparentPetHeadLookAt headLookAt;
    public TransparentPetSceneFaceTracker sceneFaceTracker;
    public TransparentPetPerformanceController performanceController;
    public TransparentPetRoute route = TransparentPetRoute.DesktopTransparent;
    public bool deriveRouteFromWindow = true;
    public Vector2 panelSize = new Vector2(460f, 540f);
    public float rightClickDragThreshold = 8f;
    public float toggleDebounceSeconds = 0.22f;
    public KeyCode menuHotkey = KeyCode.M;
    public KeyCode closeHotkey = KeyCode.Escape;

    private bool _visible;
    private bool _rightPressed;
    private bool _rightPressStartedOnTrigger;
    private Vector2 _rightPressPosition;
    private float _lastOpenTime = -100f;
    private float _lastCloseTime = -100f;
    private Vector2 _scrollPosition;
    private UnityEngine.Rect _panelScreenRect;
    private GUIStyle _panelStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _activeButtonStyle;
    private GUIStyle _closeButtonStyle;
    private GUIStyle _textFieldStyle;
    private GUIStyle _textAreaStyle;
    private TransparentPetRoute _styleRoute = (TransparentPetRoute)(-1);
    private MenuView _view = MenuView.Main;
    private Coroutine _faceTrackingStartRoutine;
    private Coroutine _voiceStartOptionsRoutine;
    private bool _voiceStartWithScreenVision = true;
    private bool _voiceStartWithRealtimeMonitoring = true;
    private bool _voiceStartWithCameraVideo = true;
    private string _selectedActionCategory = "\u5168\u90e8";
    private string _actionSearch = string.Empty;
    private const float MainPanelHeight = 340f;
    private const float MinimumPanelWidth = 460f;
    private const float MinimumPanelHeight = 540f;
    private const float HeaderHeight = 46f;
    private const float CloseButtonSize = 40f;
    private const float ButtonHeight = 44f;
    private const float CompactRowHeight = 40f;
    private const float PlacementMoveStep = 0.05f;
    private const float PlacementRotateStep = 15f;
    private const float PlacementScaleStep = 1.08f;

#if UNITY_STANDALONE_WIN
    private const int VkRButton = 0x02;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
#endif

    public bool IsVisible => _visible;

    private void Awake()
    {
        ResolveMissingReferences();
        NormalizePanelSize();
    }

    private void OnValidate()
    {
        NormalizePanelSize();
    }

    private void Update()
    {
        ResolveMissingReferences();
        if (windowController == null)
        {
            return;
        }

        if (IsKeyPressed(menuHotkey))
        {
            Vector2 cursor = windowController.TryGetCursorPositionInWindow(out Vector2 foundCursor)
                ? foundCursor
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            ToggleMenuAt(cursor);
            return;
        }

        if (_visible && IsKeyPressed(closeHotkey))
        {
            HideMenu();
            return;
        }

        if (placementController != null && placementController.PlacementMode)
        {
            return;
        }

        TrackRightClick();

        if (_visible)
        {
            windowController.SetExtraInteractiveRect(_panelScreenRect, true);
        }
    }

    private void OnGUI()
    {
        if (!_visible)
        {
            return;
        }

        Color previousGuiColor = GUI.color;
        Color previousBackgroundColor = GUI.backgroundColor;
        Color previousContentColor = GUI.contentColor;
        bool areaStarted = false;
        try
        {
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;

            EnsureStyles();
            UnityEngine.Rect guiRect = ToGuiRect(_panelScreenRect);
            GUILayout.BeginArea(guiRect, GUIContent.none, _panelStyle);
            areaStarted = true;
            DrawHeader();

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, false, false);
            switch (_view)
            {
                case MenuView.ActionCategories:
                    DrawActionCategorySection();
                    break;
                case MenuView.ActionList:
                    DrawActionListSection();
                    break;
                case MenuView.Settings:
                    DrawWindowSection();
                    DrawPerformanceSection();
                    DrawHeadLookAtSection();
                    DrawCameraMotionSection();
                    DrawDepthOfFieldSection();
                    break;
                case MenuView.Placement:
                    DrawPlacementSection();
                    break;
                case MenuView.Voice:
                    DrawVoiceSection();
                    break;
                case MenuView.Api:
                    DrawApiSection();
                    break;
                case MenuView.Prompts:
                    DrawPromptSection();
                    break;
                case MenuView.StreamSettings:
                    DrawStreamSettingsSection();
                    break;
                case MenuView.FaceTracking:
                    DrawFaceTrackingSection();
                    break;
                default:
                    DrawMainSection();
                    break;
            }
            GUILayout.EndScrollView();
        }
        finally
        {
            if (areaStarted)
            {
                GUILayout.EndArea();
            }
            GUI.color = previousGuiColor;
            GUI.backgroundColor = previousBackgroundColor;
            GUI.contentColor = previousContentColor;
        }
    }

    public void ToggleMenuAt(Vector2 screenPosition)
    {
        if (_visible)
        {
            if (Time.unscaledTime - _lastOpenTime < toggleDebounceSeconds)
            {
                return;
            }

            HideMenu();
            return;
        }

        if (Time.unscaledTime - _lastCloseTime < toggleDebounceSeconds)
        {
            return;
        }

        ShowMenu(screenPosition);
    }

    public void ShowMenu(Vector2 screenPosition)
    {
        _view = MenuView.Main;
        ResizePanel(screenPosition);
        _visible = true;
        _lastOpenTime = Time.unscaledTime;
        windowController.SetInteractionLock(false);
        windowController.SetExtraInteractiveRect(_panelScreenRect, true);
        windowController.SetClickThrough(false);
    }

    public void HideMenu()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        _lastCloseTime = Time.unscaledTime;
        if (windowController != null)
        {
            windowController.SetExtraInteractiveRect(UnityEngine.Rect.zero, false);
            windowController.SetInteractionLock(false);
        }
    }

    private void ResolveMissingReferences()
    {
        if (windowController == null)
        {
            windowController = GetComponent<TransparentWindowController>();
        }

        if (freeCamera == null)
        {
            freeCamera = GetComponentInChildren<TransparentPetFreeCamera>();
            if (freeCamera == null && Camera.main != null)
            {
                freeCamera = Camera.main.GetComponent<TransparentPetFreeCamera>();
            }

            if (freeCamera == null)
            {
                freeCamera = FindAnyObjectByType<TransparentPetFreeCamera>();
            }
        }

        if (runtimeControls == null)
        {
            runtimeControls = GetComponentInChildren<TransparentPetRuntimeControls>();
        }

        if (actionController == null)
        {
            actionController = GetComponentInChildren<TransparentPetKawaiiActionController>();
        }

        if (placementController == null)
        {
            placementController = GetComponent<TransparentPetPlacementController>();
        }

        if (voiceLauncher == null)
        {
            voiceLauncher = GetComponent<TransparentPetVoiceRuntimeLauncher>();
        }

        if (performanceController == null)
        {
            performanceController = GetComponent<TransparentPetPerformanceController>();
        }

        if (performanceController == null)
        {
            performanceController = gameObject.AddComponent<TransparentPetPerformanceController>();
        }

        if (performanceController.targetCameras == null || performanceController.targetCameras.Length == 0)
        {
            Camera camera = freeCamera != null && freeCamera.targetCamera != null ? freeCamera.targetCamera : Camera.main;
            if (camera != null)
            {
                performanceController.targetCameras = new[] { camera };
            }
        }

        if (headLookAt == null)
        {
            Camera camera = freeCamera != null && freeCamera.targetCamera != null ? freeCamera.targetCamera : Camera.main;
            headLookAt = TransparentPetHeadLookAt.EnsureForRuntimeControls(runtimeControls, camera);
        }

        if (headLookAt == null)
        {
            headLookAt = GetComponentInChildren<TransparentPetHeadLookAt>();
            if (headLookAt == null)
            {
                headLookAt = FindAnyObjectByType<TransparentPetHeadLookAt>();
            }
        }

        if (ResolveMenuRoute() == TransparentPetRoute.SceneHost)
        {
            if (sceneFaceTracker == null)
            {
                sceneFaceTracker = GetComponent<TransparentPetSceneFaceTracker>();
            }

            if (sceneFaceTracker == null)
            {
                sceneFaceTracker = GetComponentInParent<TransparentPetSceneFaceTracker>();
            }

            if (sceneFaceTracker == null)
            {
                sceneFaceTracker = GetComponentInChildren<TransparentPetSceneFaceTracker>();
            }

            if (sceneFaceTracker == null)
            {
                sceneFaceTracker = FindAnyObjectByType<TransparentPetSceneFaceTracker>();
            }

            if (sceneFaceTracker == null)
            {
                return;
            }

            sceneFaceTracker.windowController = windowController;
            sceneFaceTracker.freeCamera = freeCamera;
            sceneFaceTracker.headLookAt = headLookAt;
            sceneFaceTracker.targetCamera = freeCamera != null && freeCamera.targetCamera != null ? freeCamera.targetCamera : Camera.main;
            if (voiceLauncher != null)
            {
                voiceLauncher.sceneFaceTracker = sceneFaceTracker;
            }
        }
    }

    private void DrawHeader()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(TitleForView(), _titleStyle, GUILayout.Height(HeaderHeight));
        GUILayout.FlexibleSpace();
        if (_view != MenuView.Main && GUILayout.Button("<", _closeButtonStyle, GUILayout.Width(CloseButtonSize), GUILayout.Height(CloseButtonSize)))
        {
            SetView(_view == MenuView.ActionList ? MenuView.ActionCategories : MenuView.Main);
        }
        if (GUILayout.Button("x", _closeButtonStyle, GUILayout.Width(CloseButtonSize), GUILayout.Height(CloseButtonSize)))
        {
            HideMenu();
        }
        GUILayout.EndHorizontal();
    }

    private void DrawMainSection()
    {
        DrawButton("\ud83c\udfac \u6d4f\u89c8\u52a8\u4f5c", () => SetView(MenuView.ActionCategories));
        DrawButton("\u4f4d\u7f6e\u8c03\u6574", () => SetView(MenuView.Placement));
        DrawButton("\ud83c\udfa4 \u8bed\u97f3\u63a7\u5236", () => SetView(MenuView.Voice));
        if (ResolveMenuRoute() == TransparentPetRoute.SceneHost)
        {
            DrawButton("\ud83d\udcf7 \u4eba\u8138\u8ddf\u8e2a", () => SetView(MenuView.FaceTracking));
        }
        DrawButton("\u2699\ufe0f \u8bbe\u7f6e\u754c\u9762", () => SetView(MenuView.Settings));
        DrawButton("\ud83d\udc94 \u9000\u51fa", Application.Quit);
    }

    private void DrawActionCategorySection()
    {
        if (actionController == null)
        {
            DrawButton("\u52a8\u4f5c\u76ee\u5f55\u672a\u7ed1\u5b9a", null);
            return;
        }

        string[] categories = actionController.GetCategoryOrder();
        for (int i = 0; i < categories.Length; i++)
        {
            string category = categories[i];
            int count = CountActionsInCategory(category);
            if (count <= 0)
            {
                continue;
            }

            DrawButton(category + "  " + count, () =>
            {
                _selectedActionCategory = category;
                SetView(MenuView.ActionList);
            });
        }
    }

    private void DrawActionListSection()
    {
        if (actionController == null)
        {
            DrawButton("\u52a8\u4f5c\u76ee\u5f55\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawSection(_selectedActionCategory);
        _actionSearch = GUILayout.TextField(_actionSearch ?? string.Empty, _textFieldStyle, GUILayout.Height(CompactRowHeight));
        GUILayout.BeginHorizontal();
        DrawButton("\u4e0a\u4e00\u4e2a", () => PlayRelativeAction(-1));
        DrawButton("\u4e0b\u4e00\u4e2a", () => PlayRelativeAction(1));
        GUILayout.EndHorizontal();
        DrawButton(actionController.IsPlaying ? "\u6682\u505c" : "\u7ee7\u7eed", () => actionController.TogglePlayback());

        TransparentPetKawaiiActionController.ActionMenuEntry[] entries = actionController.GetActionEntries();
        int visibleCount = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            TransparentPetKawaiiActionController.ActionMenuEntry entry = entries[i];
            if (!actionController.ActionMatchesCategory(entry.Name, _selectedActionCategory) || !MatchesActionSearch(entry))
            {
                continue;
            }

            visibleCount++;
            bool active = actionController.CurrentActionName == entry.Name;
            GUIStyle style = active ? _activeButtonStyle : _buttonStyle;
            if (GUILayout.Button((active ? "\u25cf " : "  ") + i.ToString("000") + "  [" + entry.PrimaryCategory + "] " + entry.DisplayName, style, GUILayout.Height(ButtonHeight)))
            {
                actionController.PlayAction(entry.Name);
            }
        }

        if (visibleCount == 0)
        {
            DrawButton("\u6ca1\u6709\u5339\u914d\u52a8\u4f5c", null);
        }
    }

    private void DrawVoiceSection()
    {
        DrawSection("\u8bed\u97f3");
        if (voiceLauncher == null)
        {
            DrawButton("\u8bed\u97f3\u8fd0\u884c\u65f6\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawStatusText(voiceLauncher.Status);

        DrawSection("\u63a7\u5236");
        bool voiceRunning = IsVoiceSessionRunning();
        DrawButton(voiceRunning ? "\u505c\u6b62\u8bed\u97f3\u5bf9\u8bdd" : "\u5f00\u59cb\u8bed\u97f3\u5bf9\u8bdd", () =>
        {
            if (voiceRunning)
            {
                StopVoiceFromMenu();
            }
            else
            {
                StartVoiceFromMenu();
            }
        });

        if (voiceRunning)
        {
            DrawToggle("\u5c4f\u5e55\u8bc6\u522b", voiceLauncher.ScreenVisionActive, value =>
            {
                if (value)
                {
                    voiceLauncher.StartScreenVisionRuntime(voiceLauncher.SelectedRouteId);
                }
                else
                {
                    voiceLauncher.StopScreenVisionRuntime();
                }
            });
            DrawToggle("\u5b9e\u65f6\u76d1\u63a7", voiceLauncher.CompanionPollingActive, value =>
            {
                if (value)
                {
                    voiceLauncher.StartRealtimeMonitoringRuntime(voiceLauncher.realtimeMonitoringIntervalSec);
                }
                else
                {
                    voiceLauncher.StopCompanionPollingRuntime();
                }
            });
            DrawToggle("\u6444\u50cf\u5934\u63a8\u6d41", voiceLauncher.CameraVideoActive, value =>
            {
                if (value)
                {
                    voiceLauncher.StartCameraVideoRuntime();
                }
                else
                {
                    voiceLauncher.StopCameraVideoRuntime();
                }
            });
        }
        else
        {
            DrawToggle("\u542f\u52a8\u65f6\u770b\u5c4f\u5e55", _voiceStartWithScreenVision, value =>
            {
                _voiceStartWithScreenVision = value;
                if (!_voiceStartWithScreenVision)
                {
                    _voiceStartWithRealtimeMonitoring = false;
                    if (voiceLauncher.SelectedRouteSupportsVision)
                    {
                        voiceLauncher.SelectRoute("s2s_low_latency");
                    }
                }
                else if (!voiceLauncher.SelectedRouteSupportsVision)
                {
                    voiceLauncher.SelectRoute("traditional_vision");
                }
            });
            DrawToggle("\u542f\u52a8\u65f6\u5b9e\u65f6\u76d1\u63a7", _voiceStartWithRealtimeMonitoring, value =>
            {
                _voiceStartWithRealtimeMonitoring = value;
                if (_voiceStartWithRealtimeMonitoring)
                {
                    _voiceStartWithScreenVision = true;
                    if (!voiceLauncher.SelectedRouteSupportsVision)
                    {
                        voiceLauncher.SelectRoute("traditional_vision");
                    }
                }
            });
            DrawToggle("\u542f\u52a8\u65f6\u6444\u50cf\u5934\u63a8\u6d41", _voiceStartWithCameraVideo, value =>
            {
                _voiceStartWithCameraVideo = value;
            });
        }

        DrawButton("\u68c0\u67e5\u4e91\u7aef\u914d\u7f6e", () => voiceLauncher.CheckSelectedVoiceConfig());
        DrawButton("\u8bca\u65ad\u8bed\u97f3\u94fe\u8def", () => voiceLauncher.DiagnoseVoiceRuntime());

        DrawSection("\u8def\u7ebf");
        DrawRadio("S2S \u4f4e\u5ef6\u8fdf", voiceLauncher.SelectedRouteId == "s2s_low_latency", () => SelectVoiceRouteFromMenu("s2s_low_latency", false));
        DrawRadio("\u89c6\u89c9\u966a\u73a9", voiceLauncher.SelectedRouteId == "traditional_vision", () => SelectVoiceRouteFromMenu("traditional_vision", true));
        DrawRadio("Agent \u5916\u6302\u7aef\u53e3", voiceLauncher.SelectedRouteId == "agent_speaker", () => SelectVoiceRouteFromMenu("agent_speaker", false));

        DrawSection("\u8bbe\u7f6e");
        DrawButton("API \u670d\u52a1\u5546", () =>
        {
            voiceLauncher.LoadCustomLlmProviderFromConfig();
            SetView(MenuView.Api);
        });
        DrawButton("\u4eba\u8bbe/\u8f6e\u8be2\u63d0\u793a\u8bcd", () =>
        {
            voiceLauncher.LoadPromptSettingsFromConfig();
            SetView(MenuView.Prompts);
        });
        DrawButton("\u63a8\u6d41/\u76d1\u63a7\u8bbe\u7f6e", () => SetView(MenuView.StreamSettings));

    }

    private void DrawStreamSettingsSection()
    {
        if (voiceLauncher == null)
        {
            DrawButton("\u8bed\u97f3\u8fd0\u884c\u65f6\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawSection("\u8fd0\u884c\u65f6");
        DrawToggle("\u663e\u793a\u8bed\u97f3\u8c03\u8bd5\u7a97\u53e3", voiceLauncher.showRuntimeWindow, value => voiceLauncher.showRuntimeWindow = value);

        DrawSection("\u5b9e\u65f6\u76d1\u63a7");
        DrawSlider("\u68c0\u67e5\u95f4\u9694", voiceLauncher.realtimeMonitoringIntervalSec, 1f, 15f,
            value => voiceLauncher.SetRealtimeMonitoringInterval(value),
            voiceLauncher.realtimeMonitoringIntervalSec.ToString() + "\u79d2");

        DrawSection("\u63a8\u6d41\u6863\u4f4d");
        DrawButton("\u6027\u80fd\u4f18\u5148 720p10 + 720p10", () => voiceLauncher.ApplyPerformanceStreamPreset());
        DrawButton("\u6e05\u6670\u4f18\u5148 720p15 + 720p15", () => voiceLauncher.ApplyQualityStreamPreset());

        DrawSection("\u5c4f\u5e55\u63a8\u6d41");
        DrawRadio("1280 x 720", voiceLauncher.ScreenVisionWidth == 1280 && voiceLauncher.ScreenVisionHeight == 720,
            () => voiceLauncher.SetScreenVisionResolution(1280, 720));
        DrawRadio("1920 x 1080", voiceLauncher.ScreenVisionWidth == 1920 && voiceLauncher.ScreenVisionHeight == 1080,
            () => voiceLauncher.SetScreenVisionResolution(1920, 1080));
        DrawSlider("\u5c4f\u5e55\u5e27\u7387", voiceLauncher.ScreenVisionFps, 1f, 30f,
            value => voiceLauncher.SetScreenVisionFps(value),
            voiceLauncher.ScreenVisionFps.ToString() + " FPS");
        DrawSlider("\u5c4f\u5e55\u7801\u7387", voiceLauncher.ScreenVisionMaxKbps, 500f, 12000f,
            value => voiceLauncher.SetScreenVisionMaxKbps(value),
            voiceLauncher.ScreenVisionMaxKbps.ToString() + "k");
        DrawToggle("\u4e91\u7aef\u53ef\u89c1\u6444\u50cf\u5934\u753b\u4e2d\u753b", voiceLauncher.ScreenCameraOverlayEnabled,
            value => voiceLauncher.SetScreenCameraOverlayEnabled(value));
        if (voiceLauncher.ScreenCameraOverlayEnabled)
        {
            DrawRadio("640 x 360 (16:9)", voiceLauncher.ScreenCameraOverlayWidth == 640 && voiceLauncher.ScreenCameraOverlayHeight == 360,
                () => voiceLauncher.SetScreenCameraOverlaySize(640, 360));
            DrawRadio("800 x 450 (16:9)", voiceLauncher.ScreenCameraOverlayWidth == 800 && voiceLauncher.ScreenCameraOverlayHeight == 450,
                () => voiceLauncher.SetScreenCameraOverlaySize(800, 450));
            DrawSlider("\u753b\u4e2d\u753b\u5bbd\u5ea6", voiceLauncher.ScreenCameraOverlayWidth, 320f, 960f,
                value => voiceLauncher.SetScreenCameraOverlayWidth(value),
                voiceLauncher.ScreenCameraOverlayWidth.ToString() + "x" + voiceLauncher.ScreenCameraOverlayHeight.ToString());
            DrawSlider("\u753b\u4e2d\u753b\u8fb9\u8ddd", voiceLauncher.ScreenCameraOverlayPadding, 0f, 80f,
                value => voiceLauncher.SetScreenCameraOverlayPadding(value),
                voiceLauncher.ScreenCameraOverlayPadding.ToString() + "px");
        }

        DrawSection("\u6444\u50cf\u5934\u63a8\u6d41");
        DrawRadio("1280 x 720", voiceLauncher.CameraVideoWidth == 1280 && voiceLauncher.CameraVideoHeight == 720,
            () => voiceLauncher.SetCameraVideoResolution(1280, 720));
        DrawRadio("854 x 480", voiceLauncher.CameraVideoWidth == 854 && voiceLauncher.CameraVideoHeight == 480,
            () => voiceLauncher.SetCameraVideoResolution(854, 480));
        DrawRadio("1920 x 1080", voiceLauncher.CameraVideoWidth == 1920 && voiceLauncher.CameraVideoHeight == 1080,
            () => voiceLauncher.SetCameraVideoResolution(1920, 1080));
        DrawSlider("\u6444\u50cf\u5934\u5e27\u7387", voiceLauncher.CameraVideoFps, 5f, 60f,
            value => voiceLauncher.SetCameraVideoFps(value),
            voiceLauncher.CameraVideoFps.ToString() + " FPS");
        DrawSlider("\u6444\u50cf\u5934\u7801\u7387", voiceLauncher.CameraVideoMaxKbps, 500f, 6000f,
            value => voiceLauncher.SetCameraVideoMaxKbps(value),
            voiceLauncher.CameraVideoMaxKbps.ToString() + "k");
        DrawToggle("\u4f7f\u7528\u672c\u5730 Camera Hub", voiceLauncher.CameraVideoUseCameraHub,
            value => voiceLauncher.SetCameraVideoUseCameraHub(value));
        if (!voiceLauncher.CameraVideoUseCameraHub)
        {
            DrawToggle("\u4ec5\u4f7f\u7528\u865a\u62df\u6444\u50cf\u5934", voiceLauncher.CameraVideoUseVirtualCamera,
                value => voiceLauncher.SetCameraVideoUseVirtualCamera(value));
            if (voiceLauncher.CameraVideoUseVirtualCamera)
            {
                DrawToggle("\u627e\u4e0d\u5230\u865a\u62df\u6444\u50cf\u5934\u5219\u4e0d\u63a8\u6d41", voiceLauncher.CameraVideoRequireVirtualCamera,
                    value => voiceLauncher.SetCameraVideoRequireVirtualCamera(value));
            }

            if (!voiceLauncher.CameraVideoUseVirtualCamera)
            {
                DrawToggle("\u6444\u50cf\u5934\u6d41\u53d1\u9001\u8ddf\u8e2a\u5305", voiceLauncher.CameraVideoSendFaceTrackingPackets,
                    value => voiceLauncher.SetCameraVideoSendFaceTrackingPackets(value));
            }
        }
        if (!voiceLauncher.CameraVideoUseCameraHub &&
            !voiceLauncher.CameraVideoUseVirtualCamera &&
            voiceLauncher.CameraVideoSendFaceTrackingPackets)
        {
            DrawSlider("\u8ddf\u8e2a\u5305\u5e27\u7387", voiceLauncher.FaceTrackingPacketFps, 2f, 30f,
                value => voiceLauncher.SetFaceTrackingPacketFps(value),
                voiceLauncher.FaceTrackingPacketFps.ToString() + " FPS");
        }

        DrawButton("\u8fd4\u56de\u8bed\u97f3\u63a7\u5236", () => SetView(MenuView.Voice));
    }

    private bool IsVoiceSessionRunning()
    {
        return voiceLauncher != null && (voiceLauncher.IsBridgeRunning || voiceLauncher.IsRuntimeRunning);
    }

    private void StartVoiceFromMenu()
    {
        if (voiceLauncher == null)
        {
            return;
        }

        if (_voiceStartOptionsRoutine != null)
        {
            StopCoroutine(_voiceStartOptionsRoutine);
            _voiceStartOptionsRoutine = null;
        }

        if (_voiceStartWithScreenVision)
        {
            voiceLauncher.StartScreenVisionRuntime(voiceLauncher.SelectedRouteId);
        }
        else
        {
            voiceLauncher.StartVoiceRuntime();
        }

        if (_voiceStartWithRealtimeMonitoring || _voiceStartWithCameraVideo)
        {
            _voiceStartOptionsRoutine = StartCoroutine(StartVoiceOptionsAfterBridge());
        }
    }

    private void SelectVoiceRouteFromMenu(string routeId, bool startWithScreenVision)
    {
        if (voiceLauncher == null)
        {
            return;
        }

        voiceLauncher.SelectRoute(routeId);
        _voiceStartWithScreenVision = startWithScreenVision;
        if (startWithScreenVision)
        {
            _voiceStartWithRealtimeMonitoring = true;
        }
        else
        {
            _voiceStartWithRealtimeMonitoring = false;
        }
    }

    private IEnumerator StartVoiceOptionsAfterBridge()
    {
        float deadline = Time.realtimeSinceStartup + 12f;
        while (voiceLauncher != null && !voiceLauncher.IsBridgeReadyForRequests && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        if (voiceLauncher != null && voiceLauncher.IsBridgeReadyForRequests)
        {
            if (_voiceStartWithRealtimeMonitoring)
            {
                voiceLauncher.StartRealtimeMonitoringRuntime(voiceLauncher.realtimeMonitoringIntervalSec);
            }

            if (_voiceStartWithCameraVideo)
            {
                voiceLauncher.StartCameraVideoRuntime();
            }
        }

        _voiceStartOptionsRoutine = null;
    }

    private void StopVoiceFromMenu()
    {
        if (_voiceStartOptionsRoutine != null)
        {
            StopCoroutine(_voiceStartOptionsRoutine);
            _voiceStartOptionsRoutine = null;
        }

        if (voiceLauncher != null)
        {
            voiceLauncher.StopVoiceRuntime();
        }
    }

    private void DrawApiSection()
    {
        DrawSection("API \u670d\u52a1\u5546");
        if (voiceLauncher == null)
        {
            DrawButton("\u8bed\u97f3\u8fd0\u884c\u65f6\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawTextInput("Endpoint", voiceLauncher.customLlmUrl, value => voiceLauncher.customLlmUrl = value);
        DrawTextInput("API Key", voiceLauncher.customLlmApiKey, value => voiceLauncher.customLlmApiKey = value, true);
        DrawButton(voiceLauncher.CustomLlmModelsLoading ? "\u6b63\u5728\u68c0\u6d4b\u6a21\u578b..." : "\u68c0\u6d4b\u6a21\u578b\u5217\u8868", () =>
        {
            if (!voiceLauncher.CustomLlmModelsLoading)
            {
                voiceLauncher.FetchCustomLlmModels();
            }
        });
        if (voiceLauncher.CustomLlmModelCount > 0)
        {
            DrawSection("\u6a21\u578b\u5217\u8868");
            for (int i = 0; i < voiceLauncher.CustomLlmModelCount; i++)
            {
                string modelId = voiceLauncher.GetCustomLlmModel(i);
                DrawRadio(modelId, string.Equals(voiceLauncher.customLlmModelName, modelId, System.StringComparison.Ordinal), () =>
                {
                    voiceLauncher.SelectCustomLlmModel(modelId);
                });
            }
        }
        else
        {
            DrawTextInput("Model", voiceLauncher.customLlmModelName, value => voiceLauncher.customLlmModelName = value);
        }
        DrawButton("\u4ece\u914d\u7f6e\u91cd\u65b0\u8f7d\u5165", () => voiceLauncher.LoadCustomLlmProviderFromConfig());
        DrawButton(voiceLauncher.CustomLlmTestRunning ? "\u6b63\u5728\u6d4b\u8bd5 API..." : "\u6d4b\u8bd5\u5e76\u5e94\u7528", () =>
        {
            if (!voiceLauncher.CustomLlmTestRunning)
            {
                voiceLauncher.TestAndApplyCustomLlmProvider();
            }
        });
        DrawStatusText(voiceLauncher.CustomLlmStatus);
        DrawButton("\u8fd4\u56de\u8bed\u97f3\u63a7\u5236", () => SetView(MenuView.Voice));
    }

    private void DrawPromptSection()
    {
        DrawSection("\u63d0\u793a\u8bcd");
        if (voiceLauncher == null)
        {
            DrawButton("\u8bed\u97f3\u8fd0\u884c\u65f6\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawTextArea("\u4eba\u8bbe", voiceLauncher.personaPrompt, value => voiceLauncher.personaPrompt = value, 132f);
        DrawTextArea("\u8f6e\u8be2", voiceLauncher.companionPollingPrompt, value => voiceLauncher.companionPollingPrompt = value, 156f);
        DrawButton("\u4ece\u914d\u7f6e\u91cd\u65b0\u8f7d\u5165", () => voiceLauncher.LoadPromptSettingsFromConfig());
        DrawButton("\u4fdd\u5b58\u5e76\u5e94\u7528", () => voiceLauncher.ApplyPromptSettingsToConfig());
        DrawStatusText(voiceLauncher.PromptStatus);
        DrawButton("\u8fd4\u56de\u8bed\u97f3\u63a7\u5236", () => SetView(MenuView.Voice));
    }

    private void DrawFaceTrackingSection()
    {
        DrawSection("\u4eba\u8138\u8ddf\u8e2a");
        if (ResolveMenuRoute() != TransparentPetRoute.SceneHost)
        {
            DrawStatusText("\u4eba\u8138\u8ddf\u8e2a\u53ea\u5bf9\u573a\u666f\u7248\u5f00\u653e");
            return;
        }

        if (sceneFaceTracker == null)
        {
            DrawButton("\u4eba\u8138\u8ddf\u8e2a\u672a\u7ed1\u5b9a", null);
            return;
        }

        bool sharedReceiverOnly = IsSharedFaceTrackingReceiver();
        string cameraButtonLabel = sceneFaceTracker.IsRunning && !sharedReceiverOnly
            ? "\u5173\u95ed\u6444\u50cf\u5934"
            : (sharedReceiverOnly ? "\u5207\u5230\u5355\u72ec\u4eba\u8138" : "\u6253\u5f00\u6444\u50cf\u5934");
        DrawButton(cameraButtonLabel, () =>
        {
            if (sceneFaceTracker.IsRunning && !sharedReceiverOnly)
            {
                if (_faceTrackingStartRoutine != null)
                {
                    StopCoroutine(_faceTrackingStartRoutine);
                    _faceTrackingStartRoutine = null;
                }
                sceneFaceTracker.StopCamera();
            }
            else
            {
                if (_faceTrackingStartRoutine != null)
                {
                    StopCoroutine(_faceTrackingStartRoutine);
                }

                _faceTrackingStartRoutine = StartCoroutine(StartStandaloneFaceTrackingRoutine());
            }
        });
        DrawRadio("MediaPipe \u4eba\u8138", sceneFaceTracker.TrackingBackend == TransparentPetFaceTrackingBackend.ExternalMediaPipe,
            () => sceneFaceTracker.SetTrackingBackend(TransparentPetFaceTrackingBackend.ExternalMediaPipe));
        DrawRadio("Unity \u80a4\u8272\u540e\u5907", sceneFaceTracker.TrackingBackend == TransparentPetFaceTrackingBackend.UnityWarmBlob,
            () => sceneFaceTracker.SetTrackingBackend(TransparentPetFaceTrackingBackend.UnityWarmBlob));
        if (sceneFaceTracker.TrackingBackend == TransparentPetFaceTrackingBackend.ExternalMediaPipe)
        {
            DrawToggle("\u81ea\u52a8\u542f\u52a8 MediaPipe", sceneFaceTracker.LaunchExternalProcess,
                value => sceneFaceTracker.SetLaunchExternalProcess(value));
        }
        DrawToggle("\u542f\u7528\u8ddf\u8e2a", sceneFaceTracker.TrackingEnabled, value => sceneFaceTracker.SetTrackingEnabled(value));
        DrawToggle("\u5934\u90e8\u8ddf\u968f\u4eba\u8138", sceneFaceTracker.HeadFollowEnabled, value => sceneFaceTracker.SetHeadFollowEnabled(value));
        DrawToggle("\u955c\u5934\u8f7b\u5fae\u8ddf\u968f", sceneFaceTracker.CameraParallaxEnabled, value => sceneFaceTracker.SetCameraParallaxEnabled(value));
        DrawToggle("\u955c\u5934\u59ff\u6001\u8ddf\u968f", sceneFaceTracker.CameraOrbitEnabled, value => sceneFaceTracker.SetCameraOrbitEnabled(value));
        DrawToggle("\u6c34\u5e73\u955c\u50cf", sceneFaceTracker.MirrorHorizontal, value => sceneFaceTracker.SetMirrorHorizontal(value));
        DrawToggle("\u5782\u76f4\u955c\u50cf", sceneFaceTracker.MirrorVertical, value => sceneFaceTracker.SetMirrorVertical(value));
        DrawRadio("\u89c6\u7ebf\u8f74\u5fc3\uff1a\u4eba\u7269\u6a21\u578b", sceneFaceTracker.CameraSightMode == TransparentPetCameraSightMode.ModelAxis,
            () => sceneFaceTracker.SetCameraSightMode(TransparentPetCameraSightMode.ModelAxis));
        DrawRadio("\u89c6\u7ebf\u8f74\u5fc3\uff1a\u8ddf\u968f\u4eba\u8138", sceneFaceTracker.CameraSightMode == TransparentPetCameraSightMode.TrackingAnchor,
            () => sceneFaceTracker.SetCameraSightMode(TransparentPetCameraSightMode.TrackingAnchor));
        DrawRadio("\u8ddf\u8e2a\u70b9\uff1a\u5934\u90e8", sceneFaceTracker.TrackingAnchor == TransparentPetFaceTrackingAnchor.Head,
            () => sceneFaceTracker.SetTrackingAnchor(TransparentPetFaceTrackingAnchor.Head));
        DrawRadio("\u8ddf\u8e2a\u70b9\uff1a\u773c\u775b", sceneFaceTracker.TrackingAnchor == TransparentPetFaceTrackingAnchor.Eyes,
            () => sceneFaceTracker.SetTrackingAnchor(TransparentPetFaceTrackingAnchor.Eyes));

        DrawSection("\u6444\u50cf\u5934");
        int cameraCount = sceneFaceTracker.CameraDeviceCount;
        if (cameraCount <= 0)
        {
            DrawButton("\u672a\u68c0\u6d4b\u5230\u6444\u50cf\u5934", null);
        }
        else
        {
            for (int i = 0; i < cameraCount; i++)
            {
                int index = i;
                DrawRadio(sceneFaceTracker.GetCameraDeviceLabel(index), sceneFaceTracker.SelectedDeviceIndex == index,
                    () => sceneFaceTracker.SetSelectedDeviceIndex(index));
            }
        }
        DrawRadio("1280 x 720", sceneFaceTracker.RequestedWidth == 1280 && sceneFaceTracker.RequestedHeight == 720,
            () => sceneFaceTracker.SetRequestedResolution(1280, 720));
        DrawRadio("854 x 480", sceneFaceTracker.RequestedWidth == 854 && sceneFaceTracker.RequestedHeight == 480,
            () => sceneFaceTracker.SetRequestedResolution(854, 480));
        DrawSlider("\u6444\u50cf\u5934\u5e27\u7387", sceneFaceTracker.RequestedFps, 5f, 60f,
            value => sceneFaceTracker.SetRequestedFps(value),
            sceneFaceTracker.RequestedFps.ToString() + " FPS");
        DrawSlider("\u68c0\u6d4b\u5e27\u7387", sceneFaceTracker.DetectorFps, 2f, 30f,
            value => sceneFaceTracker.SetDetectorFps(value),
            Mathf.RoundToInt(sceneFaceTracker.DetectorFps).ToString() + " FPS");
        if (sceneFaceTracker.TrackingBackend == TransparentPetFaceTrackingBackend.ExternalMediaPipe)
        {
            DrawToggle("\u63a8\u6d41\u6297\u6296", sceneFaceTracker.ExternalPacketStabilizationEnabled,
                value => sceneFaceTracker.SetExternalPacketStabilizationEnabled(value));
            DrawSlider("\u4f4d\u7f6e\u9650\u5e45", sceneFaceTracker.MaxExternalOffsetStep, 0.02f, 0.5f,
                value => sceneFaceTracker.SetMaxExternalOffsetStep(value),
                sceneFaceTracker.MaxExternalOffsetStep.ToString("0.00"));
            DrawSlider("\u89d2\u5ea6\u9650\u5e45", sceneFaceTracker.MaxExternalAngleStepDegrees, 1f, 30f,
                value => sceneFaceTracker.SetMaxExternalAngleStepDegrees(value),
                Mathf.RoundToInt(sceneFaceTracker.MaxExternalAngleStepDegrees).ToString() + "\u00b0");
            DrawSlider("\u8fd1\u8fdc\u9650\u5e45", sceneFaceTracker.MaxExternalDepthStep, 0.01f, 0.5f,
                value => sceneFaceTracker.SetMaxExternalDepthStep(value),
                sceneFaceTracker.MaxExternalDepthStep.ToString("0.00"));
        }

        DrawSection("\u8ddf\u968f\u53c2\u6570");
        DrawSlider("\u6b7b\u533a", sceneFaceTracker.NormalizedDeadZone, 0f, 0.18f,
            value => sceneFaceTracker.SetNormalizedDeadZone(value),
            Mathf.RoundToInt(sceneFaceTracker.NormalizedDeadZone * 100f).ToString() + "%");
        DrawSlider("\u5e73\u6ed1", sceneFaceTracker.OffsetSmoothTime, 0.03f, 0.5f,
            value => sceneFaceTracker.SetOffsetSmoothTime(value),
            sceneFaceTracker.OffsetSmoothTime.ToString("0.00") + "s");
        DrawSlider("\u5934\u90e8\u5de6\u53f3", sceneFaceTracker.HeadYawStrengthDegrees, 1f, 28f,
            value => sceneFaceTracker.SetHeadYawStrengthDegrees(value),
            Mathf.RoundToInt(sceneFaceTracker.HeadYawStrengthDegrees).ToString() + "\u00b0");
        DrawSlider("\u5934\u90e8\u4e0a\u4e0b", sceneFaceTracker.HeadPitchStrengthDegrees, 1f, 20f,
            value => sceneFaceTracker.SetHeadPitchStrengthDegrees(value),
            Mathf.RoundToInt(sceneFaceTracker.HeadPitchStrengthDegrees).ToString() + "\u00b0");
        DrawSlider("\u955c\u5934\u504f\u79fb", sceneFaceTracker.CameraTargetShiftMeters, 0f, 0.2f,
            value => sceneFaceTracker.SetCameraTargetShiftMeters(value),
            Mathf.RoundToInt(sceneFaceTracker.CameraTargetShiftMeters * 1000f).ToString() + "mm");
        DrawSlider("\u8eab\u9ad8\u8ddf\u968f", sceneFaceTracker.CameraHeightFollowMeters, 0f, 1.5f,
            value => sceneFaceTracker.SetCameraHeightFollowMeters(value),
            Mathf.RoundToInt(sceneFaceTracker.CameraHeightFollowMeters * 100f).ToString() + "cm");
        DrawSlider("\u8fd1\u8fdc\u89c6\u5dee", sceneFaceTracker.CameraDepthShiftMeters, 0f, 0.2f,
            value => sceneFaceTracker.SetCameraDepthShiftMeters(value),
            Mathf.RoundToInt(sceneFaceTracker.CameraDepthShiftMeters * 1000f).ToString() + "mm");
        DrawSlider("\u955c\u5934\u5de6\u53f3\u65cb\u8f6c", sceneFaceTracker.CameraYawOrbitStrength, -1.5f, 1.5f,
            value => sceneFaceTracker.SetCameraYawOrbitStrength(value),
            Mathf.RoundToInt(sceneFaceTracker.CameraYawOrbitStrength * 100f).ToString() + "%");
        DrawSlider("\u955c\u5934\u4e0a\u4e0b\u65cb\u8f6c", sceneFaceTracker.CameraPitchOrbitStrength, -1.5f, 1.5f,
            value => sceneFaceTracker.SetCameraPitchOrbitStrength(value),
            Mathf.RoundToInt(sceneFaceTracker.CameraPitchOrbitStrength * 100f).ToString() + "%");
        DrawSlider("\u65cb\u8f6c\u5e73\u6ed1", sceneFaceTracker.CameraOrbitSmoothTime, 0.03f, 0.5f,
            value => sceneFaceTracker.SetCameraOrbitSmoothTime(value),
            sceneFaceTracker.CameraOrbitSmoothTime.ToString("0.00") + "s");
        DrawStatusText(sceneFaceTracker.Status);
    }

    private bool IsSharedFaceTrackingReceiver()
    {
        return sceneFaceTracker != null &&
            sceneFaceTracker.IsBridgePacketReceiver;
    }

    private IEnumerator StartStandaloneFaceTrackingRoutine()
    {
        if (sceneFaceTracker != null)
        {
            sceneFaceTracker.StartStandaloneLocalMediaPipe();
        }

        yield return null;
        _faceTrackingStartRoutine = null;
    }

    private void DrawWindowSection()
    {
        DrawSection("\u7a97\u53e3");
        if (windowController == null)
        {
            DrawButton("\u7a97\u53e3\u63a7\u5236\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawToggle("\u542f\u52a8\u5230\u6240\u9009\u663e\u793a\u5668", windowController.moveToSecondaryMonitorOnStart, value =>
        {
            windowController.SetMoveToSecondaryMonitorOnStart(value);
        });
        DrawRadio("\u5c0f\u7a97\u6a21\u5f0f\uff08\u53ef\u62d6\u52a8\u7f29\u653e\uff09", windowController.PresentationMode == TransparentWindowController.MonitorPresentationMode.SmallWindow, () =>
        {
            windowController.SetSmallWindowMode();
        });
        DrawButton("\u94fa\u6ee1\u5f53\u524d\u5c4f\u5e55", () => windowController.FillCurrentScreenNow());

        DrawToggle("\u5168\u5c4f\u64ad\u653e", windowController.resizeToTargetMonitorWorkArea, value =>
        {
            windowController.SetResizeToTargetMonitorWorkArea(value);
        });
        DrawRadio("\u65e0\u8fb9\u6846\u5168\u5c4f", windowController.PresentationMode == TransparentWindowController.MonitorPresentationMode.BorderlessFullscreen, () =>
        {
            windowController.SetPresentationMode(TransparentWindowController.MonitorPresentationMode.BorderlessFullscreen);
        });
        DrawRadio("\u72ec\u5360\u5168\u5c4f", windowController.PresentationMode == TransparentWindowController.MonitorPresentationMode.ExclusiveFullscreen, () =>
        {
            windowController.SetPresentationMode(TransparentWindowController.MonitorPresentationMode.ExclusiveFullscreen);
        });
        DrawRadio("\u8ddf\u968f\u5c4f\u5e55\u65b9\u5411", windowController.OrientationMode == TransparentWindowController.MonitorOrientationMode.FollowMonitor, () =>
        {
            windowController.SetOrientationMode(TransparentWindowController.MonitorOrientationMode.FollowMonitor);
        });
        DrawRadio("\u6a2a\u5c4f", windowController.OrientationMode == TransparentWindowController.MonitorOrientationMode.Landscape, () =>
        {
            windowController.SetOrientationMode(TransparentWindowController.MonitorOrientationMode.Landscape);
        });
        DrawRadio("\u7ad6\u5c4f", windowController.OrientationMode == TransparentWindowController.MonitorOrientationMode.Portrait, () =>
        {
            windowController.SetOrientationMode(TransparentWindowController.MonitorOrientationMode.Portrait);
        });

        int monitorCount = windowController.GetAvailableMonitorCount();
        for (int i = 0; i < monitorCount; i++)
        {
            int monitorIndex = i;
            DrawRadio(windowController.GetMonitorDisplayLabel(i), windowController.PreferredMonitorIndex == i, () =>
            {
                windowController.FullscreenOnMonitor(monitorIndex);
            });
        }

        DrawButton("\u5e94\u7528\u5230\u6240\u9009\u663e\u793a\u5668", () => windowController.MoveToConfiguredMonitorNow());
    }

    private void DrawPerformanceSection()
    {
        DrawSection("\u6027\u80fd");
        if (performanceController == null)
        {
            DrawButton("\u6027\u80fd\u63a7\u5236\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawToggle("\u5e27\u7387\u9650\u5236", performanceController.LimitFrameRate, value => performanceController.SetLimitFrameRate(value));
        if (performanceController.LimitFrameRate)
        {
            DrawSlider("\u76ee\u6807\u5e27\u7387", performanceController.TargetFrameRate, 15f, 144f,
                value => performanceController.SetTargetFrameRate(value),
                Mathf.RoundToInt(performanceController.TargetFrameRate).ToString() + " FPS");
        }
        DrawToggle("\u5782\u76f4\u540c\u6b65", performanceController.VerticalSync, value => performanceController.SetVerticalSync(value));

        DrawSection("\u6297\u952f\u9f7f");
        DrawRadio("MSAA Off", performanceController.MsaaSamples == 0, () => performanceController.SetMsaaSamples(0));
        DrawRadio("MSAA 2x", performanceController.MsaaSamples == 2, () => performanceController.SetMsaaSamples(2));
        DrawRadio("MSAA 4x", performanceController.MsaaSamples == 4, () => performanceController.SetMsaaSamples(4));
        DrawRadio("MSAA 8x", performanceController.MsaaSamples == 8, () => performanceController.SetMsaaSamples(8));
    }

    private void DrawHeadLookAtSection()
    {
        DrawSection("\u5934\u90e8\u8ddf\u968f");
        if (headLookAt == null)
        {
            DrawButton("\u5934\u90e8\u8ddf\u968f\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawToggle("\u5934\u770b\u5411\u955c\u5934", headLookAt.LookAtEnabled, value => headLookAt.SetLookAtEnabled(value));
        DrawSlider("\u6b7b\u533a", headLookAt.DeadZoneDegrees, 0f, 18f,
            value => headLookAt.SetDeadZoneDegrees(value),
            Mathf.RoundToInt(headLookAt.DeadZoneDegrees).ToString() + "\u00b0");
        DrawSlider("\u5e73\u6ed1", headLookAt.SmoothTime, 0.03f, 0.5f,
            value => headLookAt.SetSmoothTime(value),
            headLookAt.SmoothTime.ToString("0.00") + "s");
    }

    private void DrawCameraMotionSection()
    {
        DrawSection("\u955c\u5934");
        if (placementController == null)
        {
            DrawButton("\u955c\u5934\u8ddf\u968f\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawToggle("\u955c\u5934\u7126\u70b9\u9501\u5b9a\u4eba\u7269", placementController.CameraTargetLockedToPet,
            value => placementController.SetCameraTargetLockedToPet(value));
        DrawToggle("\u7528\u6a21\u578b\u4e2d\u5fc3\u5bf9\u7126", placementController.CameraFollowsCharacterMotion,
            value => placementController.SetCameraFollowsCharacterMotion(value));
    }

    private void DrawDepthOfFieldSection()
    {
        DrawSection("\u666f\u6df1");
        if (freeCamera == null)
        {
            DrawButton("\u666f\u6df1\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawToggle("\u542f\u7528\u666f\u6df1", freeCamera.depthOfFieldEnabled, value => freeCamera.SetDepthOfFieldEnabled(value));
        DrawToggle("\u7126\u70b9\u9501\u5b9a\u4eba\u7269", freeCamera.lockDepthOfFieldToPet, value => freeCamera.SetDepthOfFieldFocusLock(value));
        DrawSlider("\u865a\u5316\u5927\u5c0f", freeCamera.depthOfFieldBlurAmount, 0f, 1f,
            value => freeCamera.SetDepthOfFieldBlurAmount(value),
            Mathf.RoundToInt(freeCamera.depthOfFieldBlurAmount * 100f).ToString() + "%");
    }

    private void DrawPlacementSection()
    {
        DrawSection("\u8fd0\u884c\u65f6\u4f4d\u7f6e");
        if (placementController == null)
        {
            DrawButton("\u4f4d\u7f6e\u63a7\u5236\u672a\u7ed1\u5b9a", null);
            return;
        }

        DrawButton("\u628a\u5f53\u524d\u4f4d\u7f6e\u8bbe\u4e3a\u539f\u70b9", () => placementController.CaptureRuntimeOriginFromCurrent());
        DrawButton("\u56de\u5230\u539f\u70b9", () => placementController.ResetRuntimePlacement());
        DrawButton("\u4fdd\u5b58\u7528\u6237\u6446\u4f4d", () =>
        {
            placementController.SaveUserPlacementNow();
            freeCamera?.SaveUserCameraNow();
        });
        DrawButton("\u6062\u590d\u51fa\u5382\u6446\u4f4d", () =>
        {
            placementController.ResetToFactoryDefault();
            freeCamera?.ResetToFactoryDefault();
        });
        DrawToggle("\u79fb\u52a8\u65f6\u9501\u5b9a\u4eba\u7269", placementController.CameraTargetLockedToPet,
            value => placementController.SetCameraTargetLockedToPet(value));
        DrawPlacementRow("\u5de6\u53f3", "\u5de6", () => placementController.NudgeFromRuntimeOrigin(new Vector3(-PlacementMoveStep, 0f, 0f)),
            "\u53f3", () => placementController.NudgeFromRuntimeOrigin(new Vector3(PlacementMoveStep, 0f, 0f)),
            FormatSigned(placementController.RuntimeOffset.x));
        DrawPlacementRow("\u4e0a\u4e0b", "\u4e0b", () => placementController.NudgeFromRuntimeOrigin(new Vector3(0f, -PlacementMoveStep, 0f)),
            "\u4e0a", () => placementController.NudgeFromRuntimeOrigin(new Vector3(0f, PlacementMoveStep, 0f)),
            FormatSigned(placementController.RuntimeOffset.y));
        DrawPlacementRow("\u524d\u540e", "\u540e", () => placementController.NudgeFromRuntimeOrigin(new Vector3(0f, 0f, -PlacementMoveStep)),
            "\u524d", () => placementController.NudgeFromRuntimeOrigin(new Vector3(0f, 0f, PlacementMoveStep)),
            FormatSigned(placementController.RuntimeOffset.z));
        DrawPlacementRow("\u7f29\u653e", "\u7f29\u5c0f", () => placementController.ScaleFromRuntimeOrigin(1f / PlacementScaleStep),
            "\u653e\u5927", () => placementController.ScaleFromRuntimeOrigin(PlacementScaleStep),
            placementController.RuntimeUniformScale.ToString("0.00"));
        DrawPlacementRow("\u6c34\u5e73\u65cb\u8f6c", "\u5de6\u8f6c", () => placementController.RotateFromRuntimeOrigin(new Vector3(0f, -PlacementRotateStep, 0f)),
            "\u53f3\u8f6c", () => placementController.RotateFromRuntimeOrigin(new Vector3(0f, PlacementRotateStep, 0f)),
            FormatSigned(placementController.RuntimeEulerDegrees.y));
        DrawPlacementRow("\u4ef0\u4fef", "\u4e0b\u538b", () => placementController.RotateFromRuntimeOrigin(new Vector3(PlacementRotateStep, 0f, 0f)),
            "\u4e0a\u62ac", () => placementController.RotateFromRuntimeOrigin(new Vector3(-PlacementRotateStep, 0f, 0f)),
            FormatSigned(placementController.RuntimeEulerDegrees.x));
    }

    private void SetView(MenuView nextView)
    {
        Vector2 topLeft = new Vector2(_panelScreenRect.xMin, _panelScreenRect.yMax);
        _view = nextView;
        _scrollPosition = Vector2.zero;
        ResizePanel(topLeft);
        windowController?.SetExtraInteractiveRect(_panelScreenRect, true);
    }

    private int CountActionsInCategory(string category)
    {
        if (actionController == null)
        {
            return 0;
        }

        TransparentPetKawaiiActionController.ActionMenuEntry[] entries = actionController.GetActionEntries();
        int count = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (actionController.ActionMatchesCategory(entries[i].Name, category))
            {
                count++;
            }
        }

        return count;
    }

    private bool MatchesActionSearch(TransparentPetKawaiiActionController.ActionMenuEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_actionSearch))
        {
            return true;
        }

        string search = _actionSearch.ToLowerInvariant();
        return (entry.Name != null && entry.Name.ToLowerInvariant().Contains(search)) ||
            (entry.DisplayName != null && entry.DisplayName.ToLowerInvariant().Contains(search)) ||
            (entry.PrimaryCategory != null && entry.PrimaryCategory.ToLowerInvariant().Contains(search));
    }

    private void PlayRelativeAction(int direction)
    {
        if (actionController == null)
        {
            return;
        }

        TransparentPetKawaiiActionController.ActionMenuEntry[] entries = actionController.GetActionEntries();
        if (entries.Length == 0)
        {
            return;
        }

        int currentIndex = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Name == actionController.CurrentActionName)
            {
                currentIndex = i;
                break;
            }
        }

        for (int offset = 1; offset <= entries.Length; offset++)
        {
            int nextIndex = (currentIndex + direction * offset + entries.Length) % entries.Length;
            if (actionController.ActionMatchesCategory(entries[nextIndex].Name, _selectedActionCategory))
            {
                actionController.PlayAction(entries[nextIndex].Name);
                return;
            }
        }
    }

    private string TitleForView()
    {
        switch (_view)
        {
            case MenuView.ActionCategories:
                return "\u52a8\u4f5c\u5206\u7c7b";
            case MenuView.ActionList:
                return "\u52a8\u4f5c\u5217\u8868";
            case MenuView.Settings:
                return "\u8bbe\u7f6e\u754c\u9762";
            case MenuView.Placement:
                return "\u4f4d\u7f6e\u8c03\u6574";
            case MenuView.Voice:
                return "\u8bed\u97f3\u63a7\u5236";
            case MenuView.Api:
                return "API \u670d\u52a1\u5546";
            case MenuView.Prompts:
                return "\u63d0\u793a\u8bcd";
            case MenuView.StreamSettings:
                return "\u63a8\u6d41\u8bbe\u7f6e";
            case MenuView.FaceTracking:
                return "\u4eba\u8138\u8ddf\u8e2a";
            default:
                return "Kawaii";
        }
    }

    private void TrackRightClick()
    {
        bool rightDown = IsRightMouseDown();
        if (rightDown && !_rightPressed)
        {
            _rightPressed = true;
            _rightPressStartedOnTrigger = false;
            if (windowController.TryGetCursorPositionInWindow(out Vector2 cursor))
            {
                _rightPressPosition = cursor;
                _rightPressStartedOnTrigger = windowController.IsMenuTriggerHitAt(cursor);
            }
        }

        if (!rightDown && _rightPressed)
        {
            _rightPressed = false;
            if (!windowController.TryGetCursorPositionInWindow(out Vector2 cursor))
            {
                _rightPressStartedOnTrigger = false;
                return;
            }

            bool clickDistanceOk = Vector2.Distance(_rightPressPosition, cursor) <= rightClickDragThreshold;
            bool releaseOverTrigger = windowController.IsMenuTriggerHitAt(cursor);
            if (_rightPressStartedOnTrigger && clickDistanceOk && releaseOverTrigger)
            {
                ToggleMenuAt(cursor);
            }
            _rightPressStartedOnTrigger = false;
        }
    }

    private Vector2 PanelSizeForCurrentView()
    {
        Vector2 size = NormalizePanelSizeValue(panelSize);
        float height = _view == MenuView.Main ? Mathf.Min(size.y, MainPanelHeight) : size.y;
        return new Vector2(size.x, height);
    }

    private void NormalizePanelSize()
    {
        panelSize = NormalizePanelSizeValue(panelSize);
    }

    private static Vector2 NormalizePanelSizeValue(Vector2 size)
    {
        return new Vector2(Mathf.Max(size.x, MinimumPanelWidth), Mathf.Max(size.y, MinimumPanelHeight));
    }

    private void ResizePanel(Vector2 requestedTopLeft)
    {
        Vector2 size = PanelSizeForCurrentView();
        Vector2 topLeft = ClampPanelTopLeft(requestedTopLeft, size);
        _panelScreenRect = new UnityEngine.Rect(topLeft.x, topLeft.y - size.y, size.x, size.y);
    }

    private Vector2 ClampPanelTopLeft(Vector2 requested, Vector2 size)
    {
        float x = Mathf.Clamp(requested.x, 8f, Mathf.Max(8f, Screen.width - size.x - 8f));
        float y = Mathf.Clamp(requested.y, size.y + 8f, Mathf.Max(size.y + 8f, Screen.height - 8f));
        return new Vector2(x, y);
    }

    private UnityEngine.Rect ToGuiRect(UnityEngine.Rect screenRect)
    {
        return new UnityEngine.Rect(screenRect.x, Screen.height - screenRect.yMax, screenRect.width, screenRect.height);
    }

    private void DrawSection(string text)
    {
        GUILayout.Space(7f);
        GUILayout.Label(text, _sectionStyle);
    }

    private void DrawButton(string label, System.Action action)
    {
        if (GUILayout.Button(label, _buttonStyle, GUILayout.Height(ButtonHeight)))
        {
            action?.Invoke();
        }
    }

    private void DrawToggle(string label, bool active, System.Action<bool> setter)
    {
        GUIStyle style = active ? _activeButtonStyle : _buttonStyle;
        if (GUILayout.Button((active ? "\u2713 " : "  ") + label, style, GUILayout.Height(ButtonHeight)))
        {
            setter?.Invoke(!active);
        }
    }

    private void DrawRadio(string label, bool active, System.Action action)
    {
        GUIStyle style = active ? _activeButtonStyle : _buttonStyle;
        if (GUILayout.Button((active ? "\u25cf " : "  ") + label, style, GUILayout.Height(ButtonHeight)))
        {
            action?.Invoke();
        }
    }

    private void DrawTextInput(string label, string value, System.Action<string> setter, bool password = false)
    {
        string current = value ?? string.Empty;
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _sectionStyle, GUILayout.Width(92f), GUILayout.Height(CompactRowHeight));
        string next = password
            ? GUILayout.PasswordField(current, '*', _textFieldStyle, GUILayout.Height(CompactRowHeight))
            : GUILayout.TextField(current, _textFieldStyle, GUILayout.Height(CompactRowHeight));
        GUILayout.EndHorizontal();
        if (next != current)
        {
            setter?.Invoke(next);
        }
    }

    private void DrawTextArea(string label, string value, System.Action<string> setter, float height)
    {
        GUILayout.Label(label, _sectionStyle, GUILayout.Height(CompactRowHeight));
        string current = value ?? string.Empty;
        string next = GUILayout.TextArea(current, _textAreaStyle, GUILayout.MinHeight(height));
        if (next != current)
        {
            setter?.Invoke(next);
        }
    }

    private void DrawStatusText(string text)
    {
        GUIStyle statusStyle = new GUIStyle(_sectionStyle)
        {
            fontSize = 15,
            fontStyle = FontStyle.Normal,
            wordWrap = true
        };
        GUILayout.Label(text ?? string.Empty, statusStyle);
    }

    private void DrawPlacementRow(string label, string leftLabel, System.Action leftAction, string rightLabel, System.Action rightAction, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _sectionStyle, GUILayout.Width(92f), GUILayout.Height(CompactRowHeight));
        if (GUILayout.Button(leftLabel, _buttonStyle, GUILayout.Height(CompactRowHeight)))
        {
            leftAction?.Invoke();
        }
        if (GUILayout.Button(rightLabel, _buttonStyle, GUILayout.Height(CompactRowHeight)))
        {
            rightAction?.Invoke();
        }
        GUILayout.Label(value, _sectionStyle, GUILayout.Width(74f), GUILayout.Height(CompactRowHeight));
        GUILayout.EndHorizontal();
    }

    private void DrawSlider(string label, float value, float min, float max, System.Action<float> setter, string displayValue)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _sectionStyle, GUILayout.Width(116f), GUILayout.Height(CompactRowHeight));
        float next = GUILayout.HorizontalSlider(value, min, max, GUILayout.Height(CompactRowHeight));
        GUILayout.Label(displayValue, _sectionStyle, GUILayout.Width(62f), GUILayout.Height(CompactRowHeight));
        GUILayout.EndHorizontal();
        if (!Mathf.Approximately(next, value))
        {
            setter?.Invoke(next);
        }
    }

    private static string FormatSigned(float value)
    {
        return value.ToString("+0.00;-0.00;0.00");
    }

    private static bool IsKeyPressed(KeyCode keyCode)
    {
        return TransparentPetRuntimeInput.KeyDown(keyCode);
    }

    private void EnsureStyles()
    {
        TransparentPetRoute resolvedRoute = ResolveMenuRoute();
        if (_panelStyle != null && _styleRoute == resolvedRoute) return;

        _styleRoute = resolvedRoute;
        bool sceneHost = resolvedRoute == TransparentPetRoute.SceneHost;

        // ── 可爱二次元配色 ──
        var panelBg    = MakeTexture(new Color(1f, 0.966f, 0.98f, 0.95f));   // 粉白面板
        var btnBg      = MakeTexture(new Color(1f, 0.98f, 0.99f, 0.88f));   // 浅粉按钮
        var btnHover   = MakeTexture(new Color(1f, 0.9f, 0.95f, 0.92f));    // hover 粉紫
        var activeBg   = MakeTexture(new Color(1f, 0.82f, 0.92f, 0.85f));   // 选中亮粉
        var closeBg    = MakeTexture(new Color(1f, 0.94f, 0.97f, 0.6f));    // 关闭钮底色
        var headerBg   = MakeTexture(new Color(1f, 0.94f, 0.98f, 0.7f));    // 标题栏底色
        var titlePurple = new Color(0.38f, 0.15f, 0.38f, 1f);              // 标题深紫
        var textPurple  = new Color(0.32f, 0.1f, 0.3f, 1f);                // 文字紫
        var accentPink  = new Color(0.9f, 0.3f, 0.6f, 1f);                 // 强调粉
        var sectionPink = new Color(0.85f, 0.42f, 0.65f, 1f);              // 分区标签

        panelBg = MakeTexture(new Color(1f, 0.966f, 0.98f, 1f));
        btnBg = MakeTexture(new Color(1f, 0.985f, 0.995f, 1f));
        btnHover = MakeTexture(new Color(1f, 0.9f, 0.95f, 1f));
        activeBg = MakeTexture(new Color(1f, 0.82f, 0.92f, 1f));
        closeBg = MakeTexture(new Color(1f, 0.94f, 0.97f, 1f));
        titlePurple = new Color(0.38f, 0.15f, 0.38f, 1f);
        accentPink = new Color(0.9f, 0.3f, 0.6f, 1f);
        sectionPink = new Color(0.78f, 0.3f, 0.56f, 1f);

        if (sceneHost)
        {
            panelBg = MakeTexture(new Color(0.055f, 0.065f, 0.08f, 1f));
            btnBg = MakeTexture(new Color(0.12f, 0.15f, 0.18f, 1f));
            btnHover = MakeTexture(new Color(0.18f, 0.23f, 0.28f, 1f));
            activeBg = MakeTexture(new Color(0.18f, 0.34f, 0.46f, 1f));
            closeBg = MakeTexture(new Color(0.14f, 0.17f, 0.2f, 1f));
            titlePurple = new Color(0.9f, 0.96f, 1f, 1f);
            textPurple = new Color(0.86f, 0.92f, 0.97f, 1f);
            accentPink = new Color(0.45f, 0.8f, 1f, 1f);
            sectionPink = new Color(0.58f, 0.86f, 1f, 1f);
        }

        Color opaquePanel = sceneHost ? new Color(0.055f, 0.065f, 0.08f, 1f) : new Color(0.98f, 0.985f, 1f, 1f);
        Color opaqueButton = sceneHost ? new Color(0.14f, 0.16f, 0.2f, 1f) : new Color(1f, 1f, 1f, 1f);
        Color opaqueHover = sceneHost ? new Color(0.2f, 0.24f, 0.3f, 1f) : new Color(0.92f, 0.96f, 1f, 1f);
        Color opaqueActive = sceneHost ? new Color(0.16f, 0.35f, 0.48f, 1f) : new Color(0.82f, 0.92f, 1f, 1f);
        Color opaqueClose = sceneHost ? new Color(0.2f, 0.13f, 0.16f, 1f) : new Color(1f, 0.88f, 0.9f, 1f);
        titlePurple = sceneHost ? new Color(0.94f, 0.98f, 1f, 1f) : new Color(0.16f, 0.18f, 0.24f, 1f);
        textPurple = sceneHost ? new Color(0.9f, 0.94f, 0.98f, 1f) : new Color(0.16f, 0.18f, 0.24f, 1f);
        accentPink = sceneHost ? new Color(0.52f, 0.82f, 1f, 1f) : new Color(0.1f, 0.36f, 0.7f, 1f);
        sectionPink = sceneHost ? new Color(0.7f, 0.88f, 1f, 1f) : new Color(0.18f, 0.38f, 0.66f, 1f);
        panelBg = MakeTexture(opaquePanel);
        btnBg = MakeTexture(opaqueButton);
        btnHover = MakeTexture(opaqueHover);
        activeBg = MakeTexture(opaqueActive);
        closeBg = MakeTexture(opaqueClose);

        _panelStyle = new GUIStyle(GUI.skin.box)
        {
            normal = { background = panelBg },
            padding = new RectOffset(16, 16, 14, 16),
            margin = new RectOffset(0, 0, 0, 0)
        };
        _titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 25,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = titlePurple },
            padding = new RectOffset(6, 0, 2, 0)
        };
        _sectionStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            normal = { textColor = sectionPink },
            padding = new RectOffset(2, 0, 2, 1)
        };
        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 21,
            normal = { background = btnBg, textColor = textPurple },
            hover = { background = btnHover, textColor = accentPink },
            active = { background = btnHover, textColor = accentPink },
            focused = { background = btnBg, textColor = textPurple },
            padding = new RectOffset(16, 12, 0, 0),
            border = new RectOffset(8, 8, 8, 8)
        };
        _activeButtonStyle = new GUIStyle(_buttonStyle)
        {
            normal = { background = activeBg, textColor = accentPink },
            hover = { background = activeBg, textColor = new Color(1f, 0.4f, 0.7f, 1f) },
            active = { background = activeBg, textColor = new Color(1f, 0.4f, 0.7f, 1f) }
        };
        _closeButtonStyle = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 21,
            fontStyle = FontStyle.Bold,
            normal = { background = closeBg, textColor = new Color(0.75f, 0.35f, 0.55f, 1f) },
            hover = { background = MakeTexture(new Color(1f, 0.7f, 0.78f, 1f)), textColor = new Color(1f, 0.3f, 0.5f, 1f) },
            active = { background = MakeTexture(new Color(1f, 0.7f, 0.78f, 1f)), textColor = new Color(1f, 0.3f, 0.5f, 1f) }
        };
        _textFieldStyle = new GUIStyle(GUI.skin.textField)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 19,
            padding = new RectOffset(10, 10, 0, 0)
        };
        _textAreaStyle = new GUIStyle(GUI.skin.textArea)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 17,
            wordWrap = true,
            padding = new RectOffset(10, 10, 8, 8)
        };
    }

    private static Texture2D MakeTexture(Color color)
    {
        color.a = 1f;
        Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private TransparentPetRoute ResolveMenuRoute()
    {
        return deriveRouteFromWindow && windowController != null ? windowController.Route : route;
    }

    private static bool IsRightMouseDown()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return (GetAsyncKeyState(VkRButton) & unchecked((short)0x8000)) != 0;
#else
        return TransparentPetRuntimeInput.MouseButtonHeld(1);
#endif
    }

    private enum MenuView
    {
        Main,
        ActionCategories,
        ActionList,
        Settings,
        Placement,
        Voice,
        Api,
        Prompts,
        StreamSettings,
        FaceTracking
    }
}
