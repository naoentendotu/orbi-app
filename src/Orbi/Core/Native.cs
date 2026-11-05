using System.Runtime.InteropServices;

namespace Orbi.Core;

static class Native
{
    public const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_CLIPBOARDUPDATE = 0x031D;
    public const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    public const uint LLKHF_INJECTED = 0x10, KEYEVENTF_KEYUP = 2;

    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT r, int size);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>Taps an unassigned VK so the focused app's menu bar doesn't activate on Alt-up (PowerToys trick).</summary>
    public static void TapDummyKey()
    {
        var down = new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0xE8 } } };
        var up = down; up.u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    /// <summary>One mouse-wheel notch at the cursor; +1 = up.</summary>
    public static void Wheel(int direction)
    {
        var i = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { mouseData = unchecked((uint)(120 * direction)), dwFlags = 0x0800 /* MOUSEEVENTF_WHEEL */ } } };
        SendInput(1, [i], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Visible window bounds in pixels, without the invisible resize border/shadow.</summary>
    public static RECT WindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    public static void SetExStyle(IntPtr hwnd, int flag, bool on)
    {
        var s = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, on ? s | flag : s & ~flag);
    }
}
