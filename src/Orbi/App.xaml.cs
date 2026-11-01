using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Orbi.Core;
using Orbi.Modules.Clipboard;
using Orbi.Modules.Downloader;
using Orbi.Modules.Monitor;
using Orbi.Modules.Screenshot;
using Orbi.Modules.Server;
using Orbi.Modules.Snippets;
using Orbi.Modules.Mic;
using Orbi.Modules.Settings;
using Orbi.Modules.Timer;
using Orbi.Shell;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Orbi;

public partial class App : Application
{
    public static Core.Settings Settings { get; private set; } = new();

    static App Instance => (App)Current;

    public static RadialWindow Widget => Instance._window!;

    Mutex? _single;
    KeyboardHook? _hook;
    HoldDetector? _detector;
    Hotkey? _hotkey;
    bool _triggerPaused;
    RadialWindow? _window;
    Forms.NotifyIcon? _tray;
    SettingsModule? _settingsModule;
    readonly MediaPlayer _player = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\Orbi.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception); ex.Handled = true; }; // stay alive in the tray

        Settings = Core.Settings.Load();
        ApplyTheme();

        // Register new modules here.
        IModule[] modules = [new ClipboardModule(), new ScreenshotModule(), new TimerModule(), new SnippetsModule(),
            new MicModule(), new MonitorModule(), new DownloaderModule(), new ServerModule()];
        _settingsModule = new SettingsModule(modules);
        _window = new RadialWindow(() =>
            Settings.Modules.Select(id => modules.FirstOrDefault(m => m.Id == id)).OfType<IModule>().Append(_settingsModule).ToList());

        _detector = new HoldDetector(TimeSpan.FromMilliseconds(Settings.HoldDelayMs));
        _hook = new KeyboardHook(_detector);
        _hook.Action += a =>
        {
            if (a == HoldAction.Open) _window.ShowMenu(pinned: !Settings.CloseOnRelease);
            else _window.OnAltReleased();
        };
        _hotkey = new Hotkey();
        _hotkey.Pressed += () => { if (_window.IsVisible) _window.HideMenu(); else _window.ShowMenu(pinned: true); }; // combo toggles

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Orbi", null, (_, _) => _window.ShowMenu(pinned: true));
        menu.Items.Add("Ajustes", null, (_, _) => _window.ShowMenu(expand: _settingsModule));
        menu.Items.Add("Sair", null, (_, _) => Shutdown());
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream, Forms.SystemInformation.SmallIconSize),
            ContextMenuStrip = menu,
            Visible = true,
        };
        if (!ApplyTrigger()) Notify("Orbi", $"O atalho {Core.Trigger.ComboName(Settings.ComboMods, Settings.ComboKey)} já está em uso por outro programa. Troque em Ajustes.", error: true);
        _tray.MouseClick += (_, ev) => { if (ev.Button == Forms.MouseButtons.Left) _window.ShowMenu(pinned: true); };
        if (e.Args.Contains("--open")) _window.ShowMenu(pinned: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _hotkey?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }

    public static void ApplySettings()
    {
        Settings.Save();
        Instance.ApplyTheme();
        Instance._window?.Refresh();
    }

    /// <summary>Applies the open trigger (hold key or combo). Returns false if the combo is taken by another app.</summary>
    public static bool ApplyTrigger() => Instance.ApplyTriggerCore();

    /// <summary>While recording a new shortcut the current one must not fire.</summary>
    public static bool PauseTrigger(bool paused)
    {
        Instance._triggerPaused = paused;
        return Instance.ApplyTriggerCore();
    }

    bool ApplyTriggerCore()
    {
        var s = Settings;
        bool hold = s.Trigger != "combo";
        _detector!.Delay = TimeSpan.FromMilliseconds(s.HoldDelayMs);
        _hook!.HoldKey = s.HoldKey;
        _hook.Enabled = hold && !_triggerPaused;
        if (_tray != null)
        {
            var text = hold ? $"Orbi (segure {Core.Trigger.KeyName(s.HoldKey)})" : $"Orbi ({Core.Trigger.ComboName(s.ComboMods, s.ComboKey)})";
            _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
        }
        if (hold || _triggerPaused) { _hotkey!.Unregister(); return true; }
        return _hotkey!.Register(s.ComboMods, s.ComboKey);
    }

    void ApplyTheme()
    {
        bool dark = Settings.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0,
        };
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };

        // User colors override the theme's keys (top-level resources win over merged dictionaries); empty = theme default.
        var accent = Palette.Parse(Settings.AccentColor);
        Override("AccentBrush", accent is { } a ? Palette.Vertical(a) : null);
        Override("AccentTextBrush", accent is { } at ? Frozen(Palette.TextOn(at)) : null);

        var ring = Palette.Parse(Settings.RingColor);
        Override("SurfaceBrush", ring is { } r ? Palette.Vertical(r, 0xF5, 0.08, 0.06) : null);
        Override("RingBrush", ring is { } h ? Palette.Vertical(h, 0x59, 0.2, 0) : null);
        Override("RingTextBrush", ring is { } t ? Frozen(Palette.TextOn(t)) : null);
        Override("RingSubTextBrush", ring is { } st ? Frozen(Palette.TextOn(st), 0xB3) : null);
    }

    void Override(string key, object? value)
    {
        if (value == null) Resources.Remove(key);
        else Resources[key] = value;
    }

    static SolidColorBrush Frozen(Color c, byte alpha = 0xFF)
    {
        c.A = alpha;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Notification balloon (shown as a toast on Windows 10/11), for results that finish while the widget is closed.</summary>
    public static void Notify(string title, string text, bool error = false) =>
        Instance._tray?.ShowBalloonTip(5000, title, text, error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    /// <summary>Notification + sound.</summary>
    public static void Alert(string title, string text, string? soundPath)
    {
        Notify(title, text);
        var path = soundPath != null && File.Exists(soundPath) ? soundPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Alarm01.wav");
        if (!File.Exists(path)) { SystemSounds.Exclamation.Play(); return; }
        Instance._player.Open(new Uri(path));
        Instance._player.Play();
    }

    static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Storage.Dir);
            File.AppendAllText(Path.Combine(Storage.Dir, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch (IOException) { }
    }
}
