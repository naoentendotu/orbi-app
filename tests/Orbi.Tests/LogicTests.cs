using Orbi.Core;
using Orbi.Modules.Clipboard;
using Orbi.Modules.Timer;

namespace Orbi.Tests;

public class HoldDetectorTests
{
    static readonly DateTime T0 = new(2026, 1, 1);
    readonly HoldDetector _d = new(TimeSpan.FromMilliseconds(250));

    [Fact]
    public void HoldAloneOpensThenReleaseCloses()
    {
        _d.Down(T0);
        Assert.Equal(HoldAction.None, _d.Tick(T0.AddMilliseconds(100)));
        Assert.Equal(HoldAction.Open, _d.Tick(T0.AddMilliseconds(250)));
        Assert.Equal(HoldAction.Close, _d.Up());
    }

    [Fact]
    public void AutoRepeatDoesNotRestartTheHold()
    {
        _d.Down(T0);
        _d.Down(T0.AddMilliseconds(200)); // key repeat
        Assert.Equal(HoldAction.Open, _d.Tick(T0.AddMilliseconds(260)));
    }

    [Fact]
    public void AltTabNeverOpens()
    {
        _d.Down(T0);
        _d.OtherKey();
        Assert.Equal(HoldAction.None, _d.Tick(T0.AddSeconds(1)));
        Assert.Equal(HoldAction.None, _d.Up());
    }

    [Fact]
    public void OtherKeyWhileOpenClosesAndNextHoldWorks()
    {
        _d.Down(T0);
        _d.Tick(T0.AddMilliseconds(300));
        Assert.Equal(HoldAction.Close, _d.OtherKey());
        Assert.Equal(HoldAction.None, _d.Up());
        _d.Down(T0.AddSeconds(5));
        Assert.Equal(HoldAction.Open, _d.Tick(T0.AddSeconds(6)));
    }

    [Fact]
    public void QuickTapDoesNothing()
    {
        _d.Down(T0);
        Assert.Equal(HoldAction.None, _d.Up());
        Assert.Equal(HoldAction.None, _d.Tick(T0.AddSeconds(1)));
    }
}

public class ClipboardHistoryTests
{
    static ClipItem Text(string t) => new() { Kind = ClipboardHistory.Classify(t), Text = t };

    [Fact]
    public void DuplicateMovesToTopAndKeepsFavorite()
    {
        var h = new ClipboardHistory();
        h.Add(Text("a"));
        h.Add(Text("b"));
        h.Items[1].Favorite = true; // "a"
        h.Add(Text("a"));
        Assert.Equal(["a", "b"], h.Items.Select(i => i.Text));
        Assert.True(h.Items[0].Favorite);
    }

    [Fact]
    public void LimitEvictsOldestNonFavorite()
    {
        var h = new ClipboardHistory();
        h.Add(Text("oldest-fav"));
        h.Items[0].Favorite = true;
        h.Add(Text("oldest"));
        List<ClipItem> evicted = [];
        for (int i = 0; i < ClipboardHistory.Limit - 1; i++) evicted.AddRange(h.Add(Text($"n{i}")));
        Assert.Equal(ClipboardHistory.Limit, h.Items.Count);
        Assert.Equal("oldest", Assert.Single(evicted).Text);
        Assert.Contains(h.Items, i => i.Text == "oldest-fav");
    }

    [Fact]
    public void SearchClearAndClassify()
    {
        var h = new ClipboardHistory();
        h.Add(Text("https://example.com/x"));
        h.Add(Text("Hello World"));
        h.Items[1].Favorite = true;
        Assert.Equal(ClipKind.Link, h.Items[1].Kind);
        Assert.Equal(ClipKind.Text, ClipboardHistory.Classify("see https://x.com now"));
        Assert.Single(h.Search("world"));
        Assert.Single(h.Search("", favoritesOnly: true));
        Assert.Single(h.Clear());
        Assert.Single(h.Items);
    }
}

public class AlarmTests
{
    [Fact]
    public void OnceAlarmLaterTodayOrTomorrow()
    {
        var a = new Alarm { Time = new TimeSpan(7, 0, 0) };
        Assert.Equal(new DateTime(2026, 10, 30, 7, 0, 0), a.NextFire(new DateTime(2026, 10, 30, 6, 59, 0)));
        Assert.Equal(new DateTime(2026, 10, 31, 7, 0, 0), a.NextFire(new DateTime(2026, 10, 30, 7, 0, 0)));
    }

    [Fact]
    public void RecurringSkipsToNextMatchingDay()
    {
        // 2026-10-30 is a Friday; weekday alarm after it rang goes to Monday.
        var a = new Alarm { Time = new TimeSpan(7, 0, 0), Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] };
        Assert.Equal(new DateTime(2026, 11, 2, 7, 0, 0), a.NextFire(new DateTime(2026, 10, 30, 7, 0, 1)));
        // Same weekday, already passed: next week.
        var weekly = new Alarm { Time = new TimeSpan(9, 0, 0), Days = [DayOfWeek.Friday] };
        Assert.Equal(new DateTime(2026, 11, 6, 9, 0, 0), weekly.NextFire(new DateTime(2026, 10, 30, 10, 0, 0)));
    }

    [Theory]
    [InlineData("25", 25 * 60)]
    [InlineData("5:30", 330)]
    [InlineData("1:00:00", 3600)]
    [InlineData("0", null)]
    [InlineData("abc", null)]
    [InlineData("-1", null)]
    public void ParseDuration(string input, int? seconds) =>
        Assert.Equal(seconds, Alarm.ParseDuration(input) is { } t ? (int)t.TotalSeconds : null);
}
