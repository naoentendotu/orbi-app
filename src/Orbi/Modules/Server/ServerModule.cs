using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Orbi.Core;
using Orbi.Modules.Downloader;
using Orbi.Shell;
using Renci.SshNet;

namespace Orbi.Modules.Server;

public record Peer(string Name, string Dns, string Ip, bool Online, string Os);

public record TailscaleStatus(string State, List<Peer> Peers)
{
    public bool Running => State == "Running";

    public static TailscaleStatus Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var peers = new List<Peer>();
        if (r.TryGetProperty("Peer", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var p in map.EnumerateObject().Select(x => x.Value))
            {
                string S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                var ip = p.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0 ? ips[0].GetString() ?? "" : "";
                peers.Add(new Peer(S("HostName"), S("DNSName").TrimEnd('.'), ip, p.TryGetProperty("Online", out var o) && o.GetBoolean(), S("OS")));
            }
        return new TailscaleStatus(r.TryGetProperty("BackendState", out var st) ? st.GetString() ?? "" : "", peers.OrderBy(p => p.Name).ToList());
    }
}

public sealed class Shortcut
{
    public string Name = "", Url = "";
}

public sealed class ServerSettings
{
    public string Host = "";          // tailnet DNS name of the server
    public string User = "";
    public int Port = 22;
    public string KeyPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
    public string Dashboard = "";     // empty = http://{host}
    public List<Shortcut> Shortcuts = [];
    public List<string> Destinations = ["uploads"];
    public string Destination = "uploads";
    public Dictionary<string, string> HostKeys = []; // trust on first use, then pinned
}

public sealed class ServerModule : IModule
{
    const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";

    readonly ServerSettings _s = Storage.Load<ServerSettings>("server.json");
    readonly StackPanel _preview = new(), _expanded = new();
    readonly ScrollViewer _expandedView;
    readonly List<(ProgressBar Bar, TextBlock Text)> _progress = [];
    TailscaleStatus? _status;
    bool _uploading;
    string _lastTransfer = "";

    public ServerModule() => _expandedView = Ui.Scroll(_expanded, 380);

    public string Id => "server";
    public string Title => "Servidor";
    public string Glyph => "\uE968";
    public string Status => _status == null ? "Tailscale" : !_status.Running ? "Desconectado" : ServerPeer()?.Online == true ? "Online" : "Servidor offline";

    public FrameworkElement Preview { get { _ = Fill(_preview, false); return _preview; } }
    public FrameworkElement Expanded { get { _ = Fill(_expanded, true); return _expandedView; } }

    void Save() => Storage.Save("server.json", _s);

    Peer? ServerPeer() => _status?.Peers.FirstOrDefault(p => p.Dns == _s.Host || p.Name == _s.Host);

    string Expand(string url) => url.Replace("{host}", _s.Host);

    async Task<TailscaleStatus?> ReadStatus()
    {
        var exe = File.Exists(TailscaleExe) ? TailscaleExe : "tailscale.exe";
        try
        {
            var (code, json, _) = await YtDlp.Run(exe, ["status", "--json"], TimeSpan.FromSeconds(6));
            return code == 0 || json.TrimStart().StartsWith('{') ? TailscaleStatus.Parse(json) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or JsonException) { return null; }
    }

