using Orbi.Core;

namespace Orbi.Tests;

public class TriggerTests
{
    [Theory]
    [InlineData(0xA4, true)]  // left Alt
    [InlineData(0xA3, true)]  // right Ctrl
    [InlineData(0x14, true)]  // Caps Lock
    [InlineData(0x78, true)]  // F9
    [InlineData(0x41, false)] // A: would be swallowed and stop typing
    [InlineData(0x0D, false)] // Enter
    [InlineData(0x1B, false)] // Esc
    public void HoldKeyRules(int vk, bool allowed) => Assert.Equal(allowed, Trigger.IsAllowedHoldKey(vk));

    [Fact]
    public void ComboRules()
    {
        Assert.True(Trigger.IsAllowedCombo(Trigger.ModCtrl | Trigger.ModShift, 0x20)); // Ctrl+Shift+Space
        Assert.True(Trigger.IsAllowedCombo(0, 0x78));                                    // F9 alone
        Assert.False(Trigger.IsAllowedCombo(0, 0x41));                                   // plain A
        Assert.False(Trigger.IsAllowedCombo(Trigger.ModCtrl, 0xA2));                     // modifier as main key
    }

    [Fact]
    public void ModifiersAreNotSwallowedOtherKeysAre()
    {
        Assert.False(Trigger.Swallows(0xA4));
        Assert.True(Trigger.Swallows(0x14));
    }

    [Fact]
    public void Names()
    {
        Assert.Equal("Ctrl + Shift + Espaço", Trigger.ComboName(Trigger.ModCtrl | Trigger.ModShift, 0x20));
        Assert.Equal("Alt + Win + K", Trigger.ComboName(Trigger.ModWin | Trigger.ModAlt, 0x4B)); // fixed order: Ctrl, Alt, Shift, Win
        Assert.Equal("Alt esquerdo", Trigger.KeyName(0xA4));
        Assert.Equal("F12", Trigger.KeyName(0x7B));
    }
}

public class PaletteAndRetentionTests
{
    [Fact]
    public void ParsesHexAndPicksReadableText()
    {
        Assert.Equal("#FF9500", Orbi.Core.Palette.Hex(Orbi.Core.Palette.Parse("ff9500")!.Value));
        Assert.Null(Orbi.Core.Palette.Parse("#12"));
        Assert.Null(Orbi.Core.Palette.Parse(""));
        Assert.True(Orbi.Core.Palette.IsLight(Orbi.Core.Palette.Parse("#FFCC00")!.Value));   // yellow → dark text
        Assert.False(Orbi.Core.Palette.IsLight(Orbi.Core.Palette.Parse("#007AFF")!.Value));  // blue → white text
        Assert.False(Orbi.Core.Palette.IsLight(Orbi.Core.Palette.Parse("#FF9500")!.Value));  // orange keeps white text, like macOS
    }

    [Fact]
    public void PruneDropsOldNonFavoritesOnly()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0);
        var h = new Orbi.Modules.Clipboard.ClipboardHistory { KeepHours = 24 };
        h.Items.Add(new() { Text = "new", At = now.AddHours(-1) });
        h.Items.Add(new() { Text = "old", At = now.AddDays(-2) });
        h.Items.Add(new() { Text = "old-fav", At = now.AddDays(-30), Favorite = true });
        Assert.Equal("old", Assert.Single(h.Prune(now)).Text);
        Assert.Equal(["new", "old-fav"], h.Items.Select(i => i.Text));
        h.KeepHours = 0;
        h.Items.Add(new() { Text = "ancient", At = now.AddYears(-5) });
        Assert.Empty(h.Prune(now)); // "Sempre"
    }
}
