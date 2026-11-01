using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Orbi.Core;
using Orbi.Shell;

namespace Orbi.Modules.Snippets;

public sealed class Snippet
{
    public string Title = "";
    public string Content = "";
    public string Category = "";
    public bool Favorite;
}

public sealed class SnippetStore
{
    public List<string> Categories { get; set; } =
        ["Textos e mensagens", "E-mails e assinaturas", "Desenvolvimento", "Comandos", "Informações pessoais", "Personalizados"];
    public List<Snippet> Items { get; set; } = [];

    /// <summary>Matches title, content or category; favorites first, then by title.</summary>
    public IEnumerable<Snippet> Search(string query, string? category = null, bool favoritesOnly = false)
    {
        var q = query.Trim();
        return Items
            .Where(s => (category == null || s.Category == category) && (!favoritesOnly || s.Favorite)
                && (q == "" || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Content.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || s.Category.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.Favorite).ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase);
    }
}

public sealed class SnippetsModule : IModule
{
    const int PreviewCount = 7;

    readonly SnippetStore _store = Storage.Load<SnippetStore>("snippets.json");
    readonly TextBox _pvSearch = new(), _search = new() { ToolTip = "Buscar" };
    readonly StackPanel _pvList = new(), _list = new();
    readonly StackPanel _preview;
    readonly DockPanel _expanded = new(), _header;
    string? _category;

    public SnippetsModule()
    {
        _pvSearch.TextChanged += (_, _) => FillPreview();
        _search.TextChanged += (_, _) => FillList();
        _preview = Ui.Col(_pvSearch, _pvList);
        // Search takes whatever width is left; "Novo" is always visible on the right.
        var add = Ui.Btn("Novo", () => Edit(null), "\uE710", "AccentBtn");
        add.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(add, Dock.Right);
        _header = new DockPanel { Children = { add, _search } };
    }

    /// <summary>Fixed parts docked top/bottom, the last element fills (and scrolls) the remaining panel height.</summary>
    void Layout(UIElement? top, UIElement? bottom, UIElement fill)
    {
        _expanded.Children.Clear();
        foreach (var (e, dock) in new[] { (top, Dock.Top), (bottom, Dock.Bottom) })
            if (e != null) { DockPanel.SetDock(e, dock); _expanded.Children.Add(e); }
        _expanded.Children.Add(fill);
    }

    static WrapPanel Compact(WrapPanel chips)
    {
        foreach (FrameworkElement c in chips.Children) { ((Control)c).Padding = new Thickness(9, 4, 9, 4); ((Control)c).FontSize = 11.5; c.Margin = new Thickness(0, 0, 4, 4); }
        return chips;
    }

    public string Id => "snippets";
    public string Title => "Snippets";
    public string Glyph => "\uE8C8";
    public string Status => $"{_store.Items.Count} salvos";

    public FrameworkElement Preview { get { FillPreview(); return _preview; } }
    public FrameworkElement Expanded { get { ShowList(); return _expanded; } }

    void Save() => Storage.Save("snippets.json", _store);

    void FillPreview()
    {
        _pvList.Children.Clear();
        var items = _pvSearch.Text.Trim() == "" ? _store.Search("", favoritesOnly: true) : _store.Search(_pvSearch.Text);
        foreach (var s in items.Take(PreviewCount)) _pvList.Children.Add(Row(s, false));
        if (_pvList.Children.Count == 0)
            _pvList.Children.Add(Ui.Text(_store.Items.Count == 0 ? "Clique na fatia para criar snippets." : "Busque acima ou favorite (★) para ver aqui.", "Sub"));
    }

    void ShowList()
    {
        var cats = new List<(string, string)> { ("", "Todas") };
        cats.AddRange(_store.Categories.Select(c => (c, c)));
        var chips = Compact(Ui.Choice(cats, _category ?? "", v => { _category = v == "" ? null : v; FillList(); }));
        chips.Margin = new Thickness(0, 2, 0, 6);
        (_list.Parent as ScrollViewer)?.ClearValue(ContentControl.ContentProperty); // reused across rebuilds: detach first
        (_header.Parent as Panel)?.Children.Remove(_header);
        Layout(Ui.Col(_header, chips), null, new ScrollViewer { Content = _list });
        FillList();
    }

