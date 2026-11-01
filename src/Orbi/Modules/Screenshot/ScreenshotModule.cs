using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbi.Core;
using Orbi.Shell;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Orbi.Modules.Screenshot;

public sealed class ScreenshotSettings
{
    public string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Orbi");
    public string Format = "png"; // png | jpg
    public bool CopyToClipboard = true;
    public bool AutoSave = true;
    public string? Last;
}

public sealed class ScreenshotModule : IModule
{
    readonly ScreenshotSettings _s = Storage.Load<ScreenshotSettings>("screenshot.json");
    readonly StackPanel _preview = new(), _expanded = new();

    public string Id => "screenshot";
    public string Title => "Captura";
    public string Glyph => "\uE722";
    public string Status => _s.Last != null && File.Exists(_s.Last) ? $"Última {File.GetLastWriteTime(_s.Last):HH:mm}" : "Tela, janela ou região";

    public FrameworkElement Preview { get { Fill(_preview, false); return _preview; } }
    public FrameworkElement Expanded { get { Fill(_expanded, true); return _expanded; } }

    void Fill(StackPanel host, bool full)
    {
        host.Children.Clear();
        host.Children.Add(Ui.Row(
            Ui.Btn("Tela", () => Capture(Mode.Screen), "\uE7F4", "AccentBtn"),
            Ui.Btn("Janela", () => Capture(Mode.Window), "\uE737"),
            Ui.Btn("Região", () => Capture(Mode.Region), "\uF406")));

        if (_s.Last != null && File.Exists(_s.Last))
        {
            var thumb = new Image { Source = Load(_s.Last, 320), MaxHeight = 150, HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Abrir" };
            thumb.MouseLeftButtonUp += (_, _) => Launch(_s.Last);
            host.Children.Add(new Border { Child = thumb, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 4, 0, 6) });
            host.Children.Add(Ui.Row(
                Ui.Btn("Editar", () => Launch(_s.Last, "edit"), "\uE70F"),
                Ui.Btn("Pasta", () => Process.Start("explorer.exe", $"/select,\"{_s.Last}\""), "\uE838")));
        }
        else host.Children.Add(Ui.Text("Nenhuma captura ainda.", "Sub"));

        if (!full) return;
        host.Children.Add(Ui.Text("SALVAR EM", "Section"));
        var folder = Ui.Text(_s.Folder, "Sub");
        folder.ToolTip = _s.Folder;
        host.Children.Add(folder);
        host.Children.Add(Ui.Btn("Alterar pasta", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_s.Folder) ? _s.Folder : null };
            RadialWindow.InDialog = true;
            try { if (dlg.ShowDialog() == true) { _s.Folder = dlg.FolderName; Save(); Fill(host, true); } }
            finally { RadialWindow.InDialog = false; }
        }, "\uE8DA"));
        host.Children.Add(Ui.Text("FORMATO", "Section"));
        host.Children.Add(Ui.Choice([("png", "PNG"), ("jpg", "JPG")], _s.Format, v => { _s.Format = v; Save(); }));
        host.Children.Add(Check("Copiar para a área de transferência", _s.CopyToClipboard, v => _s.CopyToClipboard = v));
        host.Children.Add(Check("Salvar automaticamente na pasta", _s.AutoSave, v => _s.AutoSave = v));
    }

    CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 8, 0, 0) };
        c.Click += (_, _) => { set(c.IsChecked == true); Save(); };
        return c;
    }

    void Save() => Storage.Save("screenshot.json", _s);

    enum Mode { Screen, Window, Region }

    async void Capture(Mode mode)
    {
        var target = App.Widget.PreviousForeground;
        App.Widget.HideMenu();
        await Task.Delay(220); // let the widget disappear from the screen first

        Drawing.Bitmap? bmp;
        if (mode == Mode.Region)
        {
            using var full = Grab(Forms.SystemInformation.VirtualScreen);
            if (full == null) return;
            var area = await RegionPicker.Pick(full);
            bmp = area is { } a ? full.Clone(new Drawing.Rectangle(a.X, a.Y, a.Width, a.Height), full.PixelFormat) : null;
        }
        else if (mode == Mode.Window && target != IntPtr.Zero)
        {
            var r = Native.WindowBounds(target);
            bmp = Grab(Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
        }
        else
        {
            Native.GetCursorPos(out var p);
            bmp = Grab(Forms.Screen.FromPoint(new Drawing.Point(p.X, p.Y)).Bounds);
        }
        if (bmp == null) return;

        using (bmp)
        {
            try { Store(bmp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                App.Notify("Captura", $"Falha ao salvar: {e.Message}", error: true);
            }
        }
    }

    static Drawing.Bitmap? Grab(Drawing.Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0) return null;
        var bmp = new Drawing.Bitmap(r.Width, r.Height);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Drawing.Point.Empty, r.Size);
        return bmp;
    }

    void Store(Drawing.Bitmap bmp)
    {
        var dir = _s.AutoSave ? _s.Folder : Path.Combine(Path.GetTempPath(), "Orbi");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"Orbi {DateTime.Now:yyyy-MM-dd HH-mm-ss}.{_s.Format}");
        bmp.Save(path, _s.Format == "jpg" ? Drawing.Imaging.ImageFormat.Jpeg : Drawing.Imaging.ImageFormat.Png);
        _s.Last = path;
        Save();
        if (_s.CopyToClipboard && Load(path, 0) is { } img) System.Windows.Clipboard.SetImage(img);
        App.Notify("Captura", _s.AutoSave ? $"Salva em {Path.GetFileName(path)}" : "Copiada para a área de transferência");
    }

    static void Launch(string? path, string? verb = null)
    {
        if (path == null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb ?? "" }); }
        catch (System.ComponentModel.Win32Exception) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } // no "edit" handler
    }

    static BitmapImage? Load(string path, int width)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            if (width > 0) b.DecodePixelWidth = width;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception e) when (e is IOException or NotSupportedException) { return null; }
    }
}

