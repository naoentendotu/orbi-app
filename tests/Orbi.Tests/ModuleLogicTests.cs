using System.Windows;
using Orbi.Modules.Downloader;
using Orbi.Modules.Monitor;
using Orbi.Modules.Screenshot;
using Orbi.Modules.Server;
using Orbi.Modules.Snippets;

namespace Orbi.Tests;

public class SnippetTests
{
    [Fact]
    public void SearchFiltersAndPutsFavoritesFirst()
    {
        var s = new SnippetStore();
        s.Items.Add(new Snippet { Title = "b-sig", Content = "Abraços", Category = "E-mails e assinaturas" });
        s.Items.Add(new Snippet { Title = "a-git", Content = "git push --force-with-lease", Category = "Comandos" });
        s.Items.Add(new Snippet { Title = "z-fav", Content = "olá", Category = "Comandos", Favorite = true });
        Assert.Equal(["z-fav", "a-git", "b-sig"], s.Search("").Select(x => x.Title));
        Assert.Equal(["a-git"], s.Search("PUSH").Select(x => x.Title));
        Assert.Equal(["z-fav", "a-git"], s.Search("", "Comandos").Select(x => x.Title));
        Assert.Equal(["z-fav"], s.Search("", favoritesOnly: true).Select(x => x.Title));
    }
}

public class RegionTests
{
    [Fact]
    public void DragInAnyDirectionMapsToPixelsAndClamps()
    {
        // Overlay is 1000x500 DIPs over a 2000x1000 px screen (200% scaling).
        var r = RegionPicker.ToPixels(new Point(300, 200), new Point(100, 50), new Size(1000, 500), 2000, 1000);
        Assert.Equal(new Int32Rect(200, 100, 400, 300), r);
        var clamped = RegionPicker.ToPixels(new Point(900, 400), new Point(1200, 700), new Size(1000, 500), 2000, 1000);
        Assert.Equal(new Int32Rect(1800, 800, 200, 200), clamped);
    }
}

public class YtDlpTests
{
    [Fact]
    public void ParsesProgressTemplateLines()
    {
        Assert.Equal((42.3, "2.31MiB/s", "00:12"), YtDlp.ParseProgress("AF|  42.3%|  2.31MiB/s|00:12"));
        Assert.Null(YtDlp.ParseProgress("[download] Destination: x.mp4"));
        Assert.Null(YtDlp.ParseProgress("AF|N/A|Unknown|Unknown"));
    }

    [Fact]
    public void FormatArgsDependOnFfmpeg()
    {
        Assert.Equal(["-f", "bv*[height<=720]+ba/b[height<=720]", "--merge-output-format", "mp4"], YtDlp.FormatArgs("720", "mp4", true));
        Assert.Equal(["-f", "b"], YtDlp.FormatArgs("best", "mp4", false));
        Assert.Contains("mp3", YtDlp.FormatArgs("audio", "mp4", true));
        Assert.DoesNotContain("-x", YtDlp.FormatArgs("audio", "mp4", false));
    }

    [Fact]
    public void ParsesInfoJsonHeightsDescendingVideoOnly()
    {
        var json = """
            {"title":"Clip","extractor_key":"Youtube","duration":125.0,
             "formats":[{"height":360,"vcodec":"avc1"},{"height":1080,"vcodec":"vp9"},{"height":720,"vcodec":"avc1"},
                        {"height":720,"vcodec":"vp9"},{"vcodec":"none","acodec":"opus"},{"height":null}]}
            """;
        var info = YtDlp.ParseInfo(json);
        Assert.Equal("Clip", info.Title);
        Assert.Equal("Youtube", info.Platform);
        Assert.Equal(TimeSpan.FromSeconds(125), info.Duration);
        Assert.Equal([1080, 720, 360], info.Heights);
    }
}

public class TailscaleTests
{
    [Fact]
    public void ParsesPeers()
    {
        var json = """
            {"BackendState":"Running","Self":{"HostName":"pc"},
             "Peer":{"k1":{"HostName":"nas","DNSName":"nas.tail1.ts.net.","TailscaleIPs":["100.1.2.3"],"Online":true,"OS":"linux"},
                     "k2":{"HostName":"phone","DNSName":"phone.tail1.ts.net.","TailscaleIPs":[],"Online":false,"OS":"android"}}}
            """;
        var s = TailscaleStatus.Parse(json);
        Assert.True(s.Running);
        Assert.Equal(new Peer("nas", "nas.tail1.ts.net", "100.1.2.3", true, "linux"), s.Peers[0]);
        Assert.Equal("", s.Peers[1].Ip);
    }
}

public class DdcTests
{
    [Theory]
    [InlineData(0x10, true)]   // brightness
    [InlineData(0x04, false)]  // factory reset
    [InlineData(0x60, false)]  // input source: would black out the screen
    [InlineData(0xD6, false)]  // power mode
    public void BlocksDangerousWrites(byte code, bool safe) => Assert.Equal(safe, Ddc.IsSafeToWrite(code));
}

public class PageStitchTests
{
    // header rows 1,2 · content 100..399 (a 300-row page) seen through a 100-row window · footer row 9
    static int[] Frame(int scroll) => [1, 2, .. Enumerable.Range(100 + scroll, 100), 9];

    [Fact]
    public void FindsHeaderFooterAndScrollDistance()
    {
        int[] a = Frame(0), b = Frame(37);
        Assert.Equal(2, PageStitch.SameRows(a, b, fromBottom: false));
        Assert.Equal(1, PageStitch.SameRows(a, b, fromBottom: true));
        Assert.Equal(37, PageStitch.FindScroll(a, b, 2, a.Length - 1));
    }

    [Fact]
    public void EndOfPageAndLostTrack()
    {
        Assert.Equal(0, PageStitch.FindScroll(Frame(200), Frame(200), 2, 101)); // nothing moved
        Assert.Equal(-1, PageStitch.FindScroll(Frame(0), Frame(150), 2, 101)); // jumped past the whole screen: no overlap
    }

    [Fact]
    public void BlankRowsCantFakeAMatch()
    {
        int[] blank = [.. Enumerable.Repeat(5, 100)];
        int[] a = [.. blank, .. Enumerable.Range(100, 20)], b = [.. Enumerable.Range(100, 20).Reverse(), .. blank];
        Assert.Equal(-1, PageStitch.FindScroll(a, b, 0, a.Length));
    }
}

public class PageStitchStaticRowsTests
{
    [Fact]
    public void FixedHeaderInsideTheBandDoesNotSpoilTheMatch()
    {
        int[] header = [.. Enumerable.Range(1, 30)]; // big sticky header the first-frame detection missed
        int[] a = [.. header, .. Enumerable.Range(100, 70)], b = [.. header, .. Enumerable.Range(120, 70)];
        Assert.Equal(20, PageStitch.FindScroll(a, b, 0, a.Length));
    }
}

public class PageStitchBlankTests
{
    [Fact]
    public void BlankOverlapIsAcceptedOnlyAtTheExpectedDistance()
    {
        int[] a = [.. Enumerable.Range(1, 40), .. Enumerable.Repeat(0, 60)], b = [.. Enumerable.Range(21, 20), .. Enumerable.Repeat(0, 60), .. Enumerable.Range(500, 20)];
        Assert.True(PageStitch.Fits(a, b, 0, 100, 20));
        Assert.False(PageStitch.Fits(a, b, 0, 100, 5));
    }
}
