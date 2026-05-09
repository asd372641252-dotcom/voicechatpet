using UnityEngine;
#if UNITY_STANDALONE_WIN
using System.Runtime.InteropServices;
#endif

[DisallowMultipleComponent]
public sealed class WindowDragHandle : MonoBehaviour
{
    public TransparentWindowController windowController;

    private bool _dragging;
    private bool _lastLeftDown;
    private Vector2Int _lastCursor;

#if UNITY_STANDALONE_WIN
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
#endif

    private void Reset()
    {
        windowController = GetComponent<TransparentWindowController>();
    }

    private void Update()
    {
        if (windowController == null)
        {
            return;
        }

        bool leftDown = IsLeftMouseDown();
        bool rightDown = IsRightMouseDown();
        if (_dragging && rightDown)
        {
            _dragging = false;
            windowController.IsDragging = false;
        }

        bool leftPressed = leftDown && !_lastLeftDown;

        if (!_dragging && !rightDown && leftPressed && windowController.IsCursorOverPetHit())
        {
            _dragging = windowController.TryGetDesktopCursorPosition(out _lastCursor);
            windowController.IsDragging = _dragging;
            windowController.SetClickThrough(false);
        }

        if (_dragging && leftDown)
        {
            if (windowController.TryGetDesktopCursorPosition(out Vector2Int cursor))
            {
                Vector2Int delta = cursor - _lastCursor;
                windowController.MoveWindowBy(delta);
                _lastCursor = cursor;
            }
        }

        if (_dragging && !leftDown)
        {
            _dragging = false;
            windowController.IsDragging = false;
        }

        _lastLeftDown = leftDown;
    }

    private static bool IsLeftMouseDown()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return (GetAsyncKeyState(VkLButton) & unchecked((short)0x8000)) != 0;
#else
        return Input.GetMouseButton(0);
#endif
    }

    private static bool IsRightMouseDown()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        return (GetAsyncKeyState(VkRButton) & unchecked((short)0x8000)) != 0;
#else
        return Input.GetMouseButton(1);
#endif
    }
}
