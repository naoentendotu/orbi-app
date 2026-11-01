using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Orbi.Core;
using Orbi.Shell;

namespace Orbi.Modules.Monitor;

/// <summary>DDC/CI over dxva2. Calls are slow (~50 ms) and must not overlap, so everything goes through one lock off the UI thread.</summary>
public static class Ddc
{
    public const byte Brightness = 0x10, Contrast = 0x12, ColorPreset = 0x14, DisplayMode = 0xDC;

    // Restore factory/luminance/geometry/color defaults, degauss/settings, input source, power mode, OSD lock: one wrong write and the screen goes black or resets.
    static readonly byte[] Blocked = [0x04, 0x05, 0x06, 0x08, 0x0A, 0x60, 0xD6, 0xCA, 0xE1];

    public static bool IsSafeToWrite(byte code) => !Blocked.Contains(code);

    static readonly object Io = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    delegate bool EnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumProc cb, IntPtr data);
    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

    public static List<PHYSICAL_MONITOR> Monitors()
    {
        var list = new List<PHYSICAL_MONITOR>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out var n) && n > 0)
            {
                var arr = new PHYSICAL_MONITOR[n];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) list.AddRange(arr);
            }
            return true;
        }, IntPtr.Zero);
        return list; // shortcut: handles are never destroyed; they live for the whole app run anyway.
    }

    public static (uint Current, uint Max)? Get(IntPtr h, byte code)
    {
        lock (Io) return GetVCPFeatureAndVCPFeatureReply(h, code, IntPtr.Zero, out var c, out var m) ? (c, m) : null;
    }

    public static bool Set(IntPtr h, byte code, uint value)
    {
        if (!IsSafeToWrite(code)) return false;
        lock (Io) return SetVCPFeature(h, code, value);
    }
}

public sealed class MonitorProfile
{
    public string Name = "";
    public uint? Brightness, Contrast, ColorPreset, DisplayMode;
}

public sealed class MonitorSettings
{
    public int Index;
    public List<MonitorProfile> Profiles =
    [
        new() { Name = "Trabalho", Brightness = 35, Contrast = 50, ColorPreset = 0x04 },
        new() { Name = "Jogos", Brightness = 80, Contrast = 70, ColorPreset = 0x05, DisplayMode = 0x05 },
        new() { Name = "Filmes", Brightness = 60, Contrast = 60, ColorPreset = 0x05, DisplayMode = 0x03 },
        new() { Name = "Personalizado" },
    ];
    public byte? LowBlueCode;
    public uint LowBlueOn = 1, LowBlueOff;
    public Dictionary<byte, uint>? LastScan;
}

public sealed class MonitorModule : IModule
{
    static readonly (string, string)[] Presets = [("4", "Quente 5000K"), ("5", "Normal 6500K"), ("6", "7500K"), ("8", "Frio 9300K"), ("11", "Usuário")];
    static readonly (string, string)[] Modes = [("0", "Padrão"), ("1", "Produtividade"), ("3", "Filme"), ("5", "Jogos"), ("4", "Usuário")];

    readonly MonitorSettings _s = Storage.Load<MonitorSettings>("monitor.json");
    readonly StackPanel _preview = new(), _expanded = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    List<Ddc.PHYSICAL_MONITOR>? _monitors;
    readonly Dictionary<byte, uint> _pending = [];

    public MonitorModule()
    {
        _debounce.Tick += (_, _) => { _debounce.Stop(); Flush(); };
    }

    public string Id => "monitor";
    public string Title => "Monitor";
    public string Glyph => "\uE7F4";
    public string Status => "Brilho e perfis";

    public FrameworkElement Preview { get { _ = Fill(_preview, false); return _preview; } }
    public FrameworkElement Expanded { get { _ = Fill(_expanded, true); return _expanded; } }

    IntPtr? Handle => _monitors is { Count: > 0 } m ? m[Math.Clamp(_s.Index, 0, m.Count - 1)].Handle : null;

    void Save() => Storage.Save("monitor.json", _s);

