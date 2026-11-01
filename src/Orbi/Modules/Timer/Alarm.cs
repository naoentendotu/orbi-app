namespace Orbi.Modules.Timer;

public sealed class Alarm
{
    public TimeSpan Time;                 // time of day
    public List<DayOfWeek> Days = [];     // empty = fires once
    public string Label = "";
    public string? Sound;                 // null = default sound
    public bool Enabled = true;

    /// <summary>First moment strictly after now when this alarm should ring.</summary>
    public DateTime NextFire(DateTime now)
    {
        for (int d = 0; d <= 7; d++)
        {
            var day = now.Date.AddDays(d);
            var at = day + Time;
            if (at > now && (Days.Count == 0 || Days.Contains(day.DayOfWeek))) return at;
        }
        return DateTime.MaxValue; // unreachable: some day in the next week always matches
    }

    /// <summary>"90" = 90 min, "5:30" = 5 min 30 s, "1:00:00" = 1 h.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length > 3 || parts.Any(p => !int.TryParse(p, out var n) || n < 0)) return null;
        var n = parts.Select(int.Parse).ToArray();
        var t = n.Length switch
        {
            1 => TimeSpan.FromMinutes(n[0]),
            2 => new TimeSpan(0, n[0], n[1]),
            _ => new TimeSpan(n[0], n[1], n[2]),
        };
        return t > TimeSpan.Zero ? t : null;
    }
}

public sealed class AlarmList
{
    public List<Alarm> Items { get; set; } = [];
}
