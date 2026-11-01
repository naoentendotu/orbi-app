using System.Windows.Media;

namespace Orbi.Core;

/// <summary>User-picked colors → the soft vertical gradients the theme uses.</summary>
public static class Palette
{
    public static readonly Color DarkText = Color.FromRgb(0x2B, 0x21, 0x1E), LightText = Color.FromRgb(0xF5, 0xF3, 0xF1);

    /// <summary>"#FF9500", "ff9500" or a named color; null if empty or invalid.</summary>
    public static Color? Parse(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t == "") return null;
        if (!t.StartsWith('#') && t.All(Uri.IsHexDigit)) t = "#" + t;
        try { return ColorConverter.ConvertFromString(t) is Color c ? c : null; }
        catch (FormatException) { return null; }
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static Color Mix(Color c, Color to, double t) => Color.FromArgb(c.A,
        (byte)Math.Round(c.R + (to.R - c.R) * t), (byte)Math.Round(c.G + (to.G - c.G) * t), (byte)Math.Round(c.B + (to.B - c.B) * t));

    public static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);
    public static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);

    /// <summary>Perceived brightness; above the threshold, text on top of it should be dark (e.g. yellow, white).</summary>
    public static bool IsLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.7;

    public static Color TextOn(Color c) => IsLight(c) ? DarkText : LightText;

    /// <summary>Lighter at the top, slightly darker at the bottom, with the given opacity.</summary>
    public static LinearGradientBrush Vertical(Color c, byte alpha = 0xFF, double lighten = 0.12, double darken = 0.05)
    {
        var top = Lighten(c, lighten); top.A = alpha;
        var bottom = Darken(c, darken); bottom.A = alpha;
        var b = new LinearGradientBrush(top, bottom, 90);
        b.Freeze();
        return b;
    }
}
