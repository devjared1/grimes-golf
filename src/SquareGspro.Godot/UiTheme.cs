using Godot;

namespace SquareGspro.Range3D;

/// <summary>
/// The app's look: a dark, low-contrast chrome with one green accent, system UI font (Segoe UI on Windows), rounded
/// cards, and filled primary actions. Built in code so every control shares one <see cref="Theme"/>.
/// </summary>
public static class UiTheme
{
    public static readonly Color Bar = new(0.075f, 0.086f, 0.106f, 0.93f);
    public static readonly Color Card = new(0.11f, 0.125f, 0.15f, 0.94f);
    public static readonly Color Control = new(0.17f, 0.19f, 0.225f);
    public static readonly Color ControlHover = new(0.22f, 0.245f, 0.285f);
    public static readonly Color ControlPressed = new(0.13f, 0.15f, 0.18f);
    public static readonly Color Border = new(1, 1, 1, 0.07f);
    public static readonly Color Ink = new(0.93f, 0.95f, 0.96f);
    public static readonly Color Muted = new(0.57f, 0.62f, 0.68f);
    public static readonly Color Accent = new(0.22f, 0.78f, 0.47f);
    public static readonly Color AccentHover = new(0.29f, 0.85f, 0.54f);
    public static readonly Color AccentPressed = new(0.16f, 0.64f, 0.38f);
    public static readonly Color Warn = new(0.96f, 0.72f, 0.26f);
    public static readonly Color Danger = new(0.92f, 0.38f, 0.36f);

    /// <summary>Each player's colour (name, ball, scorecard row): gold, sky blue, pink, orange.</summary>
    private static readonly Color[] Players =
    {
        new(1.0f, 0.82f, 0.25f), new(0.45f, 0.78f, 1.0f), new(1.0f, 0.52f, 0.78f), new(1.0f, 0.6f, 0.25f),
    };

    public static Color PlayerColor(int player) => Players[((player % Players.Length) + Players.Length) % Players.Length];

    private static readonly string[] FontNames = { "Segoe UI", "Inter", "Roboto", "Helvetica Neue", "Arial", "Sans-Serif" };
    public static readonly Font Regular = new SystemFont { FontNames = FontNames };
    public static readonly Font Bold = new SystemFont { FontNames = FontNames, FontWeight = 700 };

    public static StyleBoxFlat Box(Color bg, int radius, int border, float margin, float? marginH = null)
    {
        var sb = new StyleBoxFlat { BgColor = bg, BorderColor = Border, AntiAliasing = true };
        sb.SetCornerRadiusAll(radius);
        sb.SetBorderWidthAll(border);
        sb.SetContentMarginAll(margin);
        if (marginH is { } h) { sb.ContentMarginLeft = h; sb.ContentMarginRight = h; }
        return sb;
    }

