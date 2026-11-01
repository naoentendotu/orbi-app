using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Orbi.Core;

/// <summary>Global low-level hook feeding HoldDetector. Only swallows the hold key when it is not a modifier (see Trigger.Swallows).</summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action<HoldAction>? Action;

    readonly HoldDetector _detector;
    readonly DispatcherTimer _timer = new();
    readonly Native.LowLevelKeyboardProc _proc; // kept alive: GC'd delegate = crash
    IntPtr _hook;

    /// <summary>Virtual-key code that opens the menu when held (default left Alt; right Alt is AltGr on ABNT2).</summary>
    public int HoldKey { get; set; } = 0xA4;

    /// <summary>False in combo mode or while a new shortcut is being recorded.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) { _timer.Stop(); _detector.Up(); } }
    }
    bool _enabled = true;

    public KeyboardHook(HoldDetector detector)
    {
        _detector = detector;
        _proc = Hook;
        _timer.Tick += (_, _) => { _timer.Stop(); Raise(_detector.Tick(DateTime.UtcNow)); };
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _enabled)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & Native.LLKHF_INJECTED) == 0)
            {
                bool down = wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN;
                if (k.vkCode == HoldKey)
                {
                    if (!down) { _timer.Stop(); Raise(_detector.Up()); }
                    else if (_detector.IsIdle)
                    {
                        _detector.Down(DateTime.UtcNow);
                        _timer.Interval = _detector.Delay;
                        _timer.Start();
                    }
                    if (Trigger.Swallows(HoldKey)) return 1; // dedicated key: don't let Caps Lock toggle / F-key fire
                }
                else if (down) Raise(_detector.OtherKey());
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Raise(HoldAction a)
    {
        if (a == HoldAction.None) return;
        if (a == HoldAction.Open && Trigger.IsModifier(HoldKey)) Native.TapDummyKey(); // stops Alt menu bar / Win Start menu
        // Keep the hook callback fast: Windows silently drops slow LL hooks.
        Dispatcher.CurrentDispatcher.BeginInvoke(() => Action?.Invoke(a));
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
