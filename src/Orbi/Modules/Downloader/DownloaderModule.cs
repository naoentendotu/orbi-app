using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Orbi.Core;
using Orbi.Shell;

namespace Orbi.Modules.Downloader;

public enum JobState { Queued, Running, Paused, Done, Failed, Canceled }

public sealed class DownloadJob
{
    public string Url = "", Title = "", Folder = "";
    public List<string> FormatArgs = [];
    public JobState State;
    public string? File;
    public DateTime At = DateTime.Now;
    [JsonIgnore] public double Percent;
    [JsonIgnore] public string Detail = "";
    [JsonIgnore] public Process? Process;
}

public sealed class DownloaderState
{
    public string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Orbi");
    public string Container = "mp4";
    public List<DownloadJob> History = []; // finished downloads, newest first
}

public sealed class DownloaderModule : IModule
{
    const int HistoryLimit = 100;
    static readonly Dictionary<JobState, string> StateText = new()
    {
        [JobState.Queued] = "Na fila", [JobState.Running] = "Baixando", [JobState.Paused] = "Pausado",
        [JobState.Done] = "Concluído", [JobState.Failed] = "Erro", [JobState.Canceled] = "Cancelado",
    };

    readonly DownloaderState _s = Storage.Load<DownloaderState>("downloads.json");
    readonly List<DownloadJob> _queue = [];
    readonly List<(DownloadJob Job, ProgressBar Bar, TextBlock Text)> _rows = [];
    readonly TextBox _url = new() { Width = 250 };
    readonly StackPanel _info = new(), _pvJobs = new(), _jobs = new(), _preview, _expanded = new();
    readonly ScrollViewer _expandedView;
    string _quality = "best";

