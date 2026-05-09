using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public sealed class TransparentWindowController : MonoBehaviour
{
    public enum MonitorPresentationMode
    {
        BorderlessFullscreen = 0,
        ExclusiveFullscreen = 1,
        SmallWindow = 2
    }

    public enum MonitorOrientationMode
    {
        FollowMonitor = 0,
        Landscape = 1,
        Portrait = 2
    }

    public Camera transparentCamera;
    public Transform hitRoot;
    public bool configureNativeWindow = true;
    public bool transparentBackground = true;
    public TransparentPetRoute route = TransparentPetRoute.DesktopTransparent;
    public bool alwaysOnTop = true;
    public bool clickThroughOutsideHit = true;
    public LayerMask hitMask = ~0;
    public TransparentPetSkeletonHitMask skeletonHitMask;
    public float pollIntervalSeconds = 0.02f;
    public Color transparentKeyColor = new Color(0f, 0f, 0f, 0f);
    public bool moveToSecondaryMonitorOnStart;
    public int preferredMonitorIndex = 1;
    public bool resizeToTargetMonitorWorkArea = true;
    public bool useFullMonitorBounds = true;
    public bool compensateMonitorDpiScale = true;
    public MonitorPresentationMode presentationMode = MonitorPresentationMode.BorderlessFullscreen;
    public MonitorOrientationMode orientationMode = MonitorOrientationMode.FollowMonitor;
    public Vector2 monitorNormalizedPosition = new Vector2(0.5f, 0.5f);
    public Vector2Int monitorPaddingPixels = Vector2Int.zero;
    public Vector2Int primaryRightWindowSizePixels = new Vector2Int(720, 960);
    public Vector2Int primaryRightWindowPaddingPixels = new Vector2Int(24, 48);
    public bool persistRuntimeWindowSettings = true;
    public string windowSettingsKey = "TransparentPet.Window.v1";

    private float _pollTimer;
    private float _topmostTimer;
    private float _nativeWindowRefreshTimer;
    private bool _lastClickThrough;
    private bool _interactionLocked;
    private bool _hasExtraInteractiveRect;
    private UnityEngine.Rect _extraInteractiveRect;
    private bool _windowSettingsLoaded;
    private bool _hasSavedWindowSettings;
    private float _windowSettingsSaveTimer;

#if UNITY_STANDALONE_WIN
    private IntPtr _hwnd = IntPtr.Zero;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint WsVisible = 0x10000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsMinimizeBox = 0x00020000;
    private const uint WsMaximizeBox = 0x00010000;
    private const uint WsSysMenu = 0x00080000;
    private const long WsExLayered = 0x00080000L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExTopmost = 0x00000008L;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;
    private const byte LwaColorKey = 0x01;
    private const byte LwaAlpha = 0x02;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private static readonly IntPtr HwndTopmost = new IntPtr(-1);
    private static readonly IntPtr HwndNotopmost = new IntPtr(-2);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private struct MonitorCandidate
    {
        public IntPtr Handle;
        public int Index;
        public Rect MonitorRect;
        public Rect WorkRect;
        public bool IsPrimary;
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(IntPtr hmonitor, out int scaleFactor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins pMarInset);
#endif

    public bool IsDragging { get; set; }
    public int PreferredMonitorIndex => Mathf.Max(0, preferredMonitorIndex);
    public MonitorPresentationMode PresentationMode => presentationMode;
    public MonitorOrientationMode OrientationMode => orientationMode;
    public bool IsSmallWindowMode => presentationMode == MonitorPresentationMode.SmallWindow;
    public bool HasSavedWindowSettings => _hasSavedWindowSettings;
    public TransparentPetRoute Route => route;
    public bool IsSceneHostRoute => route == TransparentPetRoute.SceneHost;
    public bool IsDesktopTransparentRoute => route == TransparentPetRoute.DesktopTransparent;

    private void Awake()
    {
        LoadWindowSettings();
        NormalizeWindowSettings();
    }

    private IEnumerator Start()
    {
        Application.runInBackground = true;
        LoadWindowSettings();
        NormalizeWindowSettings();
        if (GetComponent<TransparentPetPerformanceController>() == null)
        {
            QualitySettings.antiAliasing = Mathf.Max(QualitySettings.antiAliasing, 8);
        }
        ConfigureCamera();

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (ShouldFindNativeWindow())
        {
            for (int i = 0; i < 180 && _hwnd == IntPtr.Zero; i++)
            {
                _hwnd = FindMainWindowForCurrentProcess();
                if (_hwnd != IntPtr.Zero)
                {
                    if (configureNativeWindow || ShouldUseBorderlessMonitorMode())
                    {
                        ConfigureNativeWindow();
                    }

                    if (configureNativeWindow)
                    {
                        SetClickThrough(false);
                    }

                    ApplyStartupMonitorPlacement(false);
                    yield return null;
                    ApplyStartupMonitorPlacement(false);
                    break;
                }

                yield return null;
            }
        }
        else
        {
            yield return null;
        }
#else
        yield return null;
#endif
    }

    private void Update()
    {
        if (_interactionLocked)
        {
            SetClickThrough(false);
            KeepAlwaysOnTop();
            RefreshNativeWindowStyleIfNeeded();
            return;
        }

        if (!clickThroughOutsideHit)
        {
            SetClickThrough(false);
            RefreshNativeWindowStyleIfNeeded();
            return;
        }

        _pollTimer += Time.unscaledDeltaTime;
        if (_pollTimer < pollIntervalSeconds)
        {
            return;
        }

        _pollTimer = 0f;
        SetClickThrough(!IsDragging && !IsCursorOverHit());
        KeepAlwaysOnTop();
        RefreshNativeWindowStyleIfNeeded();
        PollRuntimeWindowSettingsSave();
    }

    private void OnApplicationQuit()
    {
        CaptureRuntimeWindowSettings();
        SetClickThrough(false);
    }

    public void MoveWindowBy(Vector2Int deltaPixels)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (_hwnd == IntPtr.Zero || deltaPixels == Vector2Int.zero)
        {
            return;
        }

        if (GetWindowRect(_hwnd, out Rect rect))
        {
            SetWindowPos(
                _hwnd,
                alwaysOnTop ? HwndTopmost : HwndNotopmost,
                rect.left + deltaPixels.x,
                rect.top + deltaPixels.y,
                0,
                0,
                SwpNosize | SwpNoactivate);
        }
#else
        transform.position += new Vector3(deltaPixels.x, -deltaPixels.y, 0f) * 0.0025f;
#endif
    }

    public bool TryGetDesktopCursorPosition(out Vector2Int position)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (GetCursorPos(out Point point))
        {
            position = new Vector2Int(point.x, point.y);
            return true;
        }
#endif
        if (Input.mousePresent)
        {
            position = Vector2Int.RoundToInt(Input.mousePosition);
            return true;
        }

        position = Vector2Int.zero;
        return false;
    }

    public void SetClickThrough(bool enabled)
    {
        if (_lastClickThrough == enabled)
        {
            return;
        }

        _lastClickThrough = enabled;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        long exStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64() | WsExLayered;
        exStyle = enabled ? exStyle | WsExTransparent : exStyle & ~WsExTransparent;
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(exStyle));
#endif
    }

    public void SetInteractionLock(bool locked)
    {
        _interactionLocked = locked;
        if (locked)
        {
            SetClickThrough(false);
        }
    }

    public void SetExtraInteractiveRect(UnityEngine.Rect windowRect, bool enabled)
    {
        _extraInteractiveRect = windowRect;
        _hasExtraInteractiveRect = enabled;
    }

    public void SetMoveToSecondaryMonitorOnStart(bool enabled)
    {
        moveToSecondaryMonitorOnStart = enabled;
        SaveWindowSettings();
        if (enabled)
        {
            MoveToConfiguredMonitorNow();
        }
    }

    public void SetResizeToTargetMonitorWorkArea(bool enabled)
    {
        resizeToTargetMonitorWorkArea = enabled;
        if (enabled && presentationMode == MonitorPresentationMode.SmallWindow)
        {
            presentationMode = MonitorPresentationMode.BorderlessFullscreen;
        }
        SaveWindowSettings();
        MoveToConfiguredMonitorNow();
    }

    public void SetPresentationMode(MonitorPresentationMode mode)
    {
        if (mode == MonitorPresentationMode.SmallWindow)
        {
            SetSmallWindowMode();
            return;
        }

        presentationMode = mode;
        resizeToTargetMonitorWorkArea = true;
        useFullMonitorBounds = true;
        moveToSecondaryMonitorOnStart = true;
        SaveWindowSettings();
        MoveToConfiguredMonitorNow();
    }

    public void SetSmallWindowMode()
    {
        presentationMode = MonitorPresentationMode.SmallWindow;
        preferredMonitorIndex = GetPrimaryMonitorIndex();
        moveToSecondaryMonitorOnStart = true;
        resizeToTargetMonitorWorkArea = false;
        useFullMonitorBounds = false;
        orientationMode = MonitorOrientationMode.FollowMonitor;
        monitorNormalizedPosition = new Vector2(1f, 0.5f);
        monitorPaddingPixels = primaryRightWindowPaddingPixels;
        NormalizeWindowSettings();
        SaveWindowSettings();
        MoveToConfiguredMonitorNow();
    }

    public void FillCurrentScreenNow()
    {
        preferredMonitorIndex = GetCurrentMonitorIndex();
        presentationMode = MonitorPresentationMode.BorderlessFullscreen;
        moveToSecondaryMonitorOnStart = true;
        resizeToTargetMonitorWorkArea = true;
        useFullMonitorBounds = true;
        orientationMode = MonitorOrientationMode.FollowMonitor;
        SaveWindowSettings();
        MoveToConfiguredMonitorNow();
    }

    public void SetOrientationMode(MonitorOrientationMode mode)
    {
        orientationMode = mode;
        resizeToTargetMonitorWorkArea = true;
        useFullMonitorBounds = true;
        moveToSecondaryMonitorOnStart = true;
        if (presentationMode == MonitorPresentationMode.SmallWindow)
        {
            presentationMode = MonitorPresentationMode.BorderlessFullscreen;
        }
        SaveWindowSettings();
        MoveToConfiguredMonitorNow();
    }

    public void SetPreferredMonitorIndex(int index)
    {
        preferredMonitorIndex = Mathf.Clamp(index, 0, Mathf.Max(0, GetAvailableMonitorCount() - 1));
        moveToSecondaryMonitorOnStart = true;
        if (presentationMode == MonitorPresentationMode.SmallWindow)
        {
            presentationMode = MonitorPresentationMode.BorderlessFullscreen;
            resizeToTargetMonitorWorkArea = true;
            useFullMonitorBounds = true;
        }
        SaveWindowSettings();
        MoveToConfiguredMonitorNow();
    }

    public void FullscreenOnMonitor(int index)
    {
        preferredMonitorIndex = Mathf.Clamp(index, 0, Mathf.Max(0, GetAvailableMonitorCount() - 1));
        moveToSecondaryMonitorOnStart = true;
        resizeToTargetMonitorWorkArea = true;
        useFullMonitorBounds = true;
        if (presentationMode == MonitorPresentationMode.SmallWindow)
        {
            presentationMode = MonitorPresentationMode.BorderlessFullscreen;
        }
        SaveWindowSettings();
        MoveToConfiguredMonitorNow();
    }

    public int GetAvailableMonitorCount()
    {
#if UNITY_STANDALONE_WIN
        return Mathf.Max(1, EnumerateMonitors().Length);
#else
        return Mathf.Max(1, Display.displays != null ? Display.displays.Length : 1);
#endif
    }

    public string GetMonitorLabel(int index)
    {
#if UNITY_STANDALONE_WIN
        MonitorCandidate[] monitors = EnumerateMonitors();
        if (index >= 0 && index < monitors.Length)
        {
            MonitorCandidate monitor = monitors[index];
            int width = monitor.MonitorRect.right - monitor.MonitorRect.left;
            int height = monitor.MonitorRect.bottom - monitor.MonitorRect.top;
            string primary = monitor.IsPrimary ? " 主屏" : string.Empty;
            string device = string.IsNullOrWhiteSpace(monitor.DeviceName) ? string.Empty : " " + monitor.DeviceName;
            return "显示器 " + (index + 1).ToString() + primary + device + "  " + width.ToString() + "x" + height.ToString();
        }
#else
        if (Display.displays != null && index >= 0 && index < Display.displays.Length)
        {
            Display display = Display.displays[index];
            return "显示器 " + (index + 1).ToString() + "  " + display.systemWidth.ToString() + "x" + display.systemHeight.ToString();
        }
#endif

        return "显示器 " + (index + 1).ToString();
    }

    public string GetMonitorDisplayLabel(int index)
    {
#if UNITY_STANDALONE_WIN
        MonitorCandidate[] monitors = EnumerateMonitors();
        if (index >= 0 && index < monitors.Length)
        {
            MonitorCandidate monitor = monitors[index];
            int width = monitor.MonitorRect.right - monitor.MonitorRect.left;
            int height = monitor.MonitorRect.bottom - monitor.MonitorRect.top;
            string primary = monitor.IsPrimary ? " Primary" : string.Empty;
            string device = string.IsNullOrWhiteSpace(monitor.DeviceName) ? string.Empty : " " + monitor.DeviceName;
            return "Monitor " + (index + 1).ToString() + primary + device + "  " + width.ToString() + "x" + height.ToString();
        }
#else
        if (Display.displays != null && index >= 0 && index < Display.displays.Length)
        {
            Display display = Display.displays[index];
            return "Monitor " + (index + 1).ToString() + "  " + display.systemWidth.ToString() + "x" + display.systemHeight.ToString();
        }
#endif

        return "Monitor " + (index + 1).ToString();
    }

    public int GetPrimaryMonitorIndex()
    {
#if UNITY_STANDALONE_WIN
        MonitorCandidate[] monitors = EnumerateMonitors();
        for (int i = 0; i < monitors.Length; i++)
        {
            if (monitors[i].IsPrimary)
            {
                return i;
            }
        }
#endif

        return 0;
    }

    public int GetCurrentMonitorIndex()
    {
#if UNITY_STANDALONE_WIN
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = FindMainWindowForCurrentProcess();
        }

        if (_hwnd != IntPtr.Zero)
        {
            IntPtr current = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
            MonitorCandidate[] monitors = EnumerateMonitors();
            for (int i = 0; i < monitors.Length; i++)
            {
                if (monitors[i].Handle == current)
                {
                    return i;
                }
            }
        }
