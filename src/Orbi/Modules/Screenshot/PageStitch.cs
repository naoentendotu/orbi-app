using System.Runtime.InteropServices;
using Drawing = System.Drawing;

namespace Orbi.Modules.Screenshot;

/// <summary>Glues screenshots of a page taken between scrolls into one tall image, by matching rows of pixels.</summary>
public static class PageStitch
{
    /// <summary>One hash per pixel row, leaving out the right edge where the scrollbar thumb moves.</summary>
    public static int[] RowHashes(Drawing.Bitmap bmp)
    {
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bmp.Width * 4];
            int len = Math.Max(1, bmp.Width - 32) * 4;
            var hashes = new int[bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                var h = new HashCode();
                h.AddBytes(row.AsSpan(0, len));
                hashes[y] = h.ToHashCode();
            }
            return hashes;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>How many rows two frames share at the top (or bottom): the fixed header/footer that doesn't scroll.</summary>
    public static int SameRows(int[] a, int[] b, bool fromBottom)
    {
        int n = 0;
        while (n < a.Length && a[fromBottom ? a.Length - 1 - n : n] == b[fromBottom ? b.Length - 1 - n : n]) n++;
        return n;
    }

    /// <summary>≥98% of rows identical: nothing scrolled (a blinking caret or a spinner doesn't count as movement).</summary>
    public static bool Still(int[] a, int[] b)
    {
        int same = 0;
        for (int y = 0; y < a.Length; y++) if (a[y] == b[y]) same++;
        return same * 50 >= a.Length * 49;
    }

    /// <summary>
    /// How far the content between top and bottom moved up from prev to next: 0 = nothing moved (end of page),
    /// -1 = no reliable match (scrolled too far, or the page changed). Only rows that are unique in prev and
    /// not static (same spot in both frames, like a title bar or sticky header) are compared, so blank lines,
    /// repeated patterns and fixed parts can't fake or spoil a match.
    /// </summary>
    public static int FindScroll(int[] prev, int[] next, int top, int bottom)
    {
        int band = bottom - top;
        if (Still(prev[top..bottom], next[top..bottom])) return 0;
        var count = new Dictionary<int, int>();
        for (int y = top; y < bottom; y++) count[prev[y]] = count.GetValueOrDefault(prev[y]) + 1;

        int best = -1, bestHits = 0;
        for (int dy = 1; dy < band; dy++)
        {
            int hits = 0, unique = 0;
            for (int y = top; y + dy < bottom; y++)
            {
                int h = prev[y + dy];
                if (count[h] != 1 || prev[y] == next[y]) continue;
                unique++;
                if (next[y] == h) hits++;
            }
            if (unique >= 8 && hits * 10 >= unique * 9 && hits > bestHits) { best = dy; bestHits = hits; } // 90%: tolerates a blinking caret or a hover effect
        }
        return best;
    }

    /// <summary>Whether next is prev moved up by dy, counting every row (for overlaps too blank for FindScroll to be sure).</summary>
    public static bool Fits(int[] prev, int[] next, int top, int bottom, int dy)
    {
        if (dy <= 0 || dy >= bottom - top) return false;
        int hits = 0, n = bottom - top - dy;
        for (int y = top; y < top + n; y++) if (next[y] == prev[y + dy]) hits++;
        return hits * 10 >= n * 9;
    }

    /// <summary>Stacks slices of equal width top to bottom.</summary>
    public static Drawing.Bitmap Join(IReadOnlyList<Drawing.Bitmap> slices)
    {
        var bmp = new Drawing.Bitmap(slices[0].Width, slices.Sum(s => s.Height));
        using var g = Drawing.Graphics.FromImage(bmp);
        int y = 0;
        foreach (var s in slices) { g.DrawImageUnscaled(s, 0, y); y += s.Height; }
        return bmp;
    }
}
