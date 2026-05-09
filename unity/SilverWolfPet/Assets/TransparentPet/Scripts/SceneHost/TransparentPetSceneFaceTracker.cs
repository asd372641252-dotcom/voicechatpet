using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

public enum TransparentPetFaceTrackingBackend
{
    ExternalMediaPipe = 0,
    UnityWarmBlob = 1
}

public enum TransparentPetFaceTrackingAnchor
{
    Head = 0,
    Eyes = 1
}

public enum TransparentPetCameraSightMode
{
    ModelAxis = 0,
    TrackingAnchor = 1
}

public enum TransparentPetFaceTrackingRuntimeSource
{
    Off = 0,
    StandaloneLocalMediaPipe = 1,
    BridgeCameraPackets = 2,
    ManualExternalPackets = 3
}

[DisallowMultipleComponent]
[DefaultExecutionOrder(13020)]
public sealed class TransparentPetSceneFaceTracker : MonoBehaviour
{
    private const string CanonicalTrackerRootName = "TransparentPetIntegrationRoot";
    private const int ExternalTrackerPortBindAttempts = 8;
    private const int CurrentSettingsVersion = 7;
    private const float StableNormalizedDeadZone = 0.07f;
    private const float StableNormalizedDepthDeadZone = 0.05f;
    private const float StableOffsetSmoothTime = 0.3f;
    private const float StableDepthSmoothTime = 0.32f;
    private const float StableCameraTargetShiftMeters = 0.08f;
    private const float StableCameraDepthShiftMeters = 0.06f;
    private const float StableCameraHeightFollowMeters = 0.55f;
    private const float StableCameraOrbitDeadZoneDegrees = 5f;
    private const float StableCameraOrbitSmoothTime = 0.32f;
    private const float StableHeadYawPoseWeight = 0.22f;
    private const float StableHeadPitchPoseWeight = 0.18f;
    private static TransparentPetSceneFaceTracker _activeSceneTracker;

    public TransparentWindowController windowController;
    public TransparentPetFreeCamera freeCamera;
    public TransparentPetHeadLookAt headLookAt;
    public Camera targetCamera;
    public TransparentPetFaceTrackingBackend trackingBackend = TransparentPetFaceTrackingBackend.ExternalMediaPipe;
    public bool trackingEnabled = true;
    public bool headFollowEnabled = true;
    public bool cameraParallaxEnabled = true;
    public bool cameraOrbitEnabled = true;
    public bool mirrorHorizontal = true;
    public bool mirrorVertical = true;
    public bool startCameraOnEnable = true;
    public bool persistRuntimeSettings = true;
    public string settingsKey = "ScenePet.FaceTracking.v3";
    public int selectedDeviceIndex;
    public int requestedWidth = 1280;
    public int requestedHeight = 720;
    public int requestedFps = 30;

    [Header("External MediaPipe")]
    public bool launchExternalProcess = true;
    public string externalTrackerRoot = "";
    public string externalPythonExecutable = "";
    public string externalTrackerScript = "head_tracker.py";
    public string externalTrackerHost = "127.0.0.1";
    public int externalTrackerPort = 5055;
    public string externalTrackerBackend = "auto";
    public bool externalFrameServerEnabled = true;
    public string externalFrameServerHost = "127.0.0.1";
    public int externalFrameServerPort = 17863;
    [Range(1, 30)]
    public int externalFrameServerFps = 15;
    [Range(35, 95)]
    public int externalFrameJpegQuality = 92;
    public TransparentPetFaceTrackingAnchor trackingAnchor = TransparentPetFaceTrackingAnchor.Head;
    public TransparentPetCameraSightMode cameraSightMode = TransparentPetCameraSightMode.ModelAxis;
    public float externalPacketTimeoutSeconds = 0.5f;
    [Range(3f, 30f)]
    public float externalStartupGraceSeconds = 15f;
    public bool externalPacketStabilizationEnabled = true;
    [Range(0.02f, 0.5f)]
    public float maxExternalOffsetStep = 0.12f;
    [Range(1f, 30f)]
    public float maxExternalAngleStepDegrees = 8f;
    [Range(0.01f, 0.5f)]
    public float maxExternalDepthStep = 0.05f;
    [Range(0f, 0.3f)]
    public float normalizedDepthDeadZone = StableNormalizedDepthDeadZone;
    [Range(0.03f, 0.8f)]
    public float depthSmoothTime = StableDepthSmoothTime;
    [Range(0f, 0.2f)]
    public float cameraDepthShiftMeters = StableCameraDepthShiftMeters;
    [Range(-1.5f, 1.5f)]
    public float cameraYawOrbitStrength = 1f;
    [Range(-1.5f, 1.5f)]
    public float cameraPitchOrbitStrength = 0.35f;
    [Range(0f, 60f)]
    public float maxCameraYawOrbitDegrees = 45f;
    [Range(0f, 35f)]
    public float maxCameraPitchOrbitDegrees = 18f;
    [Range(0f, 10f)]
    public float cameraOrbitDeadZoneDegrees = StableCameraOrbitDeadZoneDegrees;
    [Range(0.03f, 0.8f)]
    public float cameraOrbitSmoothTime = StableCameraOrbitSmoothTime;

    [Header("Unity Fallback Detector")]
    [Range(24, 160)]
    public int detectorSampleResolution = 72;
    [Range(0.03f, 0.5f)]
    public float detectorIntervalSeconds = 0.08f;
    [Range(0.01f, 0.5f)]
    public float minDetectorConfidence = 0.08f;

    [Header("Motion")]
    [Range(0f, 0.3f)]
    public float normalizedDeadZone = StableNormalizedDeadZone;
    [Range(0.03f, 0.8f)]
    public float offsetSmoothTime = StableOffsetSmoothTime;
    [Range(0.05f, 2f)]
    public float faceLostGraceSeconds = 0.8f;
    [Range(1f, 35f)]
    public float headYawStrengthDegrees = 14f;
    [Range(1f, 25f)]
    public float headPitchStrengthDegrees = 8f;
    [Range(0f, 0.2f)]
    public float cameraTargetShiftMeters = StableCameraTargetShiftMeters;
    [Range(0f, 1.5f)]
    public float cameraHeightFollowMeters = StableCameraHeightFollowMeters;

    private WebCamTexture _webCamTexture;
    private Process _externalProcess;
    private UdpClient _udpClient;
    private Thread _udpThread;
    private readonly object _packetLock = new object();
    private volatile bool _udpRunning;
    private bool _settingsLoaded;
    private bool _cameraRequested;
    private bool _hasFace;
    private float _confidence;
    private float _nextDetectTime;
    private float _lastExternalPacketRealtime;
    private float _rawDepthOffset;
    private float _smoothDepthOffset;
    private float _depthVelocity;
    private float _externalYaw;
    private float _externalPitch;
    private float _externalRoll;
    private Vector2 _rawOffset;
    private Vector2 _smoothOffset;
    private Vector2 _offsetVelocity;
    private Vector2 _rawOrbitAngles;
    private Vector2 _smoothOrbitAngles;
    private Vector2 _orbitVelocity;
    private Vector2 _smoothHeadAngles;
    private Vector2 _headAngleVelocity;
    private string _pendingPacketJson;
    private string _receiverError;
    private string _lastExternalStdout;
    private string _lastExternalStderr;
    private string _status = "Face tracking is idle.";
    private bool _suppressSettingsSave;
    private bool _hasAcceptedExternalPacket;
    private Vector2 _lastAcceptedExternalOffset;
    private float _lastAcceptedExternalYaw;
    private float _lastAcceptedExternalPitch;
    private float _lastAcceptedExternalDepth;
    private int _udpReceivedPacketCount;
    private int _externalAcceptedPacketCount;
    private int _externalIgnoredPacketCount;
    private long _lastUdpReceiveTicks;
    private string _lastPacketSource = "";
    private float _nextExternalRestartRealtime;
    private float _lastExternalProcessStartRealtime;
    private float _lastExternalFaceRealtime;
    private int _udpReceiverGeneration;
    private bool _hasReceivedExternalPacketSinceProcessStart;
    private TransparentPetFaceTrackingRuntimeSource _runtimeSource = TransparentPetFaceTrackingRuntimeSource.Off;
    private bool _disabledAsDuplicate;
#if UNITY_EDITOR
    private float _nextEditorDriveLogRealtime;
    private float _nextEditorPacketLogRealtime;
#endif

