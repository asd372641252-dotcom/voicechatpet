using UnityEngine;
using System.Collections.Generic;
using System.Diagnostics;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif

public static class TransparentPetRuntimeInput
{
    private static readonly Dictionary<KeyCode, bool> PreviousKeyStates = new Dictionary<KeyCode, bool>();

    public static bool KeyDown(KeyCode keyCode)
    {
        try
        {
            if (keyCode != KeyCode.None && Input.GetKeyDown(keyCode))
            {
                return true;
            }
        }
        catch (System.InvalidOperationException)
        {
        }

        return WinKeyPressed(keyCode);
    }

    public static bool KeyHeld(KeyCode keyCode)
    {
        try
        {
            if (keyCode != KeyCode.None && Input.GetKey(keyCode))
            {
                return true;
            }
        }
        catch (System.InvalidOperationException)
        {
        }

        return WinKeyHeld(keyCode);
    }

    public static bool MouseButtonHeld(int button)
    {
        try
        {
            if (Input.GetMouseButton(button))
            {
                return true;
            }
        }
        catch (System.InvalidOperationException)
        {
        }

        return WinMouseButtonHeld(button);
    }

    public static Vector2 MousePosition()
    {
        try
        {
            return Input.mousePosition;
        }
        catch (System.InvalidOperationException)
        {
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return GetCursorPos(out Point point) ? new Vector2(point.x, point.y) : Vector2.zero;
#else
        return Vector2.zero;
#endif
    }

    public static float ScrollY()
    {
        try
        {
            return Input.mouseScrollDelta.y;
        }
        catch (System.InvalidOperationException)
        {
            return 0f;
        }
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern System.IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint processId);

    private static bool WinKeyHeld(KeyCode keyCode)
    {
        int vk = VirtualKeyFor(keyCode);
        if (vk == 0)
        {
            return false;
        }

#if UNITY_EDITOR
        if (!Application.isFocused && !IsUnityEditorForeground())
        {
            return false;
        }

        if (!IsControlKey(keyCode) && IsAnyControlHeld())
        {
            return false;
        }
#endif

        return (GetAsyncKeyState(vk) & unchecked((short)0x8000)) != 0;
    }

    private static bool WinKeyPressed(KeyCode keyCode)
    {
        bool current = WinKeyHeld(keyCode);
        PreviousKeyStates.TryGetValue(keyCode, out bool previous);
        PreviousKeyStates[keyCode] = current;
        return current && !previous;
    }

    private static bool WinMouseButtonHeld(int button)
    {
#if UNITY_EDITOR
        return false;
#else
        int vk = button == 0 ? 0x01 : button == 1 ? 0x02 : button == 2 ? 0x04 : 0;
        return vk != 0 && (GetAsyncKeyState(vk) & unchecked((short)0x8000)) != 0;
#endif
    }

    private static int VirtualKeyFor(KeyCode keyCode)
    {
        if (keyCode >= KeyCode.A && keyCode <= KeyCode.Z)
        {
            return 0x41 + ((int)keyCode - (int)KeyCode.A);
        }

        if (keyCode >= KeyCode.Alpha0 && keyCode <= KeyCode.Alpha9)
        {
            return 0x30 + ((int)keyCode - (int)KeyCode.Alpha0);
        }

        switch (keyCode)
        {
            case KeyCode.LeftArrow: return 0x25;
            case KeyCode.UpArrow: return 0x26;
            case KeyCode.RightArrow: return 0x27;
            case KeyCode.DownArrow: return 0x28;
            case KeyCode.Home: return 0x24;
            case KeyCode.Escape: return 0x1B;
            case KeyCode.Keypad0: return 0x60;
            case KeyCode.LeftShift: return 0xA0;
            case KeyCode.RightShift: return 0xA1;
            case KeyCode.LeftControl: return 0xA2;
            case KeyCode.RightControl: return 0xA3;
            case KeyCode.LeftAlt: return 0xA4;
            case KeyCode.RightAlt: return 0xA5;
            case KeyCode.LeftBracket: return 0xDB;
            case KeyCode.RightBracket: return 0xDD;
            case KeyCode.Semicolon: return 0xBA;
            case KeyCode.Quote: return 0xDE;
            default: return 0;
        }
    }

    private static bool IsControlKey(KeyCode keyCode)
    {
        return keyCode == KeyCode.LeftControl || keyCode == KeyCode.RightControl;
    }

    private static bool IsAnyControlHeld()
    {
        return (GetAsyncKeyState(0xA2) & unchecked((short)0x8000)) != 0
            || (GetAsyncKeyState(0xA3) & unchecked((short)0x8000)) != 0
            || (GetAsyncKeyState(0x11) & unchecked((short)0x8000)) != 0;
    }

    private static bool IsUnityEditorForeground()
    {
#if UNITY_EDITOR
        System.IntPtr foreground = GetForegroundWindow();
        if (foreground == System.IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out uint processId);
        return processId == (uint)Process.GetCurrentProcess().Id;
#else
        return false;
#endif
    }
#else
    private static bool WinKeyHeld(KeyCode keyCode) => false;
    private static bool WinKeyPressed(KeyCode keyCode) => false;
    private static bool WinMouseButtonHeld(int button) => false;
#endif
}
