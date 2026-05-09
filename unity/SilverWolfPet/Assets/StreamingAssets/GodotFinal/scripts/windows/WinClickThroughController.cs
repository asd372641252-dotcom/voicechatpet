using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

[GlobalClass]
public partial class WinClickThroughController : Node
{
    private const int GwlExStyle = -20;
    private const int GwlpWndProc = -4;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExLayered = 0x00080000L;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const uint WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x02;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HtTransparent = new IntPtr(-1);

    [Export] public NodePath HitMaskPolygonPath { get; set; } = new NodePath("../HitMaskPolygon");
    [Export] public NodePath ContextMenuHandlerPath { get; set; } = new NodePath("..");
    [Export] public bool Enabled { get; set; } = true;
    [Export] public bool DebugShowHitMask { get; set; } = false;
    [Export] public bool UseNativeHitTestPassthrough { get; set; } = false;
    [Export(PropertyHint.Range, "1,20,1")] public double PollHz { get; set; } = 20.0;
    [Export(PropertyHint.Range, "0,1,0.01")] public double SwitchDebounceSec { get; set; } = 0.08;
    [Export(PropertyHint.Range, "0,40,1")] public double HitPaddingPixels { get; set; } = 14.0;
    [Export(PropertyHint.Range, "1,30,1")] public double RightClickDragThreshold { get; set; } = 8.0;
    [Export] public bool UseManualWin32Drag { get; set; } = true;
    [Export] public bool UseNativeCaptionDragFallback { get; set; } = true;
    [Export] public bool LogStateChanges { get; set; } = true;

    private Polygon2D _hitMaskPolygon;
    private Node _contextMenuHandler;
    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _originalWndProc = IntPtr.Zero;
    private WndProcDelegate _wndProcDelegate;
    private bool _clickThrough;
    private bool _lastHit;
    private bool _dragging;
    private bool _dragStartedForCurrentPress;
    private bool _interactionLocked;
    private bool _hasMouseLocalPosition;
    private bool _hasPendingEnableClickThrough;
    private bool _hasNativeHitTestDecision;
    private bool _lastNativeHitTestPassthrough;
    private bool _lastLeftButtonDown;
    private bool _lastRightButtonDown;
    private bool _rightPressedInHitMask;
    private bool _hasExtraInteractiveRect;
    private readonly Dictionary<string, Rect2> _namedExtraInteractiveRects = new Dictionary<string, Rect2>();
    private bool _initialized;
    private bool _manualDragActive;
    private Point _lastClientPoint;
    private Point _dragStartScreenPoint;
    private Rect _dragStartWindowRect;
    private Rect2 _extraInteractiveRect;
    private Vector2 _lastLocalPosition;
    private Vector2 _rightPressLocalPosition;
    private double _pollAccumulator;
    private double _pendingEnableAccumulator;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref Point lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public override async void _Ready()
    {
        if (OS.GetName() != "Windows")
        {
            GD.PushWarning("WinClickThroughController is Windows-only and is disabled on this OS.");
            Enabled = false;
            return;
        }

        _hitMaskPolygon = GetNodeOrNull<Polygon2D>(HitMaskPolygonPath);
        if (_hitMaskPolygon == null)
        {
            GD.PushError($"WinClickThroughController could not find HitMaskPolygon at {HitMaskPolygonPath}.");
            Enabled = false;
            return;
        }

        _hitMaskPolygon.Visible = DebugShowHitMask;
        _contextMenuHandler = GetNodeOrNull(ContextMenuHandlerPath);

        // Parent bootstrap applies Godot window flags in its _Ready after child _Ready calls.
        // Wait until those flags settle, then write the native Win32 extended style.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        _hwnd = new IntPtr(DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle));
        if (_hwnd == IntPtr.Zero)
        {
            GD.PushWarning("WinClickThroughController could not resolve the native HWND; disabled for this run.");
            Enabled = false;
            return;
        }

        long oldStyle = EnsureLayeredStyleOnce(ReadExStyle());
        _clickThrough = (oldStyle & WsExTransparent) != 0;
        if (UseNativeHitTestPassthrough)
        {
            GD.PushWarning("WM_NCHITTEST/HTTRANSPARENT passthrough is disabled for this Godot top-level window; using WS_EX_TRANSPARENT polling instead.");
            UseNativeHitTestPassthrough = false;
        }
        if (LogStateChanges)
        {
            GD.Print($"WinClickThroughController hwnd=0x{_hwnd.ToInt64():X} initial_exStyle=0x{ReadExStyle():X}");
        }
        SetClickThrough(false);
        _initialized = true;
        PollMouseAndApplyHitMask(SwitchDebounceSec);
    }