#endif

        return Mathf.Clamp(preferredMonitorIndex, 0, Mathf.Max(0, GetAvailableMonitorCount() - 1));
    }

    public void MoveToConfiguredMonitorNow()
    {
#if UNITY_EDITOR && UNITY_STANDALONE_WIN
        ApplyEditorGameViewMonitorPlacement(true);
#elif UNITY_STANDALONE_WIN
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = FindMainWindowForCurrentProcess();
        }

        ApplyStartupMonitorPlacement(true);
        StartCoroutine(ReapplyMonitorPlacementAfterResolution());
#endif
    }

    public bool TryGetCursorPositionInWindow(out Vector2 screenPosition)
    {
        return TryGetWindowCursorPosition(out screenPosition);
    }

    private void ConfigureCamera()
    {
        Camera cameraToConfigure = transparentCamera != null ? transparentCamera : Camera.main;
        if (cameraToConfigure == null || !transparentBackground)
        {
            return;
        }

        cameraToConfigure.clearFlags = CameraClearFlags.SolidColor;
        cameraToConfigure.backgroundColor = transparentKeyColor;
        cameraToConfigure.allowHDR = false;
        cameraToConfigure.allowMSAA = true;
    }

    public bool IsCursorOverHit()
    {
        if (!TryGetWindowCursorPosition(out Vector2 screenPosition))
        {
            return false;
        }

        return IsInsideExtraInteractiveRect(screenPosition) || IsPetHitAt(screenPosition);
    }

    public bool IsCursorOverPetHit()
    {
        return TryGetWindowCursorPosition(out Vector2 screenPosition) && IsPetHitAt(screenPosition);
    }

    public bool IsPetVisualHitAt(Vector2 screenPosition)
    {
        if (skeletonHitMask != null &&
            skeletonHitMask.TryContainsScreenPoint(screenPosition, out bool containsSkeletonHit))
        {
            return containsSkeletonHit;
        }

        return false;
    }

    public bool IsCursorOverMenuTriggerHit()
    {
        if (!TryGetWindowCursorPosition(out Vector2 screenPosition))
        {
            return false;
        }

        return IsMenuTriggerHitAt(screenPosition);
    }

    public bool IsMenuTriggerHitAt(Vector2 screenPosition)
    {
        return IsInsideExtraInteractiveRect(screenPosition) || IsPetVisualHitAt(screenPosition);
    }

    private bool IsPetHitAt(Vector2 screenPosition)
    {
        return IsPetHitAt(screenPosition, false);
    }

    private bool IsPetHitAt(Vector2 screenPosition, bool allowColliderFallback)
    {
        if (skeletonHitMask != null &&
            skeletonHitMask.TryContainsScreenPoint(screenPosition, out bool containsSkeletonHit))
        {
            if (containsSkeletonHit)
            {
                return true;
            }

            if (!allowColliderFallback)
            {
                return false;
            }
        }

        Camera rayCamera = transparentCamera != null ? transparentCamera : Camera.main;
        if (rayCamera == null)
        {
            return false;
        }

        Ray ray = rayCamera.ScreenPointToRay(screenPosition);
        return Physics.Raycast(ray, out _, 200f, hitMask, QueryTriggerInteraction.Collide);
    }

    private bool IsInsideExtraInteractiveRect(Vector2 screenPosition)
    {
        return _hasExtraInteractiveRect && _extraInteractiveRect.Contains(screenPosition);
    }

    private bool TryGetWindowCursorPosition(out Vector2 screenPosition)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (_hwnd != IntPtr.Zero &&
            GetWindowRect(_hwnd, out Rect rect) &&
            GetCursorPos(out Point point))
        {
            int width = rect.right - rect.left;
            int height = rect.bottom - rect.top;
            float x = point.x - rect.left;
            float y = height - (point.y - rect.top);
            screenPosition = new Vector2(x, y);
            return x >= 0f && x <= width && y >= 0f && y <= height;
        }