    public bool IsSceneRoute => ResolveRoute() == TransparentPetRoute.SceneHost;
    public bool UsesExternalTracker => trackingBackend == TransparentPetFaceTrackingBackend.ExternalMediaPipe;
    public bool IsRunning => UsesExternalTracker
        ? _cameraRequested && IsExternalBackendStarted()
        : _webCamTexture != null && _webCamTexture.isPlaying;
    public TransparentPetFaceTrackingRuntimeSource RuntimeSource => _runtimeSource;
    public bool IsBridgePacketReceiver => UsesExternalTracker &&
        _runtimeSource == TransparentPetFaceTrackingRuntimeSource.BridgeCameraPackets &&
        _cameraRequested &&
        _udpThread != null;
    public bool IsStandaloneLocalMediaPipe => UsesExternalTracker &&
        _runtimeSource == TransparentPetFaceTrackingRuntimeSource.StandaloneLocalMediaPipe &&
        _cameraRequested &&
        _externalProcess != null &&
        !_externalProcess.HasExited;
    public float LastExternalPacketAgeSeconds
    {
        get
        {
            return _lastUdpReceiveTicks > 0
                ? (float)((DateTime.UtcNow.Ticks - _lastUdpReceiveTicks) / (double)TimeSpan.TicksPerSecond)
                : -1f;
        }
    }
    public bool HasFreshExternalPacket
    {
        get
        {
            float age = LastExternalPacketAgeSeconds;
            return age >= 0f && age <= Mathf.Max(1.5f, externalPacketTimeoutSeconds * 4f);
        }
    }
    public bool HasFace => _hasFace;
    public float Confidence => _confidence;
    public Vector2 FaceOffset => _smoothOffset;
    public float DepthOffset => _smoothDepthOffset;
    public float ExternalYaw => _externalYaw;
    public float ExternalPitch => _externalPitch;
    public float ExternalRoll => _externalRoll;
    public string Status => _status;
    public bool TrackingEnabled => trackingEnabled;
    public bool HeadFollowEnabled => headFollowEnabled;
    public bool CameraParallaxEnabled => cameraParallaxEnabled;
    public bool CameraOrbitEnabled => cameraOrbitEnabled;
    public bool MirrorHorizontal => mirrorHorizontal;
    public bool MirrorVertical => mirrorVertical;
    public TransparentPetFaceTrackingBackend TrackingBackend => trackingBackend;
    public TransparentPetFaceTrackingAnchor TrackingAnchor => trackingAnchor;
    public TransparentPetCameraSightMode CameraSightMode => cameraSightMode;
    public bool LaunchExternalProcess => launchExternalProcess;
    public bool ExternalFrameServerEnabled => externalFrameServerEnabled;
    public string CameraHubStreamUrl => BuildExternalFrameServerUrl("/stream.mjpg");
    public string CameraHubStatusUrl => BuildExternalFrameServerUrl("/status");
    public int SelectedDeviceIndex => selectedDeviceIndex;
    public int RequestedFps => requestedFps;
    public int RequestedWidth => requestedWidth;
    public int RequestedHeight => requestedHeight;
    public float DetectorFps => detectorIntervalSeconds > 0f ? 1f / detectorIntervalSeconds : 0f;
    public int UdpReceivedPacketCount => _udpReceivedPacketCount;
    public int ExternalAcceptedPacketCount => _externalAcceptedPacketCount;
    public int ExternalIgnoredPacketCount => _externalIgnoredPacketCount;
    public bool ExternalPacketStabilizationEnabled => externalPacketStabilizationEnabled;
    public float MaxExternalOffsetStep => maxExternalOffsetStep;
    public float MaxExternalAngleStepDegrees => maxExternalAngleStepDegrees;
    public float MaxExternalDepthStep => maxExternalDepthStep;
    public float NormalizedDeadZone => normalizedDeadZone;
    public float OffsetSmoothTime => offsetSmoothTime;
    public float HeadYawStrengthDegrees => headYawStrengthDegrees;
    public float HeadPitchStrengthDegrees => headPitchStrengthDegrees;
    public float CameraTargetShiftMeters => cameraTargetShiftMeters;
    public float CameraDepthShiftMeters => cameraDepthShiftMeters;
    public float CameraHeightFollowMeters => cameraHeightFollowMeters;
    public float CameraYawOrbitStrength => cameraYawOrbitStrength;
    public float CameraPitchOrbitStrength => cameraPitchOrbitStrength;
    public float CameraOrbitSmoothTime => cameraOrbitSmoothTime;
    public int CameraDeviceCount
    {
        get
        {
            int unityDevices = WebCamTexture.devices != null ? WebCamTexture.devices.Length : 0;
            return UsesExternalTracker ? Mathf.Max(unityDevices, selectedDeviceIndex + 1) : unityDevices;
        }
    }

    private void Awake()
    {
        if (DisableIfDuplicateRuntimeTracker())
        {
            return;
        }

        ResolveMissingReferences();
        LoadSettings();
    }

    private void OnEnable()
    {
        if (_disabledAsDuplicate || DisableIfDuplicateRuntimeTracker())
        {
            return;
        }

        ResolveMissingReferences();
        LoadSettings();
        if (startCameraOnEnable && IsSceneRoute)
        {
            StartCamera();
        }
    }

    private void OnDisable()
    {
        if (_disabledAsDuplicate)
        {
            return;
        }

        StopCamera();
        ClearDrivenEffects();
    }

    private void OnDestroy()
    {
        if (_activeSceneTracker == this)
        {
            _activeSceneTracker = null;
        }

        if (_disabledAsDuplicate)
        {
            return;
        }

        StopCamera();
        ClearDrivenEffects();
    }

    private void OnValidate()
    {
        NormalizeValues();
    }

    private void Update()
    {
        if (_disabledAsDuplicate)
        {
            return;
        }

        ResolveMissingReferences();
        if (!IsSceneRoute)
        {
            StopCamera();
            ClearDrivenEffects();
            _status = "Face tracking is only available in the scene host route.";
            return;
        }

        if (_cameraRequested && !IsBackendStarted())
        {
            StartCamera();
        }

        UpdateDetection();
        UpdateSmoothedOffset();
    }

    private void LateUpdate()
    {
        if (_disabledAsDuplicate)
        {
            return;
        }

        if (!IsSceneRoute || !trackingEnabled)
        {
            ClearDrivenEffects();
            return;
        }

        ApplyHeadFollow();
        ApplyCameraParallax();
        ApplyCameraOrbit();
    }

    public void StartCamera()
    {
        if (_disabledAsDuplicate)
        {
            return;
        }

        if (!IsSceneRoute)
        {
            _status = "Camera tracking stays disabled outside the scene host route.";
            return;
        }

        _cameraRequested = true;
        NormalizeValues();
        if (UsesExternalTracker)
        {
            if (_runtimeSource == TransparentPetFaceTrackingRuntimeSource.Off)
            {
                _runtimeSource = launchExternalProcess
                    ? TransparentPetFaceTrackingRuntimeSource.StandaloneLocalMediaPipe
                    : TransparentPetFaceTrackingRuntimeSource.ManualExternalPackets;
            }

            StartExternalTracker();
            SaveSettings();
            return;
        }

        _runtimeSource = TransparentPetFaceTrackingRuntimeSource.Off;
        StartUnityCamera();
        SaveSettings();
    }

    public void StartCameraTemporary()
    {
        RunWithoutSavingSettings(StartCamera);
    }

    public void StopCamera()
    {
        _cameraRequested = false;
        _runtimeSource = TransparentPetFaceTrackingRuntimeSource.Off;
        StopUnityCameraInstance();
        StopExternalTracker();
        _hasFace = false;
        _confidence = 0f;
        _rawOffset = Vector2.zero;
        _smoothOffset = Vector2.zero;
        _offsetVelocity = Vector2.zero;
        _rawDepthOffset = 0f;
        _smoothDepthOffset = 0f;
        _depthVelocity = 0f;
        _rawOrbitAngles = Vector2.zero;
        _smoothOrbitAngles = Vector2.zero;
        _orbitVelocity = Vector2.zero;
        _smoothHeadAngles = Vector2.zero;
        _headAngleVelocity = Vector2.zero;
        _externalYaw = 0f;
        _externalPitch = 0f;
        _externalRoll = 0f;
        ResetExternalPacketStabilizer();
        _status = "Camera stopped.";
        ClearDrivenEffects();
    }

    public void StartStandaloneLocalMediaPipe()
    {
        if (!IsSceneRoute)
        {
            _status = "Camera tracking stays disabled outside the scene host route.";
            return;
        }

        trackingEnabled = true;
        trackingBackend = TransparentPetFaceTrackingBackend.ExternalMediaPipe;
        launchExternalProcess = true;
        _runtimeSource = TransparentPetFaceTrackingRuntimeSource.StandaloneLocalMediaPipe;
        ResetTrackingMotionState(true);
        RestartBackend();
        SaveSettings();
    }

    public void EnsureStandaloneLocalMediaPipeTemporary(string reason, bool restartIfPacketsStale)
    {
        RunWithoutSavingSettings(() =>
        {
            bool stalePackets = restartIfPacketsStale &&
                _cameraRequested &&
                !HasFreshExternalPacket;
            if (IsStandaloneLocalMediaPipe && IsRunning && !stalePackets)
            {
                return;
            }

            StartStandaloneLocalMediaPipe();
            if (!string.IsNullOrWhiteSpace(reason))
            {
                LogRuntimeStatus(reason);
            }
        });
    }

    public string BuildRuntimeStatus()
    {
        string processState = _externalProcess == null
            ? "none"
            : (_externalProcess.HasExited ? "exited" : "running");
        float udpAge = _lastUdpReceiveTicks > 0
            ? (float)((DateTime.UtcNow.Ticks - _lastUdpReceiveTicks) / (double)TimeSpan.TicksPerSecond)
            : -1f;
        string cameraState = freeCamera == null
            ? "none"
            : "pos=" + freeCamera.CameraWorldPosition.ToString("F3")
                + " fwd=" + freeCamera.CameraWorldForward.ToString("F3")
                + " target=" + freeCamera.EffectiveTarget.ToString("F3")
                + " yaw=" + freeCamera.CameraYawDegrees.ToString("F1")
                + " pitch=" + freeCamera.CameraPitchDegrees.ToString("F1")
                + " dist=" + freeCamera.distance.ToString("F2")
                + " follow=" + freeCamera.followPlacementTarget
                + " extOrbit=" + (freeCamera.HasExternalOrbitOffset ? freeCamera.ExternalOrbitOffset.ToString("F1") : "none")
                + " extCamera=" + (freeCamera.HasExternalCameraOffset ? freeCamera.ExternalCameraOffset.ToString("F3") : "none");
        return "route=" + ResolveRoute()
            + " enabled=" + trackingEnabled
            + " requested=" + _cameraRequested
            + " backend=" + trackingBackend
            + " source=" + _runtimeSource
            + " udp=" + (_udpThread != null)
            + " udpAlive=" + IsUdpReceiverAlive()
            + " udpCount=" + _udpReceivedPacketCount
            + " accepted=" + _externalAcceptedPacketCount
            + " ignored=" + _externalIgnoredPacketCount
            + " lastUdpAge=" + udpAge.ToString("F2")
            + " lastSource=" + (string.IsNullOrWhiteSpace(_lastPacketSource) ? "none" : _lastPacketSource)
            + " process=" + processState
            + " hasFace=" + _hasFace
            + " yaw=" + _externalYaw.ToString("F1")
            + " pitch=" + _externalPitch.ToString("F1")
            + " depth=" + _smoothDepthOffset.ToString("F3")
            + " camera=" + cameraState
            + " receiverError=" + (string.IsNullOrWhiteSpace(_receiverError) ? "none" : _receiverError)
            + " status=" + _status;
    }

    public void LogRuntimeStatus(string reason)
    {
        Debug.Log("Scene face tracking status"
            + (string.IsNullOrWhiteSpace(reason) ? "" : " [" + reason + "]")
            + " " + BuildRuntimeStatus());
    }

    public void StartBridgePacketReceiverTemporary()
    {
        trackingEnabled = true;
        StartStandaloneLocalMediaPipe();
        _status = "Bridge camera packets are disabled; scene tracking keeps the local camera.";
    }

    public string GetCameraDeviceLabel(int index)
    {
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices != null && index >= 0 && index < devices.Length)
        {
            string deviceName = string.IsNullOrWhiteSpace(devices[index].name) ? "Camera " + index : devices[index].name;
            return index.ToString("00") + "  " + deviceName;
        }