    async Task Fill(StackPanel host, bool full)
    {
        var gen = (int)(host.Tag ?? 0) + 1; // a newer Fill supersedes this one after each await
        host.Tag = gen;
        host.Children.Clear();
        host.Children.Add(Ui.Text("Lendo o monitor…", "Sub"));
        _monitors ??= await Task.Run(Ddc.Monitors);
        if (!Equals(host.Tag, gen)) return;
        if (Handle is not { } h)
        {
            host.Children.Clear();
            host.Children.Add(Ui.Text("Nenhum monitor com DDC/CI. Ative “DDC/CI” no menu do monitor.", "Sub"));
            return;
        }
        var (b, c) = await Task.Run(() => (Ddc.Get(h, Ddc.Brightness), Ddc.Get(h, Ddc.Contrast)));
        if (!Equals(host.Tag, gen)) return;
        host.Children.Clear();

        if (full && _monitors.Count > 1)
            host.Children.Add(Ui.Choice(_monitors.Select((m, i) => (i.ToString(), $"{i + 1}: {m.Description}")), _s.Index.ToString(),
                v => { _s.Index = int.Parse(v); Save(); _ = Fill(host, true); }));

        host.Children.Add(Slider("\uE706", "Brilho", Ddc.Brightness, b));
        host.Children.Add(Slider("\uE793", "Contraste", Ddc.Contrast, c));
        host.Children.Add(Ui.Text("PERFIS", "Section"));
        var profiles = new WrapPanel();
        foreach (var p in _s.Profiles) profiles.Children.Add(Ui.Btn(p.Name, () => Apply(h, p, host, full)));
        host.Children.Add(profiles);
        if (_s.LowBlueCode is { } lbl)
            host.Children.Add(Ui.Row(Ui.Text("Low Blue Light  ", "Sub"),
                Ui.Btn("Ligar", () => Write(h, lbl, _s.LowBlueOn, "Low Blue Light ligado")),
                Ui.Btn("Desligar", () => Write(h, lbl, _s.LowBlueOff, "Low Blue Light desligado"))));

        if (!full) return;
        host.Children.Add(Ui.Text("TEMPERATURA DE COR", "Section"));
        host.Children.Add(Ui.Choice(Presets, "", v => Write(h, Ddc.ColorPreset, uint.Parse(v), "Temperatura aplicada")));
        host.Children.Add(Ui.Text("MODO DE IMAGEM", "Section"));
        host.Children.Add(Ui.Choice(Modes, "", v => Write(h, Ddc.DisplayMode, uint.Parse(v), "Modo aplicado")));
        host.Children.Add(Ui.Text("Nem todo monitor aceita todos os valores.", "Sub"));

        host.Children.Add(Ui.Text("SALVAR ESTADO ATUAL NO PERFIL", "Section"));
        var save = new WrapPanel();
        foreach (var p in _s.Profiles) save.Children.Add(Ui.Btn(p.Name, () => Capture(h, p), "\uE74E"));
        host.Children.Add(save);

        host.Children.Add(Ui.Text("LOW BLUE LIGHT (CÓDIGO PRÓPRIO DA AOC)", "Section"));
        host.Children.Add(Ui.Text("Para descobrir: Escanear, mudar o Low Blue Light pelos botões do monitor, Escanear de novo. O código que mudou aparece abaixo.", "Sub"));
        ((TextBlock)host.Children[^1]).TextWrapping = TextWrapping.Wrap;
        var scanOut = Ui.Text("", "Sub");
        scanOut.TextWrapping = TextWrapping.Wrap;
        host.Children.Add(Ui.Btn("Escanear códigos VCP", () => _ = Scan(h, scanOut), "\uE721"));
        host.Children.Add(scanOut);
        var code = new TextBox { Width = 60, Text = _s.LowBlueCode is { } x ? $"{x:X2}" : "", ToolTip = "Código (hex)" };
        var on = new TextBox { Width = 50, Text = _s.LowBlueOn.ToString(), ToolTip = "Valor ligado" };
        var off = new TextBox { Width = 50, Text = _s.LowBlueOff.ToString(), ToolTip = "Valor desligado" };
        host.Children.Add(Ui.Row(Ui.Text("hex ", "Sub"), code, Ui.Text("liga ", "Sub"), on, Ui.Text("desliga ", "Sub"), off));
        host.Children.Add(Ui.Btn("Salvar Low Blue Light", () =>
        {
            if (!byte.TryParse(code.Text.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var cb) || !uint.TryParse(on.Text, out var vOn) || !uint.TryParse(off.Text, out var vOff))
            { Ui.Feedback("Código hex e valores numéricos"); return; }
            if (!Ddc.IsSafeToWrite(cb)) { Ui.Feedback("Esse código é bloqueado por segurança"); return; }
            (_s.LowBlueCode, _s.LowBlueOn, _s.LowBlueOff) = (cb, vOn, vOff);
            Save();
            Ui.Feedback("Low Blue Light configurado");
            _ = Fill(host, true);
        }, "\uE74E"));
    }

