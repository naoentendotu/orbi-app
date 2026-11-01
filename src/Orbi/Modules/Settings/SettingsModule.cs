using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Orbi.Core;
using Orbi.Shell;
using Trigger = Orbi.Core.Trigger;

namespace Orbi.Modules.Settings;

/// <summary>Always-present wedge. Settings live in the widget instead of a separate window.</summary>
public sealed class SettingsModule(IReadOnlyList<IModule> available) : IModule
{
    static readonly (string, string)[] Themes = [("auto", "Automático"), ("light", "Claro"), ("dark", "Escuro")];

    public string Id => "settings";
    public string Title => "Ajustes";
    public string Glyph => "\uE713";
    public string Status => "";

    static Core.Settings S => App.Settings;

    public FrameworkElement Preview => Ui.Col(
        Ui.Text("TEMA", "Section"),
        Ui.Choice(Themes, S.Theme, v => { S.Theme = v; App.ApplySettings(); }),
        Ui.Text("Clique na fatia para todas as opções.", "Sub"));

    public FrameworkElement Expanded
    {
        get
        {
            var modules = new StackPanel();
            FillModules(modules);
            var trigger = new StackPanel();
            FillTrigger(trigger);
            var startup = new CheckBox { Content = "Iniciar com o Windows", IsChecked = Startup.Enabled, Margin = new Thickness(0, 4, 0, 8) };
            startup.Click += (_, _) => Startup.Enabled = startup.IsChecked == true;

            var body = Ui.Col(
                Ui.Text("COMO ABRIR O MENU", "Section"), trigger,
                Ui.Text("MÓDULOS NO MENU", "Section"), modules,
                Ui.Text("TEMA", "Section"),
                Ui.Choice(Themes, S.Theme, v => { S.Theme = v; App.ApplySettings(); }),
                Ui.Text("COR DE DESTAQUE", "Section"), ColorPicker(AccentPresets, S.AccentColor, v => S.AccentColor = v),
                Ui.Text("COR DO CÍRCULO", "Section"), ColorPicker(RingPresets, S.RingColor, v => S.RingColor = v),
                Ui.Text("TAMANHO", "Section"), Slider(0.8, 1.3, S.Scale, v => S.Scale = v),
                Ui.Text("OPACIDADE", "Section"), Slider(0.6, 1, S.Opacity, v => S.Opacity = v),
                Ui.Text("Tamanho e opacidade valem na próxima abertura.", "Sub"),
                Ui.Text("POSIÇÃO AO ABRIR", "Section"),
                Ui.Choice([("cursor", "No cursor"), ("center", "Centro da tela")], S.Position, v => { S.Position = v; App.ApplySettings(); }),
                startup,
                Ui.Btn("Sair do Orbi", () => Application.Current.Shutdown(), "\uE7E8"));
            return Ui.Scroll(body, 360);
        }
    }

    // macOS system accent colors + Orbi's own red.
    static readonly (string Hex, string Name)[] AccentPresets =
    [
        ("#EE4A35", "Orbi"), ("#FF3B30", "Vermelho"), ("#FF9500", "Laranja"), ("#FFCC00", "Amarelo"), ("#34C759", "Verde"),
        ("#00C7BE", "Menta"), ("#007AFF", "Azul"), ("#5856D6", "Índigo"), ("#AF52DE", "Roxo"), ("#FF2D55", "Rosa"), ("#8E8E93", "Grafite"),
    ];

    static readonly (string Hex, string Name)[] RingPresets =
    [
        ("#2C2C2E", "Grafite"), ("#1C1C1E", "Preto"), ("#F5F5F7", "Branco"), ("#D1D1D6", "Prata"), ("#1C2A44", "Azul-noite"),
        ("#3A1C24", "Vinho"), ("#1F3A2E", "Floresta"), ("#EADBC8", "Areia"), ("#F4D6CC", "Pêssego"),
    ];

    /// <summary>"Padrão" chip + round swatches + hex field. Applies live.</summary>
    static FrameworkElement ColorPicker((string Hex, string Name)[] presets, string current, Action<string> set)
    {
        var host = new StackPanel();
        void Pick(string hex)
        {
            current = hex;
            set(hex);
            App.ApplySettings();
            Fill();
        }
        void Fill()
        {
            host.Children.Clear();
            var wrap = new WrapPanel();
            var auto = Ui.Toggle("Padrão", current == "", _ => Pick(""));
            auto.ToolTip = "Cor do tema";
            wrap.Children.Add(auto);
            foreach (var (hex, name) in presets) wrap.Children.Add(Swatch(hex, name, string.Equals(hex, current, StringComparison.OrdinalIgnoreCase), () => Pick(hex)));
            var input = new TextBox { Width = 96, Text = current, ToolTip = "Qualquer cor em hex, ex.: #FF9500" };
            void Apply()
            {
                if (input.Text.Trim() == "") Pick("");
                else if (Palette.Parse(input.Text) is { } c) Pick(Palette.Hex(c));
                else Ui.Feedback("Cor inválida (use #RRGGBB)");
            }
            input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
            host.Children.Add(wrap);
            host.Children.Add(Ui.Row(input, Ui.Btn("Usar", Apply)));
        }
        Fill();
        return host;
    }

