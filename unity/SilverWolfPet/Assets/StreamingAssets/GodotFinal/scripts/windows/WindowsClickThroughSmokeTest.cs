using Godot;
using System;
using System.Runtime.InteropServices;

[GlobalClass]
public partial class WindowsClickThroughSmokeTest : Node
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExLayered = 0x00080000L;

    [Export] public Vector2I SmokeWindowSize { get; set; } = new Vector2I(512, 720);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public override async void _Ready()
    {
        if (OS.GetName() != "Windows")
        {
            GD.PushWarning("WindowsClickThroughSmokeTest is Windows-only.");
            return;
        }

        ConfigureGodotWindow();
        BuildVisualBoundsOverlay();

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var hwnd = new IntPtr(DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle));
        if (hwnd == IntPtr.Zero)
        {
            GD.PushError("WindowsClickThroughSmokeTest hwnd=0x0; native window handle is not ready.");
            return;
        }

        long beforeStyle = ReadExStyle(hwnd);
        long requestedStyle = beforeStyle | WsExLayered | WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(requestedStyle));
        long afterStyle = ReadExStyle(hwnd);
        bool hasTransparent = (afterStyle & WsExTransparent) != 0;

        GD.Print($"WindowsClickThroughSmokeTest hwnd=0x{hwnd.ToInt64():X}");
        GD.Print($"WindowsClickThroughSmokeTest before_exStyle=0x{beforeStyle:X}");
        GD.Print($"WindowsClickThroughSmokeTest after_exStyle=0x{afterStyle:X}");
        GD.Print($"WindowsClickThroughSmokeTest has_WS_EX_TRANSPARENT={hasTransparent.ToString().ToLowerInvariant()}");
    }

    private void ConfigureGodotWindow()
    {
        Window window = GetWindow();
        window.Borderless = true;
        window.AlwaysOnTop = true;
        window.Transparent = true;
        window.TransparentBg = true;
        window.Size = SmokeWindowSize;

        GetViewport().TransparentBg = true;
        RenderingServer.SetDefaultClearColor(new Color(0.0f, 0.0f, 0.0f, 0.0f));
    }

    private void BuildVisualBoundsOverlay()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        var rect = new ColorRect
        {
            Name = "SmokeWindowBounds",
            Color = new Color(0.1f, 0.7f, 1.0f, 0.18f),
            Size = SmokeWindowSize,
        };
        layer.AddChild(rect);

        var label = new Label
        {
            Text = "Win32 full-window click-through smoke test",
            Position = new Vector2(16.0f, 16.0f),
        };
        label.AddThemeColorOverride("font_color", Colors.White);
        layer.AddChild(label);
    }

    private static long ReadExStyle(IntPtr hwnd)
    {
        return GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
    }
}