/// <summary>Full-screen overlay over a frozen screenshot; drag to select, Esc cancels.</summary>
public sealed class RegionPicker : Window
{
    readonly TaskCompletionSource<Int32Rect?> _result = new();
    readonly int _pxW, _pxH;
    readonly RectangleGeometry _hole = new();
    readonly System.Windows.Shapes.Rectangle _border = new() { StrokeThickness = 1.5, Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0x43, 0x2F)) };
    Point? _start;

    public static Task<Int32Rect?> Pick(Drawing.Bitmap frozen)
    {
        var w = new RegionPicker(frozen);
        w.Show();
        w.Activate();
        return w._result.Task;
    }

    RegionPicker(Drawing.Bitmap frozen)
    {
        _pxW = frozen.Width;
        _pxH = frozen.Height;
        var vs = Forms.SystemInformation.VirtualScreen;
        var dpi = VisualTreeHelper.GetDpi(App.Widget);
        // shortcut: one DPI for the whole virtual screen; mixed-DPI monitors may misalign. Upgrade: per-monitor overlays.
        Left = vs.Left / dpi.DpiScaleX; Top = vs.Top / dpi.DpiScaleY;
        Width = vs.Width / dpi.DpiScaleX; Height = vs.Height / dpi.DpiScaleY;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        Cursor = System.Windows.Input.Cursors.Cross;

        using var ms = new MemoryStream();
        frozen.Save(ms, Drawing.Imaging.ImageFormat.Bmp);
        ms.Position = 0;
        var img = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var dim = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)),
            Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { new RectangleGeometry(new Rect(0, 0, Width, Height)), _hole } },
        };
        var canvas = new Canvas { Children = { dim, _border } };
        Content = new Grid { Children = { new Image { Source = img, Stretch = Stretch.Fill }, canvas } };

        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(this); CaptureMouse(); };
        MouseMove += (_, e) => { if (_start is { } s) Show(new Rect(s, e.GetPosition(this))); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } s) return;
            ReleaseMouseCapture();
            var px = ToPixels(s, e.GetPosition(this), new Size(ActualWidth, ActualHeight), _pxW, _pxH);
            Finish(px.Width > 2 && px.Height > 2 ? px : null);
        };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Finish(null); };
        Deactivated += (_, _) => Finish(null);
    }

    void Show(Rect r)
    {
        _hole.Rect = r;
        Canvas.SetLeft(_border, r.X); Canvas.SetTop(_border, r.Y);
        _border.Width = r.Width; _border.Height = r.Height;
    }

    void Finish(Int32Rect? r)
    {
        if (_result.TrySetResult(r)) Close();
    }

    /// <summary>Drag points in overlay DIPs → pixel rect in the frozen bitmap, clamped to it.</summary>
    public static Int32Rect ToPixels(Point a, Point b, Size overlay, int pxW, int pxH)
    {
        double sx = pxW / overlay.Width, sy = pxH / overlay.Height;
        int x0 = Math.Clamp((int)Math.Round(Math.Min(a.X, b.X) * sx), 0, pxW), y0 = Math.Clamp((int)Math.Round(Math.Min(a.Y, b.Y) * sy), 0, pxH);
        int x1 = Math.Clamp((int)Math.Round(Math.Max(a.X, b.X) * sx), 0, pxW), y1 = Math.Clamp((int)Math.Round(Math.Max(a.Y, b.Y) * sy), 0, pxH);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
