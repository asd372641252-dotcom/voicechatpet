using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

[DisallowMultipleComponent]
public sealed class TransparentPetVoiceRuntimeLauncher : MonoBehaviour
{
    private const int StreamLimitSettingsVersion = 8;

    public string streamingRootRelativePath = "GodotFinal";
    public string projectRootOverride = "";
    public string pythonExecutable = "python";
    public string voiceRoutesConfigPath = "config/voice_routes.json";
    public string llmProviderConfigPath = "config/volc_traditional_voice_chat.local.json";
    public string companionPromptConfigPath = "config/volc_traditional_companion_polling.local.json";
    public string runtimeExePath = "tools/volc_voice_runtime/bin/Release/net8.0-windows/VolcVoiceRuntime.exe";
    public string defaultBridgeScriptPath = "scripts/run_volc_rtc_web_client.py";
    public string selectedRouteId = "traditional_vision";
    public string customLlmUrl = "";
    public string customLlmApiKey = "";
    public string customLlmModelName = "mimo-v2.5";
    public string personaPrompt = "";
    public string companionPollingPrompt = "";
    public int customLlmPingTimeoutSec = 12;
    public int defaultBridgePort = 17862;
    public int defaultGodotPosePort = 17865;
    public int companionPollingIntervalSec = 8;
    public int realtimeMonitoringIntervalSec = 8;
    public bool autoStartUrl = true;
    public bool showRuntimeWindow;
    public bool preferChromeRtcRuntime = true;
    public string chromeExecutablePath = "";
    public string chromeRuntimeProfileName = "SilverWolfRtcRuntime";
    public bool startOnPlay;
    public bool allowStartOnPlayInProduct;
    public string presentationRoute = "unity";
    public string presentationHost = "127.0.0.1";
    public int presentationPort = 17861;
    public TransparentPetSceneFaceTracker sceneFaceTracker;
    public string streamLimitSettingsKey = "TransparentPet.StreamLimits.v1";
    public int screenVisionWidth = 1280;
    public int screenVisionHeight = 720;
    public int screenVisionFps = 10;
    public int screenVisionMaxKbps = 1200;
    public bool screenCameraOverlayEnabled = true;
    public int screenCameraOverlayWidth = 640;
    public int screenCameraOverlayHeight = 360;
    public int screenCameraOverlayPadding = 24;
    public int cameraVideoWidth = 1280;
    public int cameraVideoHeight = 720;
    public int cameraVideoFps = 10;
    public int cameraVideoMaxKbps = 1000;
    public int faceTrackingPacketFps = 8;
    public bool cameraVideoUseCameraHub = true;
    public string cameraVideoHubUrl = "http://127.0.0.1:17863/stream.mjpg";
    public bool cameraVideoUseVirtualCamera;
    public bool cameraVideoRequireVirtualCamera;
    public bool cameraVideoSendFaceTrackingPackets;
    public string cameraVideoDeviceKeyword = "virtual,obs";
    public bool monitorVoiceHealth = true;
    [Range(1f, 30f)]
    public float voiceHealthPollIntervalSeconds = 3f;
    [Range(4f, 60f)]
    public float voicePendingWarnSeconds = 10f;
    [Range(6f, 90f)]
    public float voiceStaleStateWarnSeconds = 18f;

    private readonly Dictionary<string, RouteInfo> _routes = new Dictionary<string, RouteInfo>(StringComparer.OrdinalIgnoreCase);
    private Process _bridgeProcess;
    private Process _runtimeProcess;
    private string _bridgeKey = "";
    private bool _bridgeHttpReady;
    private readonly List<string> _customLlmModels = new List<string>();
    private Coroutine _startRoutine;
    private Coroutine _stopRoutine;
    private Coroutine _cameraStartRoutine;
    private Coroutine _cameraStopRoutine;
    private Coroutine _llmTestRoutine;
    private Coroutine _llmModelsRoutine;
    private Coroutine _cameraOwnershipReconcileRoutine;
    private Coroutine _voiceHealthRoutine;
    private bool _cameraVideoStartInProgress;
    private bool _sceneFaceTrackerSharedCameraActive;
    private bool _sceneFaceTrackerWasRunningBeforeSharedCamera;
    private float _nextCameraOwnershipReconcileRealtime;
    private float _nextSceneFaceTrackingWatchdogRealtime;
    private float _nextVoiceHealthPollRealtime;
    private string _lastVoiceHealthStatus = "";
    private bool _voiceHealthWarningActive;
    private int _cameraVideoRequestSerial;
    private static readonly Regex EnvPlaceholderRegex = new Regex(@"\$\{([A-Za-z0-9_]+)\}");

    public string Status { get; private set; } = "Voice idle.";
    public string CustomLlmStatus { get; private set; } = "LLM provider idle.";
    public string PromptStatus { get; private set; } = "Prompt config idle.";
    public bool IsBridgeRunning => IsProcessRunning(_bridgeProcess);
    public bool IsBridgeReadyForRequests => _bridgeHttpReady && IsBridgeRunning;
    public bool IsRuntimeRunning => IsProcessRunning(_runtimeProcess);
    public string SelectedRouteId => selectedRouteId;
    public string SelectedRouteName => FriendlyRouteName(selectedRouteId);
    public bool SelectedRouteSupportsVision => ActiveRoute().SupportsVision;
    public bool ScreenVisionActive { get; private set; }
    public bool CompanionPollingActive { get; private set; }
    public bool CameraVideoActive { get; private set; }
    public bool RealtimeMonitoringEnabled => CompanionPollingActive;
    public bool CustomLlmTestRunning { get; private set; }
    public bool CustomLlmModelsLoading { get; private set; }
    public int CustomLlmModelCount => _customLlmModels.Count;
    public int ScreenVisionWidth => screenVisionWidth;
    public int ScreenVisionHeight => screenVisionHeight;
    public int ScreenVisionFps => screenVisionFps;
    public int ScreenVisionMaxKbps => screenVisionMaxKbps;
    public bool ScreenCameraOverlayEnabled => screenCameraOverlayEnabled;
    public int ScreenCameraOverlayWidth => screenCameraOverlayWidth;
    public int ScreenCameraOverlayHeight => screenCameraOverlayHeight;
    public int ScreenCameraOverlayPadding => screenCameraOverlayPadding;
    public int CameraVideoWidth => cameraVideoWidth;
    public int CameraVideoHeight => cameraVideoHeight;
    public int CameraVideoFps => cameraVideoFps;
    public int CameraVideoMaxKbps => cameraVideoMaxKbps;
    public int FaceTrackingPacketFps => faceTrackingPacketFps;
    public bool CameraVideoUseCameraHub => cameraVideoUseCameraHub;
    public string CameraVideoHubUrl => ResolveCameraHubStreamUrl();
    public bool CameraVideoUseVirtualCamera => cameraVideoUseVirtualCamera;
    public bool CameraVideoRequireVirtualCamera => cameraVideoRequireVirtualCamera;
    public bool CameraVideoSendFaceTrackingPackets => cameraVideoSendFaceTrackingPackets;
    public string CameraVideoDeviceKeyword => cameraVideoDeviceKeyword;

    private void Awake()
    {
        LoadStreamLimitSettings();
        NormalizeStreamLimits();
        LoadVoiceRoutes();
        LoadCustomLlmProviderFromConfig();
        LoadPromptSettingsFromConfig();
    }

    private void Start()
    {
        if (Application.isPlaying && startOnPlay && allowStartOnPlayInProduct)
        {
            StartVoiceRuntime(selectedRouteId);
        }
        else if (Application.isPlaying && startOnPlay)
        {
            SetStatus("Voice idle. Start it from the right-click menu.");
        }
    }

    private void Update()
    {
        if (Application.isPlaying)
        {
            ReconcileSharedCameraOwnershipIfNeeded();
            ReconcileSceneFaceTrackingForVoiceIfNeeded();
            PollVoiceHealthIfNeeded();
        }
    }

    private void OnDestroy()
    {
        StopVoiceProcesses();
    }

    public void LoadCustomLlmProviderFromConfig()
    {
        try
        {
            string configPath = ResolveRuntimePath(llmProviderConfigPath);
            if (!File.Exists(configPath))
            {
                SetCustomLlmStatus("LLM config missing: " + configPath);
                return;
            }

            Dictionary<string, object> config = LoadMergedConfig(configPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Dictionary<string, object> llm = TransparentPetJson.AsObject(GetNestedObject(config, "StartVoiceChat", "Config", "LLMConfig"));
            if (llm == null)
            {
                SetCustomLlmStatus("LLM config missing StartVoiceChat.Config.LLMConfig.");
                return;
            }

            customLlmUrl = FirstNonEmpty(
                GetOptionalString(llm, "Url"),
                GetOptionalString(llm, "URL"),
                GetOptionalString(llm, "url"),
                GetOptionalString(llm, "Endpoint"));
            customLlmApiKey = FirstNonEmpty(
                GetOptionalString(llm, "APIKey"),
                GetOptionalString(llm, "ApiKey"),
                GetOptionalString(llm, "api_key"));
            customLlmModelName = FirstNonEmpty(
                GetOptionalString(llm, "ModelName"),
                GetOptionalString(llm, "Model"),
                GetOptionalString(llm, "model"),
                customLlmModelName);

            SetCustomLlmStatus("LLM provider loaded: " + FormatCustomLlmSummary());
        }
        catch (Exception ex)
        {
            SetCustomLlmStatus("LLM config load failed: " + ex.Message);
        }
    }

    public void TestAndApplyCustomLlmProvider()
    {
        if (_llmTestRoutine != null)
        {
            StopCoroutine(_llmTestRoutine);
        }

        _llmTestRoutine = StartCoroutine(TestAndApplyCustomLlmProviderRoutine());
    }

    public void FetchCustomLlmModels()
    {
        if (_llmModelsRoutine != null)
        {
            StopCoroutine(_llmModelsRoutine);
        }

        _llmModelsRoutine = StartCoroutine(FetchCustomLlmModelsRoutine());
    }

    public void DiagnoseVoiceRuntime()
    {
        if (!Application.isPlaying)
        {
            SetStatus("Voice diagnostics are available in Play mode.");
            return;
        }

        if (!IsBridgeRunning)
        {
            SetStatus("Voice bridge is not running.");
            return;
        }

        StartCoroutine(DiagnoseVoiceRuntimeRoutine());
    }

    public string GetCustomLlmModel(int index)
    {
        return index >= 0 && index < _customLlmModels.Count ? _customLlmModels[index] : "";
    }

    public void SelectCustomLlmModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        customLlmModelName = modelId.Trim();
        SetCustomLlmStatus("Selected model: " + customLlmModelName);
    }