#endif
        if (Input.mousePresent)
        {
            screenPosition = Input.mousePosition;
            return true;
        }

        screenPosition = Vector2.zero;
        return false;
    }

    private void PollRuntimeWindowSettingsSave()
    {
        if (presentationMode != MonitorPresentationMode.SmallWindow || !persistRuntimeWindowSettings)
        {
            return;
        }

        _windowSettingsSaveTimer += Time.unscaledDeltaTime;
        if (_windowSettingsSaveTimer < 1.25f)
        {
            return;
        }

        _windowSettingsSaveTimer = 0f;
        CaptureRuntimeWindowSettings();
    }

    private void CaptureRuntimeWindowSettings()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!persistRuntimeWindowSettings ||
            string.IsNullOrWhiteSpace(windowSettingsKey) ||
            presentationMode != MonitorPresentationMode.SmallWindow)
        {
            return;
        }

        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = FindMainWindowForCurrentProcess();
        }

        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out Rect currentRect))
        {
            return;
        }

        MonitorCandidate[] monitors = EnumerateMonitors();
        if (monitors.Length == 0)
        {
            return;
        }

        IntPtr currentMonitor = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
        int monitorIndex = 0;
        for (int i = 0; i < monitors.Length; i++)
        {
            if (monitors[i].Handle == currentMonitor)
            {
                monitorIndex = i;
                break;
            }
        }

        MonitorCandidate monitor = monitors[monitorIndex];
        Rect work = ResolveOrientedPlacementRect(ResolvePlacementRect(monitor));
        int width = Mathf.Max(320, currentRect.right - currentRect.left);
        int height = Mathf.Max(240, currentRect.bottom - currentRect.top);
        int paddingX = Mathf.Max(0, monitorPaddingPixels.x);
        int paddingY = Mathf.Max(0, monitorPaddingPixels.y);
        int minX = work.left + paddingX;
        int maxX = Mathf.Max(minX, work.right - paddingX - width);
        int minY = work.top + paddingY;
        int maxY = Mathf.Max(minY, work.bottom - paddingY - height);

        preferredMonitorIndex = monitorIndex;
        primaryRightWindowSizePixels = new Vector2Int(width, height);
        resizeToTargetMonitorWorkArea = false;
        useFullMonitorBounds = false;
        monitorNormalizedPosition = new Vector2(
            maxX > minX ? Mathf.InverseLerp(minX, maxX, currentRect.left) : 0.5f,
            maxY > minY ? Mathf.InverseLerp(minY, maxY, currentRect.top) : 0.5f);
        SaveWindowSettings();
