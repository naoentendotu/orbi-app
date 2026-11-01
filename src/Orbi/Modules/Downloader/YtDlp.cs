using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Orbi.Core;

namespace Orbi.Modules.Downloader;

public record VideoInfo(string Title, string Platform, TimeSpan? Duration, List<int> Heights);

/// <summary>Pure helpers + process plumbing around yt-dlp.</summary>
public static class YtDlp
{
    public const string ProgressPrefix = "AF|", FilePrefix = "AFFILE|";

    public static string LocalExe => Path.Combine(Storage.Dir, "bin", "yt-dlp.exe");

    public static string? FindExe() => File.Exists(LocalExe) ? LocalExe : OnPath("yt-dlp.exe");

    static string? _ffmpeg;
    public static string? FindFfmpeg()
    {
        if (_ffmpeg != null) return _ffmpeg;
        _ffmpeg = OnPath("ffmpeg.exe");
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
        if (_ffmpeg == null && Directory.Exists(winget))
            _ffmpeg = Directory.EnumerateDirectories(winget, "*FFmpeg*")
                .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories)).FirstOrDefault();
        return _ffmpeg;
    }

    static string? OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    public static VideoInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        TimeSpan? duration = r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(d.GetDouble()) : null;
        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (r.TryGetProperty("formats", out var formats))
            foreach (var f in formats.EnumerateArray())
                if (f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() > 0
                    && !(f.TryGetProperty("vcodec", out var vc) && vc.GetString() == "none"))
                    heights.Add(h.GetInt32());
        var platform = Str("extractor_key");
        return new VideoInfo(Str("title"), platform == "" ? Str("extractor") : platform, duration, heights.ToList());
    }

    /// <summary>quality: "best", "audio" or a height like "720".</summary>
    public static List<string> FormatArgs(string quality, string container, bool hasFfmpeg)
    {
        if (quality == "audio")
            return hasFfmpeg ? ["-f", "ba/b", "-x", "--audio-format", "mp3"] : ["-f", "ba[ext=m4a]/ba"];
        var limit = quality == "best" ? "" : $"[height<={int.Parse(quality)}]";
        // Without ffmpeg yt-dlp can't merge separate video+audio streams: take the best single file.
        return hasFfmpeg
            ? ["-f", $"bv*{limit}+ba/b{limit}", "--merge-output-format", container]
            : ["-f", $"b{limit}"];
    }

    public static (double Percent, string Speed, string Eta)? ParseProgress(string line)
    {
        if (!line.StartsWith(ProgressPrefix)) return null;
        var p = line[ProgressPrefix.Length..].Split('|');
        if (p.Length < 3 || !double.TryParse(p[0].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return null;
        return (pct, p[1].Trim(), p[2].Trim());
    }

    public static ProcessStartInfo Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs to completion (for -J / -U), killing it after the timeout.</summary>
    public static async Task<(int Code, string Out, string Err)> Run(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        using var p = Process.Start(Start(exe, args))!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(true); return (-1, "", "Tempo esgotado"); }
        return (p.ExitCode, await stdout, await stderr);
    }

    public static string LastError(string stderr) =>
        stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR")) ?? stderr.Trim().Split('\n').LastOrDefault() ?? "";
}