    static FrameworkElement Swatch(string hex, string name, bool selected, Action pick)
    {
        var c = Palette.Parse(hex)!.Value;
        var dot = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Palette.Vertical(c),
            BorderThickness = new Thickness(1), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0, 0, 0)),
        };
        var ring = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2), Child = dot,
            Margin = new Thickness(0, 0, 4, 6), Cursor = Cursors.Hand, ToolTip = name, Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
        };
        if (selected) ring.SetResourceReference(Border.BorderBrushProperty, "TextBrush");
        ring.MouseLeftButtonUp += (_, _) => pick();
        return ring;
    }

    static FrameworkElement Slider(double min, double max, double value, Action<double> set, bool showValue = false, Action? apply = null)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Width = 240, Focusable = false };
        var label = Ui.Text(showValue ? $"{value:0}" : "", "Sub");
        label.Margin = new Thickness(8, 0, 0, 0);
        s.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            if (showValue) label.Text = $"{e.NewValue:0}";
        };
        s.PreviewMouseUp += (_, _) => (apply ?? App.ApplySettings)(); // save once per drag, not per pixel
        return Ui.Row(s, label);
    }

    void FillTrigger(StackPanel host)
    {
        host.Children.Clear();
        host.Children.Add(Ui.Choice([("hold", "Segurar uma tecla"), ("combo", "Combinação de teclas")], S.Trigger,
            v => { S.Trigger = v; Commit(host); }));
        if (S.Trigger != "combo")
        {
            host.Children.Add(Ui.Row(Ui.Text("Tecla:  ", "Sub"), Ui.Text(Trigger.KeyName(S.HoldKey), "Title"), Spacer(), Recorder(host, combo: false)));
            if (Trigger.Swallows(S.HoldKey))
                host.Children.Add(Wrap(Ui.Text("Essa tecla fica reservada para o Orbi e deixa de fazer a função normal.", "Sub")));
            host.Children.Add(Ui.Text("TEMPO SEGURANDO (MS)", "Section"));
            host.Children.Add(Slider(150, 1500, S.HoldDelayMs, v => S.HoldDelayMs = (int)v, showValue: true, apply: () => Commit(host)));
            host.Children.Add(Ui.Text("AO SOLTAR A TECLA", "Section"));
            host.Children.Add(Ui.Choice([("close", "Fechar"), ("stay", "Manter aberto")], S.CloseOnRelease ? "close" : "stay",
                v => { S.CloseOnRelease = v == "close"; App.ApplySettings(); }));
        }
        else
        {
            host.Children.Add(Ui.Row(Ui.Text("Atalho:  ", "Sub"), Ui.Text(Trigger.ComboName(S.ComboMods, S.ComboKey), "Title"), Spacer(), Recorder(host, combo: true)));
            host.Children.Add(Wrap(Ui.Text("Abre o menu fixado; aperte de novo (ou Esc) para fechar.", "Sub")));
        }
    }

    static TextBlock Wrap(TextBlock t) { t.TextWrapping = TextWrapping.Wrap; return t; }

    static FrameworkElement Spacer() => new Border { Width = 10 };

    void Commit(StackPanel host)
    {
        App.ApplySettings();
        if (!App.ApplyTrigger()) Ui.Feedback("Esse atalho já está em uso por outro programa");
        FillTrigger(host);
    }

    /// <summary>Click, then press the key (or combo). Esc cancels. The current trigger is paused meanwhile.</summary>
    FrameworkElement Recorder(StackPanel host, bool combo)
    {
        const string idle = "Gravar…";
        var box = new TextBox
        {
            Width = 140, IsReadOnly = true, Text = idle, Cursor = Cursors.Hand, IsReadOnlyCaretVisible = false,
            ToolTip = combo ? "Clique e aperte o atalho (ex.: Ctrl + Shift + Espaço)" : "Clique e aperte a tecla que vai abrir o menu",
        };
        box.GotKeyboardFocus += (_, _) => { box.Text = combo ? "Aperte o atalho…" : "Aperte a tecla…"; App.PauseTrigger(true); };
        box.LostKeyboardFocus += (_, _) => { box.Text = idle; App.PauseTrigger(false); };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true; // also keeps Esc from closing the widget
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (!combo)
            {
                if (!Trigger.IsAllowedHoldKey(vk)) { box.Text = $"{Trigger.KeyName(vk)} não dá"; return; }
                S.HoldKey = vk;
            }
            else
            {
                if (Trigger.IsModifier(vk)) return; // wait for the main key
                int mods = (int)Keyboard.Modifiers;
                if (!Trigger.IsAllowedCombo(mods, vk)) { box.Text = "Use Ctrl/Alt/Shift/Win + tecla"; return; }
                (S.ComboMods, S.ComboKey) = (mods, vk);
            }
            App.ApplySettings();
            if (!App.PauseTrigger(false)) Ui.Feedback("Esse atalho já está em uso por outro programa");
            else Ui.Feedback(combo ? $"Atalho: {Trigger.ComboName(S.ComboMods, S.ComboKey)}" : $"Tecla: {Trigger.KeyName(S.HoldKey)}");
            FillTrigger(host);
        };
        return box;
    }

    void FillModules(StackPanel host)
    {
        host.Children.Clear();
        var enabled = S.Modules;
        var ordered = enabled.Select(id => available.FirstOrDefault(m => m.Id == id)).OfType<IModule>()
            .Concat(available.Where(m => !enabled.Contains(m.Id)));
        foreach (var m in ordered)
        {
            var id = m.Id;
            var check = new CheckBox { Content = m.Title, IsChecked = enabled.Contains(id), Width = 200 };
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) enabled.Add(id); else enabled.Remove(id);
                App.ApplySettings();
                FillModules(host);
            };
            var row = Ui.Row(check);
            if (enabled.Contains(id))
            {
                row.Children.Add(Ui.IconBtn("\uE70E", () => Move(host, id, -1), "Subir"));
                row.Children.Add(Ui.IconBtn("\uE70D", () => Move(host, id, +1), "Descer"));
            }
            host.Children.Add(row);
        }
    }

    void Move(StackPanel host, string id, int delta)
    {
        var list = S.Modules;
        int i = list.IndexOf(id), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        App.ApplySettings();
        FillModules(host);
    }
}