#endif
    }

    private void NormalizeWindowSettings()
    {
        primaryRightWindowSizePixels = new Vector2Int(
            Mathf.Clamp(primaryRightWindowSizePixels.x, 320, 1920),
            Mathf.Clamp(primaryRightWindowSizePixels.y, 240, 2160));
        primaryRightWindowPaddingPixels = new Vector2Int(
            Mathf.Clamp(primaryRightWindowPaddingPixels.x, 0, 240),
            Mathf.Clamp(primaryRightWindowPaddingPixels.y, 0, 240));
        monitorPaddingPixels = new Vector2Int(
            Mathf.Clamp(monitorPaddingPixels.x, 0, 240),
            Mathf.Clamp(monitorPaddingPixels.y, 0, 240));
        if (presentationMode == MonitorPresentationMode.SmallWindow)
        {
            preferredMonitorIndex = Mathf.Clamp(preferredMonitorIndex, 0, Mathf.Max(0, GetAvailableMonitorCount() - 1));
            resizeToTargetMonitorWorkArea = false;
            useFullMonitorBounds = false;
            monitorPaddingPixels = primaryRightWindowPaddingPixels;
        }
    }

    private void LoadWindowSettings()
    {
        if (_windowSettingsLoaded)
        {
            return;
        }

        if (!persistRuntimeWindowSettings || string.IsNullOrWhiteSpace(windowSettingsKey) || !PlayerPrefs.HasKey(windowSettingsKey))
        {
            _hasSavedWindowSettings = false;
            _windowSettingsLoaded = true;
            return;
        }

        try
        {
            WindowSettings settings = JsonUtility.FromJson<WindowSettings>(PlayerPrefs.GetString(windowSettingsKey));
            presentationMode = (MonitorPresentationMode)Mathf.Clamp(settings.presentationMode, 0, 2);
            orientationMode = (MonitorOrientationMode)Mathf.Clamp(settings.orientationMode, 0, 2);
            preferredMonitorIndex = Mathf.Max(0, settings.preferredMonitorIndex);
            moveToSecondaryMonitorOnStart = settings.moveToSecondaryMonitorOnStart;
            resizeToTargetMonitorWorkArea = settings.resizeToTargetMonitorWorkArea;
            useFullMonitorBounds = settings.useFullMonitorBounds;
            monitorNormalizedPosition = new Vector2(settings.monitorNormalizedX, settings.monitorNormalizedY);
            monitorPaddingPixels = new Vector2Int(settings.monitorPaddingX, settings.monitorPaddingY);
            int sideWidth = settings.primaryRightWindowWidth > 0 ? settings.primaryRightWindowWidth : primaryRightWindowSizePixels.x;
            int sideHeight = settings.primaryRightWindowHeight > 0 ? settings.primaryRightWindowHeight : primaryRightWindowSizePixels.y;
            int sidePaddingX = settings.primaryRightWindowPaddingX > 0 ? settings.primaryRightWindowPaddingX : primaryRightWindowPaddingPixels.x;
            int sidePaddingY = settings.primaryRightWindowPaddingY > 0 ? settings.primaryRightWindowPaddingY : primaryRightWindowPaddingPixels.y;
            primaryRightWindowSizePixels = new Vector2Int(sideWidth, sideHeight);
            primaryRightWindowPaddingPixels = new Vector2Int(sidePaddingX, sidePaddingY);
            _hasSavedWindowSettings = true;
        }
        catch (Exception exception)
        {
            _hasSavedWindowSettings = false;
            Debug.LogWarning("Failed to load transparent pet window settings: " + exception.Message);
        }

        _windowSettingsLoaded = true;
    }

    private void SaveWindowSettings()
    {
        if (!persistRuntimeWindowSettings || string.IsNullOrWhiteSpace(windowSettingsKey))
        {
            return;
        }

        NormalizeWindowSettings();
        WindowSettings settings = new WindowSettings
        {
            presentationMode = (int)presentationMode,
            orientationMode = (int)orientationMode,
            preferredMonitorIndex = preferredMonitorIndex,
            moveToSecondaryMonitorOnStart = moveToSecondaryMonitorOnStart,
            resizeToTargetMonitorWorkArea = resizeToTargetMonitorWorkArea,
            useFullMonitorBounds = useFullMonitorBounds,
            monitorNormalizedX = monitorNormalizedPosition.x,
            monitorNormalizedY = monitorNormalizedPosition.y,
            monitorPaddingX = monitorPaddingPixels.x,
            monitorPaddingY = monitorPaddingPixels.y,
            primaryRightWindowWidth = primaryRightWindowSizePixels.x,
            primaryRightWindowHeight = primaryRightWindowSizePixels.y,
            primaryRightWindowPaddingX = primaryRightWindowPaddingPixels.x,
            primaryRightWindowPaddingY = primaryRightWindowPaddingPixels.y
        };
        PlayerPrefs.SetString(windowSettingsKey, JsonUtility.ToJson(settings));
        PlayerPrefs.Save();
        _hasSavedWindowSettings = true;
    }

    [Serializable]
    private struct WindowSettings
    {
        public int presentationMode;
        public int orientationMode;
        public int preferredMonitorIndex;
        public bool moveToSecondaryMonitorOnStart;
        public bool resizeToTargetMonitorWorkArea;
        public bool useFullMonitorBounds;
        public float monitorNormalizedX;
        public float monitorNormalizedY;
        public int monitorPaddingX;
        public int monitorPaddingY;
        public int primaryRightWindowWidth;
        public int primaryRightWindowHeight;
        public int primaryRightWindowPaddingX;
        public int primaryRightWindowPaddingY;
    }

    private bool ShouldFindNativeWindow()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return configureNativeWindow || moveToSecondaryMonitorOnStart;
