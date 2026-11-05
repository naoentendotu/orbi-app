using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Orbi.Core;
using Path = System.Windows.Shapes.Path;

namespace Orbi.Shell;

/// <summary>Pie menu around the cursor + side panel (preview on hover, expanded on click).</summary>
public partial class RadialWindow : Window
{
    const double C = 150, OuterR = 148, InnerR = 58, BaseW = 676, BaseH = 460, PanelW = 376,
        Pad = 40; // transparent room around the content so drop shadows (blur 36 + depth 10) aren't cut by the window edge
    const double Gap = 3, Corner = 6, HoverLift = 5; // px between slices, slice corner radius, hover slide-out

    /// <summary>Set while a modal picker (file dialog) is open so losing focus doesn't close the widget.</summary>
    public static bool InDialog;

    sealed record Slice(Canvas Group, Path Accent, TextBlock Icon, TextBlock Label, IModule Module, double MidRad);

    readonly Func<IReadOnlyList<IModule>> _modules;
    readonly List<Slice> _slices = [];
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    TextBlock _centerTitle = null!, _centerStatus = null!;
    IModule? _shown, _hot;
    bool _expanded, _closing;
    IntPtr _hwnd;

    public bool Pinned { get; private set; }

    /// <summary>The app window that had focus when the menu opened (target for "capture window").</summary>
    public IntPtr PreviousForeground { get; private set; }