        return UsesExternalTracker
            ? index.ToString("00") + "  OpenCV/MediaPipe Camera " + index
            : "Camera " + index;
    }

    public void SetSelectedDeviceIndex(int value)
    {
        int count = CameraDeviceCount;
        selectedDeviceIndex = count > 0 ? Mathf.Clamp(value, 0, count - 1) : Mathf.Max(0, value);
        SaveSettings();
        if (_cameraRequested || IsRunning)
        {
            RestartBackend();
        }
    }

    public void SetRequestedFps(float value)
    {
        int next = Mathf.Clamp(Mathf.RoundToInt(value), 5, 60);
        if (requestedFps == next)
        {
            return;
        }

        requestedFps = next;
        SaveSettings();
        if (_cameraRequested || IsRunning)
        {
            RestartBackend();
        }
    }

    public void SetRequestedResolution(int width, int height)
    {
        int nextWidth = Mathf.Clamp(width, 160, 1920);
        int nextHeight = Mathf.Clamp(height, 120, 1080);
        if (requestedWidth == nextWidth && requestedHeight == nextHeight)
        {
            return;
        }

        requestedWidth = nextWidth;
        requestedHeight = nextHeight;
        SaveSettings();
        if (_cameraRequested || IsRunning)
        {
            RestartBackend();
        }
    }

    public void SetDetectorFps(float value)
    {
        int fps = Mathf.Clamp(Mathf.RoundToInt(value), 2, 30);
        detectorIntervalSeconds = 1f / fps;
        SaveSettings();
    }

    public void SetExternalPacketStabilizationEnabled(bool value)
    {
        externalPacketStabilizationEnabled = value;
        ResetExternalPacketStabilizer();
        SaveSettings();
    }

    public void SetMaxExternalOffsetStep(float value)
    {
        maxExternalOffsetStep = Mathf.Clamp(value, 0.02f, 0.5f);
        SaveSettings();
    }

    public void SetMaxExternalAngleStepDegrees(float value)
    {
        maxExternalAngleStepDegrees = Mathf.Clamp(value, 1f, 30f);
        SaveSettings();
    }

    public void SetMaxExternalDepthStep(float value)
    {
        maxExternalDepthStep = Mathf.Clamp(value, 0.01f, 0.5f);
        SaveSettings();
    }

    public void SetTrackingBackend(TransparentPetFaceTrackingBackend value)
    {
        if (_runtimeSource == TransparentPetFaceTrackingRuntimeSource.BridgeCameraPackets &&
            value != TransparentPetFaceTrackingBackend.ExternalMediaPipe)
        {
            _status = "Camera stream owns the camera. Stop camera streaming before switching detector backend.";
            return;
        }

        if (trackingBackend == value)
        {
            return;
        }

        trackingBackend = value;
        SaveSettings();
        if (_cameraRequested || IsRunning)
        {
            RestartBackend();
        }
    }

    public void SetTrackingBackendTemporary(TransparentPetFaceTrackingBackend value)
    {
        RunWithoutSavingSettings(() =>
        {
            if (trackingBackend == value)
            {
                return;
            }

            trackingBackend = value;
            if (_cameraRequested || IsRunning)
            {
                RestartBackend();
            }
        });
    }

    public void SetLaunchExternalProcess(bool value)
    {
        launchExternalProcess = value;
        SaveSettings();
        if (UsesExternalTracker && (_cameraRequested || IsRunning))
        {
            RestartBackend();
        }
    }

    public void SetLaunchExternalProcessTemporary(bool value)
    {
        RunWithoutSavingSettings(() =>
        {
            if (launchExternalProcess == value)
            {
                return;
            }

            launchExternalProcess = value;
            if (UsesExternalTracker && (_cameraRequested || IsRunning))
            {
                RestartBackend();
            }
        });
    }

    public void SetTrackingEnabled(bool value)
    {
        trackingEnabled = value;
        if (trackingEnabled && !IsRunning)
        {
            StartCamera();
        }
        else if (!trackingEnabled)
        {
            ClearDrivenEffects();
        }

        SaveSettings();
    }

    public void SetTrackingEnabledWithoutStarting(bool value)
    {
        trackingEnabled = value;
        if (!trackingEnabled)
        {
            ClearDrivenEffects();
        }

        SaveSettings();
    }

    public void SetHeadFollowEnabled(bool value)
    {
        headFollowEnabled = value;
        if (!headFollowEnabled)
        {
            headLookAt?.ClearExternalAdditiveLookAngles();
        }

        SaveSettings();
    }

    public void SetCameraParallaxEnabled(bool value)
    {
        cameraParallaxEnabled = value;
        if (!cameraParallaxEnabled)
        {
            RestoreCameraParallaxBase();
        }

        SaveSettings();
    }

    public void SetCameraOrbitEnabled(bool value)
    {
        cameraOrbitEnabled = value;
        if (!cameraOrbitEnabled)
        {
            _rawOrbitAngles = Vector2.zero;
            _smoothOrbitAngles = Vector2.zero;
            _orbitVelocity = Vector2.zero;
            freeCamera?.ClearExternalOrbitOffset();
        }

        SaveSettings();
    }

    public void SetMirrorHorizontal(bool value)
    {
        mirrorHorizontal = value;
        SaveSettings();
    }

    public void SetMirrorVertical(bool value)
    {
        mirrorVertical = value;
        SaveSettings();
    }

    public void SetTrackingAnchor(TransparentPetFaceTrackingAnchor value)
    {
        if (trackingAnchor == value)
        {
            return;
        }

        trackingAnchor = value;
        SaveSettings();
        if (UsesExternalTracker && (_cameraRequested || IsRunning))
        {
            RestartBackend();
        }
    }

    public void SetCameraSightMode(TransparentPetCameraSightMode value)
    {
        if (cameraSightMode == value)
        {
            return;
        }

        cameraSightMode = value;
        if (cameraSightMode == TransparentPetCameraSightMode.ModelAxis)
        {
            freeCamera?.ClearExternalTargetOffset();
        }
        else
        {
            freeCamera?.ClearExternalCameraOffset();
        }

        SaveSettings();
    }

    public void SetNormalizedDeadZone(float value)
    {
        normalizedDeadZone = Mathf.Clamp(value, 0f, 0.3f);
        SaveSettings();
    }

    public void SetOffsetSmoothTime(float value)
    {
        offsetSmoothTime = Mathf.Clamp(value, 0.03f, 0.8f);
        SaveSettings();
    }

    public void SetHeadYawStrengthDegrees(float value)
    {
        headYawStrengthDegrees = Mathf.Clamp(value, 1f, 35f);
        SaveSettings();
    }

    public void SetHeadPitchStrengthDegrees(float value)
    {
        headPitchStrengthDegrees = Mathf.Clamp(value, 1f, 25f);
        SaveSettings();
    }

    public void SetCameraTargetShiftMeters(float value)
    {
        cameraTargetShiftMeters = Mathf.Clamp(value, 0f, 0.2f);
        SaveSettings();
    }

    public void SetCameraHeightFollowMeters(float value)
    {
        cameraHeightFollowMeters = Mathf.Clamp(value, 0f, 1.5f);
        SaveSettings();
    }

    public void SetCameraDepthShiftMeters(float value)
    {
        cameraDepthShiftMeters = Mathf.Clamp(value, 0f, 0.2f);
        SaveSettings();
    }

    public void SetCameraYawOrbitStrength(float value)
    {
        cameraYawOrbitStrength = Mathf.Clamp(value, -1.5f, 1.5f);
        SaveSettings();
    }

    public void SetCameraPitchOrbitStrength(float value)
    {
        cameraPitchOrbitStrength = Mathf.Clamp(value, -1.5f, 1.5f);
        SaveSettings();
    }

    public void SetCameraOrbitSmoothTime(float value)
    {
        cameraOrbitSmoothTime = Mathf.Clamp(value, 0.03f, 0.8f);
        SaveSettings();
    }

    private void ResolveMissingReferences()
    {
        if (windowController == null)
        {
            windowController = GetComponent<TransparentWindowController>();
        }

        if (freeCamera == null)
        {
            freeCamera = FindAnyObjectByType<TransparentPetFreeCamera>();
        }

        if (targetCamera == null)
        {
            targetCamera = freeCamera != null && freeCamera.targetCamera != null
                ? freeCamera.targetCamera
                : Camera.main;
        }

        if (headLookAt == null)
        {
            headLookAt = FindAnyObjectByType<TransparentPetHeadLookAt>();
        }
    }

    private TransparentPetRoute ResolveRoute()
    {
        return windowController != null ? windowController.Route : TransparentPetRoute.DesktopTransparent;
    }

    private bool DisableIfDuplicateRuntimeTracker()
    {
        if (!Application.isPlaying)
        {
            return false;
        }

        TransparentPetSceneFaceTracker canonical = FindCanonicalRuntimeTracker();
        if (canonical != null && canonical != this)
        {
            DisableDuplicateRuntimeTracker(canonical);
            return true;
        }

        if (_activeSceneTracker != null && _activeSceneTracker != this)
        {
            DisableDuplicateRuntimeTracker(_activeSceneTracker);
            return true;
        }

        _activeSceneTracker = this;
        return false;
    }

    private static TransparentPetSceneFaceTracker FindCanonicalRuntimeTracker()
    {
        GameObject root = GameObject.Find(CanonicalTrackerRootName);
        return root != null ? root.GetComponent<TransparentPetSceneFaceTracker>() : null;
    }

    private void DisableDuplicateRuntimeTracker(TransparentPetSceneFaceTracker owner)
    {
        _disabledAsDuplicate = true;
        startCameraOnEnable = false;
        trackingEnabled = false;
        _cameraRequested = false;
        StopUnityCameraInstance();
        StopExternalTracker();
        ClearDrivenEffects();
        enabled = false;
        string ownerName = owner != null ? owner.gameObject.name : "unknown";
        Debug.LogWarning("Disabled duplicate TransparentPetSceneFaceTracker on " + gameObject.name + "; owner is " + ownerName + ".");
    }

    private bool IsBackendStarted()
    {
        if (UsesExternalTracker)
        {
            return IsExternalBackendStarted();
        }

        return _webCamTexture != null;
    }

    private bool IsExternalBackendStarted()
    {
        if (!IsUdpReceiverAlive())
        {
            return false;
        }

        if (_runtimeSource == TransparentPetFaceTrackingRuntimeSource.BridgeCameraPackets)
        {
            return true;
        }

        if (_runtimeSource == TransparentPetFaceTrackingRuntimeSource.StandaloneLocalMediaPipe)
        {
            return _externalProcess != null && !_externalProcess.HasExited;
        }

        return !launchExternalProcess || (_externalProcess != null && !_externalProcess.HasExited);
    }

    private bool IsUdpReceiverAlive()
    {
        return _udpRunning && _udpThread != null && _udpThread.IsAlive && _udpClient != null;
    }

    private bool IsCurrentUdpReceiver(int generation)
    {
        return generation == Interlocked.CompareExchange(ref _udpReceiverGeneration, 0, 0);
    }

    private void RestartBackend()
    {
        StopUnityCameraInstance();
        StopExternalTracker();
        if (_cameraRequested || trackingEnabled)
        {
            StartCamera();
        }
    }

    private void StartUnityCamera()
    {
        StopExternalTracker();

        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            _status = "No camera device found.";
            StopUnityCameraInstance();
            return;
        }

        selectedDeviceIndex = Mathf.Clamp(selectedDeviceIndex, 0, devices.Length - 1);
        StopUnityCameraInstance();

        _webCamTexture = new WebCamTexture(devices[selectedDeviceIndex].name, requestedWidth, requestedHeight, requestedFps);
        _webCamTexture.Play();
        _status = "Unity fallback camera started: " + devices[selectedDeviceIndex].name;
    }

    private void StopUnityCameraInstance()
    {
        if (_webCamTexture == null)
        {
            return;
        }

        if (_webCamTexture.isPlaying)
        {
            _webCamTexture.Stop();
        }

        if (Application.isPlaying)
        {
            Destroy(_webCamTexture);
        }
        else
        {
            DestroyImmediate(_webCamTexture);
        }

        _webCamTexture = null;
    }

    private void StartExternalTracker()
    {
        StopUnityCameraInstance();
        ResetTrackingMotionState(false);
        StartUdpReceiver();
        if (_udpThread == null)
        {
            return;
        }

        if (ShouldLaunchExternalProcess())
        {
            StartExternalProcess();
        }
        else
        {
            _status = _runtimeSource == TransparentPetFaceTrackingRuntimeSource.BridgeCameraPackets
                ? "Listening for bridge camera face packets on UDP " + externalTrackerPort.ToString() + "."
                : "Listening for MediaPipe packets on UDP " + externalTrackerPort.ToString() + ".";
        }
    }

    private bool ShouldLaunchExternalProcess()
    {
        return UsesExternalTracker &&
            launchExternalProcess &&
            _runtimeSource != TransparentPetFaceTrackingRuntimeSource.BridgeCameraPackets;
    }

    private void StopExternalTracker()
    {
        StopExternalProcess();
        StopUdpReceiver();
        lock (_packetLock)
        {
            _pendingPacketJson = null;
        }
    }

    private void StartUdpReceiver()
    {
        if (_udpThread != null || _udpClient != null)
        {
            if (IsUdpReceiverAlive())
            {
                return;
            }

            StopUdpReceiver();
        }

        UdpClient receiver = null;
        try
        {
            _receiverError = "";
            int requestedPort = Mathf.Clamp(externalTrackerPort, 1024, 65535);
            int boundPort = 0;
            Exception bindException = null;
            IPAddress bindAddress = IPAddress.Parse(externalTrackerHost);
            for (int attempt = 0; attempt < ExternalTrackerPortBindAttempts; attempt++)
            {
                int candidatePort = requestedPort + attempt;
                if (candidatePort > 65535)
                {
                    break;
                }

                try
                {
                    receiver = new UdpClient(AddressFamily.InterNetwork);
                    receiver.ExclusiveAddressUse = true;
                    receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                    receiver.Client.ReceiveTimeout = 500;
                    receiver.Client.Bind(new IPEndPoint(bindAddress, candidatePort));
                    boundPort = candidatePort;
                    break;
                }
                catch (Exception exception)
                {
                    bindException = exception;
                    try
                    {
                        receiver?.Close();
                        receiver?.Dispose();
                    }
                    catch
                    {
                        // Ignore failed bind cleanup races.
                    }

                    receiver = null;
                    SocketException socketException = exception as SocketException;
                    if (socketException == null || socketException.SocketErrorCode != SocketError.AddressAlreadyInUse)
                    {
                        break;
                    }
                }
            }

            if (receiver == null || boundPort <= 0)
            {
                throw bindException ?? new InvalidOperationException("No UDP port is available for MediaPipe face tracking.");
            }

            if (boundPort != externalTrackerPort)
            {
                Debug.LogWarning("MediaPipe UDP port " + externalTrackerPort.ToString()
                    + " is busy; switched scene face tracking to " + boundPort.ToString() + ".");
                externalTrackerPort = boundPort;
            }

            int generation = Interlocked.Increment(ref _udpReceiverGeneration);
            _udpClient = receiver;
            _udpRunning = true;
            _udpThread = new Thread(() => ReceiveUdpLoop(receiver, generation))
            {
                IsBackground = true,
                Name = "TransparentPetFaceTrackerUdp"
            };
            _udpThread.Start();
            _lastExternalPacketRealtime = Time.unscaledTime;
        }
        catch (Exception exception)
        {
            _udpRunning = false;
            try
            {
                receiver?.Close();
                receiver?.Dispose();
            }
            catch
            {
                // Ignore startup cleanup races.
            }

            _udpClient = null;
            _udpThread = null;
            _status = "Failed to listen for MediaPipe face tracking: " + exception.Message;
        }
    }

    private void StopUdpReceiver()
    {
        _udpRunning = false;
        Interlocked.Increment(ref _udpReceiverGeneration);
        UdpClient receiver = _udpClient;
        Thread receiverThread = _udpThread;
        _udpClient = null;
        _udpThread = null;

        try
        {
            receiver?.Close();
            receiver?.Dispose();
        }
        catch
        {
            // Ignore shutdown races.
        }

        if (receiverThread != null)
        {
            try
            {
                if (receiverThread.IsAlive && !receiverThread.Join(900))
                {
                    Debug.LogWarning("MediaPipe UDP receiver did not exit within timeout; a new receiver generation will ignore it.");
                }
            }
            catch
            {
                // Ignore shutdown races.
            }
        }
    }

    private void ReceiveUdpLoop(UdpClient receiver, int generation)
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (_udpRunning && IsCurrentUdpReceiver(generation))
        {
            try
            {
                byte[] data = receiver.Receive(ref remote);
                if (!IsCurrentUdpReceiver(generation))
                {
                    break;
                }

                string json = Encoding.UTF8.GetString(data);
                lock (_packetLock)
                {
                    _pendingPacketJson = json;
                    _udpReceivedPacketCount++;
                    _lastUdpReceiveTicks = DateTime.UtcNow.Ticks;
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException exception)
            {
                if (!_udpRunning || !IsCurrentUdpReceiver(generation))
                {
                    break;
                }

                if (exception.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }

                _receiverError = exception.Message;
                Thread.Sleep(40);
            }
            catch (Exception exception)
            {
                if (!_udpRunning || !IsCurrentUdpReceiver(generation))
                {
                    break;
                }

                _receiverError = exception.Message;
                Thread.Sleep(80);
            }
        }
    }

    private void StartExternalProcess()
    {
        if (_externalProcess != null && !_externalProcess.HasExited)
        {
            return;
        }

        string trackerRoot = ResolveExternalTrackerRoot();
        if (string.IsNullOrEmpty(trackerRoot))
        {
            _receiverError = "MediaPipe tracker folder not found. Expected D:\\pet\\head_tracker or StreamingAssets head_tracker.";
            _status = _receiverError;
            return;
        }

        string scriptPath = ResolveExternalTrackerScriptPath(trackerRoot);
        if (!File.Exists(scriptPath))
        {
            _receiverError = "MediaPipe tracker script not found: " + scriptPath;
            _status = _receiverError;
            return;
        }

        StopOrphanExternalTrackerProcesses(scriptPath);
        _lastExternalStdout = "";
        _lastExternalStderr = "";

        string pythonPath = ResolvePythonExecutable(trackerRoot);
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = trackerRoot,
            Arguments = BuildExternalTrackerArguments(scriptPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            _externalProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _externalProcess.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    _lastExternalStdout = args.Data;
                }
            };
            _externalProcess.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    _lastExternalStderr = args.Data;
                }
            };
            _externalProcess.Exited += (_, __) =>
            {
                if (_cameraRequested)
                {
                    _receiverError = "MediaPipe tracker exited.";
                }
            };
            _externalProcess.Start();
            _externalProcess.BeginOutputReadLine();
            _externalProcess.BeginErrorReadLine();
            _lastExternalProcessStartRealtime = Time.unscaledTime;
            _lastExternalPacketRealtime = Time.unscaledTime;
            _hasReceivedExternalPacketSinceProcessStart = false;
            _status = "MediaPipe tracker started: camera " + selectedDeviceIndex.ToString() + ", UDP " + externalTrackerPort.ToString() + ".";
        }
        catch (Exception exception)
        {
            _externalProcess = null;
            _receiverError = "Failed to start MediaPipe tracker: " + exception.Message;
            _status = _receiverError;
        }
    }

    private void RestartExternalProcess()
    {
        StopExternalProcess();
        StartExternalProcess();
    }

    private void StopExternalProcess()
    {
        string scriptPath = ResolveExternalTrackerScriptPath();
        if (_externalProcess == null)
        {
            StopOrphanExternalTrackerProcesses(scriptPath);
            return;
        }

        try
        {
            if (!_externalProcess.HasExited)
            {
                KillProcessTree(_externalProcess);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to stop MediaPipe tracker: " + exception.Message);
        }
        finally
        {
            try
            {
                _externalProcess.Dispose();
            }
            catch
            {
                // Ignore dispose races.
            }

            _externalProcess = null;
        }

        StopOrphanExternalTrackerProcesses(scriptPath);
    }

    private static void KillProcessTree(Process process)
    {
        if (process == null || process.HasExited)
        {
            return;
        }

        try
        {
            System.Reflection.MethodInfo killTree = typeof(Process).GetMethod("Kill", new[] { typeof(bool) });
            if (killTree != null)
            {
                killTree.Invoke(process, new object[] { true });
                return;
            }
        }
        catch
        {
            // Fall back to killing only the tracked process and then clearing orphans by command line.
        }

        process.Kill();
    }

    private string ResolveExternalTrackerScriptPath(string trackerRoot = "")
    {
        if (string.IsNullOrWhiteSpace(trackerRoot))
        {
            trackerRoot = ResolveExternalTrackerRoot();
        }

        if (string.IsNullOrWhiteSpace(trackerRoot))
        {
            return "";
        }

        string scriptName = string.IsNullOrWhiteSpace(externalTrackerScript) ? "head_tracker.py" : externalTrackerScript;
        return Path.GetFullPath(Path.Combine(trackerRoot, scriptName));
    }

    private void StopOrphanExternalTrackerProcesses(string scriptPath)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return;
        }

        try
        {
            string command = "$target=" + ToPowerShellSingleQuoted(Path.GetFullPath(scriptPath)) + ";"
                + "$leaf=" + ToPowerShellSingleQuoted(Path.GetFileName(scriptPath)) + ";"
                + "Get-CimInstance Win32_Process | Where-Object {"
                + "$_.CommandLine -and $_.Name -match '^pythonw?\\.exe$' -and "
                + "(($_.CommandLine -like ('*' + $target + '*')) -or ($_.CommandLine -like ('*' + $leaf + '*')))"
                + "} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process cleanup = Process.Start(startInfo))
            {
                cleanup?.WaitForExit(1200);
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to clean old MediaPipe tracker processes: " + exception.Message);
        }
#endif
    }

    private string ResolveExternalTrackerRoot()
    {
        if (!string.IsNullOrWhiteSpace(externalTrackerRoot))
        {
            string explicitRoot = ExpandPath(externalTrackerRoot);
            if (Directory.Exists(explicitRoot))
            {
                return explicitRoot;
            }
        }

        string[] candidates =
        {
            Path.Combine(Application.streamingAssetsPath, "GodotFinal", "head_tracker"),
            Path.Combine(Application.streamingAssetsPath, "head_tracker"),
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "head_tracker")),
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "head_tracker"))
        };

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return "";
    }

    private string ResolvePythonExecutable(string trackerRoot)
    {
        if (!string.IsNullOrWhiteSpace(externalPythonExecutable))
        {
            string explicitPython = ExpandPath(externalPythonExecutable);
            if (File.Exists(explicitPython))
            {
                return explicitPython;
            }
        }

        string venvPython = Path.Combine(trackerRoot, ".venv", "Scripts", "python.exe");
        return File.Exists(venvPython) ? venvPython : "python";
    }

    private string BuildExternalTrackerArguments(string scriptPath)
    {
        string backend = string.IsNullOrWhiteSpace(externalTrackerBackend) ? "auto" : externalTrackerBackend.Trim();
        string centerMode = trackingAnchor == TransparentPetFaceTrackingAnchor.Eyes ? "eyes" : "bbox";
        string arguments = Quote(scriptPath)
            + " --camera-index " + Mathf.Max(0, selectedDeviceIndex).ToString()
            + " --host " + externalTrackerHost
            + " --port " + externalTrackerPort.ToString()
            + " --width " + requestedWidth.ToString()
            + " --height " + requestedHeight.ToString()
            + " --fps " + requestedFps.ToString()
            + " --backend " + backend
            + " --center-mode " + centerMode
            + " --status-file " + Quote(ResolveExternalTrackerStatusPath())
            + " --no-mirror"
            + " --print-every 0";

        if (externalFrameServerEnabled && externalFrameServerPort > 0)
        {
            arguments += " --frame-host " + Quote(ExternalFrameServerHost())
                + " --frame-port " + Mathf.Clamp(externalFrameServerPort, 1, 65535).ToString()
                + " --frame-server-fps " + Mathf.Clamp(externalFrameServerFps, 1, 30).ToString()
                + " --frame-jpeg-quality " + Mathf.Clamp(externalFrameJpegQuality, 35, 95).ToString();
        }

        return arguments;
    }

    private string BuildExternalFrameServerUrl(string path)
    {
        if (!externalFrameServerEnabled || externalFrameServerPort <= 0)
        {
            return "";
        }

        string suffix = string.IsNullOrEmpty(path) || path[0] == '/' ? path : "/" + path;
        return "http://" + ExternalFrameServerHost() + ":" + Mathf.Clamp(externalFrameServerPort, 1, 65535).ToString() + suffix;
    }

    private string ExternalFrameServerHost()
    {
        return string.IsNullOrWhiteSpace(externalFrameServerHost) ? "127.0.0.1" : externalFrameServerHost.Trim();
    }

    private string ResolveExternalTrackerStatusPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "voicechatpet", "head_tracker_status_" + externalTrackerPort.ToString() + ".json");
    }

    private void UpdateDetection()
    {
        if (!trackingEnabled)
        {
            _hasFace = false;
            _confidence = 0f;
            _rawOffset = Vector2.zero;
            _rawDepthOffset = 0f;
            _rawOrbitAngles = Vector2.zero;
            return;
        }

        if (UsesExternalTracker)
        {
            UpdateExternalDetection();
            return;
        }

        UpdateUnityFallbackDetection();
    }

    private void UpdateExternalDetection()
    {
        if (_udpThread == null)
        {
            _hasFace = false;
            _confidence = 0f;
            _rawOffset = Vector2.zero;
            _rawDepthOffset = 0f;
            _rawOrbitAngles = Vector2.zero;
            _status = "MediaPipe tracker is not running.";
            return;
        }

        if (!IsUdpReceiverAlive())
        {
            _hasFace = false;
            _confidence = 0f;
            _rawOffset = Vector2.zero;
            _rawDepthOffset = 0f;
            _rawOrbitAngles = Vector2.zero;
            _status = "MediaPipe receiver stopped; restarting.";
            if (Time.unscaledTime >= _nextExternalRestartRealtime)
            {
                _nextExternalRestartRealtime = Time.unscaledTime + 2f;
                RestartBackend();
            }
            return;
        }

        if (!string.IsNullOrEmpty(_receiverError))
        {
            _status = "MediaPipe receiver: " + _receiverError;
        }

        if (TryConsumeExternalPacket(out ExternalTrackerPacket packet))
        {
            if (!PacketMatchesRuntimeSource(packet))
            {
#if UNITY_EDITOR
                LogEditorExternalPacket(packet, false);
#endif
                return;
            }

            _lastExternalPacketRealtime = Time.unscaledTime;
            _hasReceivedExternalPacketSinceProcessStart = true;
            _externalAcceptedPacketCount++;
            _lastPacketSource = string.IsNullOrWhiteSpace(packet.source) ? "none" : packet.source;
            _externalYaw = packet.yaw;
            _externalPitch = packet.pitch;
            _externalRoll = packet.roll;
            _confidence = packet.face_found ? 1f : 0f;
            _hasFace = packet.face_found;
#if UNITY_EDITOR
            LogEditorExternalPacket(packet, true);
#endif

            if (_hasFace)
            {
                _lastExternalFaceRealtime = Time.unscaledTime;
                Vector2 offset = new Vector2(packet.face_center_x, packet.face_center_y);
                float depthOffset = packet.z_offset;
                if (mirrorHorizontal)
                {
                    offset.x = -offset.x;
                    _externalYaw = -_externalYaw;
                    _externalRoll = -_externalRoll;
                }

                if (mirrorVertical)
                {
                    offset.y = -offset.y;
                    _externalPitch = -_externalPitch;
                    _externalRoll = -_externalRoll;
                }

                StabilizeExternalPacket(ref offset, ref _externalYaw, ref _externalPitch, ref depthOffset);
                _rawOffset = ApplyNormalizedDeadZone(offset);
                _rawDepthOffset = ApplyDeadZone(depthOffset, normalizedDepthDeadZone);
                _rawOrbitAngles = BuildCameraOrbitAngles(_externalYaw, _externalPitch);
                _status = "MediaPipe face tracked  offset " + _rawOffset.ToString("F2")
                    + "  yaw " + Mathf.RoundToInt(_externalYaw).ToString()
                    + "  pitch " + Mathf.RoundToInt(_externalPitch).ToString()
                    + "  depth " + _rawDepthOffset.ToString("F2");
            }
            else
            {
                bool holdRecentFace = _lastExternalFaceRealtime > 0f &&
                    Time.unscaledTime - _lastExternalFaceRealtime <= faceLostGraceSeconds;
                _hasFace = holdRecentFace;
                if (!holdRecentFace)
                {
                    _rawOffset = Vector2.zero;
                    _rawDepthOffset = 0f;
                    _rawOrbitAngles = Vector2.zero;
                    ResetExternalPacketStabilizer();
                }

                _status = holdRecentFace
                    ? "MediaPipe briefly lost face; holding last target."
                    : "MediaPipe is running, no face target yet.";
            }

            return;
        }

        float age = Time.unscaledTime - _lastExternalPacketRealtime;
        if (age > externalPacketTimeoutSeconds)
        {
            _hasFace = false;
            _confidence = 0f;
            _rawOffset = Vector2.zero;
            _rawDepthOffset = 0f;
            _rawOrbitAngles = Vector2.zero;
            ResetExternalPacketStabilizer();

            if (_externalProcess != null && _externalProcess.HasExited)
            {
                string tail = !string.IsNullOrEmpty(_lastExternalStderr) ? "  " + _lastExternalStderr : "";
                _status = "MediaPipe tracker exited." + tail;
            }
            else
            {
                _status = "Waiting for MediaPipe face packets on UDP " + externalTrackerPort.ToString() + ".";
            }

            bool externalProcessMissingOrExited = _externalProcess == null || _externalProcess.HasExited;
            float processStartupAge = Time.unscaledTime - _lastExternalProcessStartRealtime;
            bool silentAfterStartup = !_hasReceivedExternalPacketSinceProcessStart &&
                processStartupAge > Mathf.Max(3f, externalStartupGraceSeconds);
            bool silentAfterPackets = _hasReceivedExternalPacketSinceProcessStart &&
                age > Mathf.Max(3f, externalPacketTimeoutSeconds * 3f);
            bool staleReceiver = silentAfterPackets && UsesExternalTracker && _cameraRequested;

            if (staleReceiver && Time.unscaledTime >= _nextExternalRestartRealtime)
            {
                _nextExternalRestartRealtime = Time.unscaledTime + 2f;
                _status = "MediaPipe packets stalled; restarting receiver.";
                RestartBackend();
                return;
            }

            if (launchExternalProcess &&
                _cameraRequested &&
                Time.unscaledTime >= _nextExternalRestartRealtime &&
                (externalProcessMissingOrExited || silentAfterStartup || silentAfterPackets))
            {
                _nextExternalRestartRealtime = Time.unscaledTime + 3f;
                RestartExternalProcess();
            }
        }
    }

    private bool TryConsumeExternalPacket(out ExternalTrackerPacket packet)
    {
        packet = null;
        string json = null;
        lock (_packetLock)
        {
            if (!string.IsNullOrEmpty(_pendingPacketJson))
            {
                json = _pendingPacketJson;
                _pendingPacketJson = null;
            }
        }

        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            packet = JsonUtility.FromJson<ExternalTrackerPacket>(json);
            return packet != null;
        }
        catch (Exception exception)
        {
            _status = "Invalid MediaPipe packet: " + exception.Message;
            return false;
        }
    }

    private bool PacketMatchesRuntimeSource(ExternalTrackerPacket packet)
    {
        string source = packet != null ? packet.source : "";
        if (string.IsNullOrWhiteSpace(source))
        {
            return true;
        }

        bool fromBridge = source.IndexOf("bridge", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.IndexOf("browser", StringComparison.OrdinalIgnoreCase) >= 0;
        bool fromStandalone = source.IndexOf("standalone", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.IndexOf("mediapipe", StringComparison.OrdinalIgnoreCase) >= 0;

        if (_runtimeSource == TransparentPetFaceTrackingRuntimeSource.BridgeCameraPackets && fromStandalone)
        {
            _externalIgnoredPacketCount++;
            _lastPacketSource = source;
            _status = "Ignoring standalone face packets while camera stream owns tracking.";
            return false;
        }

        if (_runtimeSource == TransparentPetFaceTrackingRuntimeSource.StandaloneLocalMediaPipe && fromBridge)
        {
            _externalIgnoredPacketCount++;
            _lastPacketSource = source;
            _status = "Ignoring bridge face packets; scene tracking owns the real camera.";
            return false;
        }

        return true;
    }

    private void StabilizeExternalPacket(ref Vector2 offset, ref float yaw, ref float pitch, ref float depthOffset)
    {
        if (!externalPacketStabilizationEnabled)
        {
            return;
        }

        if (!_hasAcceptedExternalPacket)
        {
            _hasAcceptedExternalPacket = true;
            _lastAcceptedExternalOffset = offset;
            _lastAcceptedExternalYaw = yaw;
            _lastAcceptedExternalPitch = pitch;
            _lastAcceptedExternalDepth = depthOffset;
            return;
        }

        offset = Vector2.MoveTowards(_lastAcceptedExternalOffset, offset, maxExternalOffsetStep);
        yaw = Mathf.MoveTowards(_lastAcceptedExternalYaw, yaw, maxExternalAngleStepDegrees);
        pitch = Mathf.MoveTowards(_lastAcceptedExternalPitch, pitch, maxExternalAngleStepDegrees);
        depthOffset = Mathf.MoveTowards(_lastAcceptedExternalDepth, depthOffset, maxExternalDepthStep);
        _lastAcceptedExternalOffset = offset;
        _lastAcceptedExternalYaw = yaw;
        _lastAcceptedExternalPitch = pitch;
        _lastAcceptedExternalDepth = depthOffset;
    }

    private void ResetExternalPacketStabilizer()
    {
        _hasAcceptedExternalPacket = false;
        _lastAcceptedExternalOffset = Vector2.zero;
        _lastAcceptedExternalYaw = 0f;
        _lastAcceptedExternalPitch = 0f;
        _lastAcceptedExternalDepth = 0f;
    }

    private void ResetTrackingMotionState(bool clearDrivenEffects)
    {
        _hasFace = false;
        _confidence = 0f;
        _rawOffset = Vector2.zero;
        _smoothOffset = Vector2.zero;
        _offsetVelocity = Vector2.zero;
        _rawDepthOffset = 0f;
        _smoothDepthOffset = 0f;
        _depthVelocity = 0f;
        _rawOrbitAngles = Vector2.zero;
        _smoothOrbitAngles = Vector2.zero;
        _orbitVelocity = Vector2.zero;
        _externalYaw = 0f;
        _externalPitch = 0f;
        _externalRoll = 0f;
        ResetExternalPacketStabilizer();
        if (clearDrivenEffects)
        {
            ClearDrivenEffects();
        }
    }

    private void UpdateUnityFallbackDetection()
    {
        if (_webCamTexture == null || !_webCamTexture.isPlaying)
        {
            _hasFace = false;
            _confidence = 0f;
            _rawOffset = Vector2.zero;
            return;
        }

        if (Time.unscaledTime < _nextDetectTime)
        {
            return;
        }

        _nextDetectTime = Time.unscaledTime + Mathf.Max(0.03f, detectorIntervalSeconds);
        if (_webCamTexture.width < 32 || _webCamTexture.height < 32)
        {
            _status = "Camera warming up...";
            return;
        }

        Color32[] pixels;
        try
        {
            pixels = _webCamTexture.GetPixels32();
        }
        catch (Exception exception)
        {
            _hasFace = false;
            _confidence = 0f;
            _status = "Camera frame unavailable: " + exception.Message;
            return;
        }

        DetectWarmFaceBlob(pixels, _webCamTexture.width, _webCamTexture.height);
    }

    private void DetectWarmFaceBlob(Color32[] pixels, int width, int height)
    {
        int stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(width, height) / Mathf.Max(24f, detectorSampleResolution)));
        float weightedX = 0f;
        float weightedY = 0f;
        float totalScore = 0f;
        int samples = 0;

        for (int y = stride / 2; y < height; y += stride)
        {
            int row = y * width;
            for (int x = stride / 2; x < width; x += stride)
            {
                Color32 color = pixels[row + x];
                float faceScore = FaceLikeScore(color);
                if (faceScore <= 0.18f)
                {
                    samples++;
                    continue;
                }

                float normalizedX = ((x + 0.5f) / width) * 2f - 1f;
                float normalizedY = ((y + 0.5f) / height) * 2f - 1f;
                float centerWeight = 1f - Mathf.Clamp01((normalizedX * normalizedX + normalizedY * normalizedY) * 0.55f);
                float score = faceScore * Mathf.Lerp(0.62f, 1f, centerWeight);
                weightedX += normalizedX * score;
                weightedY += normalizedY * score;
                totalScore += score;
                samples++;
            }
        }

        float confidence = samples > 0 ? Mathf.Clamp01(totalScore / (samples * 0.08f)) : 0f;
        _confidence = confidence;
        _hasFace = confidence >= minDetectorConfidence && totalScore > 0.001f;
        if (_hasFace)
        {
            Vector2 offset = new Vector2(weightedX / totalScore, weightedY / totalScore);
            if (mirrorHorizontal)
            {
                offset.x = -offset.x;
            }

            if (mirrorVertical)
            {
                offset.y = -offset.y;
            }

            _rawOffset = ApplyNormalizedDeadZone(offset);
            _rawDepthOffset = 0f;
            _rawOrbitAngles = Vector2.zero;
            _status = "Fallback face blob tracked  offset " + _rawOffset.ToString("F2")
                + "  confidence " + Mathf.RoundToInt(confidence * 100f).ToString() + "%";
        }
        else
        {
            _rawOffset = Vector2.zero;
            _rawDepthOffset = 0f;
            _rawOrbitAngles = Vector2.zero;
            _status = "Fallback camera is running, no stable face target yet.";
        }
    }

    private static float FaceLikeScore(Color32 color)
    {
        float r = color.r;
        float g = color.g;
        float b = color.b;
        float max = Mathf.Max(r, Mathf.Max(g, b));
        float min = Mathf.Min(r, Mathf.Min(g, b));
        float luma = r * 0.299f + g * 0.587f + b * 0.114f;

        if (luma < 42f || r < 48f || g < 32f || b < 20f)
        {
            return 0f;
        }

        float warmScore = Mathf.InverseLerp(5f, 72f, r - b) * 0.48f
            + Mathf.InverseLerp(-14f, 48f, r - g) * 0.28f
            + Mathf.InverseLerp(-28f, 44f, g - b) * 0.24f;
        float brightnessScore = Mathf.InverseLerp(45f, 220f, luma);
        float chromaScore = Mathf.InverseLerp(8f, 110f, max - min);
        float score = warmScore * 0.68f + brightnessScore * 0.2f + chromaScore * 0.12f;

        if (g > r * 1.28f || b > r * 1.18f)
        {
            score *= 0.18f;
        }

        return Mathf.Clamp01(score);
    }

    private Vector2 ApplyNormalizedDeadZone(Vector2 offset)
    {
        return new Vector2(
            ApplyDeadZone(offset.x, normalizedDeadZone),
            ApplyDeadZone(offset.y, normalizedDeadZone));
    }

    private static float ApplyDeadZone(float value, float deadZone)
    {
        float magnitude = Mathf.Abs(value);
        if (magnitude <= deadZone)
        {
            return 0f;
        }

        return Mathf.Sign(value) * Mathf.Clamp01((magnitude - deadZone) / Mathf.Max(0.0001f, 1f - deadZone));
    }

    private void UpdateSmoothedOffset()
    {
        Vector2 targetOffset = _hasFace ? _rawOffset : Vector2.zero;
        float targetDepth = _hasFace ? _rawDepthOffset : 0f;
        Vector2 targetOrbit = _hasFace ? _rawOrbitAngles : Vector2.zero;
        Vector2 targetHeadAngles = _hasFace ? BuildHeadFollowAngles(_externalYaw, _externalPitch, targetOffset) : Vector2.zero;
        float deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);
        _smoothOffset = Vector2.SmoothDamp(_smoothOffset, targetOffset, ref _offsetVelocity, offsetSmoothTime, Mathf.Infinity, deltaTime);
        _smoothDepthOffset = Mathf.SmoothDamp(_smoothDepthOffset, targetDepth, ref _depthVelocity, depthSmoothTime, Mathf.Infinity, deltaTime);
        _smoothOrbitAngles = Vector2.SmoothDamp(_smoothOrbitAngles, targetOrbit, ref _orbitVelocity, cameraOrbitSmoothTime, Mathf.Infinity, deltaTime);
        _smoothHeadAngles = Vector2.SmoothDamp(_smoothHeadAngles, targetHeadAngles, ref _headAngleVelocity, offsetSmoothTime, Mathf.Infinity, deltaTime);
    }

    private void ApplyHeadFollow()
    {
        if (headLookAt == null || !headFollowEnabled)
        {
            headLookAt?.ClearExternalAdditiveLookAngles();
            return;
        }

        headLookAt.SetExternalAdditiveLookAngles(_smoothHeadAngles);
    }

    private void ApplyCameraParallax()
    {
        if (!cameraParallaxEnabled || freeCamera == null || targetCamera == null)
        {
            RestoreCameraParallaxBase();
            return;
        }

        Vector3 cameraOffset = targetCamera.transform.right * (_smoothOffset.x * cameraTargetShiftMeters)
            + targetCamera.transform.forward * (_smoothDepthOffset * cameraDepthShiftMeters);
        Vector3 heightTargetOffset = -Vector3.up * (_smoothOffset.y * cameraHeightFollowMeters);
        if (cameraSightMode == TransparentPetCameraSightMode.ModelAxis)
        {
            freeCamera.SetExternalTargetOffset(heightTargetOffset);
            freeCamera.SetExternalCameraOffset(cameraOffset);
        }
        else
        {
            freeCamera.ClearExternalCameraOffset();
            Vector3 targetOffset = targetCamera.transform.right * (_smoothOffset.x * cameraTargetShiftMeters)
                + heightTargetOffset
                + targetCamera.transform.forward * (_smoothDepthOffset * cameraDepthShiftMeters);
            freeCamera.SetExternalTargetOffset(targetOffset);
        }
#if UNITY_EDITOR
        LogEditorCameraParallax(cameraOffset, heightTargetOffset);
#endif
    }

    private void ApplyCameraOrbit()
    {
        if (freeCamera == null || !cameraOrbitEnabled)
        {
            freeCamera?.ClearExternalOrbitOffset();
            return;
        }

        freeCamera.SetExternalOrbitOffset(_smoothOrbitAngles.x, _smoothOrbitAngles.y);
    }

    private void ClearDrivenEffects()
    {
        headLookAt?.ClearExternalAdditiveLookAngles();
        freeCamera?.ClearExternalOrbitOffset();
        freeCamera?.ClearExternalCameraOffset();
        RestoreCameraParallaxBase();
    }

    private void RestoreCameraParallaxBase()
    {
        freeCamera?.ClearExternalCameraOffset();
        freeCamera?.ClearExternalTargetOffset();
    }

    private Vector2 BuildCameraOrbitAngles(float yawDegreesValue, float pitchDegreesValue)
    {
        float yaw = ApplyAngleDeadZone(yawDegreesValue, cameraOrbitDeadZoneDegrees) * cameraYawOrbitStrength;
        float pitch = ApplyAngleDeadZone(pitchDegreesValue, cameraOrbitDeadZoneDegrees) * cameraPitchOrbitStrength;
        return new Vector2(
            Mathf.Clamp(yaw, -maxCameraYawOrbitDegrees, maxCameraYawOrbitDegrees),
            Mathf.Clamp(pitch, -maxCameraPitchOrbitDegrees, maxCameraPitchOrbitDegrees));
    }

    private Vector2 BuildHeadFollowAngles(float yawDegreesValue, float pitchDegreesValue, Vector2 normalizedOffset)
    {
        float yaw = normalizedOffset.x * headYawStrengthDegrees
            + ApplyAngleDeadZone(yawDegreesValue, cameraOrbitDeadZoneDegrees) * StableHeadYawPoseWeight;
        float pitch = normalizedOffset.y * headPitchStrengthDegrees
            + ApplyAngleDeadZone(pitchDegreesValue, cameraOrbitDeadZoneDegrees) * StableHeadPitchPoseWeight;
        return new Vector2(yaw, pitch);
    }

    private static float ApplyAngleDeadZone(float value, float deadZone)
    {
        float magnitude = Mathf.Abs(value);
        if (magnitude <= deadZone)
        {
            return 0f;
        }

        return value;
    }