    public DownloaderModule()
    {
        _url.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Analyze(); };
        var analyze = Ui.Btn("", Analyze, "\uE721", tip: "Analisar link");
        _preview = Ui.Col(Ui.Row(_url, analyze), _info, _pvJobs);
        _expandedView = Ui.Scroll(_expanded, 380);
    }

    public string Id => "downloader";
    public string Title => "Vídeos";
    public string Glyph => "\uE896";
    public string Status
    {
        get
        {
            var running = _queue.FirstOrDefault(j => j.State == JobState.Running);
            return running != null ? $"{running.Percent:0}%" : _queue.Any(j => j.State == JobState.Queued) ? "Na fila" : "Cole um link";
        }
    }

    public FrameworkElement Preview
    {
        get
        {
            Attach();
            if (_url.Text == "" && TryClipboardUrl() is { } u) _url.Text = u; // copied a link? it's already here
            FillJobs();
            return _preview;
        }
    }

    public FrameworkElement Expanded
    {
        get
        {
            _expanded.Children.Clear();
            _expanded.Children.Add(Ui.Row(Detach(_url), Ui.Btn("", Analyze, "\uE721", tip: "Analisar link")));
            _expanded.Children.Add(Detach(_info));
            _expanded.Children.Add(Ui.Text("FILA", "Section"));
            _expanded.Children.Add(Ui.Scroll(Detach(_jobs), 150));
            _expanded.Children.Add(Ui.Text("HISTÓRICO", "Section"));
            var history = new StackPanel();
            foreach (var j in _s.History.Take(30)) history.Children.Add(HistoryRow(j));
            if (_s.History.Count == 0) history.Children.Add(Ui.Text("Nada baixado ainda.", "Sub"));
            _expanded.Children.Add(Ui.Scroll(history, 110));
            _expanded.Children.Add(Ui.Text("OPÇÕES", "Section"));
            _expanded.Children.Add(Ui.Choice([("mp4", "MP4"), ("mkv", "MKV"), ("webm", "WEBM")], _s.Container, v => { _s.Container = v; Save(); }));
            var folder = Ui.Text(_s.Folder, "Sub");
            _expanded.Children.Add(folder);
            _expanded.Children.Add(Ui.Row(
                Ui.Btn("Pasta…", () => PickFolder(folder), "\uE8DA"),
                Ui.Btn("Abrir pasta", () => { Directory.CreateDirectory(_s.Folder); Open(_s.Folder); }, "\uE838"),
                Ui.Btn("Atualizar yt-dlp", UpdateYtDlp, "\uE72C")));
            FillJobs();
            return _expandedView;
        }
    }

    // _url, _info and the job list are shared between preview and expanded: move them to whichever is shown.
    static T Detach<T>(T e) where T : FrameworkElement
    {
        (e.Parent as Panel)?.Children.Remove(e);
        if (e.Parent is ScrollViewer sv) sv.Content = null;
        return e;
    }

    void Attach()
    {
        var urlRow = (StackPanel)_preview.Children[0];
        if (_url.Parent == urlRow) return;
        urlRow.Children.Insert(0, Detach(_url));
        _preview.Children.Insert(1, Detach(_info));
    }

    static string? TryClipboardUrl()
    {
        try
        {
            var t = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText().Trim() : "";
            return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") && !t.Contains(' ') ? t : null;
        }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
    }

    void Save()
    {
        if (_s.History.Count > HistoryLimit) _s.History.RemoveRange(HistoryLimit, _s.History.Count - HistoryLimit);
        Storage.Save("downloads.json", _s);
    }

    // ---------- analyze ----------
    async void Analyze()
    {
        var url = _url.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("http")) { Ui.Feedback("Cole um link http(s)"); return; }
        if (YtDlp.FindExe() is not { } exe) { ShowInstall(); return; }

        _info.Children.Clear();
        _info.Children.Add(Ui.Text("Analisando…", "Sub"));
        var (code, stdout, stderr) = await YtDlp.Run(exe, ["-J", "--no-playlist", "--no-warnings", url], TimeSpan.FromSeconds(60));
        _info.Children.Clear();
        VideoInfo info;
        try
        {
            if (code != 0) throw new InvalidOperationException(YtDlp.LastError(stderr));
            info = YtDlp.ParseInfo(stdout);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException)
        {
            var err = Ui.Text($"Não deu para analisar: {e.Message}", "Sub");
            err.TextWrapping = TextWrapping.Wrap;
            _info.Children.Add(err);
            return;
        }

        var title = Ui.Text(info.Title, "Title");
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxHeight = 40;
        _info.Children.Add(title);
        _info.Children.Add(Ui.Text(info.Duration is { } d ? $"{info.Platform} · {(d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss"))}" : info.Platform, "Sub"));
        var options = new List<(string, string)> { ("best", "Melhor") };
        options.AddRange(info.Heights.Take(6).Select(h => (h.ToString(), $"{h}p")));
        options.Add(("audio", "Só áudio"));
        _quality = "best";
        _info.Children.Add(Ui.Choice(options, _quality, v => _quality = v));
        _info.Children.Add(Ui.Btn("Baixar", () => Enqueue(url, info.Title), "\uE896", "AccentBtn"));
    }

    void ShowInstall()
    {
        _info.Children.Clear();
        var t = Ui.Text("O baixador usa o yt-dlp (gratuito, oficial do GitHub). Ele ainda não está instalado.", "Sub");
        t.TextWrapping = TextWrapping.Wrap;
        _info.Children.Add(t);
        _info.Children.Add(Ui.Btn("Instalar yt-dlp", InstallYtDlp, "\uE896", "AccentBtn"));
    }

    async void InstallYtDlp()
    {
        _info.Children.Clear();
        _info.Children.Add(Ui.Text("Baixando yt-dlp…", "Sub"));
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Orbi");
            var bytes = await http.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(YtDlp.LocalExe)!);
            await File.WriteAllBytesAsync(YtDlp.LocalExe + ".tmp", bytes);
            File.Move(YtDlp.LocalExe + ".tmp", YtDlp.LocalExe, true);
            _info.Children.Clear();
            Ui.Feedback("yt-dlp instalado");
            if (_url.Text.Trim() != "") Analyze();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            _info.Children.Clear();
            _info.Children.Add(Ui.Text($"Falha ao baixar: {e.Message}", "Sub"));
        }
    }

    async void UpdateYtDlp()
    {
        if (YtDlp.FindExe() is not { } exe) { ShowInstall(); return; }
        Ui.Feedback("Atualizando yt-dlp…");
        var (code, stdout, stderr) = await YtDlp.Run(exe, ["-U"], TimeSpan.FromMinutes(2));
        Ui.Feedback(code == 0 ? stdout.Trim().Split('\n').Last() : YtDlp.LastError(stderr));
    }

    // ---------- queue ----------
    void Enqueue(string url, string title)
    {
        var job = new DownloadJob { Url = url, Title = title, Folder = _s.Folder, FormatArgs = YtDlp.FormatArgs(_quality, _s.Container, YtDlp.FindFfmpeg() != null) };
        _queue.Add(job);
        _info.Children.Clear();
        _url.Text = "";
        Ui.Feedback("Adicionado à fila");
        Pump();
        FillJobs();
    }

    void Pump()
    {
        if (_queue.Any(j => j.State == JobState.Running)) return;
        if (_queue.FirstOrDefault(j => j.State == JobState.Queued) is { } next) _ = Run(next);
    }

    async Task Run(DownloadJob job)
    {
        if (YtDlp.FindExe() is not { } exe) { job.State = JobState.Failed; job.Detail = "yt-dlp não instalado"; FillJobs(); return; }
        job.State = JobState.Running;
        job.Detail = "Iniciando…";
        var args = new List<string>
        {
            job.Url, "--no-playlist", "--newline", "--progress", "--no-colors",
            "--progress-template", $"download:{YtDlp.ProgressPrefix}%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--print", $"after_move:{YtDlp.FilePrefix}%(filepath)s",
            "-P", job.Folder, "-o", "%(title).150B [%(id)s].%(ext)s",
        };
        args.AddRange(job.FormatArgs);
        if (YtDlp.FindFfmpeg() is { } ff) args.AddRange(["--ffmpeg-location", ff]);

        string lastError = "";
        try
        {
            Directory.CreateDirectory(job.Folder);
            using var p = job.Process = Process.Start(YtDlp.Start(exe, args))!;
            p.OutputDataReceived += (_, e) => { if (e.Data is { } line) Application.Current.Dispatcher.BeginInvoke(() => OnOutput(job, line)); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { } l && l.StartsWith("ERROR")) lastError = l; };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            if (job.State == JobState.Running) // not paused/canceled by the user
            {
                job.State = p.ExitCode == 0 ? JobState.Done : JobState.Failed;
                job.Detail = p.ExitCode == 0 ? "" : lastError;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            job.State = JobState.Failed;
            job.Detail = e.Message;
        }
        job.Process = null;

        if (job.State == JobState.Done)
        {
            job.Percent = 100;
            _queue.Remove(job);
            _s.History.Insert(0, job);
            Save();
            App.Notify("Download concluído", job.Title);
        }
        else if (job.State == JobState.Failed) App.Notify("Download falhou", $"{job.Title}: {job.Detail}", error: true);
        FillJobs();
        Pump();
    }

    void OnOutput(DownloadJob job, string line)
    {
        if (line.StartsWith(YtDlp.FilePrefix)) { job.File = line[YtDlp.FilePrefix.Length..].Trim(); return; }
        if (YtDlp.ParseProgress(line) is not { } p) return;
        job.Percent = p.Percent;
        job.Detail = $"{p.Percent:0.0}% · {p.Speed} · faltam {p.Eta}";
        foreach (var (j, bar, text) in _rows)
            if (j == job) { bar.Value = p.Percent; text.Text = job.Detail; }
    }

    static void Stop(DownloadJob job, JobState state)
    {
        job.State = state;
        try { job.Process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // already exited
    }

    void FillJobs()
    {
        _rows.Clear();
        _pvJobs.Children.Clear();
        _jobs.Children.Clear();
        foreach (var j in _queue)
        {
            _pvJobs.Children.Add(JobRow(j, full: false));
            _jobs.Children.Add(JobRow(j, full: true));
        }
        if (_queue.Count == 0) _jobs.Children.Add(Ui.Text("Fila vazia.", "Sub"));
    }

    FrameworkElement JobRow(DownloadJob j, bool full)
    {
        var title = Ui.Text(j.Title);
        var bar = new ProgressBar { Maximum = 100, Value = j.Percent };
        var detail = Ui.Text(j.State == JobState.Running ? j.Detail : $"{StateText[j.State]}{(j.Detail != "" && j.State == JobState.Failed ? ": " + j.Detail : "")}", "Sub");
        detail.ToolTip = j.Detail;
        _rows.Add((j, bar, detail));

        var actions = new WrapPanel();
        void Act(string glyph, string tip, Action a) => actions.Children.Add(Ui.IconBtn(glyph, () => { a(); FillJobs(); Pump(); }, tip));
        switch (j.State)
        {
            case JobState.Running: Act("\uE769", "Pausar", () => Stop(j, JobState.Paused)); break;
            case JobState.Paused: Act("\uE768", "Continuar", () => j.State = JobState.Queued); break;
            case JobState.Failed or JobState.Canceled: Act("\uE72C", "Tentar de novo", () => { j.State = JobState.Queued; j.Detail = ""; }); break;
        }
        if (j.State is JobState.Running or JobState.Queued or JobState.Paused) Act("\uE711", "Cancelar", () => Stop(j, JobState.Canceled));
        if (full && j.State is JobState.Failed or JobState.Canceled) Act("\uE74D", "Remover", () => _queue.Remove(j));

        var head = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(title);
        return new StackPanel { Margin = new Thickness(0, 4, 0, 4), Children = { head, bar, detail } };
    }

    FrameworkElement HistoryRow(DownloadJob j)
    {
        var name = Ui.Text(j.Title);
        name.Width = 230;
        var row = Ui.Row(name);
        if (j.File != null && File.Exists(j.File))
        {
            row.Children.Add(Ui.IconBtn("\uE768", () => Open(j.File), "Abrir"));
            row.Children.Add(Ui.IconBtn("\uE838", () => Process.Start("explorer.exe", $"/select,\"{j.File}\""), "Mostrar na pasta"));
        }
        else row.Children.Add(Ui.Text("  arquivo movido", "Sub"));
        return row;
    }

    void PickFolder(TextBlock label)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_s.Folder) ? _s.Folder : null };
        RadialWindow.InDialog = true;
        try { if (dlg.ShowDialog() == true) { _s.Folder = dlg.FolderName; label.Text = _s.Folder; Save(); } }
        finally { RadialWindow.InDialog = false; }
    }

    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
