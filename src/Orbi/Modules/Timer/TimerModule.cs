using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Orbi.Core;
using Orbi.Shell;

namespace Orbi.Modules.Timer;

public sealed class TimerModule : IModule
{
    static readonly int[] PresetMinutes = [1, 5, 10, 25];
    static readonly string[] DayNames = ["Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb"];

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // Countdown
    TimeSpan _duration = TimeSpan.FromMinutes(5), _remaining = TimeSpan.FromMinutes(5);
    DateTime? _endsAt;

    // Stopwatch
    readonly Stopwatch _stopwatch = new();
    readonly List<TimeSpan> _laps = [];

    // Alarms
    readonly AlarmList _alarms = Storage.Load<AlarmList>("alarms.json");
    readonly Dictionary<Alarm, DateTime> _nextFire = [];

    // UI
    readonly StackPanel _preview, _expanded, _tabBody = new();
    readonly TextBlock _pvTime = Ui.Text("", "Big"), _pvExtra = Ui.Text("", "Sub"), _cdTime = Ui.Text("", "Big"), _swTime = Ui.Text("", "Big");
    readonly Button _pvStart, _cdStart, _swStart;
    readonly StackPanel _lapList = new(), _alarmList = new();
    string _tab = "timer";

    public TimerModule()
    {
        Rearm();
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _pvStart = Ui.Btn("", ToggleCountdown, style: "AccentBtn");
        _cdStart = Ui.Btn("", ToggleCountdown, style: "AccentBtn");
        _swStart = Ui.Btn("", ToggleStopwatch, style: "AccentBtn");
        _pvExtra.HorizontalAlignment = HorizontalAlignment.Center;

        _preview = Ui.Col(_pvTime, Center(Ui.Row(_pvStart, Ui.Btn("Zerar", ResetCountdown, "\uE72C"))), Presets(), _pvExtra);
        var tabs = Ui.Choice([("timer", "Temporizador"), ("stopwatch", "Cronômetro"), ("alarms", "Alarmes")], _tab, t => { _tab = t; FillTab(); });
        _expanded = Ui.Col(tabs, _tabBody);
        Update();
    }

    public string Id => "timer";
    public string Title => "Timer";
    public string Glyph => "\uE916";
    public string Status => _endsAt != null ? Fmt(_remaining) : _stopwatch.IsRunning ? Fmt(_stopwatch.Elapsed) : NextAlarmText() ?? "Pronto";

    public FrameworkElement Preview { get { Update(); return _preview; } }
    public FrameworkElement Expanded { get { FillTab(); return _expanded; } }

