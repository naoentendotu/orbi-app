using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Orbi.Core;
using Orbi.Shell;
using WinClipboard = System.Windows.Clipboard;

namespace Orbi.Modules.Clipboard;

public sealed class ClipboardModule : IModule
{
    const int MaxTextChars = 100_000, PreviewCount = 6;

    readonly ClipboardHistory _history = Storage.Load<ClipboardHistory>("clipboard.json");
    readonly string _imageDir = Path.Combine(Storage.Dir, "clipboard-images");
    readonly HwndSource _listener;
    readonly StackPanel _previewList = new(), _fullList = new();
    readonly TextBox _search = new() { Width = 170 };
    readonly ToggleButton _favoritesOnly;
    readonly DockPanel _expanded;
    long _ignoreUntil;

    public ClipboardModule()
    {
        // Message-only window: receives WM_CLIPBOARDUPDATE, never shown.
        _listener = new HwndSource(new HwndSourceParameters("OrbiClipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _listener.AddHook(WndProc);

        // Image paths follow the data folder (it can move): rebuild them from the content hash.
        foreach (var img in _history.Items.Where(i => i.Kind == ClipKind.Image)) img.ImagePath = Path.Combine(_imageDir, img.Text + ".png");
        if (!Native.AddClipboardFormatListener(_listener.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());

        _search.TextChanged += (_, _) => FillFull();
        _favoritesOnly = Ui.Toggle("", false, _ => FillFull(), "\uE734");
        _favoritesOnly.ToolTip = "Só favoritos";
        var tools = Ui.Row(_search, _favoritesOnly, Ui.Btn("Limpar", ClearAll, "\uE74D", tip: "Apaga tudo menos os favoritos"));
        DockPanel.SetDock(tools, Dock.Top);
        var keep = Ui.Col(Ui.Text("GUARDAR ITENS COPIADOS POR", "Section"),
            Ui.Choice([("1", "1 hora"), ("24", "1 dia"), ("168", "7 dias"), ("720", "30 dias"), ("0", "Sempre")], _history.KeepHours.ToString(), v =>
            {
                _history.KeepHours = int.Parse(v);
                Prune(save: true);
                Ui.Feedback(v == "0" ? "Itens guardados para sempre" : "Itens mais antigos serão apagados (favoritos ficam)");
            }));
        DockPanel.SetDock(keep, Dock.Bottom);
        _expanded = new DockPanel { Children = { tools, keep, Ui.Scroll(_fullList, 290) } };

        // Expired items leave the disk even while the app sits idle.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        timer.Tick += (_, _) => Prune();
        timer.Start();
        Prune();
    }

    void Prune(bool save = false)
    {
        var expired = _history.Prune(DateTime.Now);
        foreach (var gone in expired) DeleteImage(gone);
        if (expired.Count > 0 || save) Changed();
    }

    public string Id => "clipboard";
    public string Title => "Clipboard";
    public string Glyph => "\uE77F";
    public string Status => $"{_history.Items.Count} itens";

    public FrameworkElement Preview { get { FillPreview(); return _previewList; } }
    public FrameworkElement Expanded { get { FillFull(); return _expanded; } }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE) _listener.Dispatcher.BeginInvoke(Capture);
        return IntPtr.Zero;
    }

    void Capture()
    {
        if (Environment.TickCount64 < _ignoreUntil) return; // our own re-copy
        ClipItem? item = null;
        try
        {
            var data = WinClipboard.GetDataObject();
            // Password managers set this to opt out of clipboard history: never store their content.
            if (data == null || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string t && !string.IsNullOrWhiteSpace(t))
                item = new ClipItem { Kind = ClipboardHistory.Classify(t), Text = t.Length > MaxTextChars ? t[..MaxTextChars] : t };
            else if (data.GetDataPresent(DataFormats.Bitmap))
                item = SaveImage(WinClipboard.GetImage());
        }
        catch (Exception e) when (e is COMException or ExternalException or IOException) { return; } // clipboard locked by another app
        if (item == null) return;
        foreach (var gone in _history.Add(item).Concat(_history.Prune(DateTime.Now))) DeleteImage(gone);
        Changed();
    }

    ClipItem? SaveImage(BitmapSource? image)
    {
        if (image == null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..32];
        Directory.CreateDirectory(_imageDir);
        var path = Path.Combine(_imageDir, hash + ".png");
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return new ClipItem { Kind = ClipKind.Image, Text = hash, ImagePath = path };
    }

    static void DeleteImage(ClipItem item)
    {
        if (item.ImagePath != null) try { File.Delete(item.ImagePath); } catch (IOException) { }
    }

    void CopyBack(ClipItem item)
    {
        _ignoreUntil = Environment.TickCount64 + 500;
        try
        {
            if (item.Kind != ClipKind.Image) WinClipboard.SetText(item.Text);
            else if (LoadImage(item.ImagePath, 0) is { } img) WinClipboard.SetImage(img);
            else { Ui.Feedback("Imagem não encontrada"); return; }
        }
        catch (Exception e) when (e is COMException or ExternalException)
        {
            Ui.Feedback("Clipboard ocupado, tente de novo");
            return;
        }
        _history.Add(item); // move to top
        Changed();
        Ui.Feedback("Copiado");
    }

    void ClearAll()
    {
        foreach (var gone in _history.Clear()) DeleteImage(gone);
        Changed();
        Ui.Feedback("Histórico limpo (favoritos mantidos)");
    }

    void Changed()
    {
        Storage.Save("clipboard.json", _history);
        if (_previewList.IsVisible) FillPreview();
        if (_expanded.IsVisible) FillFull();
    }

    void FillPreview()
    {
        _previewList.Children.Clear();
        foreach (var item in _history.Items.Take(PreviewCount)) _previewList.Children.Add(Row(item, full: false));
        if (_history.Items.Count == 0) _previewList.Children.Add(Ui.Text("Copie algo para começar o histórico.", "Sub"));
    }

    void FillFull()
    {
        _fullList.Children.Clear();
        foreach (var item in _history.Search(_search.Text, _favoritesOnly.IsChecked == true)) _fullList.Children.Add(Row(item, full: true));
        if (_fullList.Children.Count == 0) _fullList.Children.Add(Ui.Text("Nada encontrado.", "Sub"));
    }

    FrameworkElement Row(ClipItem item, bool full)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var kind = Ui.Icon(item.Kind switch { ClipKind.Link => "\uE71B", ClipKind.Image => "\uEB9F", _ => "\uE8C1" }, 13);
        kind.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        grid.Children.Add(kind);

        FrameworkElement content;
        object tip;
        if (item.Kind == ClipKind.Image)
        {
            content = new Image { Source = LoadImage(item.ImagePath, 120), Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
            tip = new Image { Source = LoadImage(item.ImagePath, 400), MaxWidth = 400 };
        }
        else
        {
            content = Ui.Text(item.Text.ReplaceLineEndings(" ").Trim());
            tip = new TextBlock { Text = item.Text.Length > 2000 ? item.Text[..2000] + "…" : item.Text, TextWrapping = TextWrapping.Wrap };
        }
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var actions = Ui.Row(Ui.IconBtn(item.Favorite ? "\uE735" : "\uE734", () => { item.Favorite = !item.Favorite; Changed(); }, "Favoritar"));
        if (full) actions.Children.Add(Ui.IconBtn("\uE74D", () =>
        {
            _history.Items.Remove(item);
            DeleteImage(item);
            Changed();
        }, "Excluir"));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return Ui.Card(grid, () => CopyBack(item), tip);
    }

    static BitmapImage? LoadImage(string? path, int decodeWidth)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file (eviction deletes it)
            img.UriSource = new Uri(path);
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is IOException or NotSupportedException) { return null; }
    }
}