#else
        return false;
#endif
    }

    private bool ShouldUseBorderlessMonitorMode()
    {
        return moveToSecondaryMonitorOnStart &&
            resizeToTargetMonitorWorkArea &&
            (presentationMode == MonitorPresentationMode.BorderlessFullscreen ||
                orientationMode != MonitorOrientationMode.FollowMonitor);
    }

#if UNITY_STANDALONE_WIN
    private void ConfigureNativeWindow()
    {
        long style = GetWindowLongPtr(_hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu);
        style |= WsPopup | WsVisible;
        SetWindowLongPtr(_hwnd, GwlStyle, new IntPtr(style));

        long exStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64() | WsExLayered;
        if (alwaysOnTop)
        {
            exStyle |= WsExTopmost;
        }

        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(exStyle));
        SetLayeredWindowAttributes(_hwnd, ColorToColorRef(transparentKeyColor), 255, LwaColorKey | LwaAlpha);

        Margins margins = new Margins
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        SetWindowPos(
            _hwnd,
            alwaysOnTop ? HwndTopmost : HwndNotopmost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpFramechanged);
    }

    private void ConfigureResizableWindowFrame()
    {
        if (_hwnd == IntPtr.Zero || configureNativeWindow)
        {
            return;
        }

        long style = GetWindowLongPtr(_hwnd, GwlStyle).ToInt64();
        style &= ~WsPopup;
        style |= WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu | WsVisible;
        SetWindowLongPtr(_hwnd, GwlStyle, new IntPtr(style));

        long exStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        exStyle = alwaysOnTop ? exStyle | WsExTopmost : exStyle & ~WsExTopmost;
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(exStyle));

        SetWindowPos(
            _hwnd,
            alwaysOnTop ? HwndTopmost : HwndNotopmost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpFramechanged);
    }

    private void ApplyStartupMonitorPlacement(bool force)
    {
#if !UNITY_EDITOR
        if (_hwnd == IntPtr.Zero || (!force && !moveToSecondaryMonitorOnStart))
        {
            return;
        }

        if (!TryResolveTargetMonitor(out MonitorCandidate monitor))
        {
            return;
        }

        if (!GetWindowRect(_hwnd, out Rect currentRect))
        {
            return;
        }

        if (presentationMode == MonitorPresentationMode.SmallWindow)
        {
            ConfigureResizableWindowFrame();
        }

        Rect placementRect = ResolvePlacementRect(monitor);
        Rect work = ResolveOrientedPlacementRect(placementRect);
        int workWidth = Mathf.Max(1, work.right - work.left);
        int workHeight = Mathf.Max(1, work.bottom - work.top);
        int paddingX = Mathf.Max(0, monitorPaddingPixels.x);
        int paddingY = Mathf.Max(0, monitorPaddingPixels.y);

        int x;
        int y;
        int width;
        int height;
        if (presentationMode == MonitorPresentationMode.SmallWindow)
        {
            int maxWidth = Mathf.Max(320, workWidth - paddingX * 2);
            int maxHeight = Mathf.Max(240, workHeight - paddingY * 2);
            int currentWidth = Mathf.Max(1, primaryRightWindowSizePixels.x);
            int currentHeight = Mathf.Max(1, primaryRightWindowSizePixels.y);
            width = Mathf.Clamp(currentWidth, 320, maxWidth);
            height = Mathf.Clamp(currentHeight, 240, maxHeight);
            int minX = work.left + paddingX;
            int maxX = Mathf.Max(minX, work.right - paddingX - width);
            int minY = work.top + paddingY;
            int maxY = Mathf.Max(minY, work.bottom - paddingY - height);
            x = Mathf.RoundToInt(Mathf.Lerp(minX, maxX, Mathf.Clamp01(monitorNormalizedPosition.x)));
            y = Mathf.RoundToInt(Mathf.Lerp(minY, maxY, Mathf.Clamp01(monitorNormalizedPosition.y)));
        }
        else if (resizeToTargetMonitorWorkArea)
        {
            x = work.left + paddingX;
            y = work.top + paddingY;
            width = Mathf.Max(320, workWidth - paddingX * 2);
            height = Mathf.Max(240, workHeight - paddingY * 2);
        }
        else
        {
            width = Mathf.Max(1, currentRect.right - currentRect.left);
            height = Mathf.Max(1, currentRect.bottom - currentRect.top);
            int maxX = Mathf.Max(work.left + paddingX, work.right - paddingX - width);
            int maxY = Mathf.Max(work.top + paddingY, work.bottom - paddingY - height);
            float normalizedX = Mathf.Clamp01(monitorNormalizedPosition.x);
            float normalizedY = Mathf.Clamp01(monitorNormalizedPosition.y);
            x = Mathf.RoundToInt(Mathf.Lerp(work.left + paddingX, maxX, normalizedX));
            y = Mathf.RoundToInt(Mathf.Lerp(work.top + paddingY, maxY, normalizedY));
        }

        IntPtr zOrder = alwaysOnTop ? HwndTopmost : HwndNotopmost;
        if (resizeToTargetMonitorWorkArea &&
            presentationMode == MonitorPresentationMode.ExclusiveFullscreen &&
            IsSameRect(placementRect, work))
        {
            SetWindowPos(_hwnd, zOrder, x, y, width, height, SwpNoactivate | SwpFramechanged);
            Screen.SetResolution(width, height, FullScreenMode.ExclusiveFullScreen);
            SetWindowPos(_hwnd, zOrder, x, y, width, height, SwpNoactivate | SwpFramechanged);
            if (configureNativeWindow)
            {
                ConfigureNativeWindow();
            }
            return;
        }

        if (resizeToTargetMonitorWorkArea || presentationMode == MonitorPresentationMode.SmallWindow)
        {
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
        }

        SetWindowPos(
            _hwnd,
            zOrder,
            x,
            y,
            width,
            height,
            SwpNoactivate | SwpFramechanged);

        if (configureNativeWindow)
        {
            ConfigureNativeWindow();
        }
#endif
    }

    private static uint ColorToColorRef(Color color)
    {
        uint r = (uint)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f);
        uint g = (uint)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f);
        uint b = (uint)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f);
        return r | (g << 8) | (b << 16);
    }

    private IEnumerator ReapplyMonitorPlacementAfterResolution()
    {
#if !UNITY_EDITOR
        yield return null;
        yield return null;
        ApplyStartupMonitorPlacement(true);
#else
        yield break;
#endif
    }