    void FillList()
    {
        _list.Children.Clear();
        foreach (var s in _store.Search(_search.Text, _category)) _list.Children.Add(Row(s, true));
        if (_list.Children.Count == 0) _list.Children.Add(Ui.Text("Nada aqui ainda.", "Sub"));
    }

    void Changed()
    {
        Save();
        if (_pvList.IsVisible) FillPreview();
        if (_list.IsVisible) FillList();
    }

    void Copy(Snippet s)
    {
        try { System.Windows.Clipboard.SetText(s.Content); Ui.Feedback($"“{s.Title}” copiado"); }
        catch (ExternalException) { Ui.Feedback("Clipboard ocupado, tente de novo"); }
    }

    FrameworkElement Row(Snippet s, bool full)
    {
        var text = Ui.Col(Ui.Text(s.Title), Ui.Text(s.Content.ReplaceLineEndings(" "), "Sub"));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = Ui.Row(Ui.IconBtn(s.Favorite ? "\uE735" : "\uE734", () => { s.Favorite = !s.Favorite; Changed(); }, "Favoritar"));
        if (full)
        {
            actions.Children.Add(Ui.IconBtn("\uE70F", () => Edit(s), "Editar"));
            actions.Children.Add(Ui.IconBtn("\uE74D", () => { _store.Items.Remove(s); Changed(); Ui.Feedback("Excluído"); }, "Excluir"));
        }
        Grid.SetColumn(actions, 1);
        grid.Children.Add(text);
        grid.Children.Add(actions);

        var tip = new TextBlock { Text = s.Content.Length > 2000 ? s.Content[..2000] + "…" : s.Content, TextWrapping = TextWrapping.Wrap };
        return Ui.Card(grid, () => Copy(s), tip);
    }

    void Edit(Snippet? existing)
    {
        var title = new TextBox { Text = existing?.Title ?? "" };
        var content = new TextBox
        {
            Text = existing?.Content ?? "", Height = 120, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        content.SetResourceReference(Control.FontFamilyProperty, existing?.Category == "Desenvolvimento" || existing?.Category == "Comandos" ? "MonoFont" : "UiFont");
        var category = existing?.Category ?? _category ?? _store.Categories[0];
        var newCategory = new TextBox { ToolTip = "Nova categoria (opcional)" };
        var orNew = Ui.Text("ou nova:  ", "Sub");
        orNew.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(orNew, Dock.Left);

        var form = Ui.Col(
            Ui.Text("TÍTULO", "Section"), title,
            Ui.Text("CATEGORIA", "Section"), Compact(Ui.Choice(_store.Categories.Select(c => (c, c)), category, v => category = v)),
            new DockPanel { Children = { orNew, newCategory } },
            Ui.Text("CONTEÚDO", "Section"), content);
        form.Margin = new Thickness(0, 0, 6, 0); // room for the scrollbar

        // Save/Cancel pinned to the bottom: never pushed out of view by a long form.
        var buttons = Ui.Row(
            Ui.Btn("Salvar", () =>
            {
                if (title.Text.Trim() == "" || content.Text == "") { Ui.Feedback("Preencha título e conteúdo"); return; }
                var cat = newCategory.Text.Trim() != "" ? newCategory.Text.Trim() : category;
                if (!_store.Categories.Contains(cat)) _store.Categories.Add(cat);
                var s = existing ?? new Snippet();
                s.Title = title.Text.Trim();
                s.Content = content.Text;
                s.Category = cat;
                if (existing == null) _store.Items.Add(s);
                Save();
                Ui.Feedback("Snippet salvo");
                ShowList();
            }, "\uE74E", "AccentBtn"),
            Ui.Btn("Cancelar", ShowList));
        buttons.Margin = new Thickness(0, 8, 0, 0);

        var heading = Ui.Text(existing == null ? "Novo snippet" : "Editar snippet", "Title");
        Layout(heading, buttons, new ScrollViewer { Content = form });
        title.Focus();
    }
}
