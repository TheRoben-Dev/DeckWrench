using DeckWrench.App.Ui;
using Godot;

namespace DeckWrench.App.Desktop;

/// <summary>
/// Desktop mode's look: the same palette and font as the chosen theme, at desktop density
/// (14 px text, compact rows), with styles for the controls a desktop utility uses: menu bar,
/// tables, lists, fields, dialogs, splitters and tooltips. Focus rings are per control here.
/// </summary>
public static class DesktopTheme
{
    public const int Size = 14;
    public const int Small = 12;

    public static Theme Build()
    {
        var t = new Theme { DefaultFontSize = Size };
        if (UiTheme.Spec.FontKind is "mono" or "pixel" or "chakra")
        {
            t.DefaultFont = UiTheme.CurrentFont;
        }
        var text = UiTheme.Text;
        var dim = UiTheme.Dim;
        var accent = UiTheme.Accent;
        var sel = accent with { A = UiTheme.Spec.Light ? 0.25f : 0.28f };

        StyleBoxFlat Flat(Color bg, int border = 0, Color? borderColor = null, int radius = 4, int padX = 8, int padY = 4)
        {
            var b = new StyleBoxFlat { BgColor = bg, BorderColor = borderColor ?? UiTheme.Line };
            b.SetBorderWidthAll(border);
            b.SetCornerRadiusAll(Math.Min(radius, UiTheme.Spec.Radius));
            b.ContentMarginLeft = b.ContentMarginRight = padX;
            b.ContentMarginTop = b.ContentMarginBottom = padY;
            return b;
        }

        var focusRing = Flat(new Color(0, 0, 0, 0), 2, accent, 4, 0, 0);
        focusRing.DrawCenter = false;

        foreach (var type in new[] { "Button", "OptionButton", "MenuButton", "CheckBox", "CheckButton" })
        {
            var flatType = type is "CheckBox" or "CheckButton";
            t.SetStylebox("normal", type, flatType ? Flat(new Color(0, 0, 0, 0)) : Flat(UiTheme.PanelHi, 1));
            t.SetStylebox("hover", type, flatType ? Flat(UiTheme.PanelHi) : Flat(UiTheme.PanelHi.Lightened(0.06f), 1, accent.Darkened(0.2f)));
            t.SetStylebox("pressed", type, Flat(accent.Darkened(0.35f), 1, accent));
            t.SetStylebox("disabled", type, flatType ? Flat(new Color(0, 0, 0, 0)) : Flat(UiTheme.Panel, 1));
            t.SetStylebox("focus", type, focusRing);
            t.SetColor("font_color", type, text);
            t.SetColor("font_hover_color", type, text);
            t.SetColor("font_focus_color", type, text);
            t.SetColor("font_pressed_color", type, UiTheme.Bright);
            t.SetColor("font_disabled_color", type, dim.Darkened(0.2f));
        }

        t.SetColor("font_color", "Label", text);
        t.SetStylebox("panel", "PanelContainer", Flat(UiTheme.Panel, 0, null, 0, 0, 0));
        t.SetStylebox("panel", "Panel", Flat(UiTheme.Panel, 0, null, 0, 0, 0));

        // Menu bar and menus.
        t.SetStylebox("normal", "MenuBar", Flat(new Color(0, 0, 0, 0), 0, null, 3, 10, 4));
        t.SetStylebox("hover", "MenuBar", Flat(UiTheme.PanelHi, 0, null, 3, 10, 4));
        t.SetStylebox("pressed", "MenuBar", Flat(sel, 0, null, 3, 10, 4));
        t.SetStylebox("focus", "MenuBar", new StyleBoxEmpty());
        t.SetColor("font_color", "MenuBar", text);
        t.SetColor("font_hover_color", "MenuBar", text);
        t.SetColor("font_pressed_color", "MenuBar", UiTheme.Bright);
        t.SetStylebox("panel", "PopupMenu", Flat(UiTheme.Panel, 1, UiTheme.Line, 4, 4, 4));
        t.SetStylebox("hover", "PopupMenu", Flat(sel, 0, null, 3));
        t.SetColor("font_color", "PopupMenu", text);
        t.SetColor("font_hover_color", "PopupMenu", UiTheme.Bright);
        t.SetColor("font_disabled_color", "PopupMenu", dim.Darkened(0.2f));
        t.SetColor("font_accelerator_color", "PopupMenu", dim);
        t.SetColor("font_separator_color", "PopupMenu", dim);
        t.SetConstant("v_separation", "PopupMenu", 6);

        // Tables and lists.
        foreach (var type in new[] { "Tree", "ItemList" })
        {
            t.SetStylebox("panel", type, Flat(UiTheme.Panel, 1, UiTheme.Line, 4, 2, 2));
            t.SetStylebox("focus", type, focusRing);
            t.SetStylebox("selected", type, Flat(sel, 0, null, 2));
            t.SetStylebox("selected_focus", type, Flat(sel, 0, null, 2));
            t.SetStylebox("cursor", type, new StyleBoxEmpty());
            t.SetStylebox("cursor_unfocused", type, new StyleBoxEmpty());
            t.SetStylebox("hovered", type, Flat(UiTheme.PanelHi, 0, null, 2));
            t.SetColor("font_color", type, text);
            t.SetColor("font_selected_color", type, UiTheme.Bright);
            t.SetColor("font_hovered_color", type, text);
            t.SetColor("guide_color", type, UiTheme.Line);
        }
        t.SetStylebox("title_button_normal", "Tree", Flat(UiTheme.PanelHi, 0, null, 0, 8, 5));
        t.SetStylebox("title_button_hover", "Tree", Flat(UiTheme.PanelHi.Lightened(0.06f), 0, null, 0, 8, 5));
        t.SetStylebox("title_button_pressed", "Tree", Flat(sel, 0, null, 0, 8, 5));
        t.SetColor("title_button_color", "Tree", dim);
        t.SetConstant("v_separation", "Tree", 6);
        t.SetConstant("h_separation", "Tree", 8);
        t.SetConstant("item_margin", "Tree", 8);
        t.SetConstant("draw_guides", "Tree", 0);
        t.SetConstant("v_separation", "ItemList", 6);

        // Fields.
        foreach (var type in new[] { "LineEdit", "TextEdit" })
        {
            t.SetStylebox("normal", type, Flat(UiTheme.Bg, 1, UiTheme.Line, 4, 8, 6));
            t.SetStylebox("focus", type, Flat(UiTheme.Bg, 1, accent, 4, 8, 6));
            t.SetStylebox("read_only", type, Flat(UiTheme.Bg, 1, UiTheme.Line, 4, 8, 6));
            t.SetColor("font_color", type, text);
            t.SetColor("font_placeholder_color", type, dim);
            t.SetColor("caret_color", type, accent);
            t.SetColor("selection_color", type, sel);
        }
        t.SetStylebox("normal", "RichTextLabel", new StyleBoxEmpty());
        t.SetColor("default_color", "RichTextLabel", text);

        // Progress, splitters, tooltips.
        t.SetStylebox("background", "ProgressBar", Flat(UiTheme.Bg, 1, UiTheme.Line, 3, 0, 0));
        t.SetStylebox("fill", "ProgressBar", Flat(accent, 0, null, 3, 0, 0));
        t.SetColor("font_color", "ProgressBar", text);
        foreach (var type in new[] { "HSplitContainer", "VSplitContainer" })
        {
            t.SetConstant("separation", type, 6);
        }
        t.SetStylebox("panel", "TooltipPanel", Flat(UiTheme.PanelHi, 1, UiTheme.Line, 4, 8, 5));
        t.SetColor("font_color", "TooltipLabel", text);

        // Dialog windows (embedded in the main window).
        var frame = Flat(UiTheme.Panel, 1, UiTheme.Line, 6, 10, 8);
        frame.ExpandMarginTop = 30;
        frame.ExpandMarginLeft = frame.ExpandMarginRight = frame.ExpandMarginBottom = 1;
        frame.ShadowColor = new Color(0, 0, 0, 0.5f);
        frame.ShadowSize = 16;
        t.SetStylebox("embedded_border", "Window", frame);
        t.SetStylebox("embedded_unfocused_border", "Window", frame);
        t.SetColor("title_color", "Window", text);
        t.SetConstant("title_height", "Window", 30);
        t.SetStylebox("panel", "AcceptDialog", Flat(UiTheme.Panel, 0, null, 0, 12, 10));

        foreach (var bar in new[] { "VScrollBar", "HScrollBar" })
        {
            t.SetStylebox("scroll", bar, Flat(new Color(0, 0, 0, 0.12f), 0, null, 3, 4, 4));
            t.SetStylebox("grabber", bar, Flat(dim.Darkened(0.4f), 0, null, 3, 4, 4));
            t.SetStylebox("grabber_highlight", bar, Flat(dim, 0, null, 3, 4, 4));
            t.SetStylebox("grabber_pressed", bar, Flat(accent, 0, null, 3, 4, 4));
        }
        return t;
    }
}