    public static Label Text(string text, int size, Color color, bool bold = false)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        if (bold) l.AddThemeFontOverride("font", Bold);
        return l;
    }

    /// <summary>Small uppercase label above a value.</summary>
    public static Label Caption(string text)
    {
        var l = Text(text.ToUpperInvariant(), 11, Muted, bold: true);
        return l;
    }

    public static MarginContainer Pad(Control c, int horizontal, int vertical, bool mouse = true)
    {
        var m = new MarginContainer { MouseFilter = mouse ? Godot.Control.MouseFilterEnum.Pass : Godot.Control.MouseFilterEnum.Ignore };
        m.AddThemeConstantOverride("margin_left", horizontal);
        m.AddThemeConstantOverride("margin_right", horizontal);
        m.AddThemeConstantOverride("margin_top", vertical);
        m.AddThemeConstantOverride("margin_bottom", vertical);
        m.AddChild(c);
        return m;
    }

    public static Theme Build()
    {
        var t = new Theme { DefaultFont = Regular, DefaultFontSize = 14 };
        t.SetColor("font_color", "Label", Ink);

        void ButtonStyles(string type, Color normal, Color hover, Color pressed, Color font, int radius = 8, float padH = 14)
        {
            t.SetStylebox("normal", type, Box(normal, radius, 0, 8, padH));
            t.SetStylebox("hover", type, Box(hover, radius, 0, 8, padH));
            t.SetStylebox("pressed", type, Box(pressed, radius, 0, 8, padH));
            t.SetStylebox("hover_pressed", type, Box(pressed, radius, 0, 8, padH));
            t.SetStylebox("disabled", type, Box(new Color(normal, normal.A * 0.45f), radius, 0, 8, padH));
            var focus = Box(new Color(0, 0, 0, 0), radius, 2, 8, padH);
            focus.BorderColor = new Color(Accent, 0.6f);
            t.SetStylebox("focus", type, focus);
            foreach (var name in new[] { "font_color", "font_hover_color", "font_focus_color", "font_hover_pressed_color" }) t.SetColor(name, type, font);
            t.SetColor("font_pressed_color", type, font);
            t.SetColor("font_disabled_color", type, new Color(font, 0.4f));
        }

        ButtonStyles("Button", Control, ControlHover, ControlPressed, Ink);
        ButtonStyles("OptionButton", Control, ControlHover, ControlPressed, Ink);
        ButtonStyles("CheckBox", new Color(0, 0, 0, 0), new Color(1, 1, 1, 0.04f), new Color(0, 0, 0, 0), Ink, padH: 4);

        t.SetTypeVariation("PrimaryButton", "Button");
        ButtonStyles("PrimaryButton", Accent, AccentHover, AccentPressed, new Color(0.03f, 0.08f, 0.05f), radius: 10, padH: 18);
        t.SetFont("font", "PrimaryButton", Bold);
        t.SetFontSize("font_size", "PrimaryButton", 16);

        t.SetTypeVariation("FlatButton", "Button");
        ButtonStyles("FlatButton", new Color(1, 1, 1, 0.0f), new Color(1, 1, 1, 0.07f), new Color(1, 1, 1, 0.03f), Muted, padH: 10);
        t.SetColor("font_hover_color", "FlatButton", Ink);

        // Mode tabs: quiet until selected, then filled with the accent.
        t.SetTypeVariation("TabButton", "Button");
        ButtonStyles("TabButton", new Color(1, 1, 1, 0.0f), new Color(1, 1, 1, 0.07f), new Color(Accent, 0.22f), Muted, radius: 8, padH: 16);
        t.SetStylebox("hover_pressed", "TabButton", Box(new Color(Accent, 0.28f), 8, 0, 8, 16));
        t.SetColor("font_pressed_color", "TabButton", Accent);
        t.SetColor("font_hover_pressed_color", "TabButton", AccentHover);
        t.SetFont("font", "TabButton", Bold);

        var popup = Box(new Color(0.12f, 0.135f, 0.16f), 8, 1, 6);
        t.SetStylebox("panel", "PopupMenu", popup);
        t.SetStylebox("hover", "PopupMenu", Box(new Color(Accent, 0.2f), 6, 0, 4));
        t.SetColor("font_color", "PopupMenu", Ink);
        t.SetColor("font_hover_color", "PopupMenu", Ink);
        t.SetConstant("v_separation", "PopupMenu", 8);
        t.SetStylebox("panel", "TooltipPanel", Box(new Color(0.06f, 0.07f, 0.085f, 0.97f), 6, 1, 8));
        t.SetColor("font_color", "TooltipLabel", Ink);

        var list = Box(new Color(0, 0, 0, 0.25f), 8, 1, 6);
        t.SetStylebox("panel", "ItemList", list);
        t.SetStylebox("focus", "ItemList", new StyleBoxEmpty());
        t.SetStylebox("selected", "ItemList", Box(new Color(Accent, 0.22f), 6, 0, 4));
        t.SetStylebox("selected_focus", "ItemList", Box(new Color(Accent, 0.3f), 6, 0, 4));
        t.SetColor("font_color", "ItemList", Ink);
        t.SetColor("font_selected_color", "ItemList", Ink);

        t.SetStylebox("panel", "Tree", Box(new Color(0, 0, 0, 0), 0, 0, 0));
        t.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        t.SetStylebox("selected", "Tree", Box(new Color(Accent, 0.18f), 4, 0, 2));
        t.SetStylebox("selected_focus", "Tree", Box(new Color(Accent, 0.25f), 4, 0, 2));
        t.SetStylebox("title_button_normal", "Tree", Box(new Color(0, 0, 0, 0), 0, 0, 4));
        t.SetStylebox("title_button_hover", "Tree", Box(new Color(1, 1, 1, 0.04f), 0, 0, 4));
        t.SetStylebox("title_button_pressed", "Tree", Box(new Color(1, 1, 1, 0.04f), 0, 0, 4));
        t.SetColor("title_button_color", "Tree", Muted);
        t.SetFont("title_button_font", "Tree", Bold);
        t.SetFontSize("title_button_font_size", "Tree", 11);
        t.SetColor("font_color", "Tree", Ink);
        t.SetColor("font_selected_color", "Tree", Ink);
        t.SetColor("guide_color", "Tree", new Color(1, 1, 1, 0.04f));
        t.SetConstant("draw_guides", "Tree", 1);
        t.SetFontSize("font_size", "Tree", 13);

        t.SetStylebox("background", "ProgressBar", Box(new Color(0, 0, 0, 0.35f), 4, 0, 0));
        t.SetStylebox("fill", "ProgressBar", Box(Accent, 4, 0, 0));

        var sep = new StyleBoxLine { Color = Border, Thickness = 1 };
        t.SetStylebox("separator", "HSeparator", sep);
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Border, Thickness = 1, Vertical = true });
        t.SetConstant("separation", "HSeparator", 10);
        t.SetConstant("separation", "VSeparator", 2);

        t.SetColor("default_color", "RichTextLabel", Muted);

        var field = Box(new Color(0, 0, 0, 0.3f), 6, 1, 5, 8);
        var fieldFocus = Box(new Color(0, 0, 0, 0.3f), 6, 1, 5, 8);
        fieldFocus.BorderColor = new Color(Accent, 0.7f);
        t.SetStylebox("normal", "LineEdit", field);
        t.SetStylebox("focus", "LineEdit", fieldFocus);
        t.SetStylebox("read_only", "LineEdit", field);
        t.SetColor("font_color", "LineEdit", Ink);
        t.SetColor("caret_color", "LineEdit", Accent);
        t.SetColor("selection_color", "LineEdit", new Color(Accent, 0.35f));
        t.SetColor("font_placeholder_color", "LineEdit", new Color(Muted, 0.7f));

        StyleBoxFlat Track(Color c)
        {
            var sb = Box(c, 3, 0, 0);
            sb.ContentMarginTop = sb.ContentMarginBottom = 2;
            return sb;
        }
        t.SetStylebox("slider", "HSlider", Track(new Color(1, 1, 1, 0.12f)));
        t.SetStylebox("grabber_area", "HSlider", Track(new Color(Accent, 0.7f)));
        t.SetStylebox("grabber_area_highlight", "HSlider", Track(Accent));
        t.SetIcon("grabber", "HSlider", Dot(14, Ink));
        t.SetIcon("grabber_highlight", "HSlider", Dot(14, Colors.White));

        // Check boxes drawn to show up on the dark chrome (the default box is nearly invisible on it).
        t.SetIcon("unchecked", "CheckBox", CheckIcon(false));
        t.SetIcon("checked", "CheckBox", CheckIcon(true));
        return t;
    }

    /// <summary>An anti-aliased filled circle.</summary>
    private static Texture2D Dot(int size, Color color)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float r = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x + 0.5f - r, y + 0.5f - r).Length();
                img.SetPixel(x, y, new Color(color, Mathf.Clamp(r - 0.5f - d + 0.5f, 0, 1)));
            }
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>A 16 px rounded box: outlined when unchecked, filled with the accent and a tick when checked.</summary>
    private static Texture2D CheckIcon(bool on)
    {
        const int size = 16;
        const float half = 7.5f, radius = 3.5f;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var tick = new[] { new Vector2(4.2f, 8.2f), new Vector2(6.8f, 10.8f), new Vector2(11.8f, 5.2f) };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                // Signed distance to a rounded square centred in the icon.
                var q = new Vector2(Mathf.Abs(p.X - 8), Mathf.Abs(p.Y - 8)) - new Vector2(half - radius, half - radius);
                float box = new Vector2(Mathf.Max(q.X, 0), Mathf.Max(q.Y, 0)).Length() + Mathf.Min(Mathf.Max(q.X, q.Y), 0) - radius;
                float inside = Mathf.Clamp(0.5f - box, 0, 1);
                Color c;
                if (on)
                {
                    float d = Mathf.Min(SegmentDistance(p, tick[0], tick[1]), SegmentDistance(p, tick[1], tick[2]));
                    float mark = Mathf.Clamp(1.4f - d, 0, 1);
                    c = new Color(Accent.Lerp(new Color(0.03f, 0.08f, 0.05f), mark), inside);
                }
                else
                {
                    float ring = Mathf.Clamp(0.5f - Mathf.Abs(box + 0.75f) + 0.75f, 0, 1);
                    c = new Color(Muted, Mathf.Min(inside, ring) + inside * 0.12f);
                }
                img.SetPixel(x, y, c);
            }
        return ImageTexture.CreateFromImage(img);
    }

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
        return p.DistanceTo(a + ab * t);
    }
}