#if UNITY_EDITOR
    private void ApplyEditorGameViewMonitorPlacement(bool force)
    {
#if UNITY_STANDALONE_WIN
        if (!force && !moveToSecondaryMonitorOnStart)
        {
            return;
        }

        if (!TryResolveTargetMonitor(out MonitorCandidate monitor))
        {
            return;
        }

        Rect placementRect = ResolveOrientedPlacementRect(ResolvePlacementRect(monitor));
        int paddingX = Mathf.Max(0, monitorPaddingPixels.x);
        int paddingY = Mathf.Max(0, monitorPaddingPixels.y);
        int x = placementRect.left + paddingX;
        int y = placementRect.top + paddingY;
        int width = Mathf.Max(320, placementRect.right - placementRect.left - paddingX * 2);
        int height = Mathf.Max(240, placementRect.bottom - placementRect.top - paddingY * 2);

        if (presentationMode == MonitorPresentationMode.SmallWindow)
        {
            int maxWidth = Mathf.Max(320, placementRect.right - placementRect.left - paddingX * 2);
            int maxHeight = Mathf.Max(240, placementRect.bottom - placementRect.top - paddingY * 2);
            width = Mathf.Clamp(Mathf.Max(1, primaryRightWindowSizePixels.x), 320, maxWidth);
            height = Mathf.Clamp(Mathf.Max(1, primaryRightWindowSizePixels.y), 240, maxHeight);
            int maxX = Mathf.Max(x, placementRect.right - paddingX - width);
            int maxY = Mathf.Max(y, placementRect.bottom - paddingY - height);
            x = Mathf.RoundToInt(Mathf.Lerp(x, maxX, Mathf.Clamp01(monitorNormalizedPosition.x)));
            y = Mathf.RoundToInt(Mathf.Lerp(y, maxY, Mathf.Clamp01(monitorNormalizedPosition.y)));
        }
        else if (!resizeToTargetMonitorWorkArea)
        {
            width = Mathf.Max(1, Screen.width);
            height = Mathf.Max(1, Screen.height);
            int maxX = Mathf.Max(x, placementRect.right - paddingX - width);
            int maxY = Mathf.Max(y, placementRect.bottom - paddingY - height);
            x = Mathf.RoundToInt(Mathf.Lerp(x, maxX, Mathf.Clamp01(monitorNormalizedPosition.x)));
            y = Mathf.RoundToInt(Mathf.Lerp(y, maxY, Mathf.Clamp01(monitorNormalizedPosition.y)));
        }

        Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        if (gameViewType == null)
        {
            Debug.LogWarning("Unity GameView type not found; cannot move editor preview to monitor.");
            return;
        }

        EditorWindow gameView = EditorWindow.GetWindow(gameViewType);
        gameView.ShowPopup();
        gameView.position = new UnityEngine.Rect(x, y, width, height);
        gameView.Focus();
#endif
    }
