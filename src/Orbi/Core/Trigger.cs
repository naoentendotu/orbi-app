using System.Windows.Input;
using System.Windows.Interop;

namespace Orbi.Core;

/// <summary>What opens the menu: a key held alone, or a key combination. Pure rules + names.</summary>
public static class Trigger
{
    public const int ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8; // same bits as RegisterHotKey and WPF ModifierKeys

    static readonly int[] Modifiers = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C]; // L/R Shift, Ctrl, Alt, Win

    public static bool IsModifier(int vk) => Modifiers.Contains(vk);

    static bool IsFunctionKey(int vk) => vk is >= 0x70 and <= 0x87; // F1–F24

    /// <summary>Hold keys: modifiers (still work normally) or keys rarely typed (taken over by Orbi).
    /// Letters, Enter, Esc etc. are refused: they would be swallowed and stop working.</summary>
    public static bool IsAllowedHoldKey(int vk) =>
        IsModifier(vk) || IsFunctionKey(vk) || vk is 0x14 /* CapsLock */ or 0x91 /* ScrollLock */ or 0x13 /* Pause */ or 0x2D /* Insert */ or 0x5D /* Menu */;

    /// <summary>Combos need a modifier, except F-keys/Pause/ScrollLock, so normal typing is never hijacked.</summary>
    public static bool IsAllowedCombo(int mods, int vk) =>
        !IsModifier(vk) && vk != 0 && (mods != 0 || IsFunctionKey(vk) || vk is 0x13 or 0x91);

    /// <summary>Non-modifier hold keys are swallowed by the hook (otherwise Caps Lock would toggle, F-keys would fire).</summary>
    public static bool Swallows(int vk) => !IsModifier(vk);

    public static string KeyName(int vk) => vk switch
    {
        0xA4 => "Alt esquerdo",
        0xA5 => "Alt direito (AltGr)",
        0xA2 => "Ctrl esquerdo",
        0xA3 => "Ctrl direito",
        0xA0 => "Shift esquerdo",
        0xA1 => "Shift direito",
        0x5B or 0x5C => "Win",
        0x14 => "Caps Lock",
        0x91 => "Scroll Lock",
        0x13 => "Pause",
        0x2D => "Insert",
        0x5D => "Menu",
        0x20 => "Espaço",
        0x0D => "Enter",
        0x09 => "Tab",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };

    public static string ComboName(int mods, int vk)
    {
        var parts = new List<string>();
        if ((mods & ModCtrl) != 0) parts.Add("Ctrl");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join(" + ", parts);
    }
}

/// <summary>System-wide hotkey via RegisterHotKey (fails cleanly if another app owns the combo).</summary>
public sealed class Hotkey : IDisposable
{
    const int Id = 1, WM_HOTKEY = 0x0312, MOD_NOREPEAT = 0x4000;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    readonly HwndSource _window = new(new HwndSourceParameters("OrbiHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });

    public event Action? Pressed;

    public Hotkey() => _window.AddHook((IntPtr _, int msg, IntPtr w, IntPtr __, ref bool handled) =>
    {
        if (msg == WM_HOTKEY && (int)w == Id) { handled = true; Pressed?.Invoke(); }
        return IntPtr.Zero;
    });

    public bool Register(int mods, int vk)
    {
        Unregister();
        return RegisterHotKey(_window.Handle, Id, (uint)(mods | MOD_NOREPEAT), (uint)vk);
    }

    public void Unregister() => UnregisterHotKey(_window.Handle, Id);

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }
}