    FrameworkElement Slider(string glyph, string name, byte code, (uint Current, uint Max)? value)
    {
        if (value is not { } v) return Ui.Text($"{name}: não suportado", "Sub");
        var s = new Slider { Minimum = 0, Maximum = v.Max, Value = v.Current, Width = 240, Focusable = false, IsSnapToTickEnabled = true, TickFrequency = 1 };
        var label = Ui.Text($"{v.Current}", "Sub");
        label.Width = 30;
        s.ValueChanged += (_, e) =>
        {
            label.Text = $"{e.NewValue:0}";
            _pending[code] = (uint)e.NewValue;
            _debounce.Stop(); _debounce.Start(); // DDC is slow: send only the latest value
        };
        var icon = Ui.Icon(glyph, 15);
        icon.ToolTip = name;
        icon.Margin = new Thickness(0, 0, 8, 0);
        return Ui.Row(icon, s, label);
    }

    void Flush()
    {
        if (Handle is not { } h) return;
        var batch = _pending.ToList();
        _pending.Clear();
        _ = Task.Run(() => { foreach (var (code, v) in batch) Ddc.Set(h, code, v); });
    }

    void Write(IntPtr h, byte code, uint value, string ok) =>
        _ = Task.Run(() => Ddc.Set(h, code, value)).ContinueWith(t => Ui.Feedback(t.Result ? ok : "O monitor recusou esse valor"),
            TaskScheduler.FromCurrentSynchronizationContext());

    async void Apply(IntPtr h, MonitorProfile p, StackPanel host, bool full)
    {
        if (p.Brightness == null && p.Contrast == null && p.ColorPreset == null && p.DisplayMode == null)
        { Ui.Feedback($"Perfil “{p.Name}” vazio: salve o estado atual nele"); return; }
        var ok = await Task.Run(() =>
            new[] { (Ddc.DisplayMode, p.DisplayMode), (Ddc.ColorPreset, p.ColorPreset), (Ddc.Brightness, p.Brightness), (Ddc.Contrast, p.Contrast) }
                .Where(x => x.Item2 != null).All(x => Ddc.Set(h, x.Item1, x.Item2!.Value)));
        Ui.Feedback(ok ? $"Perfil {p.Name} aplicado" : $"Perfil {p.Name} aplicado em parte (monitor recusou algum valor)");
        await Fill(host, full);
    }

    async void Capture(IntPtr h, MonitorProfile p)
    {
        var (b, c, t, m) = await Task.Run(() => (Ddc.Get(h, Ddc.Brightness), Ddc.Get(h, Ddc.Contrast), Ddc.Get(h, Ddc.ColorPreset), Ddc.Get(h, Ddc.DisplayMode)));
        (p.Brightness, p.Contrast, p.ColorPreset, p.DisplayMode) = (b?.Current, c?.Current, t?.Current, m?.Current);
        Save();
        Ui.Feedback($"Estado salvo em {p.Name}");
    }

    async Task Scan(IntPtr h, TextBlock output)
    {
        output.Text = "Escaneando… (10–20 s, só leitura)";
        var now = await Task.Run(() =>
        {
            var d = new Dictionary<byte, uint>();
            for (int code = 1; code < 256; code++)
                if (Ddc.Get(h, (byte)code) is { } v) d[(byte)code] = v.Current;
            return d;
        });
        var before = _s.LastScan;
        _s.LastScan = now;
        Save();
        if (before == null) { output.Text = $"{now.Count} códigos lidos. Mude o Low Blue Light no monitor e escaneie de novo."; return; }
        var diff = now.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .Select(kv => $"0x{kv.Key:X2}: {(before.TryGetValue(kv.Key, out var o) ? o : 0)} → {kv.Value}").ToList();
        output.Text = diff.Count == 0 ? "Nada mudou desde o último scan." : "Mudou: " + string.Join(",  ", diff);
    }
}