    public void LoadPromptSettingsFromConfig()
    {
        try
        {
            string personaConfigPath = ResolveRuntimePath(llmProviderConfigPath);
            Dictionary<string, object> personaConfig = LoadMergedConfig(personaConfigPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            string loadedPersona = Convert.ToString(
                GetNestedObject(personaConfig, "StartVoiceChat", "Config", "LLMConfig", "SystemMessages", 0) ?? "",
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(loadedPersona))
            {
                personaPrompt = loadedPersona;
            }

            string pollingConfigPath = ResolveRuntimePath(companionPromptConfigPath);
            if (!File.Exists(pollingConfigPath))
            {
                pollingConfigPath = personaConfigPath;
            }

            Dictionary<string, object> pollingConfig = LoadMergedConfig(pollingConfigPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            string loadedPollingPrompt = Convert.ToString(
                GetNestedObject(pollingConfig, "CompanionVision", "Prompt") ?? "",
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(loadedPollingPrompt))
            {
                companionPollingPrompt = loadedPollingPrompt;
            }

            SetPromptStatus("Prompt config loaded.");
        }
        catch (Exception ex)
        {
            SetPromptStatus("Prompt load failed: " + ex.Message);
        }
    }

    public void ApplyPromptSettingsToConfig()
    {
        if (!SavePromptSettingsToConfig(out string status))
        {
            SetPromptStatus("Prompt save failed: " + status);
            return;
        }

        bool hadRunningVoice = IsBridgeRunning || IsRuntimeRunning;
        if (hadRunningVoice)
        {
            StopVoiceProcesses();
        }

        _bridgeKey = "";
        SetPromptStatus(status + (hadRunningVoice ? " Voice bridge reset." : ""));
    }

    public bool SavePromptSettingsToConfig(out string status)
    {
        status = "";
        string persona = (personaPrompt ?? string.Empty).Trim();
        string pollingPrompt = (companionPollingPrompt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(persona))
        {
            status = "Persona prompt is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(pollingPrompt))
        {
            status = "Polling prompt is empty.";
            return false;
        }

        try
        {
            int personaWrites = SavePersonaPromptToMirroredConfigs(llmProviderConfigPath, persona);
            int pollingWrites = 0;
            pollingWrites += SaveCompanionPromptToMirroredConfigs(llmProviderConfigPath, pollingPrompt);
            if (!string.Equals(companionPromptConfigPath, llmProviderConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                pollingWrites += SaveCompanionPromptToMirroredConfigs(companionPromptConfigPath, pollingPrompt);
            }

            if (personaWrites <= 0 || pollingWrites <= 0)
            {
                status = "No prompt config files were updated.";
                return false;
            }

            personaPrompt = persona;
            companionPollingPrompt = pollingPrompt;
            LoadVoiceRoutes();
            status = "Prompts saved. Persona files: " + personaWrites + ", polling files: " + pollingWrites + ".";
            return true;
        }
        catch (Exception ex)
        {
            status = ex.Message;
            return false;
        }
    }

    public bool SaveCustomLlmProviderToConfig(out string status)
    {
        status = "";
        if (!ValidateCustomLlmConfig(out status))
        {
            return false;
        }

        try
        {
            string configPath = ResolveRuntimePath(llmProviderConfigPath);
            if (!File.Exists(configPath))
            {
                status = "LLM config missing: " + configPath;
                return false;
            }

            Dictionary<string, object> root = TransparentPetJson.AsObject(TransparentPetJson.Parse(File.ReadAllText(configPath, Encoding.UTF8)));
            if (root == null)
            {
                status = "LLM config root is not an object.";
                return false;
            }

            Dictionary<string, object> llm = EnsureNestedObject(root, "StartVoiceChat", "Config", "LLMConfig");
            customLlmUrl = NormalizeOpenAiCompatibleUrl(customLlmUrl);
            llm["Mode"] = "CustomLLM";
            llm["Url"] = customLlmUrl;
            llm["APIKey"] = customLlmApiKey.Trim();
            llm["ModelName"] = customLlmModelName.Trim();

            File.WriteAllText(configPath, TransparentPetJson.Stringify(root, true) + Environment.NewLine, new UTF8Encoding(false));
            _bridgeKey = "";
            LoadVoiceRoutes();
            status = "saved to " + Path.GetFileName(configPath) + ".";
            return true;
        }
        catch (Exception ex)
        {
            status = ex.Message;
            return false;
        }
    }

    public void SelectRoute(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId))
        {
            return;
        }

        if (!_routes.ContainsKey(routeId))
        {
            LoadVoiceRoutes();
        }

        if (!_routes.ContainsKey(routeId))
        {
            SetStatus("Unknown voice route: " + routeId);
            return;
        }

        selectedRouteId = routeId;
        ScreenVisionActive = false;
        CompanionPollingActive = false;
        SetStatus("Voice route selected: " + FriendlyRouteName(routeId));
    }

    public void CheckSelectedVoiceConfig()
    {
        RouteInfo route = ActiveRoute();
        string configPath = ResolveRuntimePath(route.ConfigPath);
        if (!File.Exists(configPath))
        {
            SetStatus("Voice config missing: " + configPath);
            return;
        }

        if (TryGetMissingCloudConfigStatus(route, configPath, out string cloudConfigStatus))
        {
            SetStatus(cloudConfigStatus);
            return;
        }

        SetStatus("Cloud config ready: " + FriendlyRouteName(route.Id));
    }

    public void StartVoiceRuntime(string routeId = "")
    {
        if (!string.IsNullOrWhiteSpace(routeId))
        {
            SelectRoute(routeId);
        }

        if (_startRoutine != null)
        {
            StopCoroutine(_startRoutine);
        }

        bool requestVision = ShouldRequestScreenVision(ActiveRoute());
        ScreenVisionActive = requestVision;
        CompanionPollingActive = requestVision && IsCompanionPollingRoute(ActiveRoute().Id);
        ResetVoiceHealthWarning();
        _startRoutine = StartCoroutine(StartVoiceRuntimeRoutine(requestVision));
    }

    public void StartScreenVisionRuntime(string preferredRouteId = "")
    {
        string routeId = DirectScreenVisionRouteId(preferredRouteId);
        if (string.IsNullOrWhiteSpace(routeId))
        {
            SetStatus("No screen-vision voice route is available.");
            return;
        }

        SelectRoute(routeId);
        ScreenVisionActive = true;
        CompanionPollingActive = IsCompanionPollingRoute(routeId);
        ResetVoiceHealthWarning();
        if (_startRoutine != null)
        {
            StopCoroutine(_startRoutine);
        }

        _startRoutine = StartCoroutine(StartVoiceRuntimeRoutine(true));
    }

    public void StartCompanionPollingRuntime(int intervalSeconds)
    {
        companionPollingIntervalSec = NormalizeCompanionPollingInterval(intervalSeconds);
        StartScreenVisionRuntime("traditional_companion_polling");
    }

    public void StartRealtimeMonitoringRuntime(int intervalSeconds)
    {
        companionPollingIntervalSec = NormalizeCompanionPollingInterval(intervalSeconds);
        ScreenVisionActive = true;
        CompanionPollingActive = true;
        if (!IsBridgeRunning)
        {
            StartScreenVisionRuntime(selectedRouteId);
            CompanionPollingActive = true;
            return;
        }

        StartCoroutine(StartRealtimeMonitoringRuntimeRoutine());
    }

    public void StopCompanionPollingRuntime()
    {
        CompanionPollingActive = false;
        StartCoroutine(PostBridgeJson("/api/companion_vision/stop", "Companion polling stopped."));
    }

    public void StopScreenVisionRuntime()
    {
        ScreenVisionActive = false;
        CompanionPollingActive = false;
        StartCoroutine(PostBridgeJson("/api/vision/stop", "Screen vision stopped."));
    }

    public void StartCameraVideoRuntime()
    {
        EnsureSceneFaceTrackingForVoice();
        _cameraVideoRequestSerial++;
        int requestSerial = _cameraVideoRequestSerial;
        if (_cameraStartRoutine != null)
        {
            StopCoroutine(_cameraStartRoutine);
            _cameraStartRoutine = null;
        }

        if (_cameraStopRoutine != null)
        {
            StopCoroutine(_cameraStopRoutine);
            _cameraStopRoutine = null;
        }

        CameraVideoActive = true;
        EnsureCloudVisibleCameraOverlayForCameraVideo();
        bool ensureScreenVisionForCloud = ActiveRoute().SupportsVision && !ScreenVisionActive;
        if (ensureScreenVisionForCloud)
        {
            ScreenVisionActive = true;
        }

        if (!IsBridgeRunning)
        {
            if (_startRoutine != null)
            {
                StopCoroutine(_startRoutine);
            }

            SetStatus("Voice bridge starting for camera video.");
            _startRoutine = StartCoroutine(StartVoiceRuntimeRoutine(ShouldRequestScreenVision(ActiveRoute()) || ensureScreenVisionForCloud));
            return;
        }

        _cameraStartRoutine = StartCoroutine(StartCameraVideoRuntimeRoutine(ensureScreenVisionForCloud, requestSerial));
    }

    public void StopCameraVideoRuntime()
    {
        StopCameraVideoRuntime(true);
    }

    public void StopCameraVideoRuntime(bool restoreExclusiveTracker)
    {
        _cameraVideoRequestSerial++;
        _cameraVideoStartInProgress = false;
        if (_cameraStartRoutine != null)
        {
            StopCoroutine(_cameraStartRoutine);
            _cameraStartRoutine = null;
        }

        CameraVideoActive = false;
        if (_cameraStopRoutine != null)
        {
            StopCoroutine(_cameraStopRoutine);
        }

        _cameraStopRoutine = StartCoroutine(StopCameraVideoRuntimeRoutine(restoreExclusiveTracker, _cameraVideoRequestSerial));
    }

    public IEnumerator StopCameraVideoRuntimeForStandaloneFaceTracking()
    {
        _cameraVideoRequestSerial++;
        _cameraVideoStartInProgress = false;
        if (_cameraStartRoutine != null)
        {
            StopCoroutine(_cameraStartRoutine);
            _cameraStartRoutine = null;
        }

        CameraVideoActive = false;
        if (!IsBridgeRunning)
        {
            yield break;
        }

        if (_cameraStopRoutine != null)
        {
            StopCoroutine(_cameraStopRoutine);
            _cameraStopRoutine = null;
        }

        yield return PostBridgeJson("/api/camera/stop", "Camera video stopped.", "{\"force\":true,\"source\":\"unity_standalone_face_tracking\"}");
        yield return WaitForBridgeCameraPublished(false, 2f);
    }

    public void SetCompanionPollingInterval(int seconds)
    {
        companionPollingIntervalSec = NormalizeCompanionPollingInterval(seconds);
        if (IsBridgeRunning && CompanionPollingActive)
        {
            string body = "{\"interval_sec\":" + companionPollingIntervalSec + "}";
            StartCoroutine(PostBridgeJson("/api/companion_vision/interval", "Companion interval set.", body));
        }
    }

    public void SetRealtimeMonitoring(bool enabled)
    {
        if (enabled)
        {
            SetCompanionPollingInterval(realtimeMonitoringIntervalSec);
        }
    }

    public void SetRealtimeMonitoringInterval(float seconds)
    {
        realtimeMonitoringIntervalSec = NormalizeCompanionPollingInterval(Mathf.RoundToInt(seconds));
        SetCompanionPollingInterval(realtimeMonitoringIntervalSec);
    }

    public void SetScreenVisionResolution(int width, int height)
    {
        screenVisionWidth = width;
        screenVisionHeight = height;
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetScreenVisionFps(float value)
    {
        screenVisionFps = Mathf.RoundToInt(value);
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetScreenVisionMaxKbps(float value)
    {
        screenVisionMaxKbps = Mathf.RoundToInt(value);
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetScreenCameraOverlayEnabled(bool value)
    {
        screenCameraOverlayEnabled = value;
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetScreenCameraOverlaySize(int width, int height)
    {
        screenCameraOverlayWidth = width;
        screenCameraOverlayHeight = height;
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetScreenCameraOverlayWidth(float value)
    {
        int width = Mathf.RoundToInt(value);
        screenCameraOverlayWidth = width;
        screenCameraOverlayHeight = Mathf.RoundToInt(width * 9f / 16f);
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetScreenCameraOverlayPadding(float value)
    {
        screenCameraOverlayPadding = Mathf.RoundToInt(value);
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetCameraVideoResolution(int width, int height)
    {
        cameraVideoWidth = width;
        cameraVideoHeight = height;
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetCameraVideoFps(float value)
    {
        cameraVideoFps = Mathf.RoundToInt(value);
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetCameraVideoMaxKbps(float value)
    {
        cameraVideoMaxKbps = Mathf.RoundToInt(value);
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetFaceTrackingPacketFps(float value)
    {
        faceTrackingPacketFps = Mathf.RoundToInt(value);
        SaveStreamLimitsAndApplyToBridge();
    }

    public void ApplyPerformanceStreamPreset()
    {
        screenVisionWidth = 1280;
        screenVisionHeight = 720;
        screenVisionFps = 10;
        screenVisionMaxKbps = 1200;
        cameraVideoWidth = 1280;
        cameraVideoHeight = 720;
        cameraVideoFps = 10;
        cameraVideoMaxKbps = 1000;
        faceTrackingPacketFps = 8;
        SaveStreamLimitsAndApplyToBridge();
    }

    public void ApplyQualityStreamPreset()
    {
        screenVisionWidth = 1280;
        screenVisionHeight = 720;
        screenVisionFps = 15;
        screenVisionMaxKbps = 1800;
        cameraVideoWidth = 1280;
        cameraVideoHeight = 720;
        cameraVideoFps = 15;
        cameraVideoMaxKbps = 1500;
        faceTrackingPacketFps = 12;
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetCameraVideoUseCameraHub(bool value)
    {
        cameraVideoUseCameraHub = value;
        if (value)
        {
            cameraVideoUseVirtualCamera = false;
            cameraVideoRequireVirtualCamera = false;
            cameraVideoSendFaceTrackingPackets = false;
        }
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetCameraVideoUseVirtualCamera(bool value)
    {
        cameraVideoUseVirtualCamera = value;
        if (value)
        {
            cameraVideoUseCameraHub = false;
            cameraVideoSendFaceTrackingPackets = false;
        }
        else
        {
            cameraVideoRequireVirtualCamera = false;
        }
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetCameraVideoRequireVirtualCamera(bool value)
    {
        cameraVideoRequireVirtualCamera = value;
        if (value)
        {
            cameraVideoUseVirtualCamera = true;
            cameraVideoUseCameraHub = false;
            cameraVideoSendFaceTrackingPackets = false;
        }
        SaveStreamLimitsAndApplyToBridge();
    }

    public void SetCameraVideoSendFaceTrackingPackets(bool value)
    {
        cameraVideoSendFaceTrackingPackets = value;
        SaveStreamLimitsAndApplyToBridge();
    }

    public void StopVoiceRuntime()
    {
        if (_stopRoutine != null)
        {
            StopCoroutine(_stopRoutine);
        }

        _stopRoutine = StartCoroutine(StopVoiceRuntimeRoutine());
    }

    public void StopVoiceProcesses()
    {
        _cameraVideoRequestSerial++;
        _cameraVideoStartInProgress = false;
        if (_cameraStartRoutine != null)
        {
            StopCoroutine(_cameraStartRoutine);
            _cameraStartRoutine = null;
        }

        if (_cameraStopRoutine != null)
        {
            StopCoroutine(_cameraStopRoutine);
            _cameraStopRoutine = null;
        }

        StopProcess(ref _runtimeProcess);
        StopProcess(ref _bridgeProcess);
        StopOrphanVoiceProcesses(ActiveRoute());
        _bridgeKey = "";
        _bridgeHttpReady = false;
        ScreenVisionActive = false;
        CompanionPollingActive = false;
        CameraVideoActive = false;
        ResetVoiceHealthWarning();
    }

    private IEnumerator StartVoiceRuntimeRoutine(bool requestVision)
    {
        RouteInfo route = ActiveRoute();
        EnsurePresentationServerForVoice();
        EnsureSceneFaceTrackingForVoice();
        SetLocalVoicePresentationState("listening");
        SetStatus("Voice bridge starting: " + FriendlyRouteName(route.Id));
        StartBridgeIfNeeded(route);
        if (!IsBridgeRunning)
        {
            _bridgeHttpReady = false;
            SetLocalVoicePresentationState("idle");
            yield break;
        }

        bool bridgeReady = false;
        yield return WaitForBridgeHttpReady(route.BridgePort, 8f, value => bridgeReady = value);
        _bridgeHttpReady = bridgeReady;
        if (!bridgeReady)
        {
            SetStatus(IsBridgeRunning
                ? "Voice bridge did not become ready in time."
                : "Voice bridge exited after start. Check cloud config.");
            SetLocalVoicePresentationState("idle");
            yield break;
        }

        if (!route.RequiresRuntimeWindow)
        {
            SetStatus("Agent speaker bridge ready at http://127.0.0.1:" + route.BridgePort);
            yield break;
        }

        yield return new WaitForSecondsRealtime(0.85f);
        if (!IsBridgeRunning || !IsBridgeReadyForRequests)
        {
            SetStatus("Voice bridge exited after start. Check cloud config.");
            SetLocalVoicePresentationState("idle");
            yield break;
        }

        yield return PostBridgeJson("/api/vision/settings", "Screen stream settings applied.", BuildScreenVisionSettingsBody());
        yield return PostBridgeJson("/api/camera/settings", "Camera stream settings applied.", BuildCameraVideoSettingsBody());
        StartRuntimeWindow(route, requestVision);
        if (requestVision)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            bool requestCompanionPolling = CompanionPollingActive || route.Id == "traditional_companion_polling";
            if (requestCompanionPolling)
            {
                string body = "{\"interval_sec\":" + companionPollingIntervalSec + "}";
                yield return PostBridgeJson("/api/companion_vision/interval", "Companion interval set.", body);
            }
            yield return PostBridgeJson("/api/vision/start", "Screen vision requested.");
            if (requestCompanionPolling)
            {
                yield return PostBridgeJson("/api/companion_vision/start", "Companion polling requested.");
            }
        }

        if (CameraVideoActive && _cameraStartRoutine == null && IsBridgeReadyForRequests)
        {
            _cameraStartRoutine = StartCoroutine(StartCameraVideoRuntimeRoutine(false, _cameraVideoRequestSerial));
        }
    }

    private IEnumerator StartCameraVideoRuntimeRoutine(bool ensureScreenVisionForCloud, int requestSerial)
    {
        _cameraVideoStartInProgress = true;
        EnsureSceneFaceTrackingForVoice();
        if (!IsBridgeReadyForRequests)
        {
            bool bridgeReady = false;
            yield return WaitForBridgeHttpReady(ActiveRoute().BridgePort, 6f, value => bridgeReady = value);
            _bridgeHttpReady = bridgeReady;
            if (!bridgeReady || !IsCurrentCameraStartRequest(requestSerial))
            {
                SetStatus("Camera video waits for voice bridge readiness.");
                _cameraVideoStartInProgress = false;
                if (_cameraStartRoutine != null)
                {
                    _cameraStartRoutine = null;
                }
                yield break;
            }
        }

        if (_sceneFaceTrackerSharedCameraActive)
        {
            yield return new WaitForSecondsRealtime(0.8f);
        }

        if (!IsCurrentCameraStartRequest(requestSerial))
        {
            _cameraVideoStartInProgress = false;
            if (_cameraStartRoutine != null)
            {
                _cameraStartRoutine = null;
            }
            yield break;
        }

        yield return PostBridgeJson("/api/camera/settings", "Camera stream settings applied.", BuildCameraVideoSettingsBody());
        if (cameraVideoUseCameraHub)
        {
            yield return WaitForSceneCameraHubReady(6f);
        }
        if (!IsCurrentCameraStartRequest(requestSerial))
        {
            _cameraVideoStartInProgress = false;
            _cameraStartRoutine = null;
            yield break;
        }

        yield return PostBridgeJson("/api/camera/start", "Camera video requested.");
        if (!IsCurrentCameraStartRequest(requestSerial))
        {
            _cameraVideoStartInProgress = false;
            _cameraStartRoutine = null;
            yield break;
        }

        bool cameraPublished = false;
        yield return WaitForBridgeCameraPublished(true, 12f, value => cameraPublished = value);
        if (!IsCurrentCameraStartRequest(requestSerial))
        {
            _cameraVideoStartInProgress = false;
            _cameraStartRoutine = null;
            yield break;
        }

        _cameraVideoStartInProgress = false;
        _cameraStartRoutine = null;
        EnsureSceneFaceTrackingForVoice();
        if (!cameraPublished)
        {
            SetStatus("Camera video still starting; request kept active.");
        }

        if (ensureScreenVisionForCloud)
        {
            yield return PostBridgeJson("/api/vision/settings", "Screen stream settings applied.", BuildScreenVisionSettingsBody());
            yield return PostBridgeJson("/api/vision/start", "Screen vision requested for dual stream.");
        }
    }

    private IEnumerator StartRealtimeMonitoringRuntimeRoutine()
    {
        string body = "{\"interval_sec\":" + companionPollingIntervalSec + "}";
        yield return PostBridgeJson("/api/vision/start", "Screen vision requested.");
        yield return PostBridgeJson("/api/companion_vision/interval", "Realtime monitor interval set.", body);
        yield return PostBridgeJson("/api/companion_vision/start", "Realtime monitor requested.");
    }

    private IEnumerator StopVoiceRuntimeRoutine()
    {
        SetStatus("Voice stopping...");
        ScreenVisionActive = false;
        CompanionPollingActive = false;
        CameraVideoActive = false;
        RouteInfo route = ActiveRoute();
        if (route.SupportsVision)
        {
            yield return PostBridgeJson("/api/vision/stop", "Screen vision stopped.");
        }
        yield return PostBridgeJson("/api/camera/stop", "Camera video stopped.", "{\"force\":true,\"source\":\"unity_voice_stop\"}");
        yield return WaitForBridgeCameraPublished(false, 2f);
        yield return PostBridgeJson("/api/stop_voice_chat", "Cloud voice session stopped.");
        yield return new WaitForSecondsRealtime(0.25f);
        StopVoiceProcesses();
        yield return new WaitForSecondsRealtime(0.25f);
        RestoreSceneFaceTrackerExclusiveCamera();
        SetLocalVoicePresentationState("idle");
        ResetVoiceHealthWarning();
        SetStatus("Voice stopped.");
    }

    private IEnumerator StopCameraVideoRuntimeRoutine(bool restoreExclusiveTracker, int requestSerial)
    {
        yield return PostBridgeJson("/api/camera/stop", "Camera video stopped.", "{\"force\":true,\"source\":\"unity_camera_stop\"}");
        yield return WaitForBridgeCameraPublished(false, 2f);
        if (restoreExclusiveTracker && requestSerial == _cameraVideoRequestSerial)
        {
            yield return new WaitForSecondsRealtime(0.45f);
            RestoreSceneFaceTrackerExclusiveCamera();
        }

        if (requestSerial == _cameraVideoRequestSerial)
        {
            _cameraStopRoutine = null;
        }
    }

    private void PrepareSceneFaceTrackerForSharedCamera()
    {
        EnsureSceneFaceTrackingForVoice();
    }

    private bool IsCurrentCameraStartRequest(int requestSerial)
    {
        return requestSerial == _cameraVideoRequestSerial && CameraVideoActive && IsBridgeRunning;
    }

    private void EnsureSceneFaceTrackingForVoice()
    {
        TransparentPetSceneFaceTracker tracker = ResolveSceneFaceTracker();
        if (tracker == null || !tracker.IsSceneRoute)
        {
            return;
        }

        if (!tracker.TrackingEnabled)
        {
            tracker.SetTrackingEnabled(true);
        }

        _sceneFaceTrackerSharedCameraActive = false;
        _sceneFaceTrackerWasRunningBeforeSharedCamera = false;
        if (!tracker.IsStandaloneLocalMediaPipe || !tracker.IsRunning)
        {
            tracker.EnsureStandaloneLocalMediaPipeTemporary("voice start keeps scene face tracking local", true);
        }
    }

    private void ReconcileSceneFaceTrackingForVoiceIfNeeded()
    {
        if (!CameraVideoActive && !_cameraVideoStartInProgress)
        {
            return;
        }

        if (Time.realtimeSinceStartup < _nextSceneFaceTrackingWatchdogRealtime)
        {
            return;
        }

        _nextSceneFaceTrackingWatchdogRealtime = Time.realtimeSinceStartup + 1f;
        TransparentPetSceneFaceTracker tracker = ResolveSceneFaceTracker();
        if (tracker == null || !tracker.IsSceneRoute)
        {
            return;
        }

        if (!tracker.TrackingEnabled)
        {
            tracker.SetTrackingEnabled(true);
        }

        if (!tracker.IsStandaloneLocalMediaPipe || !tracker.IsRunning)
        {
            tracker.EnsureStandaloneLocalMediaPipeTemporary("voice watchdog keeps scene face tracking local", true);
        }
    }

    private void EnsurePresentationServerForVoice()
    {
        PetControlServer server = FindAnyObjectByType<PetControlServer>();
        if (server != null)
        {
            server.StartServer();
            if (server.port > 0 && presentationPort != server.port)
            {
                presentationPort = server.port;
                _bridgeKey = "";
            }
        }
    }

    private void RestoreSceneFaceTrackerExclusiveCamera()
    {
        TransparentPetSceneFaceTracker tracker = ResolveSceneFaceTracker();
        if (tracker == null || !tracker.IsSceneRoute)
        {
            _sceneFaceTrackerSharedCameraActive = false;
            return;
        }

        bool wasShared = _sceneFaceTrackerSharedCameraActive;
        bool wasRunningBeforeShared = _sceneFaceTrackerWasRunningBeforeSharedCamera;
        _sceneFaceTrackerSharedCameraActive = false;
        _sceneFaceTrackerWasRunningBeforeSharedCamera = false;
        if (!tracker.TrackingEnabled)
        {
            return;
        }

        if (wasShared || wasRunningBeforeShared || tracker.IsBridgePacketReceiver || !tracker.IsRunning)
        {
            tracker.EnsureStandaloneLocalMediaPipeTemporary("exclusive camera restored", true);
        }
    }

    private void ReconcileSharedCameraOwnershipIfNeeded()
    {
        if (_cameraVideoStartInProgress)
        {
            return;
        }

        if (!IsBridgeRunning)
        {
            if (_sceneFaceTrackerSharedCameraActive || CameraVideoActive)
            {
                RestoreSceneFaceTrackerExclusiveCamera();
            }

            if (CameraVideoActive && Time.realtimeSinceStartup >= _nextCameraOwnershipReconcileRealtime)
            {
                _nextCameraOwnershipReconcileRealtime = Time.realtimeSinceStartup + 2f;
                if (_startRoutine != null)
                {
                    StopCoroutine(_startRoutine);
                }

                SetStatus("Camera video bridge dropped; restarting voice bridge.");
                _startRoutine = StartCoroutine(StartVoiceRuntimeRoutine(ShouldRequestScreenVision(ActiveRoute())));
            }
            return;
        }

        if (_cameraOwnershipReconcileRoutine != null ||
            Time.realtimeSinceStartup < _nextCameraOwnershipReconcileRealtime)
        {
            return;
        }

        _nextCameraOwnershipReconcileRealtime = Time.realtimeSinceStartup + 1f;
        _cameraOwnershipReconcileRoutine = StartCoroutine(ReconcileSharedCameraOwnershipRoutine());
    }

    private IEnumerator ReconcileSharedCameraOwnershipRoutine()
    {
        bool statusRead = false;
        bool desired = true;
        bool published = true;
        yield return ReadBridgeCameraStatus((nextDesired, nextPublished) =>
        {
            statusRead = true;
            desired = nextDesired;
            published = nextPublished;
        });

        _cameraOwnershipReconcileRoutine = null;

        if (!statusRead)
        {
            yield break;
        }

        if (desired && published)
        {
            CameraVideoActive = true;
            EnsureSceneFaceTrackingForVoice();
            yield break;
        }

        if (!desired && published)
        {
            if (CameraVideoActive)
            {
                yield return PostBridgeJson("/api/camera/start", "Camera video desired state restored.");
                EnsureSceneFaceTrackingForVoice();
            }
            else
            {
                yield return PostBridgeJson("/api/camera/stop", "Camera video stopped.", "{\"force\":true,\"source\":\"unity_reconcile_desired_off\"}");
                yield return WaitForBridgeCameraPublished(false, 2f);
                yield return new WaitForSecondsRealtime(0.45f);
                RestoreSceneFaceTrackerExclusiveCamera();
            }

            yield break;
        }

        if (!desired && !published)
        {
            if (CameraVideoActive)
            {
                yield return PostBridgeJson("/api/camera/start", "Camera video requested.");
                EnsureSceneFaceTrackingForVoice();
            }
            else
            {
                yield return new WaitForSecondsRealtime(0.45f);
                RestoreSceneFaceTrackerExclusiveCamera();
            }

            yield break;
        }

        if (desired && !published)
        {
            if (CameraVideoActive)
            {
                EnsureSceneFaceTrackingForVoice();
                SetStatus("Camera video requested; waiting for publish.");
            }
            else
            {
                SetStatus("External camera video request waiting for publish.");
            }
        }
    }

    private void SetLocalVoicePresentationState(string state)
    {
        PetStateController controller = FindAnyObjectByType<PetStateController>();
        if (controller != null)
        {
            controller.SetState(state, null);
        }
    }

    private IEnumerator WaitForBridgeCameraPublished(bool expectedPublished, float timeoutSeconds)
    {
        yield return WaitForBridgeCameraPublished(expectedPublished, timeoutSeconds, null);
    }

    private IEnumerator WaitForBridgeCameraPublished(bool expectedPublished, float timeoutSeconds, Action<bool> onCompleted)
    {
        if (!IsBridgeRunning)
        {
            onCompleted?.Invoke(false);
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, timeoutSeconds);
        bool matched = false;
        while (Time.realtimeSinceStartup < deadline)
        {
            bool statusRead = false;
            bool published = false;
            yield return ReadBridgeCameraPublished(value =>
            {
                statusRead = true;
                published = value;
            });

            if (statusRead && published == expectedPublished)
            {
                matched = true;
                onCompleted?.Invoke(true);
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.15f);
        }

        onCompleted?.Invoke(matched);
    }

    private IEnumerator ReadBridgeCameraPublished(Action<bool> onRead)
    {
        yield return ReadBridgeCameraStatus((_, published) => onRead?.Invoke(published));
    }

    private IEnumerator ReadBridgeCameraStatus(Action<bool, bool> onRead)
    {
        RouteInfo route = ActiveRoute();
        string url = "http://127.0.0.1:" + route.BridgePort + "/api/camera/status";
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 2;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success ||
                request.responseCode < 200 ||
                request.responseCode >= 300)
            {
                yield break;
            }

            CameraStatusResponse status = JsonUtility.FromJson<CameraStatusResponse>(request.downloadHandler.text);
            onRead?.Invoke(status != null && status.desired, status != null && status.cameraPublished);
        }
    }

    private TransparentPetSceneFaceTracker ResolveSceneFaceTracker()
    {
        if (sceneFaceTracker == null)
        {
            sceneFaceTracker = GetComponent<TransparentPetSceneFaceTracker>();
        }

        if (sceneFaceTracker == null)
        {
            sceneFaceTracker = FindAnyObjectByType<TransparentPetSceneFaceTracker>();
        }

        return sceneFaceTracker;
    }

    private IEnumerator FetchCustomLlmModelsRoutine()
    {
        if (!ValidateCustomLlmEndpointForModels(out string validationStatus))
        {
            SetCustomLlmStatus(validationStatus);
            _llmModelsRoutine = null;
            yield break;
        }

        CustomLlmModelsLoading = true;
        _customLlmModels.Clear();
        string modelsUrl = BuildModelsUrl(customLlmUrl);
        SetCustomLlmStatus("Fetching model list...");

        UnityWebRequest request = UnityWebRequest.Get(modelsUrl);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Authorization", "Bearer " + customLlmApiKey.Trim());
        request.timeout = Mathf.Clamp(customLlmPingTimeoutSec, 3, 60);

        yield return request.SendWebRequest();

        bool success = request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300;
        long responseCode = request.responseCode;
        string responseText = request.downloadHandler != null ? request.downloadHandler.text : "";
        string errorText = request.error;
        request.Dispose();

        CustomLlmModelsLoading = false;
        _llmModelsRoutine = null;

        if (!success)
        {
            SetCustomLlmStatus("Model list failed: HTTP " + responseCode + " " + TrimForStatus(FirstNonEmpty(responseText, errorText)));
            yield break;
        }

        List<string> models = ParseModelIds(responseText);
        if (models.Count == 0)
        {
            SetCustomLlmStatus("Model list returned no model ids.");
            yield break;
        }

        _customLlmModels.AddRange(models);
        if (string.IsNullOrWhiteSpace(customLlmModelName) || !_customLlmModels.Contains(customLlmModelName.Trim()))
        {
            customLlmModelName = _customLlmModels[0];
        }

        SetCustomLlmStatus("Loaded " + _customLlmModels.Count + " models. Selected: " + customLlmModelName);
    }

    private IEnumerator TestAndApplyCustomLlmProviderRoutine()
    {
        if (!ValidateCustomLlmConfig(out string validationStatus))
        {
            SetCustomLlmStatus(validationStatus);
            _llmTestRoutine = null;
            yield break;
        }

        CustomLlmTestRunning = true;
        SetCustomLlmStatus("Testing LLM API...");

        string endpoint = NormalizeOpenAiCompatibleUrl(customLlmUrl);
        byte[] body = Encoding.UTF8.GetBytes(BuildLlmPingBody(customLlmModelName.Trim()));
        UnityWebRequest request = new UnityWebRequest(endpoint, "POST");
        request.uploadHandler = new UploadHandlerRaw(body);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", "Bearer " + customLlmApiKey.Trim());
        request.timeout = Mathf.Clamp(customLlmPingTimeoutSec, 3, 60);

        yield return request.SendWebRequest();

        bool success = request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300;
        long responseCode = request.responseCode;
        string responseText = request.downloadHandler != null ? request.downloadHandler.text : "";
        string errorText = request.error;
        request.Dispose();

        CustomLlmTestRunning = false;
        _llmTestRoutine = null;

        if (!success)
        {
            SetCustomLlmStatus("API test failed: HTTP " + responseCode + " " + TrimForStatus(FirstNonEmpty(responseText, errorText)));
            yield break;
        }

        if (!SaveCustomLlmProviderToConfig(out string saveStatus))
        {
            SetCustomLlmStatus("API ping passed; save failed: " + saveStatus);
            yield break;
        }

        bool hadRunningVoice = IsBridgeRunning || IsRuntimeRunning;
        if (hadRunningVoice)
        {
            StopVoiceProcesses();
        }

        SetCustomLlmStatus("API ping passed; " + saveStatus + (hadRunningVoice ? " Voice bridge reset." : ""));
    }

    private void StartBridgeIfNeeded(RouteInfo route)
    {
        SyncPresentationPortFromControlServer();
        string scriptPath = ResolveRuntimePath(route.BridgeScriptPath);
        string configPath = ResolveRuntimePath(route.ConfigPath);
        string key = scriptPath + "|" + configPath + "|" + route.BridgePort + "|" + presentationRoute + "|" + presentationPort;

        if (IsBridgeRunning && _bridgeKey == key)
        {
            return;
        }

        StopProcess(ref _runtimeProcess);
        StopProcess(ref _bridgeProcess);
        StopOrphanVoiceProcesses(route);

        if (!File.Exists(scriptPath))
        {
            SetStatus("Voice bridge script missing: " + scriptPath);
            return;
        }

        if (!File.Exists(configPath))
        {
            SetStatus("Voice config missing: " + configPath);
            return;
        }

        if (TryGetMissingCloudConfigStatus(route, configPath, out string cloudConfigStatus))
        {
            SetStatus(cloudConfigStatus);
            return;
        }

        string arguments =
            Quote(scriptPath) +
            " --config " + Quote(configPath) +
            " --port " + route.BridgePort +
            " --godot-port " + route.GodotPosePort +
            " --presentation-route " + Quote(presentationRoute) +
            " --presentation-host " + Quote(presentationHost) +
            " --presentation-port " + presentationPort;

        _bridgeProcess = StartProcess(pythonExecutable, arguments, "Voice bridge", ProjectRoot);
        if (IsBridgeRunning)
        {
            _bridgeKey = key;
        }
    }

    private void SyncPresentationPortFromControlServer()
    {
        PetControlServer server = FindAnyObjectByType<PetControlServer>();
        if (server == null)
        {
            return;
        }

        server.StartServer();
        if (server.port > 0 && presentationPort != server.port)
        {
            presentationPort = server.port;
            _bridgeKey = "";
        }
    }

    private void StartRuntimeWindow(RouteInfo route, bool requestVision)
    {
        if (IsRuntimeRunning)
        {
            SetStatus("Voice runtime is already running.");
            return;
        }

        string query = autoStartUrl ? "?autostart=1" : "";
        if (requestVision)
        {
            query += string.IsNullOrEmpty(query) ? "?vision=1" : "&vision=1";
        }

        string url = "http://127.0.0.1:" + route.BridgePort + "/" + query;
        if (preferChromeRtcRuntime && TryStartChromeRuntime(url, route))
        {
            return;
        }

        string exe = ResolveRuntimePath(runtimeExePath);
        if (!File.Exists(exe))
        {
            SetStatus("Voice runtime exe missing: " + exe);
            return;
        }

        string arguments = "--url " + Quote(url);
        if (!showRuntimeWindow)
        {
            arguments += " --hidden";
        }

        _runtimeProcess = StartProcess(exe, arguments, "Voice runtime", Path.GetDirectoryName(exe));
        SetStatus((showRuntimeWindow ? "Voice runtime window started: " : "Voice runtime hidden in background: ") + FriendlyRouteName(route.Id));
    }

    private bool TryStartChromeRuntime(string url, RouteInfo route)
    {
        string chrome = ResolveChromeExecutable();
        if (string.IsNullOrWhiteSpace(chrome) || !File.Exists(chrome))
        {
            return false;
        }

        string profileName = string.IsNullOrWhiteSpace(chromeRuntimeProfileName)
            ? "SilverWolfRtcRuntime"
            : SanitizeFileName(chromeRuntimeProfileName);
        string profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "voicechatpet",
            profileName);
        Directory.CreateDirectory(profilePath);

        string arguments =
            "--new-window " +
            "--app=" + Quote(url) + " " +
            "--user-data-dir=" + Quote(profilePath) + " " +
            "--no-first-run " +
            "--no-default-browser-check " +
            "--disable-features=Translate,MediaRouter,CalculateNativeWinOcclusion " +
            "--disable-background-timer-throttling " +
            "--disable-backgrounding-occluded-windows " +
            "--disable-renderer-backgrounding " +
            "--autoplay-policy=no-user-gesture-required " +
            "--use-fake-ui-for-media-stream " +
            "--enable-usermedia-screen-capturing " +
            "--allow-http-screen-capture " +
            "--auto-select-desktop-capture-source=" + Quote("Entire screen") + " " +
            "--video-capture-use-gpu-memory-buffer";
        if (!showRuntimeWindow)
        {
            arguments += " --window-position=-10000,-10000 --window-size=640,620";
        }

        _runtimeProcess = StartProcess(chrome, arguments, "Chrome RTC runtime", ProjectRoot);
        if (!IsRuntimeRunning)
        {
            return false;
        }

        SetStatus((showRuntimeWindow ? "Chrome RTC runtime started: " : "Chrome RTC runtime hidden in background: ") + FriendlyRouteName(route.Id));
        return true;
    }

    private string ResolveChromeExecutable()
    {
        if (!string.IsNullOrWhiteSpace(chromeExecutablePath))
        {
            string configured = Environment.ExpandEnvironmentVariables(chromeExecutablePath.Trim());
            if (File.Exists(configured))
            {
                return configured;
            }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string[] candidates =
        {
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe")
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private static string SanitizeFileName(string value)
    {
        string cleaned = value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "SilverWolfRtcRuntime" : cleaned;
    }

    private void SaveStreamLimitsAndApplyToBridge()
    {
        NormalizeStreamLimits();
        SaveStreamLimitSettings();
        if (Application.isPlaying && IsBridgeRunning)
        {
            StartCoroutine(PostBridgeJson("/api/vision/settings", "Screen stream settings applied.", BuildScreenVisionSettingsBody()));
            StartCoroutine(PostBridgeJson("/api/camera/settings", "Camera stream settings applied.", BuildCameraVideoSettingsBody()));
        }
    }

    private void EnsureCloudVisibleCameraOverlayForCameraVideo()
    {
        if (!ActiveRoute().SupportsVision || screenCameraOverlayEnabled)
        {
            return;
        }

        screenCameraOverlayEnabled = true;
        NormalizeStreamLimits();
        SaveStreamLimitSettings();
    }

    private void NormalizeStreamLimits()
    {
        screenVisionWidth = Mathf.Clamp(screenVisionWidth, 640, 3840);
        screenVisionHeight = Mathf.Clamp(screenVisionHeight, 360, 2160);
        screenVisionFps = Mathf.Clamp(screenVisionFps, 1, 30);
        screenVisionMaxKbps = Mathf.Clamp(screenVisionMaxKbps, 500, 12000);
        screenCameraOverlayWidth = Mathf.Clamp(screenCameraOverlayWidth, 320, 1280);
        screenCameraOverlayHeight = Mathf.RoundToInt(screenCameraOverlayWidth * 9f / 16f);
        screenCameraOverlayHeight = Mathf.Clamp(screenCameraOverlayHeight, 180, 720);
        screenCameraOverlayPadding = Mathf.Clamp(screenCameraOverlayPadding, 0, 160);
        cameraVideoWidth = Mathf.Clamp(cameraVideoWidth, 640, 1920);
        cameraVideoHeight = Mathf.Clamp(cameraVideoHeight, 480, 1080);
        if (cameraVideoHeight >= 480 && cameraVideoWidth < 854)
        {
            cameraVideoWidth = 854;
        }
        cameraVideoFps = Mathf.Clamp(cameraVideoFps, 5, 60);
        cameraVideoMaxKbps = Mathf.Clamp(cameraVideoMaxKbps, 500, 6000);
        faceTrackingPacketFps = Mathf.Clamp(faceTrackingPacketFps, 2, 30);
        if (string.IsNullOrWhiteSpace(cameraVideoHubUrl))
        {
            cameraVideoHubUrl = "http://127.0.0.1:17863/stream.mjpg";
        }
        if (string.IsNullOrWhiteSpace(cameraVideoDeviceKeyword))
        {
            cameraVideoDeviceKeyword = "virtual,obs";
        }
        EnforceSceneCameraHubOwnership();
    }

    private void EnforceSceneCameraHubOwnership()
    {
        TransparentPetSceneFaceTracker tracker = ResolveSceneFaceTracker();
        if (tracker == null || !tracker.IsSceneRoute || !tracker.ExternalFrameServerEnabled)
        {
            return;
        }

        if (cameraVideoUseVirtualCamera || cameraVideoRequireVirtualCamera)
        {
            cameraVideoUseVirtualCamera = true;
            cameraVideoUseCameraHub = false;
            cameraVideoSendFaceTrackingPackets = false;
            return;
        }

        cameraVideoUseCameraHub = true;
        cameraVideoSendFaceTrackingPackets = false;

        if (!string.IsNullOrWhiteSpace(tracker.CameraHubStreamUrl))
        {
            cameraVideoHubUrl = tracker.CameraHubStreamUrl;
        }
    }

    private void LoadStreamLimitSettings()
    {
        if (string.IsNullOrWhiteSpace(streamLimitSettingsKey) || !PlayerPrefs.HasKey(streamLimitSettingsKey))
        {
            return;
        }

        try
        {
            StreamLimitSettings settings = JsonUtility.FromJson<StreamLimitSettings>(PlayerPrefs.GetString(streamLimitSettingsKey));
            screenVisionWidth = settings.screenVisionWidth > 0 ? settings.screenVisionWidth : screenVisionWidth;
            screenVisionHeight = settings.screenVisionHeight > 0 ? settings.screenVisionHeight : screenVisionHeight;
            screenVisionFps = settings.screenVisionFps > 0 ? settings.screenVisionFps : screenVisionFps;
            screenVisionMaxKbps = settings.screenVisionMaxKbps > 0 ? settings.screenVisionMaxKbps : screenVisionMaxKbps;
            if (settings.settingsVersion >= 7)
            {
                screenCameraOverlayEnabled = settings.screenCameraOverlayEnabled;
                screenCameraOverlayWidth = settings.screenCameraOverlayWidth > 0 ? settings.screenCameraOverlayWidth : screenCameraOverlayWidth;
                screenCameraOverlayHeight = settings.screenCameraOverlayHeight > 0 ? settings.screenCameraOverlayHeight : screenCameraOverlayHeight;
                screenCameraOverlayPadding = settings.screenCameraOverlayPadding >= 0 ? settings.screenCameraOverlayPadding : screenCameraOverlayPadding;
            }
            cameraVideoWidth = settings.cameraVideoWidth > 0 ? settings.cameraVideoWidth : cameraVideoWidth;
            cameraVideoHeight = settings.cameraVideoHeight > 0 ? settings.cameraVideoHeight : cameraVideoHeight;
            cameraVideoFps = settings.cameraVideoFps > 0 ? settings.cameraVideoFps : cameraVideoFps;
            cameraVideoMaxKbps = settings.cameraVideoMaxKbps > 0 ? settings.cameraVideoMaxKbps : cameraVideoMaxKbps;
            if (settings.settingsVersion < 4 &&
                cameraVideoWidth <= 854 &&
                cameraVideoHeight <= 480 &&
                cameraVideoMaxKbps <= 1500)
            {
                cameraVideoWidth = 1280;
                cameraVideoHeight = 720;
                cameraVideoMaxKbps = 3000;
            }
            faceTrackingPacketFps = settings.faceTrackingPacketFps > 0 ? settings.faceTrackingPacketFps : faceTrackingPacketFps;
            if (settings.settingsVersion < StreamLimitSettingsVersion)
            {
                MigrateLegacyAggressiveStreamSettings();
            }
            if (settings.settingsVersion >= 2)
            {
                cameraVideoUseVirtualCamera = settings.cameraVideoUseVirtualCamera;
                cameraVideoRequireVirtualCamera = settings.cameraVideoRequireVirtualCamera;
                cameraVideoSendFaceTrackingPackets = settings.cameraVideoSendFaceTrackingPackets;
                cameraVideoDeviceKeyword = string.IsNullOrWhiteSpace(settings.cameraVideoDeviceKeyword)
                    ? cameraVideoDeviceKeyword
                    : settings.cameraVideoDeviceKeyword;
            }
            if (settings.settingsVersion >= 3)
            {
                cameraVideoUseCameraHub = settings.cameraVideoUseCameraHub;
                cameraVideoHubUrl = string.IsNullOrWhiteSpace(settings.cameraVideoHubUrl)
                    ? cameraVideoHubUrl
                    : settings.cameraVideoHubUrl;
            }
            else
            {
                cameraVideoUseCameraHub = true;
                cameraVideoUseVirtualCamera = false;
                cameraVideoRequireVirtualCamera = false;
                cameraVideoSendFaceTrackingPackets = false;
            }

        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to load transparent pet stream limits: " + exception.Message);
        }
    }

    private void MigrateLegacyAggressiveStreamSettings()
    {
        if (screenVisionWidth >= 1920 || screenVisionHeight >= 1080)
        {
            screenVisionWidth = 1280;
            screenVisionHeight = 720;
            screenVisionFps = Mathf.Min(screenVisionFps, 10);
            screenVisionMaxKbps = Mathf.Min(screenVisionMaxKbps, 1200);
        }
        else
        {
            screenVisionFps = Mathf.Min(screenVisionFps, 15);
            screenVisionMaxKbps = Mathf.Min(screenVisionMaxKbps, 1800);
        }

        if (cameraVideoWidth >= 1280 && cameraVideoHeight >= 720)
        {
            cameraVideoFps = Mathf.Min(cameraVideoFps, 10);
            cameraVideoMaxKbps = Mathf.Min(cameraVideoMaxKbps, 1000);
        }
        else
        {
            cameraVideoFps = Mathf.Min(cameraVideoFps, 15);
            cameraVideoMaxKbps = Mathf.Min(cameraVideoMaxKbps, 1000);
        }

        faceTrackingPacketFps = Mathf.Min(faceTrackingPacketFps, 8);
    }

    private void SaveStreamLimitSettings()
    {
        if (string.IsNullOrWhiteSpace(streamLimitSettingsKey))
        {
            return;
        }

        StreamLimitSettings settings = new StreamLimitSettings
        {
            settingsVersion = StreamLimitSettingsVersion,
            screenVisionWidth = screenVisionWidth,
            screenVisionHeight = screenVisionHeight,
            screenVisionFps = screenVisionFps,
            screenVisionMaxKbps = screenVisionMaxKbps,
            screenCameraOverlayEnabled = screenCameraOverlayEnabled,
            screenCameraOverlayWidth = screenCameraOverlayWidth,
            screenCameraOverlayHeight = screenCameraOverlayHeight,
            screenCameraOverlayPadding = screenCameraOverlayPadding,
            cameraVideoWidth = cameraVideoWidth,
            cameraVideoHeight = cameraVideoHeight,
            cameraVideoFps = cameraVideoFps,
            cameraVideoMaxKbps = cameraVideoMaxKbps,
            faceTrackingPacketFps = faceTrackingPacketFps,
            cameraVideoUseCameraHub = cameraVideoUseCameraHub,
            cameraVideoHubUrl = ResolveCameraHubStreamUrl(),
            cameraVideoUseVirtualCamera = cameraVideoUseVirtualCamera,
            cameraVideoRequireVirtualCamera = cameraVideoRequireVirtualCamera,
            cameraVideoSendFaceTrackingPackets = cameraVideoSendFaceTrackingPackets,
            cameraVideoDeviceKeyword = cameraVideoDeviceKeyword
        };
        PlayerPrefs.SetString(streamLimitSettingsKey, JsonUtility.ToJson(settings));
        PlayerPrefs.Save();
    }

    private string BuildScreenVisionSettingsBody()
    {
        NormalizeStreamLimits();
        Dictionary<string, object> body = new Dictionary<string, object>
        {
            { "width", screenVisionWidth },
            { "height", screenVisionHeight },
            { "snapshotHeight", screenVisionHeight },
            { "fps", screenVisionFps },
            { "maxKbps", screenVisionMaxKbps },
            { "cameraOverlayEnabled", screenCameraOverlayEnabled },
            { "cameraOverlayWidth", screenCameraOverlayWidth },
            { "cameraOverlayHeight", screenCameraOverlayHeight },
            { "cameraOverlayPadding", screenCameraOverlayPadding },
            { "cameraOverlayPosition", "bottomLeft" },
            { "cameraOverlaySourceUrl", ResolveCameraHubStreamUrl() }
        };
        return TransparentPetJson.Stringify(body, false);
    }

    private string BuildCameraVideoSettingsBody()
    {
        NormalizeStreamLimits();
        Dictionary<string, object> body = new Dictionary<string, object>
        {
            { "width", cameraVideoWidth },
            { "height", cameraVideoHeight },
            { "fps", cameraVideoFps },
            { "maxKbps", cameraVideoMaxKbps },
            { "faceTrackingPacketFps", faceTrackingPacketFps },
            { "useCameraHub", cameraVideoUseCameraHub },
            { "cameraHubUrl", ResolveCameraHubStreamUrl() },
            { "useVirtualCamera", cameraVideoUseVirtualCamera },
            { "requireVirtualCamera", cameraVideoRequireVirtualCamera },
            { "sendFaceTrackingPackets", cameraVideoSendFaceTrackingPackets },
            { "deviceKeyword", cameraVideoDeviceKeyword }
        };
        return TransparentPetJson.Stringify(body, false);
    }

    private string ResolveCameraHubStreamUrl()
    {
        TransparentPetSceneFaceTracker tracker = ResolveSceneFaceTracker();
        if (tracker != null && tracker.ExternalFrameServerEnabled && !string.IsNullOrWhiteSpace(tracker.CameraHubStreamUrl))
        {
            return tracker.CameraHubStreamUrl;
        }

        return string.IsNullOrWhiteSpace(cameraVideoHubUrl)
            ? "http://127.0.0.1:17863/stream.mjpg"
            : cameraVideoHubUrl.Trim();
    }

    private IEnumerator WaitForSceneCameraHubReady(float timeoutSeconds)
    {
        NormalizeStreamLimits();
        if (!cameraVideoUseCameraHub)
        {
            yield break;
        }

        TransparentPetSceneFaceTracker tracker = ResolveSceneFaceTracker();
        if (tracker == null || !tracker.IsSceneRoute || !tracker.ExternalFrameServerEnabled)
        {
            yield break;
        }

        string statusUrl = tracker.CameraHubStatusUrl;
        if (string.IsNullOrWhiteSpace(statusUrl))
        {
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, timeoutSeconds);
        while (Time.realtimeSinceStartup < deadline)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(statusUrl))
            {
                request.timeout = 1;
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success &&
                    IsCameraHubStatusReady(request.downloadHandler != null ? request.downloadHandler.text : ""))
                {
                    SetStatus("Camera Hub ready for RTC camera stream.");
                    yield break;
                }
            }

            if (!tracker.IsStandaloneLocalMediaPipe || !tracker.IsRunning)
            {
                tracker.EnsureStandaloneLocalMediaPipeTemporary("camera hub waits for scene face tracking", true);
            }
            yield return new WaitForSecondsRealtime(0.25f);
        }

        SetStatus("Camera Hub is not ready yet; keeping scene face tracking alive.");
    }

    private static bool IsCameraHubStatusReady(string json)
    {
        try
        {
            Dictionary<string, object> data = TransparentPetJson.AsObject(TransparentPetJson.Parse(json));
            if (data == null)
            {
                return false;
            }

            int frameCount = TransparentPetJson.GetInt(data, "frameCount", 0);
            float frameAge = TransparentPetJson.GetFloat(data, "lastFrameAgeSec", 99f);
            int width = TransparentPetJson.GetInt(data, "width", 0);
            int height = TransparentPetJson.GetInt(data, "height", 0);
            return frameCount > 0 && frameAge < 2.5f && width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerator PostBridgeJson(string path, string successText, string body = "{}")
    {
        RouteInfo route = ActiveRoute();
        string url = "http://127.0.0.1:" + route.BridgePort + path;
        byte[] bytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(body) ? "{}" : body);
        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.uploadHandler = new UploadHandlerRaw(bytes);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = 4;

        yield return request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300)
        {
            SetStatus(successText);
        }
        else
        {
            SetStatus("Bridge request failed: " + path + " " + request.responseCode);
        }

        request.Dispose();
    }

    private IEnumerator DiagnoseVoiceRuntimeRoutine()
    {
        RouteInfo route = ActiveRoute();
        string url = "http://127.0.0.1:" + route.BridgePort + "/api/voice_diagnostics";
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 3;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success ||
                request.responseCode < 200 ||
                request.responseCode >= 300)
            {
                SetStatus("Voice diagnostics failed: " + request.responseCode);
                yield break;
            }

            try
            {
                Dictionary<string, object> root = TransparentPetJson.AsObject(TransparentPetJson.Parse(request.downloadHandler.text));
                SetStatus(BuildVoiceDiagnosticStatus(root));
            }
            catch (Exception ex)
            {
                SetStatus("Voice diagnostics parse failed: " + ex.Message);
            }
        }
    }

    private void PollVoiceHealthIfNeeded()
    {
        if (!monitorVoiceHealth || _voiceHealthRoutine != null)
        {
            return;
        }

        if (Time.unscaledTime < _nextVoiceHealthPollRealtime)
        {
            return;
        }

        RouteInfo route = ActiveRoute();
        if (!route.RequiresRuntimeWindow || !IsBridgeReadyForRequests)
        {
            ResetVoiceHealthWarning();
            _nextVoiceHealthPollRealtime = Time.unscaledTime + Mathf.Max(1f, voiceHealthPollIntervalSeconds);
            return;
        }

        _voiceHealthRoutine = StartCoroutine(ReadVoiceHealthRoutine(route));
    }

    private IEnumerator ReadVoiceHealthRoutine(RouteInfo route)
    {
        _nextVoiceHealthPollRealtime = Time.unscaledTime + Mathf.Max(1f, voiceHealthPollIntervalSeconds);
        string status = "";
        bool warning = false;
        string url = "http://127.0.0.1:" + route.BridgePort + "/api/voice_diagnostics";
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 2;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success ||
                request.responseCode < 200 ||
                request.responseCode >= 300)
            {
                status = "语音链路告警: 诊断接口不可用 " + request.responseCode;
                warning = true;
            }
            else
            {
                try
                {
                    Dictionary<string, object> root = TransparentPetJson.AsObject(TransparentPetJson.Parse(request.downloadHandler.text));
                    if (TryBuildVoiceHealthWarningStatus(root, out status))
                    {
                        warning = true;
                    }
                    else if (_voiceHealthWarningActive)
                    {
                        status = "语音链路恢复正常。";
                    }
                }
                catch (Exception ex)
                {
                    status = "语音链路告警: 诊断解析失败 " + ex.Message;
                    warning = true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            ReportVoiceHealthStatus(status, warning);
            if (!warning)
            {
                ResetVoiceHealthWarning();
            }
        }

        _voiceHealthRoutine = null;
    }

    private bool TryBuildVoiceHealthWarningStatus(Dictionary<string, object> root, out string status)
    {
        status = "";
        if (root == null)
        {
            status = "语音链路告警: 诊断数据为空。";
            return true;
        }

        bool voiceActive = GetNestedBool(root, false, "voiceActive");
        bool botUidPresent = GetNestedBool(root, true, "botUidPresent");
        string state = Convert.ToString(
            GetNestedObject(root, "runtime", "current_state") ??
            GetNestedObject(root, "ai", "lastState") ??
            "unknown",
            System.Globalization.CultureInfo.InvariantCulture);
        float stateAge = GetNestedFloat(root, -1f, "runtime", "current_state_age_sec");
        float aiStateAge = GetNestedFloat(root, -1f, "ai", "lastStateAgeSec");
        bool audioActive = GetNestedBool(root, false, "runtime", "audio_active");
        bool watchdogPending = GetNestedBool(root, false, "speechWatchdog", "pending");
        float lastUserAge = GetNestedFloat(root, -1f, "speechWatchdog", "lastUserAgeSec");
        int pendingExternal = GetNestedInt(root, 0, "externalText", "pendingCount");
        string lastError = Convert.ToString(
            GetNestedObject(root, "externalText", "lastResult", "error") ?? "",
            System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(lastError))
        {
            status = "语音链路告警: 云端回复错误 " + lastError;
            return true;
        }

        float inactiveAge = Mathf.Max(stateAge, aiStateAge);
        if (IsRuntimeRunning && !voiceActive && inactiveAge >= Mathf.Max(4f, voicePendingWarnSeconds))
        {
            status = "语音链路告警: 本地运行中，但云端会话未激活。";
            return true;
        }

        if (voiceActive && !botUidPresent)
        {
            status = "语音链路告警: 云端会话缺少 bot UID。";
            return true;
        }

        if (watchdogPending && lastUserAge >= Mathf.Max(4f, voicePendingWarnSeconds))
        {
            status = "语音链路告警: 用户语音后等待云端回复 " +
                lastUserAge.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                "s。";
            return true;
        }

        if (pendingExternal > 0 && lastUserAge >= Mathf.Max(4f, voicePendingWarnSeconds))
        {
            status = "语音链路告警: 回复队列积压 " + pendingExternal + " 条。";
            return true;
        }

        if (IsPotentiallyStaleVoiceState(state) &&
            stateAge >= Mathf.Max(6f, voiceStaleStateWarnSeconds) &&
            !audioActive)
        {
            status = "语音链路告警: 云端状态停在 " + state + " " +
                stateAge.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                "s，且没有音频输出。";
            return true;
        }

        return false;
    }

    private void ReportVoiceHealthStatus(string text, bool warning)
    {
        if (string.Equals(_lastVoiceHealthStatus, text, StringComparison.Ordinal))
        {
            return;
        }

        _lastVoiceHealthStatus = text;
        _voiceHealthWarningActive = warning;
        SetStatus(text);
    }

    private void ResetVoiceHealthWarning()
    {
        _lastVoiceHealthStatus = "";
        _voiceHealthWarningActive = false;
    }

    private static string BuildVoiceDiagnosticStatus(Dictionary<string, object> root)
    {
        if (root == null)
        {
            return "Voice diagnostics returned empty data.";
        }

        bool voiceActive = GetNestedBool(root, false, "voiceActive");
        string state = Convert.ToString(
            GetNestedObject(root, "runtime", "current_state") ??
            GetNestedObject(root, "ai", "lastState") ??
            "unknown",
            System.Globalization.CultureInfo.InvariantCulture);
        bool audioActive = GetNestedBool(root, false, "runtime", "audio_active");
        bool watchdogPending = GetNestedBool(root, false, "speechWatchdog", "pending");
        int pendingExternal = GetNestedInt(root, 0, "externalText", "pendingCount");
        int resultCount = GetNestedInt(root, 0, "externalText", "resultCount");
        float lastUserAge = GetNestedFloat(root, -1f, "speechWatchdog", "lastUserAgeSec");
        string lastUser = lastUserAge >= 0f
            ? lastUserAge.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : "-";
        string lastError = Convert.ToString(
            GetNestedObject(root, "externalText", "lastResult", "error") ?? "",
            System.Globalization.CultureInfo.InvariantCulture);

        string status = "语音诊断: active=" + (voiceActive ? "是" : "否") +
            " state=" + state +
            " audio=" + (audioActive ? "是" : "否") +
            " watchdog=" + (watchdogPending ? "等待" : "空") +
            " userAge=" + lastUser +
            " external=" + pendingExternal + "/" + resultCount;
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            status += " err=" + lastError;
        }

        return status;
    }

    private IEnumerator WaitForBridgeHttpReady(int bridgePort, float timeoutSeconds, Action<bool> onCompleted)
    {
        float deadline = Time.realtimeSinceStartup + Mathf.Max(0.5f, timeoutSeconds);
        string url = "http://127.0.0.1:" + bridgePort + "/api/config";
        while (Time.realtimeSinceStartup < deadline)
        {
            if (!IsBridgeRunning)
            {
                onCompleted?.Invoke(false);
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 1;
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success &&
                    request.responseCode >= 200 &&
                    request.responseCode < 300)
                {
                    onCompleted?.Invoke(true);
                    yield break;
                }
            }

            yield return new WaitForSecondsRealtime(0.15f);
        }

        onCompleted?.Invoke(false);
    }

    private bool ValidateCustomLlmConfig(out string status)
    {
        if (!ValidateCustomLlmEndpointForModels(out status))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(customLlmModelName) && _customLlmModels.Count > 0)
        {
            customLlmModelName = _customLlmModels[0];
        }

        if (string.IsNullOrWhiteSpace(customLlmModelName))
        {
            status = "Model name is empty.";
            return false;
        }

        status = "";
        return true;
    }

    private bool ValidateCustomLlmEndpointForModels(out string status)
    {
        if (string.IsNullOrWhiteSpace(customLlmUrl))
        {
            status = "Endpoint URL is empty.";
            return false;
        }

        string endpoint = NormalizeOpenAiCompatibleUrl(customLlmUrl);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            status = "Endpoint URL must be http or https.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(customLlmApiKey))
        {
            status = "API key is empty.";
            return false;
        }

        status = "";
        return true;
    }

    private string BuildLlmPingBody(string modelName)
    {
        Dictionary<string, object> body = new Dictionary<string, object>
        {
            { "model", modelName },
            { "stream", false },
            { "max_tokens", 1L },
            {
                "messages",
                new List<object>
                {
                    new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", "ping" }
                    }
                }
            }
        };

        return TransparentPetJson.Stringify(body, false);
    }

    private int SavePersonaPromptToMirroredConfigs(string relativePath, string prompt)
    {
        int writes = 0;
        List<string> paths = ResolveMirroredConfigPaths(relativePath);
        for (int i = 0; i < paths.Count; i++)
        {
            Dictionary<string, object> root = LoadConfigRootForWrite(paths[i]);
            Dictionary<string, object> llm = EnsureNestedObject(root, "StartVoiceChat", "Config", "LLMConfig");
            List<object> systemMessages = null;
            if (TryGetDictionaryValue(llm, "SystemMessages", out object messagesValue))
            {
                systemMessages = TransparentPetJson.AsArray(messagesValue);
            }

            if (systemMessages == null)
            {
                systemMessages = new List<object>();
                llm["SystemMessages"] = systemMessages;
            }

            if (systemMessages.Count == 0)
            {
                systemMessages.Add(prompt);
            }
            else
            {
                systemMessages[0] = prompt;
            }

            WriteConfigRoot(paths[i], root);
            writes++;
        }

        return writes;
    }

    private int SaveCompanionPromptToMirroredConfigs(string relativePath, string prompt)
    {
        int writes = 0;
        List<string> paths = ResolveMirroredConfigPaths(relativePath);
        for (int i = 0; i < paths.Count; i++)
        {
            Dictionary<string, object> root = LoadConfigRootForWrite(paths[i]);
            Dictionary<string, object> companionVision = EnsureNestedObject(root, "CompanionVision");
            companionVision["Prompt"] = prompt;
            WriteConfigRoot(paths[i], root);
            writes++;
        }

        return writes;
    }

    private Dictionary<string, object> LoadConfigRootForWrite(string path)
    {
        Dictionary<string, object> root = TransparentPetJson.AsObject(TransparentPetJson.Parse(File.ReadAllText(path, Encoding.UTF8)));
        if (root == null)
        {
            throw new InvalidDataException("Config root is not an object: " + path);
        }

        return root;
    }

    private static void WriteConfigRoot(string path, Dictionary<string, object> root)
    {
        File.WriteAllText(path, TransparentPetJson.Stringify(root, true) + Environment.NewLine, new UTF8Encoding(false));
    }

    private List<string> ResolveMirroredConfigPaths(string relativePath)
    {
        List<string> paths = new List<string>();
        string normalizedRelative = NormalizeRelativeConfigPath(relativePath);
        AddExistingConfigPath(paths, ResolveRuntimePath(normalizedRelative));

        List<string> roots = ResolvePromptConfigRoots();
        for (int i = 0; i < roots.Count; i++)
        {
            AddExistingConfigPath(paths, Path.Combine(roots[i], normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        }

        return paths;
    }

    private List<string> ResolvePromptConfigRoots()
    {
        List<string> roots = new List<string>();
        AddPromptConfigRoot(roots, ProjectRoot);
        AddPromptConfigRoot(roots, Application.streamingAssetsPath.Length > 0
            ? Path.Combine(Application.streamingAssetsPath, streamingRootRelativePath)
            : "");
        AddPromptConfigRootsFromAncestorChain(roots, Application.dataPath);
        AddPromptConfigRootsFromAncestorChain(roots, ProjectRoot);
        return roots;
    }

    private void AddPromptConfigRootsFromAncestorChain(List<string> roots, string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return;
        }

        DirectoryInfo current = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : Directory.GetParent(startPath);
        int guard = 0;
        while (current != null && guard++ < 10)
        {
            AddPromptConfigRoot(roots, current.FullName);
            AddPromptConfigRoot(roots, Path.Combine(current.FullName, "Assets", "StreamingAssets", streamingRootRelativePath));
            TryAddUnityProjectStreamingRoots(roots, current.FullName);
            current = current.Parent;
        }
    }

    private void TryAddUnityProjectStreamingRoots(List<string> roots, string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            string[] candidates = Directory.GetDirectories(root, "*URP*20260503", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < candidates.Length; i++)
            {
                AddPromptConfigRoot(roots, Path.Combine(candidates[i], "Assets", "StreamingAssets", streamingRootRelativePath));
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Prompt config mirror scan failed: " + ex.Message);
        }
    }

    private static string NormalizeRelativeConfigPath(string path)
    {
        string cleaned = (path ?? string.Empty).Replace('\\', '/');
        if (cleaned.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring("res://".Length);
        }

        return cleaned.TrimStart('/');
    }

    private static void AddPromptConfigRoot(List<string> roots, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch
        {
            return;
        }

        if (!File.Exists(Path.Combine(fullRoot, "config", "voice_routes.json")) &&
            !File.Exists(Path.Combine(fullRoot, "scripts", "run_volc_rtc_web_client.py")))
        {
            return;
        }

        for (int i = 0; i < roots.Count; i++)
        {
            if (string.Equals(roots[i], fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        roots.Add(fullRoot);
    }

    private static void AddExistingConfigPath(List<string> paths, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        string fullPath = Path.GetFullPath(path);
        for (int i = 0; i < paths.Count; i++)
        {
            if (string.Equals(paths[i], fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        paths.Add(fullPath);
    }

    private static Dictionary<string, object> EnsureNestedObject(Dictionary<string, object> root, params string[] path)
    {
        Dictionary<string, object> current = root;
        for (int i = 0; i < path.Length; i++)
        {
            string key = path[i];
            Dictionary<string, object> next = null;
            if (TryGetDictionaryValue(current, key, out object value))
            {
                next = TransparentPetJson.AsObject(value);
            }

            if (next == null)
            {
                next = new Dictionary<string, object>(StringComparer.Ordinal);
                current[key] = next;
            }

            current = next;
        }

        return current;
    }

    private static string NormalizeOpenAiCompatibleUrl(string value)
    {
        string url = (value ?? string.Empty).Trim().TrimEnd('/');
        string lowered = url.ToLowerInvariant();
        if (lowered.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return url;
        }

        if (lowered.EndsWith("/v1", StringComparison.Ordinal) ||
            lowered.EndsWith("/compatible-mode/v1", StringComparison.Ordinal))
        {
            return url + "/chat/completions";
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            string path = uri.AbsolutePath.TrimEnd('/');
            if (string.IsNullOrEmpty(path) || path == "/" ||
                string.Equals(path, "/beta", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(path, @"/v[0-9][A-Za-z0-9_./-]*$", RegexOptions.IgnoreCase))
            {
                return url + "/chat/completions";
            }
        }

        return url;
    }

    private static string BuildModelsUrl(string value)
    {
        string url = (value ?? string.Empty).Trim().TrimEnd('/');
        const string chatPath = "/chat/completions";
        if (url.ToLowerInvariant().EndsWith(chatPath, StringComparison.Ordinal))
        {
            url = url.Substring(0, url.Length - chatPath.Length).TrimEnd('/');
        }

        return url + "/models";
    }

    private static List<string> ParseModelIds(string json)
    {
        List<string> models = new List<string>();
        try
        {
            Dictionary<string, object> root = TransparentPetJson.AsObject(TransparentPetJson.Parse(json));
            if (root == null)
            {
                return models;
            }

            if (TryGetDictionaryValue(root, "data", out object dataValue))
            {
                AddModelIdsFromValue(models, dataValue);
            }

            if (TryGetDictionaryValue(root, "models", out object modelsValue))
            {
                AddModelIdsFromValue(models, modelsValue);
            }

            models.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Model list parse failed: " + ex.Message);
        }

        return models;
    }

    private static void AddModelIdsFromValue(List<string> models, object value)
    {
        List<object> array = TransparentPetJson.AsArray(value);
        if (array == null)
        {
            string single = Convert.ToString(value ?? "", System.Globalization.CultureInfo.InvariantCulture);
            AddModelId(models, single);
            return;
        }

        for (int i = 0; i < array.Count; i++)
        {
            object item = array[i];
            Dictionary<string, object> data = TransparentPetJson.AsObject(item);
            if (data != null && TryGetDictionaryValue(data, "id", out object idValue))
            {
                AddModelId(models, Convert.ToString(idValue ?? "", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            AddModelId(models, Convert.ToString(item ?? "", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void AddModelId(List<string> models, string modelId)
    {
        string cleaned = (modelId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned) || models.Contains(cleaned))
        {
            return;
        }

        models.Add(cleaned);
    }

    private string FormatCustomLlmSummary()
    {
        string model = string.IsNullOrWhiteSpace(customLlmModelName) ? "no model" : customLlmModelName.Trim();
        string host = "";
        if (Uri.TryCreate(customLlmUrl, UriKind.Absolute, out Uri uri))
        {
            host = uri.Host;
        }

        return string.IsNullOrWhiteSpace(host)
            ? model
            : model + " / " + host;
    }

    private void SetCustomLlmStatus(string text)
    {
        CustomLlmStatus = text;
        Debug.Log(text);
    }

    private void SetPromptStatus(string text)
    {
        PromptStatus = text;
        Debug.Log(text);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return "";
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
            {
                return values[i];
            }
        }

        return "";
    }

    private static string TrimForStatus(string text)
    {
        string cleaned = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        if (cleaned.Length > 180)
        {
            return cleaned.Substring(0, 180) + "...";
        }

        return cleaned;
    }

    private void LoadVoiceRoutes()
    {
        string previousRouteId = selectedRouteId;
        _routes.Clear();
        AddFallbackRoutes();

        string path = ResolveRuntimePath(voiceRoutesConfigPath);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            Dictionary<string, object> root = TransparentPetJson.AsObject(TransparentPetJson.Parse(File.ReadAllText(path, Encoding.UTF8)));
            if (root == null)
            {
                return;
            }

            string defaultRoute = TransparentPetJson.GetString(root, "default_route", selectedRouteId);
            Dictionary<string, object> routes = root.ContainsKey("routes")
                ? TransparentPetJson.AsObject(root["routes"])
                : null;
            if (routes == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> pair in routes)
            {
                Dictionary<string, object> data = TransparentPetJson.AsObject(pair.Value);
                RouteInfo fallback = _routes.ContainsKey(pair.Key) ? _routes[pair.Key] : RouteInfo.Default(pair.Key, defaultBridgeScriptPath, defaultBridgePort, defaultGodotPosePort);
                _routes[pair.Key] = RouteInfo.FromJson(pair.Key, data, fallback);
            }

            if (!string.IsNullOrWhiteSpace(previousRouteId) && _routes.ContainsKey(previousRouteId))
            {
                selectedRouteId = previousRouteId;
            }
            else if (_routes.ContainsKey(defaultRoute))
            {
                selectedRouteId = defaultRoute;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Voice routes parse failed: " + ex.Message);
        }
    }

    private void AddFallbackRoutes()
    {
        _routes["s2s_low_latency"] = new RouteInfo
        {
            Id = "s2s_low_latency",
            ConfigPath = "config/volc_start_voice_chat.local.json",
            BridgeScriptPath = defaultBridgeScriptPath,
            BridgePort = defaultBridgePort,
            GodotPosePort = defaultGodotPosePort,
            SupportsVision = false,
            RequiresRuntimeWindow = true
        };
        _routes["traditional_vision"] = new RouteInfo
        {
            Id = "traditional_vision",
            ConfigPath = "config/volc_traditional_voice_chat.local.json",
            BridgeScriptPath = defaultBridgeScriptPath,
            BridgePort = defaultBridgePort,
            GodotPosePort = defaultGodotPosePort,
            SupportsVision = true,
            RequiresRuntimeWindow = true
        };
        _routes["traditional_companion_polling"] = new RouteInfo
        {
            Id = "traditional_companion_polling",
            ConfigPath = "config/volc_traditional_companion_polling.local.json",
            BridgeScriptPath = defaultBridgeScriptPath,
            BridgePort = defaultBridgePort,
            GodotPosePort = defaultGodotPosePort,
            SupportsVision = true,
            RequiresRuntimeWindow = true
        };
        _routes["agent_speaker"] = new RouteInfo
        {
            Id = "agent_speaker",
            ConfigPath = "config/agent_speaker.local.json",
            BridgeScriptPath = "scripts/run_agent_speaker_server.py",
            BridgePort = 17342,
            GodotPosePort = defaultGodotPosePort,
            SupportsVision = false,
            RequiresRuntimeWindow = false
        };
    }

    private string DirectScreenVisionRouteId(string preferredRouteId)
    {
        if (!string.IsNullOrWhiteSpace(preferredRouteId) &&
            _routes.TryGetValue(preferredRouteId, out RouteInfo preferred) &&
            preferred.SupportsVision &&
            !preferredRouteId.StartsWith("s2s", StringComparison.OrdinalIgnoreCase))
        {
            return preferredRouteId;
        }

        if (_routes.ContainsKey(selectedRouteId) &&
            _routes[selectedRouteId].SupportsVision &&
            !selectedRouteId.StartsWith("s2s", StringComparison.OrdinalIgnoreCase) &&
            !IsCompanionPollingRoute(selectedRouteId))
        {
            return selectedRouteId;
        }

        if (_routes.ContainsKey("traditional_vision"))
        {
            return "traditional_vision";
        }

        foreach (RouteInfo route in _routes.Values)
        {
            if (route.SupportsVision && !route.Id.StartsWith("s2s", StringComparison.OrdinalIgnoreCase))
            {
                return route.Id;
            }
        }

        return "";
    }

    private static bool ShouldRequestScreenVision(RouteInfo route)
    {
        return route.SupportsVision && !route.Id.StartsWith("s2s", StringComparison.OrdinalIgnoreCase);
    }

    private RouteInfo ActiveRoute()
    {
        if (_routes.Count == 0)
        {
            LoadVoiceRoutes();
        }

        if (!_routes.TryGetValue(selectedRouteId, out RouteInfo route))
        {
            route = _routes.ContainsKey("traditional_vision") ? _routes["traditional_vision"] : RouteInfo.Default("traditional_vision", defaultBridgeScriptPath, defaultBridgePort, defaultGodotPosePort);
            selectedRouteId = route.Id;
        }

        return route;
    }

    private string ResolveRuntimePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ProjectRoot;
        }

        string cleaned = path.Replace('\\', '/');
        if (cleaned.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring("res://".Length);
        }

        if (Path.IsPathRooted(cleaned))
        {
            return Path.GetFullPath(cleaned);
        }

        return Path.GetFullPath(Path.Combine(ProjectRoot, cleaned.Replace('/', Path.DirectorySeparatorChar)));
    }

    private string ProjectRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(projectRootOverride))
            {
                return Path.GetFullPath(projectRootOverride);
            }

            string streamingRoot = Path.Combine(Application.streamingAssetsPath, streamingRootRelativePath);
            if (File.Exists(Path.Combine(streamingRoot, "scripts", "run_volc_rtc_web_client.py")))
            {
                return streamingRoot;
            }

            DirectoryInfo projectDir = Directory.GetParent(Application.dataPath);
            if (projectDir != null)
            {
                string editorStreamingRoot = Path.Combine(projectDir.FullName, "Assets", "StreamingAssets", streamingRootRelativePath);
                if (File.Exists(Path.Combine(editorStreamingRoot, "scripts", "run_volc_rtc_web_client.py")))
                {
                    return editorStreamingRoot;
                }
            }

            return streamingRoot;
        }
    }

    private Process StartProcess(string executable, string arguments, string label, string workingDirectory)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? ProjectRoot : workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Process process = Process.Start(info);
            Debug.Log(label + " started: " + executable + " " + arguments);
            return process;
        }
        catch (Exception ex)
        {
            SetStatus(label + " failed: " + ex.Message);
            return null;
        }
    }

    private void SetStatus(string text)
    {
        Status = text;
        Debug.Log(text);
    }

    private static bool IsProcessRunning(Process process)
    {
        if (process == null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void StopProcess(ref Process process)
    {
        if (!IsProcessRunning(process))
        {
            process = null;
            return;
        }

        try
        {
            process.Kill();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Failed to stop voice process: " + ex.Message);
        }
        finally
        {
            process = null;
        }
    }

    private void StopOrphanVoiceProcesses(RouteInfo route)
    {
        if (Application.platform != RuntimePlatform.WindowsEditor &&
            Application.platform != RuntimePlatform.WindowsPlayer)
        {
            return;
        }

        try
        {
            string root = Path.GetFullPath(ProjectRoot).TrimEnd('\\', '/');
            string chromeProfile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "voicechatpet",
                SanitizeFileName(string.IsNullOrWhiteSpace(chromeRuntimeProfileName) ? "SilverWolfRtcRuntime" : chromeRuntimeProfileName));
            string command =
                "$ErrorActionPreference='SilentlyContinue';" +
                "$root=" + PowerShellSingleQuote(root) + ";" +
                "$chromeProfile=" + PowerShellSingleQuote(chromeProfile) + ";" +
                "$port=" + route.BridgePort + ";" +
                "foreach($endpoint in @('/api/vision/stop','/api/camera/stop','/api/stop_voice_chat','/api/stop')){" +
                "try{Invoke-WebRequest -UseBasicParsing -Method Post -Uri ('http://127.0.0.1:'+$port+$endpoint) -ContentType 'application/json' -Body '{}' -TimeoutSec 1|Out-Null}catch{}" +
                "};" +
                "Start-Sleep -Milliseconds 250;" +
                "$targets=@();" +
                "$targets+=Get-CimInstance Win32_Process|Where-Object{" +
                "$_.ProcessId -ne $PID -and " +
                "((" +
                "$_.Name -eq 'python.exe' -and " +
                "$_.CommandLine -like '*run_volc_rtc_web_client.py*' -and " +
                "$_.CommandLine -like ('*'+$root+'*')" +
                ") -or (" +
                "$_.Name -eq 'VolcVoiceRuntime.exe' -and " +
                "$_.CommandLine -like ('*'+$root+'*')" +
                ") -or (" +
                "($_.Name -eq 'chrome.exe' -or $_.Name -eq 'msedge.exe') -and " +
                "$_.CommandLine -like ('*'+$chromeProfile+'*')" +
                "))" +
                "};" +
                "$owners=Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue|Select-Object -ExpandProperty OwningProcess -Unique;" +
                "foreach($owner in $owners){" +
                "$p=Get-CimInstance Win32_Process -Filter \"ProcessId=$owner\";" +
                "if($p -and $p.ProcessId -ne $PID -and (($p.Name -eq 'python.exe' -and $p.CommandLine -like '*run_volc_rtc_web_client.py*') -or $p.Name -eq 'VolcVoiceRuntime.exe' -or (($p.Name -eq 'chrome.exe' -or $p.Name -eq 'msedge.exe') -and $p.CommandLine -like ('*'+$chromeProfile+'*')))){$targets+=$p}" +
                "};" +
                "$targets|Sort-Object ProcessId -Unique|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}";

            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + Quote(command),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process cleanup = Process.Start(info))
            {
                if (cleanup != null)
                {
                    if (!cleanup.WaitForExit(6000))
                    {
                        try
                        {
                            cleanup.Kill();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning("Failed to stop voice cleanup helper: " + ex.Message);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Failed to stop orphan voice processes: " + ex.Message);
        }
    }

    private bool TryGetMissingCloudConfigStatus(RouteInfo route, string configPath, out string status)
    {
        status = "";
        try
        {
            Dictionary<string, object> config = LoadMergedConfig(configPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (route.Id == "agent_speaker")
            {
                CollectAgentSpeakerMissingConfig(config, missing);
            }
            else
            {
                CollectVolcVoiceMissingConfig(config, missing);
            }

            if (missing.Count == 0)
            {
                return false;
            }

            status = "Cloud config missing: " + FormatMissingNames(missing, 8) + ".";
            return true;
        }
        catch (Exception ex)
        {
            status = "Voice config parse failed: " + ex.Message;
            return true;
        }
    }

    private Dictionary<string, object> LoadMergedConfig(string configPath, HashSet<string> visited)
    {
        string fullPath = Path.GetFullPath(configPath);
        if (!visited.Add(fullPath))
        {
            return new Dictionary<string, object>();
        }

        Dictionary<string, object> data = TransparentPetJson.AsObject(TransparentPetJson.Parse(File.ReadAllText(fullPath, Encoding.UTF8)));
        if (data == null)
        {
            throw new InvalidDataException("Config root is not an object: " + fullPath);
        }

        string extends = GetOptionalString(data, "Extends");
        if (string.IsNullOrWhiteSpace(extends))
        {
            extends = GetOptionalString(data, "extends");
        }

        data.Remove("Extends");
        data.Remove("extends");

        if (string.IsNullOrWhiteSpace(extends))
        {
            return data;
        }

        string basePath = ResolveConfigReferencePath(fullPath, extends);
        if (!File.Exists(basePath))
        {
            throw new FileNotFoundException("Parent config missing", basePath);
        }

        Dictionary<string, object> merged = LoadMergedConfig(basePath, visited);
        DeepMergeConfig(merged, data);
        return merged;
    }

    private string ResolveConfigReferencePath(string currentConfigPath, string referencePath)
    {
        string cleaned = (referencePath ?? string.Empty).Replace("\\/", "/").Replace('\\', '/');
        if (cleaned.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveRuntimePath(cleaned);
        }

        if (Path.IsPathRooted(cleaned))
        {
            return Path.GetFullPath(cleaned);
        }

        string rootRelative = Path.GetFullPath(Path.Combine(ProjectRoot, cleaned.Replace('/', Path.DirectorySeparatorChar)));
        if (File.Exists(rootRelative))
        {
            return rootRelative;
        }

        string currentDir = Path.GetDirectoryName(currentConfigPath) ?? ProjectRoot;
        return Path.GetFullPath(Path.Combine(currentDir, cleaned.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static void DeepMergeConfig(Dictionary<string, object> target, Dictionary<string, object> source)
    {
        foreach (KeyValuePair<string, object> pair in source)
        {
            if (pair.Value == null)
            {
                target.Remove(pair.Key);
                continue;
            }

            Dictionary<string, object> sourceObject = TransparentPetJson.AsObject(pair.Value);
            if (sourceObject != null &&
                target.TryGetValue(pair.Key, out object targetValue) &&
                TransparentPetJson.AsObject(targetValue) is Dictionary<string, object> targetObject)
            {
                DeepMergeConfig(targetObject, sourceObject);
                continue;
            }

            target[pair.Key] = pair.Value;
        }
    }

    private static void CollectVolcVoiceMissingConfig(Dictionary<string, object> config, HashSet<string> missing)
    {
        AddRequiredValue(config, missing, "OpenAPIAuth.AccessKeyId", "OpenAPIAuth", "AccessKeyId");
        AddRequiredValue(config, missing, "OpenAPIAuth.SecretAccessKey", "OpenAPIAuth", "SecretAccessKey");
        AddRequiredValue(config, missing, "StartVoiceChat.AppId", "StartVoiceChat", "AppId");
        AddRequiredValue(config, missing, "StartVoiceChat.RoomId", "StartVoiceChat", "RoomId");
        AddRequiredValue(config, missing, "ClientRTC.AppKey", "ClientRTC", "AppKey");
        AddRequiredAny(
            config,
            missing,
            "RTC user id",
            new object[] { "StartVoiceChat", "AgentConfig", "TargetUserId", 0 },
            new object[] { "ClientRTC", "UserId" },
            new object[] { "ClientRTC", "UserID" });
        AddRequiredValue(config, missing, "StartVoiceChat.AgentConfig.UserId", "StartVoiceChat", "AgentConfig", "UserId");

        if (GetNestedObject(config, "StartVoiceChat", "Config", "S2SConfig") is Dictionary<string, object>)
        {
            AddRequiredValue(config, missing, "S2S app id", "StartVoiceChat", "Config", "S2SConfig", "ProviderParams", "app", "appid");
            AddRequiredValue(config, missing, "S2S access token", "StartVoiceChat", "Config", "S2SConfig", "ProviderParams", "app", "token");

            if (GetNestedBool(config, false, "StartVoiceChat", "Config", "S2SConfig", "ProviderParams", "dialog", "extra", "enable_volc_websearch"))
            {
                AddRequiredValue(config, missing, "Volc websearch api key", "StartVoiceChat", "Config", "S2SConfig", "ProviderParams", "dialog", "extra", "volc_websearch_api_key");
            }
        }

        object llmModeValue = GetNestedObject(config, "StartVoiceChat", "Config", "LLMConfig", "Mode");
        string llmMode = Convert.ToString(llmModeValue ?? "", System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(llmMode, "CustomLLM", StringComparison.OrdinalIgnoreCase))
        {
            AddRequiredAny(
                config,
                missing,
                "CustomLLM URL",
                new object[] { "StartVoiceChat", "Config", "LLMConfig", "Url" },
                new object[] { "StartVoiceChat", "Config", "LLMConfig", "URL" },
                new object[] { "StartVoiceChat", "Config", "LLMConfig", "url" },
                new object[] { "StartVoiceChat", "Config", "LLMConfig", "Endpoint" });
            AddRequiredAny(
                config,
                missing,
                "CustomLLM APIKey",
                new object[] { "StartVoiceChat", "Config", "LLMConfig", "APIKey" },
                new object[] { "StartVoiceChat", "Config", "LLMConfig", "ApiKey" },
                new object[] { "StartVoiceChat", "Config", "LLMConfig", "api_key" });
        }
        else if (string.Equals(llmMode, "ArkV3", StringComparison.OrdinalIgnoreCase))
        {
            AddRequiredValue(config, missing, "LLMConfig.EndPointId", "StartVoiceChat", "Config", "LLMConfig", "EndPointId");
        }

        if (GetNestedObject(config, "StartVoiceChat", "Config", "ASRConfig") is Dictionary<string, object>)
        {
            AddRequiredValue(config, missing, "ASR AppId", "StartVoiceChat", "Config", "ASRConfig", "ProviderParams", "Credential", "AppId");
            AddRequiredValue(config, missing, "ASR AccessToken", "StartVoiceChat", "Config", "ASRConfig", "ProviderParams", "Credential", "AccessToken");
        }

        if (GetNestedObject(config, "StartVoiceChat", "Config", "TTSConfig") is Dictionary<string, object>)
        {
            AddRequiredValue(config, missing, "TTS AppId", "StartVoiceChat", "Config", "TTSConfig", "ProviderParams", "Credential", "AppId");
            AddRequiredAny(
                config,
                missing,
                "TTS access token",
                new object[] { "StartVoiceChat", "Config", "TTSConfig", "ProviderParams", "Credential", "Token" },
                new object[] { "StartVoiceChat", "Config", "TTSConfig", "ProviderParams", "Credential", "AccessToken" });
            AddRequiredValue(config, missing, "TTS speaker", "StartVoiceChat", "Config", "TTSConfig", "ProviderParams", "VolcanoTTSParameters");
        }
    }

    private static void CollectAgentSpeakerMissingConfig(Dictionary<string, object> config, HashSet<string> missing)
    {
        if (GetNestedBool(config, false, "AgentPlugin", "RequireAuth"))
        {
            AddRequiredValue(config, missing, "AgentPlugin.ApiKey", "AgentPlugin", "ApiKey");
        }
    }

    private static void AddRequiredAny(Dictionary<string, object> root, HashSet<string> missing, string label, params object[][] paths)
    {
        object firstValue = null;
        foreach (object[] path in paths)
        {
            object value = GetNestedObject(root, path);
            if (firstValue == null || string.IsNullOrWhiteSpace(Convert.ToString(firstValue, System.Globalization.CultureInfo.InvariantCulture)))
            {
                firstValue = value;
            }

            if (IsConfiguredValue(value))
            {
                return;
            }
        }

        AddMissingName(missing, firstValue, label);
    }

    private static void AddRequiredValue(Dictionary<string, object> root, HashSet<string> missing, string label, params object[] path)
    {
        object value = GetNestedObject(root, path);
        if (IsConfiguredValue(value))
        {
            return;
        }

        AddMissingName(missing, value, label);
    }

    private static bool IsConfiguredValue(object value)
    {
        if (value == null)
        {
            return false;
        }

        string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (Match match in EnvPlaceholderRegex.Matches(text))
        {
            string envName = match.Groups[1].Value;
            if (!IsEnvResolved(envName))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddMissingName(HashSet<string> missing, object value, string fallbackLabel)
    {
        string text = Convert.ToString(value ?? "", System.Globalization.CultureInfo.InvariantCulture);
        bool added = false;
        foreach (Match match in EnvPlaceholderRegex.Matches(text))
        {
            string envName = match.Groups[1].Value;
            if (!IsEnvResolved(envName))
            {
                missing.Add(envName);
                added = true;
            }
        }

        if (!added || string.IsNullOrWhiteSpace(text))
        {
            missing.Add(fallbackLabel);
        }
    }

    private static bool IsEnvResolved(string envName)
    {
        string value = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !string.Equals(value.Trim(), "${" + envName + "}", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMissingNames(HashSet<string> missing, int maxNames)
    {
        List<string> names = new List<string>(missing);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        int count = Math.Min(maxNames, names.Count);
        string text = string.Join(", ", names.GetRange(0, count).ToArray());
        if (names.Count > count)
        {
            text += " +" + (names.Count - count) + " more";
        }

        return text;
    }

    private static object GetNestedObject(object root, params object[] path)
    {
        object current = root;
        foreach (object segment in path)
        {
            if (segment is string key)
            {
                Dictionary<string, object> dict = TransparentPetJson.AsObject(current);
                if (dict == null || !TryGetDictionaryValue(dict, key, out current))
                {
                    return null;
                }
            }
            else if (segment is int index)
            {
                List<object> list = TransparentPetJson.AsArray(current);
                if (list == null || index < 0 || index >= list.Count)
                {
                    return null;
                }

                current = list[index];
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    private static bool GetNestedBool(Dictionary<string, object> root, bool fallback, params object[] path)
    {
        object value = GetNestedObject(root, path);
        if (value == null)
        {
            return fallback;
        }

        if (value is bool boolValue)
        {
            return boolValue;
        }

        return bool.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out bool parsed)
            ? parsed
            : fallback;
    }

    private static int GetNestedInt(Dictionary<string, object> root, int fallback, params object[] path)
    {
        object value = GetNestedObject(root, path);
        if (value == null)
        {
            return fallback;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        if (value is long longValue)
        {
            if (longValue > int.MaxValue) return int.MaxValue;
            if (longValue < int.MinValue) return int.MinValue;
            return (int)longValue;
        }

        return int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out int parsed)
            ? parsed
            : fallback;
    }

    private static float GetNestedFloat(Dictionary<string, object> root, float fallback, params object[] path)
    {
        object value = GetNestedObject(root, path);
        if (value == null)
        {
            return fallback;
        }

        if (value is float floatValue)
        {
            return floatValue;
        }

        if (value is double doubleValue)
        {
            return (float)doubleValue;
        }

        return float.TryParse(
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float parsed)
            ? parsed
            : fallback;
    }

    private static bool TryGetDictionaryValue(Dictionary<string, object> data, string key, out object value)
    {
        if (data.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (KeyValuePair<string, object> pair in data)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string GetOptionalString(Dictionary<string, object> data, string key)
    {
        if (!TryGetDictionaryValue(data, key, out object value) || value == null)
        {
            return "";
        }

        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int NormalizeCompanionPollingInterval(int seconds)
    {
        if (seconds <= 1) return 1;
        if (seconds <= 3) return 2;
        if (seconds <= 6) return 5;
        if (seconds <= 9) return 8;
        if (seconds <= 12) return 10;
        return 15;
    }

    private static string FriendlyRouteName(string routeId)
    {
        switch (routeId)
        {
            case "s2s_low_latency":
                return "S2S \u4f4e\u5ef6\u8fdf";
            case "traditional_vision":
                return "\u89c6\u89c9\u966a\u73a9";
            case "traditional_companion_polling":
                return "\u966a\u73a9\u8f6e\u8be2";
            case "agent_speaker":
                return "Agent \u5916\u6302\u7aef\u53e3";
            default:
                return string.IsNullOrWhiteSpace(routeId) ? "unknown" : routeId;
        }
    }

    private static bool IsCompanionPollingRoute(string routeId)
    {
        return string.Equals(routeId, "traditional_companion_polling", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPotentiallyStaleVoiceState(string state)
    {
        string normalized = (state ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "thinking" ||
            normalized == "speaking" ||
            normalized == "processing" ||
            normalized == "responding" ||
            normalized == "interrupted";
    }

    private static string Quote(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }

    private static string PowerShellSingleQuote(string value)
    {
        return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
    }

    [Serializable]
    private sealed class CameraStatusResponse
    {
        public bool desired;
        public bool cameraPublished;
    }

    [Serializable]
    private sealed class StreamLimitSettings
    {
        public int settingsVersion = StreamLimitSettingsVersion;
        public int screenVisionWidth = 1280;
        public int screenVisionHeight = 720;
        public int screenVisionFps = 10;
        public int screenVisionMaxKbps = 1200;
        public bool screenCameraOverlayEnabled;
        public int screenCameraOverlayWidth = 640;
        public int screenCameraOverlayHeight = 360;
        public int screenCameraOverlayPadding = 24;
        public int cameraVideoWidth = 1280;
        public int cameraVideoHeight = 720;
        public int cameraVideoFps = 10;
        public int cameraVideoMaxKbps = 1000;
        public int faceTrackingPacketFps = 8;
        public bool cameraVideoUseCameraHub = true;
        public string cameraVideoHubUrl = "http://127.0.0.1:17863/stream.mjpg";
        public bool cameraVideoUseVirtualCamera;
        public bool cameraVideoRequireVirtualCamera;
        public bool cameraVideoSendFaceTrackingPackets;
        public string cameraVideoDeviceKeyword = "virtual,obs";
    }

    private struct RouteInfo
    {
        public string Id;
        public string ConfigPath;
        public string BridgeScriptPath;
        public int BridgePort;
        public int GodotPosePort;
        public bool SupportsVision;
        public bool RequiresRuntimeWindow;

        public static RouteInfo Default(string id, string scriptPath, int bridgePort, int godotPosePort)
        {
            return new RouteInfo
            {
                Id = id,
                ConfigPath = "config/volc_start_voice_chat.local.json",
                BridgeScriptPath = scriptPath,
                BridgePort = bridgePort,
                GodotPosePort = godotPosePort,
                SupportsVision = false,
                RequiresRuntimeWindow = true
            };
        }

        public static RouteInfo FromJson(string id, Dictionary<string, object> data, RouteInfo fallback)
        {
            RouteInfo route = fallback;
            route.Id = id;
            route.ConfigPath = TransparentPetJson.GetString(data, "config_path", fallback.ConfigPath);
            route.BridgeScriptPath = TransparentPetJson.GetString(data, "bridge_script_path", fallback.BridgeScriptPath);
            route.BridgePort = TransparentPetJson.GetInt(data, "bridge_port", fallback.BridgePort);
            route.GodotPosePort = TransparentPetJson.GetInt(data, "godot_pose_port", fallback.GodotPosePort);
            route.SupportsVision = TransparentPetJson.GetBool(data, "supports_vision", fallback.SupportsVision);
            route.RequiresRuntimeWindow = TransparentPetJson.GetBool(data, "requires_runtime_window", fallback.RequiresRuntimeWindow);
            return route;
        }
    }
}
