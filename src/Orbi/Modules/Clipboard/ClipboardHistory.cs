namespace Orbi.Modules.Clipboard;

public enum ClipKind { Text, Link, Image }

public sealed class ClipItem
{
    public ClipKind Kind;
    public string Text = "";      // the text/link, or the content hash for images
    public string? ImagePath;
    public bool Favorite;
    public DateTime At = DateTime.Now;
}

/// <summary>Newest first, deduped, capped at Limit; favorites never get evicted.</summary>
public sealed class ClipboardHistory
{
    public const int Limit = 200;

    public List<ClipItem> Items { get; set; } = [];

    /// <summary>How long copied items are kept; 0 = forever. Favorites never expire.</summary>
    public int KeepHours { get; set; }

    /// <summary>Removes non-favorites older than KeepHours. Returns them so the caller can delete their files.</summary>
    public List<ClipItem> Prune(DateTime now)
    {
        if (KeepHours <= 0) return [];
        var cutoff = now.AddHours(-KeepHours);
        var expired = Items.Where(x => !x.Favorite && x.At < cutoff).ToList();
        Items.RemoveAll(expired.Contains);
        return expired;
    }

    /// <summary>Adds (or moves an identical item to the top). Returns evicted items so the caller can delete their files.</summary>
    public List<ClipItem> Add(ClipItem item)
    {
        var i = Items.FindIndex(x => x.Kind == item.Kind && x.Text == item.Text);
        if (i >= 0)
        {
            var existing = Items[i]; // keep its favorite flag
            existing.At = item.At;
            Items.RemoveAt(i);
            item = existing;
        }
        Items.Insert(0, item);

        var evicted = new List<ClipItem>();
        for (int j = Items.Count - 1; j >= 0 && Items.Count > Limit; j--)
            if (!Items[j].Favorite) { evicted.Add(Items[j]); Items.RemoveAt(j); }
        return evicted;
    }

    public IEnumerable<ClipItem> Search(string query, bool favoritesOnly = false) =>
        Items.Where(x => (!favoritesOnly || x.Favorite)
            && (string.IsNullOrWhiteSpace(query) || (x.Kind != ClipKind.Image && x.Text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))));

    /// <summary>Removes everything except favorites; returns what was removed.</summary>
    public List<ClipItem> Clear()
    {
        var removed = Items.Where(x => !x.Favorite).ToList();
        Items.RemoveAll(x => !x.Favorite);
        return removed;
    }

    public static ClipKind Classify(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && !text.Trim().Contains(' ')
            ? ClipKind.Link : ClipKind.Text;
}