    async Task Fill(StackPanel host, bool full)
    {
        var gen = (int)(host.Tag ?? 0) + 1;
        host.Tag = gen;
        if (host.Children.Count == 0) host.Children.Add(Ui.Text("Consultando Tailscale…", "Sub"));
        _status = await ReadStatus();
        if (!Equals(host.Tag, gen)) return;
        host.Children.Clear();
        _progress.RemoveAll(p => !p.Bar.IsVisible);

        // Status line
        var peer = ServerPeer();
        bool ok = _status?.Running == true;
        var dot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), Fill = new SolidColorBrush(!ok ? Color.FromRgb(0x99, 0x99, 0x99) : peer?.Online == true ? Color.FromRgb(0x2E, 0xB8, 0x5C) : Color.FromRgb(0xE8, 0x43, 0x2F)) };
        var text = _status == null ? "Tailscale não encontrado"
            : !ok ? $"Tailscale: {_status.State}"
            : _s.Host == "" ? "Escolha o servidor (clique na fatia)"
            : $"{peer?.Name ?? _s.Host} · {(peer?.Online == true ? "online" : "offline")}";
        var status = Ui.Row(dot, Ui.Text(text));
        if (_status != null && !ok) status.Children.Add(Ui.Btn("Conectar", Connect, "\uE703"));
        status.Margin = new Thickness(0, 0, 0, 8);
        host.Children.Add(status);

        // Dashboard + shortcuts
        var links = new WrapPanel();
        if (_s.Host != "") links.Children.Add(Ui.Btn("Dashboard", () => Browse(_s.Dashboard == "" ? $"http://{_s.Host}" : Expand(_s.Dashboard)), "\uE80F", "AccentBtn"));
        foreach (var sc in _s.Shortcuts) links.Children.Add(Ui.Btn(sc.Name, () => Browse(Expand(sc.Url))));
        host.Children.Add(links);

        // Upload
        host.Children.Add(DropZone());
        var bar = new ProgressBar { Maximum = 100, Visibility = _uploading ? Visibility.Visible : Visibility.Collapsed };
        var info = Ui.Text(_lastTransfer, "Sub");
        _progress.Add((bar, info));
        host.Children.Add(bar);
        host.Children.Add(info);

        if (full) FillSettings(host);
    }

    FrameworkElement DropZone()
    {
        var label = Ui.Text($"Solte arquivos ou clique · destino: {_s.Destination}", "Sub");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var zone = new Border
        {
            Child = Ui.Col(Center(Ui.Icon("\uE898", 20)), label), Padding = new Thickness(10, 12, 10, 12), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5), AllowDrop = true, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 6, 0, 4),
            ToolTip = "Fixe o painel (alfinete) antes de arrastar do Explorer",
        };
        zone.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        zone.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        zone.DragEnter += (_, _) => zone.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        zone.DragLeave += (_, _) => zone.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        zone.Drop += (_, e) =>
        {
            zone.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Upload(files.Where(File.Exists).ToArray());
        };
        zone.MouseLeftButtonUp += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
            RadialWindow.InDialog = true;
            try { if (dlg.ShowDialog() == true) Upload(dlg.FileNames); }
            finally { RadialWindow.InDialog = false; }
        };
        return zone;
    }

    static T Center<T>(T e) where T : FrameworkElement { e.HorizontalAlignment = HorizontalAlignment.Center; return e; }

    void FillSettings(StackPanel host)
    {
        host.Children.Add(Ui.Text("DESTINO NO SERVIDOR", "Section"));
        host.Children.Add(Ui.Choice(_s.Destinations.Select(d => (d, d)), _s.Destination, v => { _s.Destination = v; Save(); }));
        var newDest = new TextBox { Width = 220, ToolTip = "Pasta relativa ao home (ex.: uploads/fotos) ou absoluta (/srv/arquivos)" };
        host.Children.Add(Ui.Row(newDest, Ui.Btn("Adicionar", () =>
        {
            var d = newDest.Text.Trim();
            if (d == "") return;
            if (!_s.Destinations.Contains(d)) _s.Destinations.Add(d);
            _s.Destination = d;
            Save();
            _ = Fill(host, true);
        }, "\uE710")));

        host.Children.Add(Ui.Text("SERVIDOR (TAILNET)", "Section"));
        if (_status?.Peers.Count > 0)
            host.Children.Add(Ui.Choice(_status.Peers.Select(p => (p.Dns, $"{(p.Online ? "● " : "○ ")}{p.Name}")), _s.Host,
                v => { _s.Host = v; Save(); _ = Fill(host, true); }));
        else host.Children.Add(Ui.Text("Nenhum dispositivo na tailnet.", "Sub"));

        var user = new TextBox { Width = 110, Text = _s.User, ToolTip = "Usuário SSH" };
        var port = new TextBox { Width = 55, Text = _s.Port.ToString(), ToolTip = "Porta SSH" };
        var key = new TextBox { Width = 300, Text = _s.KeyPath, ToolTip = "Chave privada (sem senha)" };
        var dash = new TextBox { Width = 300, Text = _s.Dashboard, ToolTip = "URL do dashboard; {host} = servidor. Vazio = http://{host}" };
        host.Children.Add(Ui.Text("SSH: USUÁRIO / PORTA / CHAVE", "Section"));
        host.Children.Add(Ui.Row(user, port));
        host.Children.Add(key);
        host.Children.Add(Ui.Text("DASHBOARD", "Section"));
        host.Children.Add(dash);
        host.Children.Add(Ui.Btn("Salvar", () =>
        {
            if (!int.TryParse(port.Text, out var p) || p is < 1 or > 65535) { Ui.Feedback("Porta inválida"); return; }
            (_s.User, _s.Port, _s.KeyPath, _s.Dashboard) = (user.Text.Trim(), p, key.Text.Trim(), dash.Text.Trim());
            Save();
            Ui.Feedback("Servidor salvo");
        }, "\uE74E", "AccentBtn"));

        host.Children.Add(Ui.Text("ATALHOS PARA SERVIÇOS", "Section"));
        foreach (var sc in _s.Shortcuts)
        {
            var name = Ui.Text($"{sc.Name}  ·  {sc.Url}", "Sub");
            name.Width = 290;
            host.Children.Add(Ui.Row(name, Ui.IconBtn("\uE74D", () => { _s.Shortcuts.Remove(sc); Save(); _ = Fill(host, true); }, "Excluir")));
        }
        var scName = new TextBox { Width = 90, ToolTip = "Nome" };
        var scUrl = new TextBox { Width = 170, Text = "http://{host}:", ToolTip = "URL; {host} = servidor" };
        host.Children.Add(Ui.Row(scName, scUrl, Ui.Btn("", () =>
        {
            if (scName.Text.Trim() == "" || !Uri.TryCreate(Expand(scUrl.Text.Trim()), UriKind.Absolute, out _)) { Ui.Feedback("Nome e URL válidos"); return; }
            _s.Shortcuts.Add(new Shortcut { Name = scName.Text.Trim(), Url = scUrl.Text.Trim() });
            Save();
            _ = Fill(host, true);
        }, "\uE710", tip: "Adicionar atalho")));
    }

    static void Browse(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else Ui.Feedback("URL inválida");
    }

    async void Connect()
    {
        Ui.Feedback("Conectando…");
        var (code, _, err) = await YtDlp.Run(File.Exists(TailscaleExe) ? TailscaleExe : "tailscale.exe", ["up"], TimeSpan.FromSeconds(30));
        Ui.Feedback(code == 0 ? "Tailscale conectado" : err.Trim().Split('\n').FirstOrDefault() ?? "Falha ao conectar");
        if (_preview.IsVisible) _ = Fill(_preview, false);
        if (_expanded.IsVisible) _ = Fill(_expanded, true);
    }

    void Report(string text, double? percent)
    {
        _lastTransfer = text;
        foreach (var (bar, t) in _progress)
        {
            t.Text = text;
            bar.Visibility = percent == null ? Visibility.Collapsed : Visibility.Visible;
            if (percent is { } p) bar.Value = p;
        }
    }

    async void Upload(string[] files)
    {
        if (files.Length == 0) return;
        if (_uploading) { Ui.Feedback("Já existe um envio em andamento"); return; }
        if (_s.Host == "" || _s.User == "") { Ui.Feedback("Configure servidor e usuário SSH (clique na fatia)"); return; }
        if (!File.Exists(_s.KeyPath)) { Ui.Feedback("Chave SSH não encontrada"); return; }

        _uploading = true;
        var (host, port, user, keyPath, dest) = (_s.Host, _s.Port, _s.User, _s.KeyPath, _s.Destination);
        var dispatcher = Application.Current.Dispatcher;
        bool keyChanged = false;
        Report($"Conectando a {host}…", 0);
        try
        {
            await Task.Run(() =>
            {
                using var key = new PrivateKeyFile(keyPath);
                var conn = new ConnectionInfo(host, port, user, new PrivateKeyAuthenticationMethod(user, key)) { Timeout = TimeSpan.FromSeconds(10) };
                using var sftp = new SftpClient(conn);
                sftp.HostKeyReceived += (_, e) =>
                {
                    lock (_s.HostKeys)
                    {
                        if (!_s.HostKeys.TryGetValue(host, out var known)) _s.HostKeys[host] = e.FingerPrintSHA256; // first contact: pin it
                        else if (known != e.FingerPrintSHA256) { e.CanTrust = false; keyChanged = true; }
                    }
                };
                sftp.Connect();
                if (!sftp.Exists(dest)) sftp.CreateDirectory(dest);

                long total = files.Sum(f => new FileInfo(f).Length), sent = 0, lastUi = 0;
                var clock = Stopwatch.StartNew();
                foreach (var f in files)
                {
                    long before = sent;
                    var name = System.IO.Path.GetFileName(f);
                    using var stream = File.OpenRead(f);
                    sftp.UploadFile(stream, $"{dest.TrimEnd('/')}/{name}", true, done =>
                    {
                        sent = before + (long)done;
                        if (clock.ElapsedMilliseconds - lastUi < 150) return; // throttle UI updates
                        lastUi = clock.ElapsedMilliseconds;
                        double pct = total == 0 ? 100 : sent * 100.0 / total, speed = sent / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                        dispatcher.BeginInvoke(() => Report($"{name} · {pct:0}% · {Size(speed)}/s", pct));
                    });
                    sent = before + stream.Length;
                }
            });
            Save(); // persists a newly pinned host key
            Report($"{files.Length} arquivo(s) enviados para {dest}", null);
            Ui.Feedback("Envio concluído");
            App.Notify("Servidor", $"{files.Length} arquivo(s) enviados para {host}:{dest}");
        }
        catch (Exception e) // network/SSH boundary: show the reason, don't crash
        {
            var msg = keyChanged ? "A chave do servidor mudou. Envio bloqueado por segurança (veja server.json → HostKeys)."
                : e is Renci.SshNet.Common.SshPassPhraseNullOrEmptyException ? "A chave SSH tem senha; use uma chave sem senha."
                : e.Message;
            Report($"Falha: {msg}", null);
            App.Notify("Servidor", msg, error: true);
        }
        finally { _uploading = false; }
    }

    static string Size(double bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (1 << 30):0.0} GB",
        >= 1 << 20 => $"{bytes / (1 << 20):0.0} MB",
        >= 1 << 10 => $"{bytes / (1 << 10):0} KB",
        _ => $"{bytes:0} B",
    };
}
