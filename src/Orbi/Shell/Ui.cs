using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Orbi.Shell;

/// <summary>Tiny factory for themed controls so module UIs stay short.</summary>
public static class Ui
{
    public static event Action<string>? FeedbackRequested;

    /// <summary>Visual confirmation in the open panel ("Copiado", "Alarme criado"...).</summary>
    public static void Feedback(string message) => FeedbackRequested?.Invoke(message);

    public static TextBlock Text(string text, string style = "Body")
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, style);
        return t;
    }

    public static TextBlock Icon(string glyph, double size = 16)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return t;
    }

    // Button content has no Foreground of its own so it inherits accent/normal colors from the style.
    static object Content(string text, string? glyph)
    {
        if (glyph == null) return text;
        var icon = new TextBlock { Text = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        if (text == "") return icon;
        icon.Margin = new Thickness(0, 0, 6, 0);
        return Row(icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
    }

    public static Button Btn(string text, Action click, string? glyph = null, string style = "Btn", string? tip = null)
    {
        var b = new Button { Content = Content(text, glyph), ToolTip = tip };
        b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += (_, _) => click();
        return b;
    }

    public static Button IconBtn(string glyph, Action click, string tip) => Btn("", click, glyph, "IconBtn", tip);

    public static ToggleButton Toggle(string text, bool on, Action<bool> changed, string? glyph = null)
    {
        var b = new ToggleButton { Content = Content(text, glyph), IsChecked = on };
        b.Click += (_, _) => changed(b.IsChecked == true);
        return b;
    }

    /// <summary>Segmented chips: exactly one checked.</summary>
    public static WrapPanel Choice(IEnumerable<(string Value, string Label)> options, string current, Action<string> changed)
    {
        var wrap = new WrapPanel();
        foreach (var (value, label) in options)
        {
            var b = new ToggleButton { Content = label, IsChecked = value == current, Tag = value };
            b.Click += (_, _) =>
            {
                foreach (ToggleButton o in wrap.Children) o.IsChecked = o == b;
                changed(value);
            };
            wrap.Children.Add(b);
        }
        return wrap;
    }

    public static StackPanel Row(params UIElement[] children) => Stack(Orientation.Horizontal, children);
    public static StackPanel Col(params UIElement[] children) => Stack(Orientation.Vertical, children);

    static StackPanel Stack(Orientation o, UIElement[] children)
    {
        var s = new StackPanel { Orientation = o };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Rounded list row: soft background, hover tint that fades in/out, optional click.</summary>
    public static FrameworkElement Card(UIElement content, Action? click = null, object? tip = null)
    {
        var bg = new Border { CornerRadius = new CornerRadius(12) };
        bg.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        var hover = new Border { CornerRadius = new CornerRadius(12), Opacity = 0 };
        hover.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
        var card = new Grid
        {
            Margin = new Thickness(0, 0, 0, 5), ToolTip = tip, Background = System.Windows.Media.Brushes.Transparent,
            Cursor = click != null ? Cursors.Hand : null,
            Children = { bg, hover, new Border { Child = content, Padding = new Thickness(10, 6, 4, 6) } },
        };
        card.MouseEnter += (_, _) => hover.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)) { EasingFunction = Ease });
        card.MouseLeave += (_, _) => hover.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease });
        if (click != null) card.MouseLeftButtonUp += (_, _) => click(); // inner buttons mark their clicks handled
        return card;
    }

    public static ScrollViewer Scroll(UIElement content, double maxHeight) => new() { Content = content, MaxHeight = maxHeight };
}