#if UNITY_EDITOR
    private void LogEditorExternalPacket(ExternalTrackerPacket packet, bool accepted)
    {
        if (Time.unscaledTime < _nextEditorPacketLogRealtime)
        {
            return;
        }

        _nextEditorPacketLogRealtime = Time.unscaledTime + 0.75f;
        Debug.Log("Scene face tracking packet"
            + " accepted=" + accepted
            + " source=" + (packet != null && !string.IsNullOrWhiteSpace(packet.source) ? packet.source : "none")
            + " runtime=" + _runtimeSource
            + " face=" + (packet != null && packet.face_found)
            + " x=" + (packet != null ? packet.face_center_x.ToString("F2") : "0.00")
            + " y=" + (packet != null ? packet.face_center_y.ToString("F2") : "0.00")
            + " yaw=" + (packet != null ? packet.yaw.ToString("F1") : "0.0")
            + " pitch=" + (packet != null ? packet.pitch.ToString("F1") : "0.0")
            + " udpCount=" + _udpReceivedPacketCount
            + " acceptedCount=" + _externalAcceptedPacketCount
            + " ignoredCount=" + _externalIgnoredPacketCount
            + " status=" + _status);
    }

    private void LogEditorCameraParallax(Vector3 cameraOffset, Vector3 targetOffset)
    {
        if (Time.unscaledTime < _nextEditorDriveLogRealtime)
        {
            return;
        }

        _nextEditorDriveLogRealtime = Time.unscaledTime + 0.75f;
        if (!_hasFace && cameraOffset.sqrMagnitude < 0.000001f && targetOffset.sqrMagnitude < 0.000001f)
        {
            return;
        }

        Debug.Log("Scene face tracking drive"
            + " hasFace=" + _hasFace
            + " smoothOffset=" + _smoothOffset.ToString("F3")
            + " smoothOrbit=" + _smoothOrbitAngles.ToString("F1")
            + " yaw=" + _externalYaw.ToString("F1")
            + " pitch=" + _externalPitch.ToString("F1")
            + " depth=" + _smoothDepthOffset.ToString("F3")
            + " cameraOffset=" + cameraOffset.ToString("F3")
            + " targetOffset=" + targetOffset.ToString("F3")
            + " freeTarget=" + (freeCamera != null ? freeCamera.target.ToString("F3") : "null")
            + " cameraPos=" + (freeCamera != null ? freeCamera.CameraWorldPosition.ToString("F3") : "null")
            + " cameraYaw=" + (freeCamera != null ? freeCamera.CameraYawDegrees.ToString("F1") : "0.0")
            + " cameraPitch=" + (freeCamera != null ? freeCamera.CameraPitchDegrees.ToString("F1") : "0.0")
            + " followPlacement=" + (freeCamera != null && freeCamera.followPlacementTarget)
            + " status=" + _status);
    }