#endif

    private Rect ResolvePlacementRect(MonitorCandidate monitor)
    {
        Rect rect = resizeToTargetMonitorWorkArea && useFullMonitorBounds ? monitor.MonitorRect : monitor.WorkRect;
        if (!compensateMonitorDpiScale)
        {
            return rect;
        }

        float scale = GetMonitorDpiScale(monitor);
        if (scale <= 1.001f)
        {
            return rect;
        }

        return new Rect
        {
            left = Mathf.RoundToInt(rect.left * scale),
            top = Mathf.RoundToInt(rect.top * scale),
            right = Mathf.RoundToInt(rect.right * scale),
            bottom = Mathf.RoundToInt(rect.bottom * scale)
        };
    }

    private Rect ResolveOrientedPlacementRect(Rect rect)
    {
        if (orientationMode == MonitorOrientationMode.FollowMonitor)
        {
            return rect;
        }

        int rectWidth = Mathf.Max(1, rect.right - rect.left);
        int rectHeight = Mathf.Max(1, rect.bottom - rect.top);
        bool wantsLandscape = orientationMode == MonitorOrientationMode.Landscape;
        bool isLandscape = rectWidth >= rectHeight;
        if (wantsLandscape == isLandscape)
        {
            return rect;
        }

        int longSide = Mathf.Max(rectWidth, rectHeight);
        int shortSide = Mathf.Max(1, Mathf.Min(rectWidth, rectHeight));
        float targetAspect = wantsLandscape ? longSide / (float)shortSide : shortSide / (float)longSide;
        int width = rectWidth;
        int height = Mathf.RoundToInt(width / targetAspect);
        if (height > rectHeight)
        {
            height = rectHeight;
            width = Mathf.RoundToInt(height * targetAspect);
        }

        width = Mathf.Clamp(width, 1, rectWidth);
        height = Mathf.Clamp(height, 1, rectHeight);
        int centerX = rect.left + rectWidth / 2;
        int centerY = rect.top + rectHeight / 2;
        int left = centerX - width / 2;
        int top = centerY - height / 2;
        return new Rect
        {
            left = left,
            top = top,
            right = left + width,
            bottom = top + height
        };
    }

    private static bool IsSameRect(Rect a, Rect b)
    {
        return a.left == b.left &&
            a.top == b.top &&
            a.right == b.right &&
            a.bottom == b.bottom;
    }

    private static float GetMonitorDpiScale(MonitorCandidate monitor)
    {
        try
        {
            if (monitor.Handle != IntPtr.Zero && GetScaleFactorForMonitor(monitor.Handle, out int scaleFactor) == 0)
            {
                return Mathf.Max(1f, scaleFactor / 100f);
            }

            if (monitor.Handle != IntPtr.Zero && GetDpiForMonitor(monitor.Handle, 0, out uint dpiX, out uint dpiY) == 0)
            {
                return Mathf.Max(1f, Mathf.Max(dpiX, dpiY) / 96f);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 1f;
    }

    private bool TryResolveTargetMonitor(out MonitorCandidate monitor)
    {
        MonitorCandidate[] monitors = EnumerateMonitors();
        if (monitors.Length == 0)
        {
            monitor = default;
            return false;
        }

        int targetIndex = Mathf.Clamp(preferredMonitorIndex, 0, monitors.Length - 1);
        if (preferredMonitorIndex >= 0 && preferredMonitorIndex < monitors.Length)
        {
            monitor = monitors[targetIndex];
            return true;
        }

        for (int i = 0; i < monitors.Length; i++)
        {
            if (!monitors[i].IsPrimary)
            {
                monitor = monitors[i];
                return true;
            }
        }

        IntPtr nearest = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
        for (int i = 0; i < monitors.Length; i++)
        {
            if (monitors[i].Handle == nearest)
            {
                monitor = monitors[i];
                return true;
            }
        }

        monitor = monitors[0];
        return true;
    }

    private static MonitorCandidate[] EnumerateMonitors()
    {
        var monitors = new System.Collections.Generic.List<MonitorCandidate>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect monitorRect, IntPtr __) =>
        {
            MonitorInfoEx info = new MonitorInfoEx
            {
                cbSize = Marshal.SizeOf(typeof(MonitorInfoEx))
            };

            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new MonitorCandidate
                {
                    Handle = hMonitor,
                    Index = monitors.Count,
                    MonitorRect = info.rcMonitor,
                    WorkRect = info.rcWork,
                    IsPrimary = (info.dwFlags & 1u) != 0u,
                    DeviceName = info.szDevice
                });
            }
            else
            {
                monitors.Add(new MonitorCandidate
                {
                    Handle = hMonitor,
                    Index = monitors.Count,
                    MonitorRect = monitorRect,
                    WorkRect = monitorRect,
                    IsPrimary = false,
                    DeviceName = string.Empty
                });
            }

            return true;
        }, IntPtr.Zero);

        return monitors.ToArray();
    }

    private void KeepAlwaysOnTop()
    {
#if !UNITY_EDITOR
        if (_hwnd == IntPtr.Zero || !alwaysOnTop)
        {
            return;
        }

        _topmostTimer += Time.unscaledDeltaTime;
        if (_topmostTimer < 1f)
        {
            return;
        }

        _topmostTimer = 0f;
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
#endif
    }

    private void RefreshNativeWindowStyleIfNeeded()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!configureNativeWindow || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _nativeWindowRefreshTimer += Time.unscaledDeltaTime;
        if (_nativeWindowRefreshTimer < 0.5f)
        {
            return;
        }

        _nativeWindowRefreshTimer = 0f;
        long style = GetWindowLongPtr(_hwnd, GwlStyle).ToInt64();
        long exStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        bool needsStyleRepair =
            (style & WsCaption) != 0 ||
            (style & WsThickFrame) != 0 ||
            (style & WsMinimizeBox) != 0 ||
            (style & WsMaximizeBox) != 0 ||
            (style & WsSysMenu) != 0 ||
            (style & WsPopup) == 0;
        bool needsExStyleRepair =
            (exStyle & WsExLayered) == 0 ||
            (alwaysOnTop && (exStyle & WsExTopmost) == 0);

        if (needsStyleRepair || needsExStyleRepair)
        {
            ConfigureNativeWindow();
            long repairedExStyle = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
            repairedExStyle = _lastClickThrough
                ? repairedExStyle | WsExTransparent
                : repairedExStyle & ~WsExTransparent;
            SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(repairedExStyle));
            return;
        }

        SetLayeredWindowAttributes(_hwnd, ColorToColorRef(transparentKeyColor), 255, LwaColorKey | LwaAlpha);
#endif
    }

    private static IntPtr FindMainWindowForCurrentProcess()
    {
        uint currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        IntPtr fallback = IntPtr.Zero;
        IntPtr result = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowProcessId);
            if (windowProcessId != currentProcessId || !IsWindowVisible(hWnd))
            {
                return true;
            }

            if (fallback == IntPtr.Zero)
            {
                fallback = hWnd;
            }

            if (GetWindowTextLength(hWnd) > 0)
            {
                result = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result != IntPtr.Zero ? result : fallback;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }
#endif
}
