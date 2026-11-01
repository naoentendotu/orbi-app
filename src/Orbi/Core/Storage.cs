using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Orbi.Core;

public static class Storage
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbi");

    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string name) where T : new()
    {
        var path = Path.Combine(Dir, name);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts) ?? new T() : new T(); }
        catch (Exception)
        {
            // Keep the broken file for inspection instead of overwriting it on next save.
            try { File.Move(path, path + ".bad", true); } catch { }
            return new T();
        }
    }

    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts)); // write-then-rename: no half-written file on crash
        File.Move(tmp, path, true);
    }
}

public sealed class Settings
{
    public List<string> Modules = ["clipboard", "screenshot", "timer", "snippets", "mic", "monitor", "downloader", "server"]; // enabled, in ring order
    public string Theme = "auto";        // auto | light | dark
    public string AccentColor = "";      // hex; empty = theme default
    public string RingColor = "";        // hex; empty = theme default
    public double Scale = 1.0;
    public double Opacity = 1.0;
    public string Position = "cursor";   // cursor | center
    public string Trigger = "hold";      // hold | combo
    public int HoldKey = 0xA4;           // virtual-key held alone (left Alt)
    public int HoldDelayMs = 250;
    public int ComboMods = 2 | 4;        // Ctrl + Shift (Core.Trigger.Mod*)
    public int ComboKey = 0x20;          // Space
    public bool CloseOnRelease = true;

    public static Settings Load() => Storage.Load<Settings>("settings.json");
    public void Save() => Storage.Save("settings.json", this);
}

public static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Orbi";

    public static bool Enabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(Name) is string; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(Name, false);
        }
    }
}