#endif

    private void NormalizeValues()
    {
        selectedDeviceIndex = Mathf.Max(0, selectedDeviceIndex);
        requestedWidth = Mathf.Clamp(requestedWidth, 640, 1920);
        requestedHeight = Mathf.Clamp(requestedHeight, 480, 1080);
        if (requestedHeight >= 480 && requestedWidth < 854)
        {
            requestedWidth = 854;
        }
        requestedFps = Mathf.Clamp(requestedFps, 5, 60);
        detectorSampleResolution = Mathf.Clamp(detectorSampleResolution, 24, 160);
        detectorIntervalSeconds = Mathf.Clamp(detectorIntervalSeconds, 0.03f, 0.5f);
        normalizedDeadZone = Mathf.Clamp(normalizedDeadZone, 0f, 0.3f);
        normalizedDepthDeadZone = Mathf.Clamp(normalizedDepthDeadZone, 0f, 0.3f);
        offsetSmoothTime = Mathf.Clamp(offsetSmoothTime, 0.03f, 0.8f);
        depthSmoothTime = Mathf.Clamp(depthSmoothTime, 0.03f, 0.8f);
        headYawStrengthDegrees = Mathf.Clamp(headYawStrengthDegrees, 1f, 35f);
        headPitchStrengthDegrees = Mathf.Clamp(headPitchStrengthDegrees, 1f, 25f);
        cameraTargetShiftMeters = Mathf.Clamp(cameraTargetShiftMeters, 0f, 0.2f);
        cameraDepthShiftMeters = Mathf.Clamp(cameraDepthShiftMeters, 0f, 0.2f);
        cameraHeightFollowMeters = Mathf.Clamp(cameraHeightFollowMeters, 0f, 1.5f);
        cameraYawOrbitStrength = Mathf.Clamp(cameraYawOrbitStrength, -1.5f, 1.5f);
        cameraPitchOrbitStrength = Mathf.Clamp(cameraPitchOrbitStrength, -1.5f, 1.5f);
        maxCameraYawOrbitDegrees = Mathf.Clamp(maxCameraYawOrbitDegrees, 0f, 60f);
        maxCameraPitchOrbitDegrees = Mathf.Clamp(maxCameraPitchOrbitDegrees, 0f, 35f);
        cameraOrbitDeadZoneDegrees = Mathf.Clamp(cameraOrbitDeadZoneDegrees, 0f, 10f);
        cameraOrbitSmoothTime = Mathf.Clamp(cameraOrbitSmoothTime, 0.03f, 0.8f);
        minDetectorConfidence = Mathf.Clamp(minDetectorConfidence, 0.01f, 0.5f);
        externalTrackerPort = Mathf.Clamp(externalTrackerPort, 1024, 65535);
        externalFrameServerPort = Mathf.Clamp(externalFrameServerPort, 0, 65535);
        externalFrameServerFps = Mathf.Clamp(externalFrameServerFps, 1, 30);
        externalFrameJpegQuality = Mathf.Clamp(externalFrameJpegQuality, 35, 95);
        if (string.IsNullOrWhiteSpace(externalFrameServerHost))
        {
            externalFrameServerHost = "127.0.0.1";
        }
        externalPacketTimeoutSeconds = Mathf.Clamp(externalPacketTimeoutSeconds, 0.1f, 3f);
        externalStartupGraceSeconds = Mathf.Clamp(externalStartupGraceSeconds, 3f, 30f);
        maxExternalOffsetStep = Mathf.Clamp(maxExternalOffsetStep, 0.02f, 0.5f);
        maxExternalAngleStepDegrees = Mathf.Clamp(maxExternalAngleStepDegrees, 1f, 30f);
        maxExternalDepthStep = Mathf.Clamp(maxExternalDepthStep, 0.01f, 0.5f);
        if (trackingBackend != TransparentPetFaceTrackingBackend.ExternalMediaPipe &&
            trackingBackend != TransparentPetFaceTrackingBackend.UnityWarmBlob)
        {
            trackingBackend = TransparentPetFaceTrackingBackend.ExternalMediaPipe;
        }

        if (trackingAnchor != TransparentPetFaceTrackingAnchor.Head &&
            trackingAnchor != TransparentPetFaceTrackingAnchor.Eyes)
        {
            trackingAnchor = TransparentPetFaceTrackingAnchor.Head;
        }

        if (cameraSightMode != TransparentPetCameraSightMode.ModelAxis &&
            cameraSightMode != TransparentPetCameraSightMode.TrackingAnchor)
        {
            cameraSightMode = TransparentPetCameraSightMode.ModelAxis;
        }
    }

    private void ApplyStableTrackingDefaults()
    {
        normalizedDeadZone = StableNormalizedDeadZone;
        normalizedDepthDeadZone = StableNormalizedDepthDeadZone;
        offsetSmoothTime = StableOffsetSmoothTime;
        depthSmoothTime = StableDepthSmoothTime;
        cameraTargetShiftMeters = StableCameraTargetShiftMeters;
        cameraDepthShiftMeters = StableCameraDepthShiftMeters;
        cameraHeightFollowMeters = StableCameraHeightFollowMeters;
        cameraOrbitDeadZoneDegrees = StableCameraOrbitDeadZoneDegrees;
        cameraOrbitSmoothTime = StableCameraOrbitSmoothTime;
    }

    private void LoadSettings()
    {
        if (_settingsLoaded || !persistRuntimeSettings || string.IsNullOrWhiteSpace(settingsKey) || !PlayerPrefs.HasKey(settingsKey))
        {
            _settingsLoaded = true;
            return;
        }

        try
        {
            SceneFaceTrackerSettings settings = JsonUtility.FromJson<SceneFaceTrackerSettings>(PlayerPrefs.GetString(settingsKey));
            trackingBackend = settings.trackingBackend == 1
                ? TransparentPetFaceTrackingBackend.UnityWarmBlob
                : TransparentPetFaceTrackingBackend.ExternalMediaPipe;
            // Scene face tracking is a product-default behavior. Old runtime saves
            // can remember a temporary menu disable, so do not let them silently
            // boot the scene with tracking visuals off.
            trackingEnabled = startCameraOnEnable || settings.trackingEnabled;
            headFollowEnabled = settings.headFollowEnabled;
            cameraParallaxEnabled = settings.cameraParallaxEnabled;
            cameraOrbitEnabled = settings.cameraOrbitEnabled;
            mirrorHorizontal = settings.mirrorHorizontal;
            mirrorVertical = settings.settingsVersion <= 0 ? true : settings.mirrorVertical;
            trackingAnchor = settings.trackingAnchor == 1
                ? TransparentPetFaceTrackingAnchor.Eyes
                : TransparentPetFaceTrackingAnchor.Head;
            cameraSightMode = settings.cameraSightMode == 1
                ? TransparentPetCameraSightMode.TrackingAnchor
                : TransparentPetCameraSightMode.ModelAxis;
            launchExternalProcess = settings.settingsVersion <= 1 ? true : settings.launchExternalProcess;
            selectedDeviceIndex = settings.selectedDeviceIndex;
            requestedWidth = settings.requestedWidth > 0 ? settings.requestedWidth : requestedWidth;
            requestedHeight = settings.requestedHeight > 0 ? settings.requestedHeight : requestedHeight;
            if (settings.settingsVersion < 5 && requestedWidth <= 854 && requestedHeight <= 480)
            {
                requestedWidth = 1280;
                requestedHeight = 720;
            }
            requestedFps = settings.requestedFps > 0 ? settings.requestedFps : requestedFps;
            detectorIntervalSeconds = settings.detectorIntervalSeconds > 0f ? settings.detectorIntervalSeconds : detectorIntervalSeconds;
            externalTrackerPort = settings.externalTrackerPort > 0 ? settings.externalTrackerPort : externalTrackerPort;
            externalPacketStabilizationEnabled = settings.settingsVersion <= 3 ? true : settings.externalPacketStabilizationEnabled;
            maxExternalOffsetStep = settings.maxExternalOffsetStep > 0f ? settings.maxExternalOffsetStep : maxExternalOffsetStep;
            maxExternalAngleStepDegrees = settings.maxExternalAngleStepDegrees > 0f ? settings.maxExternalAngleStepDegrees : maxExternalAngleStepDegrees;
            maxExternalDepthStep = settings.maxExternalDepthStep > 0f ? settings.maxExternalDepthStep : maxExternalDepthStep;
            normalizedDeadZone = settings.normalizedDeadZone;
            normalizedDepthDeadZone = settings.normalizedDepthDeadZone > 0f ? settings.normalizedDepthDeadZone : normalizedDepthDeadZone;
            offsetSmoothTime = settings.offsetSmoothTime > 0f ? settings.offsetSmoothTime : offsetSmoothTime;
            depthSmoothTime = settings.depthSmoothTime > 0f ? settings.depthSmoothTime : depthSmoothTime;
            headYawStrengthDegrees = settings.headYawStrengthDegrees > 0f ? settings.headYawStrengthDegrees : headYawStrengthDegrees;
            headPitchStrengthDegrees = settings.headPitchStrengthDegrees > 0f ? settings.headPitchStrengthDegrees : headPitchStrengthDegrees;
            cameraTargetShiftMeters = Mathf.Max(0f, settings.cameraTargetShiftMeters);
            cameraDepthShiftMeters = Mathf.Max(0f, settings.cameraDepthShiftMeters);
            cameraHeightFollowMeters = settings.cameraHeightFollowMeters > 0f ? settings.cameraHeightFollowMeters : cameraHeightFollowMeters;
            cameraYawOrbitStrength = settings.cameraYawOrbitStrength != 0f ? settings.cameraYawOrbitStrength : cameraYawOrbitStrength;
            cameraPitchOrbitStrength = settings.cameraPitchOrbitStrength != 0f ? settings.cameraPitchOrbitStrength : cameraPitchOrbitStrength;
            cameraOrbitDeadZoneDegrees = settings.cameraOrbitDeadZoneDegrees > 0f ? settings.cameraOrbitDeadZoneDegrees : cameraOrbitDeadZoneDegrees;
            cameraOrbitSmoothTime = settings.cameraOrbitSmoothTime > 0f ? settings.cameraOrbitSmoothTime : cameraOrbitSmoothTime;
            if (settings.settingsVersion < CurrentSettingsVersion)
            {
                ApplyStableTrackingDefaults();
            }
            NormalizeValues();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to load scene face tracking settings: " + exception.Message);
        }

        _settingsLoaded = true;
    }

    private void SaveSettings()
    {
        NormalizeValues();
        if (_suppressSettingsSave || !persistRuntimeSettings || string.IsNullOrWhiteSpace(settingsKey))
        {
            return;
        }

        SceneFaceTrackerSettings settings = new SceneFaceTrackerSettings
        {
            settingsVersion = CurrentSettingsVersion,
            trackingBackend = trackingBackend == TransparentPetFaceTrackingBackend.UnityWarmBlob ? 1 : 0,
            trackingEnabled = startCameraOnEnable || trackingEnabled,
            headFollowEnabled = headFollowEnabled,
            cameraParallaxEnabled = cameraParallaxEnabled,
            cameraOrbitEnabled = cameraOrbitEnabled,
            mirrorHorizontal = mirrorHorizontal,
            mirrorVertical = mirrorVertical,
            trackingAnchor = trackingAnchor == TransparentPetFaceTrackingAnchor.Eyes ? 1 : 0,
            cameraSightMode = cameraSightMode == TransparentPetCameraSightMode.TrackingAnchor ? 1 : 0,
            launchExternalProcess = launchExternalProcess,
            selectedDeviceIndex = selectedDeviceIndex,
            requestedWidth = requestedWidth,
            requestedHeight = requestedHeight,
            requestedFps = requestedFps,
            detectorIntervalSeconds = detectorIntervalSeconds,
            externalTrackerPort = externalTrackerPort,
            externalPacketStabilizationEnabled = externalPacketStabilizationEnabled,
            maxExternalOffsetStep = maxExternalOffsetStep,
            maxExternalAngleStepDegrees = maxExternalAngleStepDegrees,
            maxExternalDepthStep = maxExternalDepthStep,
            normalizedDeadZone = normalizedDeadZone,
            normalizedDepthDeadZone = normalizedDepthDeadZone,
            offsetSmoothTime = offsetSmoothTime,
            depthSmoothTime = depthSmoothTime,
            headYawStrengthDegrees = headYawStrengthDegrees,
            headPitchStrengthDegrees = headPitchStrengthDegrees,
            cameraTargetShiftMeters = cameraTargetShiftMeters,
            cameraDepthShiftMeters = cameraDepthShiftMeters,
            cameraHeightFollowMeters = cameraHeightFollowMeters,
            cameraYawOrbitStrength = cameraYawOrbitStrength,
            cameraPitchOrbitStrength = cameraPitchOrbitStrength,
            cameraOrbitDeadZoneDegrees = cameraOrbitDeadZoneDegrees,
            cameraOrbitSmoothTime = cameraOrbitSmoothTime
        };
        PlayerPrefs.SetString(settingsKey, JsonUtility.ToJson(settings));
        PlayerPrefs.Save();
    }

    private void RunWithoutSavingSettings(Action action)
    {
        bool previous = _suppressSettingsSave;
        _suppressSettingsSave = true;
        try
        {
            action?.Invoke();
        }
        finally
        {
            _suppressSettingsSave = previous;
        }
    }

    private static string Quote(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }

    private static string ToPowerShellSingleQuoted(string value)
    {
        return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(expanded);
    }

    [Serializable]
    private sealed class ExternalTrackerPacket
    {
        public string source;
        public bool face_found;
        public float face_center_x;
        public float face_center_y;
        public float face_width_px;
        public float yaw;
        public float pitch;
        public float roll;
        public float z_cm;
        public float z_offset;
        public float timestamp;
    }

    [Serializable]
    private sealed class SceneFaceTrackerSettings
    {
        public int settingsVersion = CurrentSettingsVersion;
        public int trackingBackend;
        public bool trackingEnabled = true;
        public bool headFollowEnabled = true;
        public bool cameraParallaxEnabled = true;
        public bool cameraOrbitEnabled = true;
        public bool mirrorHorizontal = true;
        public bool mirrorVertical = true;
        public int trackingAnchor;
        public int cameraSightMode;
        public bool launchExternalProcess = true;
        public int selectedDeviceIndex;
        public int requestedWidth = 1280;
        public int requestedHeight = 720;
        public int requestedFps = 30;
        public float detectorIntervalSeconds = 0.08f;
        public int externalTrackerPort = 5055;
        public bool externalPacketStabilizationEnabled = true;
        public float maxExternalOffsetStep = 0.12f;
        public float maxExternalAngleStepDegrees = 8f;
        public float maxExternalDepthStep = 0.05f;
        public float normalizedDeadZone = StableNormalizedDeadZone;
        public float normalizedDepthDeadZone = StableNormalizedDepthDeadZone;
        public float offsetSmoothTime = StableOffsetSmoothTime;
        public float depthSmoothTime = StableDepthSmoothTime;
        public float headYawStrengthDegrees = 14f;
        public float headPitchStrengthDegrees = 8f;
        public float cameraTargetShiftMeters = StableCameraTargetShiftMeters;
        public float cameraDepthShiftMeters = StableCameraDepthShiftMeters;
        public float cameraHeightFollowMeters = StableCameraHeightFollowMeters;
        public float cameraYawOrbitStrength = 1f;
        public float cameraPitchOrbitStrength = 0.35f;
        public float cameraOrbitDeadZoneDegrees = StableCameraOrbitDeadZoneDegrees;
        public float cameraOrbitSmoothTime = StableCameraOrbitSmoothTime;
    }
}