    public override void _Process(double delta)
    {
        if (!Enabled || !_initialized || _hwnd == IntPtr.Zero || _hitMaskPolygon == null)
        {
            return;
        }

        _hitMaskPolygon.Visible = DebugShowHitMask;
        if (_manualDragActive)
        {
            UpdateManualWindowDrag();
            return;
        }

        _pollAccumulator += delta;
        double interval = 1.0 / Math.Max(PollHz, 1.0);
        if (_pollAccumulator < interval)
        {
            return;
        }

        double elapsed = _pollAccumulator;
        _pollAccumulator = 0.0;
        PollMouseAndApplyHitMask(elapsed);
    }

    public override void _ExitTree()
    {
        if (_hwnd != IntPtr.Zero)
        {
            RestoreNativeHitTestPassthrough();
            SetClickThrough(false);
        }
    }

    public void SetDebugShowHitMask(bool visible)
    {
        DebugShowHitMask = visible;
        if (_hitMaskPolygon != null)
        {
            _hitMaskPolygon.Visible = visible;
        }
    }

    public void SetInteractionLock(bool locked)
    {
        _interactionLocked = locked;
        if (locked)
        {
            ClearPendingEnableClickThrough();
            SetClickThrough(false);
        }
    }

    public void SetExtraInteractiveRect(Rect2 rect, bool enabled)
    {
        _extraInteractiveRect = rect;
        _hasExtraInteractiveRect = enabled;
    }

    public void SetExtraInteractiveRectNamed(string key, Rect2 rect, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            SetExtraInteractiveRect(rect, enabled);
            return;
        }

