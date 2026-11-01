using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Orbi.Core;
using Orbi.Shell;
using NAudio.CoreAudioApi;

namespace Orbi.Modules.Mic;

public sealed class MicModule : IModule
{
    readonly MMDeviceEnumerator _devices = new();
    StackPanel? _preview, _expanded;

    public string Id => "mic";
    public string Title => "Microfone";
    public string Glyph => IsMuted() == true ? "\uF781" : "\uE720";
    public string Status => IsMuted() switch { null => "Sem microfone", true => "Mudo", false => "Ativo" };

    public FrameworkElement Preview => Render(ref _preview, full: false);
    public FrameworkElement Expanded => Render(ref _expanded, full: true);

    MMDevice? Default() =>
        _devices.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var d) ? d : null;

    bool? IsMuted()
    {
        try { using var d = Default(); return d?.AudioEndpointVolume.Mute; }
        catch (COMException) { return null; } // device unplugged mid-call
    }

    void ToggleMute()
    {
        try
        {
            using var d = Default();
            if (d == null) return;
            d.AudioEndpointVolume.Mute = !d.AudioEndpointVolume.Mute;
            Ui.Feedback(d.AudioEndpointVolume.Mute ? "Microfone silenciado" : "Microfone ativado");
        }
        catch (COMException) { Ui.Feedback("Não foi possível alterar o microfone"); }
        Refresh();
    }

    void SetDefault(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
        Ui.Feedback("Dispositivo alterado");
        Refresh();
    }

    // Rebuilt on every show so state changed elsewhere (Windows settings, headset button) shows up.
    StackPanel Render(ref StackPanel? host, bool full)
    {
        host ??= new StackPanel();
        Fill(host, full);
        return host;
    }

    void Refresh()
    {
        if (_preview?.IsVisible == true) Fill(_preview, false);
        if (_expanded?.IsVisible == true) Fill(_expanded, true);
    }

    void Fill(StackPanel host, bool full)
    {
        host.Children.Clear();
        using var current = Default();
        var muted = IsMuted();

        var toggle = Ui.Toggle(muted == true ? "Silenciado" : "Ativo", muted == false, _ => ToggleMute(),
            muted == true ? "\uF781" : "\uE720");
        toggle.FontSize = 16;
        toggle.Padding = new Thickness(18, 14, 18, 14);
        toggle.HorizontalAlignment = HorizontalAlignment.Stretch;
        toggle.IsEnabled = muted != null;
        host.Children.Add(toggle);
        host.Children.Add(Ui.Text(current?.FriendlyName ?? "Nenhum dispositivo de entrada", "Sub"));

        if (!full) return;
        host.Children.Add(Ui.Text("DISPOSITIVOS DE ENTRADA", "Section"));
        foreach (var d in _devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            var id = d.ID;
            var b = Ui.Toggle(d.FriendlyName, id == current?.ID, _ => SetDefault(id), "\uE720");
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            host.Children.Add(b);
            d.Dispose();
        }
    }

    // Undocumented but stable since Vista: the only way to change the default endpoint. Unused slots keep the vtable order.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
        void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
        void GetPropertyValue(); void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }
}