    public RadialWindow(Func<IReadOnlyList<IModule>> modules)
    {
        InitializeComponent();
        _modules = modules;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW, true); // no Alt+Tab entry
            Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, true); // don't steal focus from the app you're in
        };
        new WindowInteropHelper(this).EnsureHandle();
        Deactivated += (_, _) => { if (Pinned && !InDialog) HideMenu(); };
        // Typing needs focus: clicking a text field in a preview pins (and activates) the widget.
        Panel.PreviewMouseDown += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is TextBox) { if (!Pinned) SetPinned(true); return; }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HideMenu(); };
        Ui.FeedbackRequested += ShowToast;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Animate(Toast, OpacityProperty, 0, 260);
            Animate(ToastShift, TranslateTransform.YProperty, 4, 260);
        };
    }

    static void Animate(DependencyObject target, DependencyProperty p, double to, int ms, double? from = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ui.Ease };
        if (from != null) a.From = from;
        ((IAnimatable)target).BeginAnimation(p, a);
    }

    public void ShowMenu(bool pinned = false, IModule? expand = null)
    {
        if (_closing) { _closing = false; Hide(); } // reopened during the close animation: start fresh
        if (!IsVisible)
        {
            var fg = Native.GetForegroundWindow();
            if (fg != _hwnd) PreviousForeground = fg;
            var s = App.Settings;
            BuildRing();
            _expanded = false;
            _shown = null;
            Panel.Visibility = Visibility.Collapsed;
            BackBtn.Visibility = Visibility.Collapsed;
            Opacity = s.Opacity;
            Root.LayoutTransform = new ScaleTransform(s.Scale, s.Scale);
            Root.Margin = new Thickness(Pad * s.Scale);
            // +1: pixel rounding of a scaled window can leave Root a fraction too small, and WPF then clips it
            // (a hovered slice sliding past the ring's edge got cut, plus its shadow)
            Width = (BaseW + 2 * Pad) * s.Scale + 1;
            Height = (BaseH + 2 * Pad) * s.Scale + 1;
            PlaceNearCursor(s);
            Show();
            Topmost = false; Topmost = true; // jump above other always-on-top windows opened since last time

            // Open: gentle zoom + a small twist of the ring, like a macOS popover settling in.
            Animate(Pop, ScaleTransform.ScaleXProperty, 1, 220, 0.86);
            Animate(Pop, ScaleTransform.ScaleYProperty, 1, 220, 0.86);
            Animate(Spin, RotateTransform.AngleProperty, 0, 260, -14);
            Animate(Root, OpacityProperty, 1, 160, 0);
        }
        if (expand != null) Expand(expand);
        else if (pinned) SetPinned(true);
    }

    public void OnAltReleased() { if (!Pinned) HideMenu(); }

    public void HideMenu()
    {
        Pinned = false;
        _expanded = false;
        PinBtn.Content = "";
        if (!IsVisible || _closing) return;

        // Close: quick fade + shrink, then actually hide.
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = Ui.Ease };
        fade.Completed += (_, _) =>
        {
            if (!_closing) return; // reopened meanwhile
            _closing = false;
            Hide();
            Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, true);
        };
        Animate(Pop, ScaleTransform.ScaleXProperty, 0.94, 120);
        Animate(Pop, ScaleTransform.ScaleYProperty, 0.94, 120);
        Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Rebuild after settings change (module list, order) while open.</summary>
    public void Refresh()
    {
        if (!IsVisible) return;
        BuildRing();
        if (_shown != null) Highlight(_shown, animate: false);
    }

    void PlaceNearCursor(Settings s)
    {
        Native.GetCursorPos(out var p);
        var wa0 = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).WorkingArea;
        // shortcut: converts with this window's DPI; on mixed-DPI multi-monitor the ring may land slightly off. Upgrade: PerMonitorV2 manifest + SetWindowPos in pixels.
        var dpi = VisualTreeHelper.GetDpi(this);
        var wa = new Rect(wa0.X / dpi.DpiScaleX, wa0.Y / dpi.DpiScaleY, wa0.Width / dpi.DpiScaleX, wa0.Height / dpi.DpiScaleY);
        double cx = p.X / dpi.DpiScaleX, cy = p.Y / dpi.DpiScaleY;
        if (s.Position == "center") { cx = wa.Left + wa.Width / 2; cy = wa.Top + wa.Height / 2; }

        double ring = C * s.Scale;
        bool panelLeft = cx + ring + PanelW * s.Scale > wa.Right; // no room on the right: flip
        Grid.SetColumn(Ring, panelLeft ? 2 : 0);
        Grid.SetColumn(Panel, panelLeft ? 0 : 2);
        ColA.Width = new GridLength(panelLeft ? 360 : 300);
        ColB.Width = new GridLength(panelLeft ? 300 : 360);
        Root.RenderTransformOrigin = new Point(panelLeft ? (BaseW - C) / BaseW : C / BaseW, 0.5);
        Panel.RenderTransformOrigin = new Point(panelLeft ? 1 : 0, 0.5);

        // the transparent padding may hang off-screen; only the visible content is kept inside the work area
        double pad = Pad * s.Scale;
        Left = Math.Clamp(panelLeft ? cx - (Width - pad - ring) : cx - pad - ring, wa.Left - pad, Math.Max(wa.Left - pad, wa.Right - Width + pad));
        Top = Math.Clamp(cy - Height / 2, wa.Top - pad, Math.Max(wa.Top - pad, wa.Bottom - Height + pad));
    }

    void BuildRing()
    {
        Ring.Children.Clear();
        _slices.Clear();
        _hot = null;

        // Glass halo behind the slices, with a bright top edge.
        var halo = new Ellipse { Width = 2 * C, Height = 2 * C, StrokeThickness = 1 };
        halo.SetResourceReference(Shape.FillProperty, "RingBrush");
        halo.SetResourceReference(Shape.StrokeProperty, "GlassEdgeBrush");
        Ring.Children.Add(halo);

        var mods = _modules();
        double step = 2 * Math.PI / mods.Count;
        for (int i = 0; i < mods.Count; i++)
        {
            var mod = mods[i];
            double a0 = -Math.PI / 2 - step / 2 + i * step, a1 = a0 + step, mid = (a0 + a1) / 2; // first slice centered on top
            var shape = Wedge(a0, a1);

            // Rounded corners: geometry inset by Corner, then a same-brush stroke of 2×Corner with round joins.
            var baseSlice = new Path { Data = shape, StrokeThickness = 2 * Corner, StrokeLineJoin = PenLineJoin.Round };
            baseSlice.SetResourceReference(Shape.FillProperty, "SurfaceBrush");
            baseSlice.SetResourceReference(Shape.StrokeProperty, "SurfaceBrush");
            var accent = new Path { Data = shape, StrokeThickness = 2 * Corner, StrokeLineJoin = PenLineJoin.Round, Opacity = 0 };
            accent.SetResourceReference(Shape.FillProperty, "AccentBrush");
            accent.SetResourceReference(Shape.StrokeProperty, "AccentBrush");

            var icon = Ui.Icon(mod.Glyph, 21);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            var label = Ui.Text(mod.Title.ToUpperInvariant(), "Sub");
            label.FontSize = 9.5;
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(0, 3, 0, 0);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var text = Ui.Col(icon, label);
            text.Width = 84;
            text.IsHitTestVisible = false;
            double rm = (OuterR + InnerR) / 2 + 3;
            Canvas.SetLeft(text, C + rm * Math.Cos(mid) - 42);
            Canvas.SetTop(text, C + rm * Math.Sin(mid) - 21);

            var group = new Canvas { Cursor = Cursors.Hand, RenderTransform = new TranslateTransform() };
            group.Children.Add(baseSlice);
            group.Children.Add(accent);
            group.Children.Add(text);
            group.MouseEnter += (_, _) => Hover(mod);
            group.MouseLeftButtonUp += (_, _) => Expand(mod);
            Ring.Children.Add(group);
            _slices.Add(new Slice(group, accent, icon, label, mod, mid));
        }

        double cr = InnerR - 8;
        var center = new Ellipse { Width = 2 * cr, Height = 2 * cr, IsHitTestVisible = false, StrokeThickness = 1 };
        center.SetResourceReference(Shape.FillProperty, "SurfaceBrush");
        center.SetResourceReference(Shape.StrokeProperty, "GlassEdgeBrush");
        Canvas.SetLeft(center, C - cr);
        Canvas.SetTop(center, C - cr);
        _centerTitle = Ui.Text("Orbi", "Title");
        _centerTitle.FontSize = 12.5;
        _centerStatus = Ui.Text("", "Sub");
        _centerStatus.FontSize = 10.5;
        foreach (var t in new[] { _centerTitle, _centerStatus }) t.HorizontalAlignment = HorizontalAlignment.Center;
        _centerTitle.SetResourceReference(TextBlock.ForegroundProperty, "RingTextBrush");
        _centerStatus.SetResourceReference(TextBlock.ForegroundProperty, "RingSubTextBrush");
        var centerText = Ui.Col(_centerTitle, _centerStatus);
        centerText.Width = 2 * cr - 12;
        centerText.IsHitTestVisible = false;
        Canvas.SetLeft(centerText, C - cr + 6);
        Canvas.SetTop(centerText, C - 16);
        Ring.Children.Add(center);
        Ring.Children.Add(centerText);
        Highlight(null, animate: false);
    }

    /// <summary>Annular sector with straight, parallel gaps (constant width at any radius), inset for rounded corners.</summary>
    static Geometry Wedge(double a0, double a1)
    {
        double d = Gap / 2 + Corner, ro = OuterR - Corner, ri = InnerR + Corner + 4;
        Point P(double a, double r) => new(C + r * Math.Cos(a), C + r * Math.Sin(a));
        double so = Math.Asin(d / ro), si = Math.Asin(d / ri); // angular offset of the gap edge at each radius
        bool large = a1 - a0 - 2 * so > Math.PI;
        var fig = new PathFigure { StartPoint = P(a0 + so, ro), IsClosed = true };
        fig.Segments.Add(new ArcSegment(P(a1 - so, ro), new Size(ro, ro), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(a1 - si, ri), true));
        fig.Segments.Add(new ArcSegment(P(a0 + si, ri), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true));
        return new PathGeometry([fig]);
    }

    void Highlight(IModule? active, bool animate = true)
    {
        if (animate && active == _hot) return;
        _hot = active;
        int ms = animate ? 170 : 0;
        foreach (var s in _slices)
        {
            bool on = s.Module == active;
            Animate(s.Accent, OpacityProperty, on ? 1 : 0, ms);
            var shift = (TranslateTransform)s.Group.RenderTransform;
            Animate(shift, TranslateTransform.XProperty, on ? HoverLift * Math.Cos(s.MidRad) : 0, ms);
            Animate(shift, TranslateTransform.YProperty, on ? HoverLift * Math.Sin(s.MidRad) : 0, ms);
            s.Icon.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentTextBrush" : "RingTextBrush");
            s.Label.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentTextBrush" : "RingSubTextBrush");
        }
    }

    void Hover(IModule mod)
    {
        Highlight(mod);
        _centerTitle.Text = mod.Title;
        _centerStatus.Text = mod.Status;
        if (!_expanded) ShowPanel(mod, mod.Preview);
    }

    void Expand(IModule mod)
    {
        Highlight(mod);
        _expanded = true;
        BackBtn.Visibility = Visibility.Visible;
        ShowPanel(mod, mod.Expanded);
        SetPinned(true); // longer interaction: keep open after the trigger key is released
    }

    void ShowPanel(IModule mod, FrameworkElement content)
    {
        _shown = mod;
        PanelTitle.Text = mod.Title;
        if (Panel.Visibility != Visibility.Visible)
        {
            Panel.Visibility = Visibility.Visible;
            Animate(Panel, OpacityProperty, 1, 180, 0);
            Animate(PanelPop, ScaleTransform.ScaleXProperty, 1, 220, 0.95);
            Animate(PanelPop, ScaleTransform.ScaleYProperty, 1, 220, 0.95);
        }
        if (PanelContent.Content != content)
        {
            PanelContent.Content = content;
            Animate(PanelContent, OpacityProperty, 1, 180, 0);
            Animate(ContentShift, TranslateTransform.YProperty, 0, 220, 8);
        }
    }

    void SetPinned(bool on)
    {
        Pinned = on;
        PinBtn.Content = on ? "" : "";
        PinBtn.ToolTip = on ? "Desafixar e fechar" : "Fixar aberto";
        if (!on) return;
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, false); // needs focus for text fields
        Activate();
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        _expanded = false;
        BackBtn.Visibility = Visibility.Collapsed;
        if (_shown != null) ShowPanel(_shown, _shown.Preview);
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (Pinned) HideMenu();
        else SetPinned(true);
    }

    void ShowToast(string message)
    {
        if (!IsVisible) return;
        ToastText.Text = message;
        Animate(Toast, OpacityProperty, 1, 160);
        Animate(ToastShift, TranslateTransform.YProperty, 0, 220, 6);
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