        if (enabled)
        {
            _namedExtraInteractiveRects[key] = rect;
        }
        else
        {
            _namedExtraInteractiveRects.Remove(key);
        }
    }

    public void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        long oldStyle = ReadExStyle();
        bool actualClickThrough = (oldStyle & WsExTransparent) != 0;
        bool actualLayered = (oldStyle & WsExLayered) != 0;
        if (_clickThrough == enabled && actualClickThrough == enabled && actualLayered)
        {
            return;
        }

        long layeredStyle = oldStyle | WsExLayered;
        long newStyle = enabled
            ? layeredStyle | WsExTransparent
            : layeredStyle & ~WsExTransparent;

        if (newStyle != oldStyle)
        {
            SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(newStyle));
        }

        _clickThrough = enabled;
        LogState();
    }

    private void PollMouseAndApplyHitMask(double elapsed)
    {
        if (!TryGetMouseLocalPosition(out Vector2 localPosition))
        {
            return;
        }

        bool leftButtonDown = IsLeftButtonDown();
        bool rightButtonDown = IsRightButtonDown();
        if (_dragging && !leftButtonDown)
        {
            _dragging = false;
            _dragStartedForCurrentPress = false;
            _manualDragActive = false;
        }
        if (!leftButtonDown)
        {
            _dragStartedForCurrentPress = false;
        }

        bool maskHit = IsInsideHitMask(localPosition);
        bool extraHit = IsInsideExtraInteractiveRect(localPosition);
        bool hit = maskHit || extraHit;
        _lastHit = hit;

        if (_interactionLocked)
        {
            ClearPendingEnableClickThrough();
            SetClickThrough(false);
            TrackNativeButtonEdges(localPosition, hit, maskHit, leftButtonDown, rightButtonDown);
            _lastLocalPosition = localPosition;
            return;
        }

        if (_dragging)
        {
            ClearPendingEnableClickThrough();
            SetClickThrough(false);
            TrackNativeButtonEdges(localPosition, hit, maskHit, leftButtonDown, rightButtonDown);
            _lastLocalPosition = localPosition;
            return;
        }

        Point currentClientPoint = new Point
        {
            X = (int)localPosition.X,
            Y = (int)localPosition.Y
        };
        bool mouseMoved = !_hasMouseLocalPosition ||
            currentClientPoint.X != _lastClientPoint.X ||
            currentClientPoint.Y != _lastClientPoint.Y;

        _lastLocalPosition = localPosition;
        TrackNativeButtonEdges(localPosition, hit, maskHit, leftButtonDown, rightButtonDown);
        if (!mouseMoved)
        {
            AdvancePendingEnableClickThrough(elapsed);
            return;
        }

        _lastClientPoint = currentClientPoint;
        _hasMouseLocalPosition = true;

        if (hit)
        {
            ClearPendingEnableClickThrough();
            SetClickThrough(false);
        }
        else
        {
            QueueEnableClickThrough(elapsed);
        }
    }

    private void TrackNativeButtonEdges(Vector2 localPosition, bool hit, bool maskHit, bool leftButtonDown, bool rightButtonDown)
    {
        if (leftButtonDown && !_lastLeftButtonDown && maskHit && !_dragStartedForCurrentPress)
        {
            _dragging = true;
            _dragStartedForCurrentPress = true;
            ClearPendingEnableClickThrough();
            SetClickThrough(false);
            StartWindowDrag(localPosition, hit);
        }

        if (rightButtonDown && !_lastRightButtonDown && maskHit)
        {
            _rightPressedInHitMask = true;
            _rightPressLocalPosition = localPosition;
            ClearPendingEnableClickThrough();
            SetClickThrough(false);
        }

        if (!rightButtonDown && _lastRightButtonDown)
        {
            bool clickDistanceOk = _rightPressLocalPosition.DistanceTo(localPosition) <= RightClickDragThreshold;
            if (_rightPressedInHitMask && clickDistanceOk)
            {
                ClearPendingEnableClickThrough();
                SetClickThrough(false);
                if (LogStateChanges)
                {
                    GD.Print($"Native right menu mouse_local_position={localPosition} hit={hit.ToString().ToLowerInvariant()}");
                }
                _contextMenuHandler?.Call("show_context_menu_from_native", localPosition);
            }
            _rightPressedInHitMask = false;
        }

        if (rightButtonDown && _rightPressedInHitMask)
        {
            ClearPendingEnableClickThrough();
            SetClickThrough(false);
        }

        _lastLeftButtonDown = leftButtonDown;
        _lastRightButtonDown = rightButtonDown;
    }

    private void QueueEnableClickThrough(double elapsed)
    {
        if (UseNativeHitTestPassthrough)
        {
            ClearPendingEnableClickThrough();
            return;
        }

        if (_clickThrough)
        {
            ClearPendingEnableClickThrough();
            return;
        }

        if (!_hasPendingEnableClickThrough)
        {
            _hasPendingEnableClickThrough = true;
            _pendingEnableAccumulator = 0.0;
        }

        AdvancePendingEnableClickThrough(elapsed);
    }

    private void StartWindowDrag(Vector2 localPosition, bool hit)
    {
        if (LogStateChanges)
        {
            GD.Print($"Native left drag start mouse_local_position={localPosition} hit={hit.ToString().ToLowerInvariant()}");
        }

        if (UseManualWin32Drag)
        {
            StartManualWindowDrag();
        }
        else if (UseNativeCaptionDragFallback)
        {
            ReleaseCapture();
            SendMessage(_hwnd, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
        }
        else
        {
            DisplayServer.WindowStartDrag();
        }
    }

    private void StartManualWindowDrag()
    {
        if (!GetCursorPos(out _dragStartScreenPoint) || !GetWindowRect(_hwnd, out _dragStartWindowRect))
        {
            if (UseNativeCaptionDragFallback)
            {
                ReleaseCapture();
                SendMessage(_hwnd, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
            }
            else
            {
                DisplayServer.WindowStartDrag();
            }
            return;
        }

        _manualDragActive = true;
    }

    private void UpdateManualWindowDrag()
    {
        if (!IsLeftButtonDown())
        {
            _manualDragActive = false;
            _dragging = false;
            _dragStartedForCurrentPress = false;
            return;
        }

        if (!GetCursorPos(out Point currentScreenPoint))
        {
            return;
        }

        int dx = currentScreenPoint.X - _dragStartScreenPoint.X;
        int dy = currentScreenPoint.Y - _dragStartScreenPoint.Y;
        SetClickThrough(false);
        SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            _dragStartWindowRect.Left + dx,
            _dragStartWindowRect.Top + dy,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate
        );
    }

    private void AdvancePendingEnableClickThrough(double elapsed)
    {
        if (!_hasPendingEnableClickThrough || _clickThrough || _dragging)
        {
            return;
        }

        _pendingEnableAccumulator += elapsed;
        if (_pendingEnableAccumulator >= SwitchDebounceSec)
        {
            SetClickThrough(true);
            ClearPendingEnableClickThrough();
        }
    }

    private void ClearPendingEnableClickThrough()
    {
        _hasPendingEnableClickThrough = false;
        _pendingEnableAccumulator = 0.0;
    }

    private void InstallNativeHitTestPassthrough()
    {
        if (_hwnd == IntPtr.Zero || _originalWndProc != IntPtr.Zero)
        {
            return;
        }

        _wndProcDelegate = NativeWndProc;
        IntPtr callback = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = SetWindowLongPtr(_hwnd, GwlpWndProc, callback);
        if (_originalWndProc == IntPtr.Zero)
        {
            GD.PushWarning("WinClickThroughController could not install native WM_NCHITTEST passthrough; falling back to WS_EX_TRANSPARENT toggling.");
            UseNativeHitTestPassthrough = false;
            return;
        }

        if (LogStateChanges)
        {
            GD.Print($"WinClickThroughController native_hit_test_passthrough=true original_wndproc=0x{_originalWndProc.ToInt64():X}");
        }
    }

    private void RestoreNativeHitTestPassthrough()
    {
        if (_hwnd == IntPtr.Zero || _originalWndProc == IntPtr.Zero)
        {
            return;
        }

        SetWindowLongPtr(_hwnd, GwlpWndProc, _originalWndProc);
        _originalWndProc = IntPtr.Zero;
        _wndProcDelegate = null;
    }

    private IntPtr NativeWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (hWnd == _hwnd && msg == WmNcHitTest && ShouldReturnTransparentHitTest())
        {
            return HtTransparent;
        }

        if (_originalWndProc != IntPtr.Zero)
        {
            return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private bool ShouldReturnTransparentHitTest()
    {
        if (!Enabled || !_initialized || _interactionLocked || _dragging || _hitMaskPolygon == null)
        {
            LogNativeHitTestDecision(false, _lastLocalPosition, _lastHit);
            return false;
        }

        if (!TryGetMouseLocalPosition(out Vector2 localPosition))
        {
            LogNativeHitTestDecision(false, _lastLocalPosition, _lastHit);
            return false;
        }

        bool hit = IsInsideHitMask(localPosition) || IsInsideExtraInteractiveRect(localPosition);
        _lastHit = hit;
        _lastLocalPosition = localPosition;
        bool passthrough = !hit;
        LogNativeHitTestDecision(passthrough, localPosition, hit);
        return passthrough;
    }

    private void LogNativeHitTestDecision(bool passthrough, Vector2 localPosition, bool hit)
    {
        if (!LogStateChanges)
        {
            return;
        }

        if (_hasNativeHitTestDecision && _lastNativeHitTestPassthrough == passthrough)
        {
            return;
        }

        _hasNativeHitTestDecision = true;
        _lastNativeHitTestPassthrough = passthrough;
        GD.Print($"NativeHitTest passthrough={passthrough.ToString().ToLowerInvariant()} mouse_local_position={localPosition} hit={hit.ToString().ToLowerInvariant()}");
    }

    private long EnsureLayeredStyleOnce(long oldStyle)
    {
        long newStyle = oldStyle | WsExLayered;
        if (newStyle != oldStyle)
        {
            SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr(newStyle));
        }

        return newStyle;
    }

    private bool TryGetMouseLocalPosition(out Vector2 localPosition)
    {
        localPosition = Vector2.Zero;
        if (!GetCursorPos(out Point screenPoint))
        {
            return false;
        }

        Point clientPoint = screenPoint;
        if (!ScreenToClient(_hwnd, ref clientPoint))
        {
            return false;
        }

        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        Vector2I windowSize = DisplayServer.WindowGetSize();
        float scaleX = windowSize.X > 0 ? viewportSize.X / windowSize.X : 1.0f;
        float scaleY = windowSize.Y > 0 ? viewportSize.Y / windowSize.Y : 1.0f;
        localPosition = new Vector2(clientPoint.X * scaleX, clientPoint.Y * scaleY);
        return true;
    }

    private bool IsInsideHitMask(Vector2 windowLocalPosition)
    {
        if (_hitMaskPolygon == null)
        {
            return false;
        }

        if (_hitMaskPolygon.HasMethod("is_point_inside_hit_mask"))
        {
            Variant result = _hitMaskPolygon.Call("is_point_inside_hit_mask", windowLocalPosition);
            if (result.VariantType == Variant.Type.Bool)
            {
                return result.AsBool();
            }
        }

        if (_hitMaskPolygon.Polygon.Length < 3)
        {
            return false;
        }

        Vector2 polygonLocalPosition = _hitMaskPolygon.ToLocal(windowLocalPosition);
        if (Geometry2D.IsPointInPolygon(polygonLocalPosition, _hitMaskPolygon.Polygon))
        {
            return true;
        }

        return HitPaddingPixels > 0.0 && IsNearPolygonEdge(polygonLocalPosition, (float)HitPaddingPixels);
    }

    private bool IsInsideExtraInteractiveRect(Vector2 windowLocalPosition)
    {
        if (_hasExtraInteractiveRect && _extraInteractiveRect.HasPoint(windowLocalPosition))
        {
            return true;
        }
        foreach (Rect2 rect in _namedExtraInteractiveRects.Values)
        {
            if (rect.HasPoint(windowLocalPosition))
            {
                return true;
            }
        }
        return false;
    }

    private long ReadExStyle()
    {
        return GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
    }

    private bool IsLeftButtonDown()
    {
        return (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
    }

    private bool IsRightButtonDown()
    {
        return (GetAsyncKeyState(VkRButton) & 0x8000) != 0;
    }

    private bool IsNearPolygonEdge(Vector2 point, float padding)
    {
        Vector2[] polygon = _hitMaskPolygon.Polygon;
        float paddingSquared = padding * padding;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % polygon.Length];
            if (DistanceSquaredToSegment(point, a, b) <= paddingSquared)
            {
                return true;
            }
        }
        return false;
    }

    private static float DistanceSquaredToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared <= 0.000001f)
        {
            return point.DistanceSquaredTo(a);
        }

        float t = Mathf.Clamp((point - a).Dot(ab) / lengthSquared, 0.0f, 1.0f);
        Vector2 closest = a + ab * t;
        return point.DistanceSquaredTo(closest);
    }

    private void LogState()
    {
        if (!LogStateChanges)
        {
            return;
        }

        string click = _clickThrough.ToString().ToLowerInvariant();
        string hit = _lastHit.ToString().ToLowerInvariant();
        long style = ReadExStyle();
        bool hasTransparent = (style & WsExTransparent) != 0;
        GD.Print($"SetClickThrough click_through={click} mouse_local_position={_lastLocalPosition} hit={hit} exStyle=0x{style:X} has_WS_EX_TRANSPARENT={hasTransparent.ToString().ToLowerInvariant()}");
    }
}