    static StackPanel Center(StackPanel s) { s.HorizontalAlignment = HorizontalAlignment.Center; return s; }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    WrapPanel Presets()
    {
        var w = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var m in PresetMinutes) w.Children.Add(Ui.Btn($"{m} min", () => StartCountdown(TimeSpan.FromMinutes(m))));
        return w;
    }

    // ---------- Countdown ----------
    void StartCountdown(TimeSpan d)
    {
        _duration = _remaining = d;
        _endsAt = DateTime.Now + d;
        Ui.Feedback($"Temporizador de {Fmt(d)} iniciado");
        Update();
    }

    void ToggleCountdown()
    {
        if (_endsAt != null) { _remaining = _endsAt.Value - DateTime.Now; _endsAt = null; }
        else _endsAt = DateTime.Now + _remaining;
        Update();
    }

    void ResetCountdown() { _endsAt = null; _remaining = _duration; Update(); }

    // ---------- Stopwatch ----------
    void ToggleStopwatch() { if (_stopwatch.IsRunning) _stopwatch.Stop(); else _stopwatch.Start(); Update(); }

    void Lap()
    {
        if (!_stopwatch.IsRunning) return;
        _laps.Insert(0, _stopwatch.Elapsed);
        FillLaps();
    }

    void ResetStopwatch() { _stopwatch.Reset(); _laps.Clear(); FillLaps(); Update(); }

    void FillLaps()
    {
        _lapList.Children.Clear();
        for (int i = 0; i < _laps.Count; i++)
            _lapList.Children.Add(Ui.Text($"Volta {_laps.Count - i}    {_laps[i]:hh\\:mm\\:ss\\.ff}", "Sub"));
    }

    // ---------- Alarms ----------
    void Rearm()
    {
        _nextFire.Clear();
        foreach (var a in _alarms.Items.Where(a => a.Enabled)) _nextFire[a] = a.NextFire(DateTime.Now);
    }

    void SaveAlarms()
    {
        Storage.Save("alarms.json", _alarms);
        Rearm();
        if (_alarmList.IsVisible) FillAlarms();
    }

    string? NextAlarmText() =>
        _nextFire.Count == 0 ? null : $"Alarme {_nextFire.Values.Min():ddd HH:mm}";

    void OnTick()
    {
        var now = DateTime.Now;
        if (_endsAt != null)
        {
            _remaining = _endsAt.Value - now;
            if (_remaining <= TimeSpan.Zero)
            {
                _endsAt = null;
                _remaining = _duration;
                App.Alert("Temporizador", $"Tempo esgotado ({Fmt(_duration)})", null);
            }
        }

        var due = _nextFire.Where(kv => now >= kv.Value).Select(kv => kv.Key).ToList();
        foreach (var a in due)
        {
            App.Alert("Alarme", string.IsNullOrWhiteSpace(a.Label) ? $"{a.Time:hh\\:mm}" : $"{a.Time:hh\\:mm} · {a.Label}", a.Sound);
            if (a.Days.Count == 0) a.Enabled = false;
        }
        if (due.Count > 0) SaveAlarms();

        if (_preview.IsVisible || _expanded.IsVisible) Update();
    }

    void Update()
    {
        bool running = _endsAt != null;
        _pvTime.Text = _cdTime.Text = Fmt(_remaining < TimeSpan.Zero ? TimeSpan.Zero : _remaining);
        _pvStart.Content = _cdStart.Content = running ? "Pausar" : "Iniciar";
        _swTime.Text = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss\.f");
        _swStart.Content = _stopwatch.IsRunning ? "Pausar" : "Iniciar";
        var extra = new List<string>();
        if (_stopwatch.IsRunning) extra.Add($"Cronômetro {Fmt(_stopwatch.Elapsed)}");
        if (NextAlarmText() is { } a) extra.Add(a);
        _pvExtra.Text = string.Join("  ·  ", extra);
    }

    void FillTab()
    {
        _tabBody.Children.Clear();
        foreach (var e in new UIElement[] { _cdTime, _swTime, _lapList }) (LogicalTreeHelper.GetParent(e) as Panel)?.Children.Remove(e);
        foreach (var b in new[] { _cdStart, _swStart }) (LogicalTreeHelper.GetParent(b) as Panel)?.Children.Remove(b);

        switch (_tab)
        {
            case "timer":
                var input = new TextBox { Width = 110, ToolTip = "Minutos, mm:ss ou h:mm:ss" };
                void SetCustom()
                {
                    if (Alarm.ParseDuration(input.Text) is { } d) StartCountdown(d);
                    else Ui.Feedback("Use minutos, mm:ss ou h:mm:ss");
                }
                input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) SetCustom(); };
                _tabBody.Children.Add(_cdTime);
                _tabBody.Children.Add(Center(Ui.Row(_cdStart, Ui.Btn("Zerar", ResetCountdown, "\uE72C"))));
                _tabBody.Children.Add(Presets());
                _tabBody.Children.Add(Ui.Text("PERSONALIZADO", "Section"));
                _tabBody.Children.Add(Ui.Row(input, Ui.Btn("Iniciar", SetCustom)));
                break;
            case "stopwatch":
                _tabBody.Children.Add(_swTime);
                _tabBody.Children.Add(Center(Ui.Row(_swStart, Ui.Btn("Volta", Lap, "\uE7C1"), Ui.Btn("Zerar", ResetStopwatch, "\uE72C"))));
                _tabBody.Children.Add(Ui.Scroll(_lapList, 200));
                FillLaps();
                break;
            default:
                _tabBody.Children.Add(Ui.Scroll(_alarmList, 170));
                _tabBody.Children.Add(Ui.Text("NOVO ALARME", "Section"));
                _tabBody.Children.Add(NewAlarmForm());
                FillAlarms();
                break;
        }
        Update();
    }

    void FillAlarms()
    {
        _alarmList.Children.Clear();
        if (_alarms.Items.Count == 0) _alarmList.Children.Add(Ui.Text("Nenhum alarme.", "Sub"));
        foreach (var a in _alarms.Items.OrderBy(a => a.Time))
        {
            var toggle = Ui.Toggle($"{a.Time:hh\\:mm}", a.Enabled, on => { a.Enabled = on; SaveAlarms(); });
            toggle.ToolTip = "Ativar / desativar";
            var days = a.Days.Count == 0 ? "Uma vez" : a.Days.Count == 7 ? "Todo dia" : string.Join(" ", a.Days.Order().Select(d => DayNames[(int)d]));
            var info = Ui.Text(string.IsNullOrWhiteSpace(a.Label) ? days : $"{a.Label} · {days}", "Sub");
            info.Width = 200;
            _alarmList.Children.Add(Ui.Row(toggle, info, Ui.IconBtn("\uE74D", () => { _alarms.Items.Remove(a); SaveAlarms(); }, "Excluir")));
        }
    }

    StackPanel NewAlarmForm()
    {
        var time = new TextBox { Width = 70, Text = "07:00", ToolTip = "HH:mm" };
        var label = new TextBox { Width = 170, ToolTip = "Nome (opcional)" };
        var dayToggles = DayNames.Select(n => new ToggleButton { Content = n, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 3, 6) }).ToArray();
        string? sound = null;
        var soundBtn = Ui.Btn("Som padrão", () => { }, "\uE8D6");
        soundBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Áudio|*.wav;*.mp3;*.wma;*.m4a" };
            RadialWindow.InDialog = true;
            try { if (dlg.ShowDialog() == true) { sound = dlg.FileName; soundBtn.Content = System.IO.Path.GetFileName(sound); } }
            finally { RadialWindow.InDialog = false; }
        };
        var add = Ui.Btn("Adicionar", () =>
        {
            if (!TimeSpan.TryParseExact(time.Text.Trim(), [@"h\:mm", @"hh\:mm"], null, out var t) || t >= TimeSpan.FromDays(1))
            {
                Ui.Feedback("Horário inválido (use HH:mm)");
                return;
            }
            var days = dayToggles.Select((b, i) => (b, i)).Where(x => x.b.IsChecked == true).Select(x => (DayOfWeek)x.i).ToList();
            _alarms.Items.Add(new Alarm { Time = t, Days = days, Label = label.Text.Trim(), Sound = sound });
            SaveAlarms();
            Ui.Feedback($"Alarme {t:hh\\:mm} criado");
        }, "\uE710", "AccentBtn");

        var daysRow = new WrapPanel();
        foreach (var b in dayToggles) daysRow.Children.Add(b);
        return Ui.Col(Ui.Row(time, label), daysRow, Ui.Row(soundBtn, add), Ui.Text("Sem dias marcados = toca uma vez.", "Sub"));
    }
}
