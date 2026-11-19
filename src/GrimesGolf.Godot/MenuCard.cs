using Godot;

namespace GrimesGolf.Game;

/// <summary>The pictures drawn by <see cref="Glyphs"/>: the main menu's buttons (and the Practice ones) and the Quit / Back icons.</summary>
public enum Glyph { Resume, NewRound, Practice, Online, Games, History, Settings, Range, Putting, Chipping, ClosestToPin, LongDrive, PuttPutt, Quit, Back }

/// <summary>
/// Icons for the menu, drawn in code so they stay sharp at any size and need no image files. Each is drawn inside a
/// square of half-size <c>s</c> around <c>c</c>.
/// </summary>
public static class Glyphs
{
    public static void Draw(CanvasItem ci, Glyph glyph, Vector2 c, float s, Color line, float width)
    {
        var solid = new Color(line, line.A * 0.92f);
        var faint = new Color(line, line.A * 0.3f);
        Vector2 P(float x, float y) => c + new Vector2(x, y) * s;
        switch (glyph)
        {
            case Glyph.Resume:
            {
                // A circular arrow round a play button, over a rise of ground with a flag.
                var cc = P(-0.12f, -0.2f);
                float r = s * 0.74f;
                Arc(ci, cc, r, 125, 335, line, width);
                float end = Mathf.DegToRad(335);
                Arrowhead(ci, cc + new Vector2(Mathf.Cos(end), Mathf.Sin(end)) * r, new Vector2(-Mathf.Sin(end), Mathf.Cos(end)), s * 0.3f, line);
                ci.DrawColoredPolygon(new[] { cc + new Vector2(-0.22f, -0.34f) * s, cc + new Vector2(-0.22f, 0.34f) * s, cc + new Vector2(0.4f, 0) * s }, solid);
                HillAndFlag(ci, c, s, line, width);
                break;
            }
            case Glyph.NewRound:
            {
                // An open circle with a plus in it, over a rise of ground with a flag.
                var cc = P(-0.12f, -0.2f);
                Arc(ci, cc, s * 0.74f, 125, 335, line, width);
                ci.DrawLine(cc + new Vector2(-0.3f, 0) * s, cc + new Vector2(0.3f, 0) * s, solid, width * 1.5f, true);
                ci.DrawLine(cc + new Vector2(0, -0.3f) * s, cc + new Vector2(0, 0.3f) * s, solid, width * 1.5f, true);
                HillAndFlag(ci, c, s, line, width);
                break;
            }
            case Glyph.Practice:
            {
                // A golfer at the top of the swing, with a tee and ball.
                ci.DrawCircle(P(0.12f, -0.74f), s * 0.22f, solid);
                ci.DrawLine(P(0.05f, -0.42f), P(-0.1f, 0.12f), solid, width * 2.2f, true);
                ci.DrawLine(P(-0.1f, 0.12f), P(-0.4f, 0.86f), solid, width * 1.6f, true);
                ci.DrawLine(P(-0.1f, 0.12f), P(0.14f, 0.86f), solid, width * 1.6f, true);
                ci.DrawLine(P(0.05f, -0.4f), P(0.5f, -0.38f), solid, width * 1.4f, true);
                ci.DrawLine(P(0.5f, -0.38f), P(-0.5f, -0.95f), line, width * 0.8f, true);
                Arc(ci, P(0.0f, -0.1f), s * 0.9f, 150, 235, line, width);
                ci.DrawCircle(P(0.78f, 0.7f), s * 0.11f, solid);
                ci.DrawLine(P(0.78f, 0.8f), P(0.78f, 0.9f), line, width, true);
                ci.DrawLine(P(-0.95f, 0.92f), P(0.95f, 0.92f), line, width * 0.8f, true);
                break;
            }
            case Glyph.Online:
            {
                // A globe with a signal, over a rise of ground with a flag.
                var cc = P(-0.12f, -0.22f);
                float r = s * 0.68f;
                ci.DrawArc(cc, r, 0, Mathf.Tau, 64, line, width, true);
                ci.DrawLine(cc + new Vector2(-r, 0), cc + new Vector2(r, 0), line, width * 0.7f, true);
                ci.DrawLine(cc + new Vector2(0, -r), cc + new Vector2(0, r), line, width * 0.7f, true);
                foreach (var y in new[] { -0.5f, 0.5f })
                {
                    float half = r * Mathf.Sqrt(1 - y * y);
                    ci.DrawLine(cc + new Vector2(-half, y * r), cc + new Vector2(half, y * r), line, width * 0.7f, true);
                }
                ci.DrawPolyline(Ellipse(cc, r * 0.45f, r, 32), line, width * 0.7f, true);
                var wifi = P(0.42f, 0.5f);
                Arc(ci, wifi, s * 0.5f, 225, 315, line, width * 0.9f);
                Arc(ci, wifi, s * 0.28f, 225, 315, line, width * 0.9f);
                ci.DrawCircle(wifi, width * 0.7f, solid);
                Hill(ci, P(-0.95f, 0.88f), P(0.1f, 0.78f), P(0.95f, 0.9f), line, width);
                Flag(ci, P(0.72f, 0.86f), s * 0.5f, line, solid, width * 0.7f);
                break;
            }
            case Glyph.History:
            {
                // A clock with an arrow winding back.
                float r = s * 0.9f;
                Arc(ci, c, r, 215, 545, line, width);
                float start = Mathf.DegToRad(215);
                Arrowhead(ci, c + new Vector2(Mathf.Cos(start), Mathf.Sin(start)) * r, new Vector2(Mathf.Sin(start), -Mathf.Cos(start)), s * 0.32f, line);
                ci.DrawPolyline(new[] { c + new Vector2(0.05f, -0.5f) * s, c + new Vector2(0.05f, 0.05f) * s, c + new Vector2(0.45f, 0.3f) * s }, line, width, true);
                break;
            }
            case Glyph.Settings:
            {
                // A gear.
                var pts = new List<Vector2>();
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.Tau / 8;
                    foreach (var (da, r) in new[] { (-0.30f, 0.68f), (-0.19f, 0.95f), (0.19f, 0.95f), (0.30f, 0.68f) })
                        pts.Add(c + new Vector2(Mathf.Cos(a + da), Mathf.Sin(a + da)) * s * r);
                }
                pts.Add(pts[0]);
                ci.DrawPolyline(pts.ToArray(), line, width, true);
                ci.DrawArc(c, s * 0.3f, 0, Mathf.Tau, 40, line, width, true);
                break;
            }
            case Glyph.Range:
            {
                // A ball on a tee, hit to a target with a flag.
                ci.DrawCircle(P(-0.62f, -0.12f), s * 0.3f, faint);
                ci.DrawArc(P(-0.62f, -0.12f), s * 0.3f, 0, Mathf.Tau, 24, line, width * 0.8f, true);
                ci.DrawLine(P(-0.62f, 0.2f), P(-0.62f, 0.8f), line, width, true);
                ci.DrawLine(P(-0.9f, 0.84f), P(-0.34f, 0.84f), line, width, true);
                Dashes(ci, P(-0.4f, -0.4f), P(0.35f, -0.3f), s * 0.6f, line, width * 0.8f, 10);
                Flag(ci, P(0.5f, 0.45f), s * 1.0f, line, solid, width * 0.8f);
                ci.DrawPolyline(Ellipse(P(0.5f, 0.55f), s * 0.55f, s * 0.17f, 28), line, width * 0.8f, true);
                ci.DrawPolyline(Ellipse(P(0.5f, 0.55f), s * 0.3f, s * 0.09f, 24), line, width * 0.8f, true);
                break;
            }
            case Glyph.Putting:
            {
                // A putter, a ball and a flag in the hole.
                ci.DrawLine(P(-0.85f, -0.9f), P(-0.58f, 0.42f), line, width, true);
                var head = new[] { P(-0.9f, 0.44f), P(-0.22f, 0.44f), P(-0.22f, 0.78f), P(-0.9f, 0.78f), P(-0.9f, 0.44f) };
                ci.DrawColoredPolygon(head, faint);
                ci.DrawPolyline(head, line, width, true);
                ci.DrawCircle(P(0.02f, 0.7f), s * 0.13f, new Color(1, 1, 1, line.A));
                Flag(ci, P(0.7f, 0.62f), s * 1.05f, line, solid, width * 0.8f);
                ci.DrawPolyline(Ellipse(P(0.68f, 0.72f), s * 0.34f, s * 0.11f, 24), line, width * 0.8f, true);
                break;
            }
            case Glyph.Chipping:
            {
                // An iron, a chip in flight and a flag in the hole.
                ci.DrawLine(P(-0.85f, -0.9f), P(-0.62f, 0.32f), line, width, true);
                ci.DrawColoredPolygon(new[] { P(-0.68f, 0.3f), P(-0.2f, 0.42f), P(-0.26f, 0.8f), P(-0.74f, 0.68f) }, solid);
                ci.DrawCircle(P(-0.02f, 0.72f), s * 0.13f, new Color(1, 1, 1, line.A));
                Dashes(ci, P(0.0f, 0.4f), P(0.4f, -0.3f), s * 0.5f, line, width * 0.8f, 8);
                Flag(ci, P(0.72f, 0.58f), s * 1.0f, line, solid, width * 0.8f);
                ci.DrawPolyline(Ellipse(P(0.7f, 0.68f), s * 0.3f, s * 0.1f, 24), line, width * 0.8f, true);
                break;
            }
            case Glyph.Games:
            {
                // A target with a flag in the bullseye.
                var cc = P(-0.1f, 0.05f);
                ci.DrawArc(cc, s * 0.85f, 0, Mathf.Tau, 48, line, width, true);
                ci.DrawArc(cc, s * 0.5f, 0, Mathf.Tau, 40, line, width * 0.9f, true);
                ci.DrawCircle(cc, s * 0.17f, solid);
                Flag(ci, P(0.5f, 0.0f), s * 0.95f, line, solid, width * 0.8f);
                ci.DrawCircle(P(0.5f, 0.0f), s * 0.07f, solid);
                break;
            }
            case Glyph.ClosestToPin:
            {
                // A ball on its way to a flag in the hole, with a measuring line.
                Hill(ci, P(-0.95f, 0.88f), P(0.0f, 0.8f), P(0.95f, 0.9f), line, width);
                Flag(ci, P(0.55f, 0.84f), s * 1.3f, line, solid, width * 0.8f);
                ci.DrawPolyline(Ellipse(P(0.55f, 0.86f), s * 0.3f, s * 0.09f, 24), line, width * 0.8f, true);
                Dashes(ci, P(-0.7f, 0.2f), P(-0.05f, -0.55f), s * 0.5f, line, width * 0.8f, 8);
                ci.DrawCircle(P(-0.02f, 0.6f), s * 0.13f, new Color(1, 1, 1, line.A));
                ci.DrawLine(P(-0.02f, 0.84f), P(0.4f, 0.84f), faint, width * 0.9f, true);
                break;
            }
            case Glyph.LongDrive:
            {
                // A ball flying off the tee a long way, with a distance arrow along the ground.
                ci.DrawLine(P(-0.95f, 0.8f), P(0.95f, 0.8f), line, width * 0.8f, true);
                ci.DrawCircle(P(-0.75f, 0.55f), s * 0.13f, new Color(1, 1, 1, line.A));
                ci.DrawLine(P(-0.75f, 0.68f), P(-0.75f, 0.8f), line, width, true);
                Dashes(ci, P(-0.65f, 0.4f), P(0.8f, 0.5f), s * 1.5f, line, width * 0.8f, 12);
                ci.DrawLine(P(-0.75f, 0.95f), P(0.9f, 0.95f), faint, width * 0.9f, true);
                break;
            }
            case Glyph.PuttPutt:
            {
                // A windmill over a putter and ball, as on a mini-golf course.
                var hub = P(0.0f, -0.35f);
                ci.DrawLine(hub, P(0.0f, 0.45f), line, width, true);
                foreach (float deg in new[] { 20f, 110f, 200f, 290f })
                {
                    var tip = hub + new Vector2(Mathf.Cos(Mathf.DegToRad(deg)), Mathf.Sin(Mathf.DegToRad(deg))) * s * 0.7f;
                    var side = new Vector2(-(tip - hub).Y, (tip - hub).X).Normalized() * s * 0.18f;
                    ci.DrawColoredPolygon(new[] { hub, tip, tip + side }, faint);
                    ci.DrawPolyline(new[] { hub, tip, tip + side, hub }, line, width * 0.8f, true);
                }
                ci.DrawCircle(hub, s * 0.1f, solid);
                ci.DrawLine(P(-0.95f, 0.88f), P(0.95f, 0.88f), line, width * 0.8f, true);
                ci.DrawCircle(P(-0.6f, 0.74f), s * 0.12f, new Color(1, 1, 1, line.A));
                break;
            }
            case Glyph.Quit:
            {
                // A power symbol.
                ci.DrawArc(c, s * 0.8f, Mathf.DegToRad(-55), Mathf.DegToRad(235), 48, line, width, true);
                ci.DrawLine(c + new Vector2(0, -s), c + new Vector2(0, -s * 0.1f), line, width, true);
                break;
            }
            case Glyph.Back:
                ci.DrawPolyline(new[] { c + new Vector2(0.35f, -0.85f) * s, c + new Vector2(-0.45f, 0) * s, c + new Vector2(0.35f, 0.85f) * s }, line, width, true);
                break;
        }
    }

    /// <summary>An arc (angles in degrees, clockwise from the right as on screen).</summary>
    private static void Arc(CanvasItem ci, Vector2 c, float r, float fromDeg, float toDeg, Color line, float width) =>
        ci.DrawArc(c, r, Mathf.DegToRad(fromDeg), Mathf.DegToRad(toDeg), 48, line, width, true);

    private static Vector2[] Ellipse(Vector2 c, float rx, float ry, int points = 40)
    {
        var p = new Vector2[points + 1];
        for (int i = 0; i <= points; i++)
        {
            float a = i * Mathf.Tau / points;
            p[i] = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }
        return p;
    }

    private static void Arrowhead(CanvasItem ci, Vector2 at, Vector2 along, float size, Color color)
    {
        var side = new Vector2(-along.Y, along.X);
        ci.DrawColoredPolygon(new[] { at + along * size, at - along * size * 0.3f + side * size * 0.8f, at - along * size * 0.3f - side * size * 0.8f }, color);
    }

    /// <summary>A curve of ground from <paramref name="a"/> to <paramref name="b"/>, bending towards <paramref name="through"/>.</summary>
    private static void Hill(CanvasItem ci, Vector2 a, Vector2 through, Vector2 b, Color line, float width)
    {
        const int n = 20;
        var p = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            p[i] = a * ((1 - t) * (1 - t)) + through * (2 * (1 - t) * t) + b * (t * t);
        }
        ci.DrawPolyline(p, line, width * 1.2f, true);
    }

    /// <summary>The rise of ground and the flag at the foot of Resume, New Round and Online's icons.</summary>
    private static void HillAndFlag(CanvasItem ci, Vector2 c, float s, Color line, float width)
    {
        Vector2 P(float x, float y) => c + new Vector2(x, y) * s;
        Hill(ci, P(-0.55f, 0.45f), P(-0.4f, 0.98f), P(0.95f, 0.8f), line, width);
        Flag(ci, P(0.62f, 0.8f), s * 0.8f, line, new Color(line, line.A * 0.92f), width * 0.7f);
    }

    /// <summary>A dashed arc from <paramref name="a"/> to <paramref name="b"/>, rising <paramref name="height"/> in the middle.</summary>
    private static void Dashes(CanvasItem ci, Vector2 a, Vector2 b, float height, Color line, float width, int dashes)
    {
        Vector2 At(float t) => a.Lerp(b, t) - new Vector2(0, 4 * height * t * (1 - t));
        for (int i = 0; i < dashes; i++)
            ci.DrawLine(At((i + 0.1f) / dashes), At((i + 0.65f) / dashes), line, width, true);
        var tip = At(1);
        var back = At(0.93f);
        Arrowhead(ci, tip, (tip - back).Normalized(), width * 1.8f, line);
    }

    private static void Flag(CanvasItem ci, Vector2 foot, float height, Color line, Color fill, float width)
    {
        var top = foot - new Vector2(0, height);
        ci.DrawLine(foot, top, line, width, true);
        var pennant = new[] { top, top + new Vector2(height * 0.5f, height * 0.15f), top + new Vector2(0, height * 0.3f) };
        ci.DrawColoredPolygon(pennant, fill);
    }
}

/// <summary>
/// A button on the main menu: a dark pill with a green border, a green icon at the left, a divider, and the label (with
/// a small note under it when there is one). Hover and focus brighten it; a disabled one is dimmed. Works with a mouse,
/// keyboard or gamepad (it's a button).
/// </summary>
public partial class MenuCard : Button
{
    public const int PillHeight = 70, PillWidth = 440;
    private const float DividerAt = 1.44f, TextAt = 1.71f;

    private readonly Glyph _glyph;
    private Label? _note;

    public MenuCard(string label, Glyph glyph, string tooltip)
    {
        _glyph = glyph;
        Text = label;
        TooltipText = tooltip;
        CustomMinimumSize = new Vector2(PillWidth, PillHeight);
        Alignment = HorizontalAlignment.Left;
        AddThemeFontSizeOverride("font_size", 25);
        AddThemeFontOverride("font", UiTheme.Bold);
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_hover_pressed_color" })
            AddThemeColorOverride(name, UiTheme.Ink);
        AddThemeColorOverride("font_disabled_color", new Color(UiTheme.Ink, 0.42f));
        SetNoteRoom(false);
        _art = new PillArt(this);
        AddChild(_art);
    }

    private readonly PillArt _art;

    public override void _Notification(int what)
    {
        // Hover, focus, press and disabled all redraw the button: the pill behind it follows.
        if (what == NotificationDraw) _art?.QueueRedraw();
    }

    /// <summary>The pill itself, drawn behind the button.</summary>
    private sealed partial class PillArt : Control
    {
        private readonly MenuCard _card;

        public PillArt(MenuCard card)
        {
            _card = card;
            ShowBehindParent = true;
            MouseFilter = MouseFilterEnum.Ignore;
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }

        public override void _Draw() => _card.Paint(this);

        public override void _Notification(int what)
        {
            if (what == NotificationResized) QueueRedraw();
        }
    }

    /// <summary>The pill is drawn by <see cref="_Draw"/>; the styleboxes are empty and only place the label (higher when there's a note under it).</summary>
    private void SetNoteRoom(bool note)
    {
        var room = new StyleBoxEmpty { ContentMarginLeft = PillHeight * TextAt, ContentMarginRight = 28, ContentMarginBottom = note ? 22 : 0 };
        foreach (var name in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" }) AddThemeStyleboxOverride(name, room);
    }

    /// <summary>A small line under the label (what Resume will resume, or why the button is off).</summary>
    public void SetNote(string? text)
    {
        bool has = !string.IsNullOrEmpty(text);
        SetNoteRoom(has);
        if (_note == null)
        {
            if (!has) return;
            _note = UiTheme.Text("", 13, UiTheme.Muted);
            _note.MouseFilter = MouseFilterEnum.Ignore;
            _note.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            _note.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
            _note.OffsetLeft = PillHeight * TextAt;
            _note.OffsetRight = -28;
            _note.OffsetBottom = -9;
            _note.OffsetTop = -29;
            _note.GrowVertical = GrowDirection.Begin;
            _note.VerticalAlignment = VerticalAlignment.Bottom;
            AddChild(_note);
        }
        _note.Text = text ?? "";
        _note.Visible = has;
    }

    /// <summary>Draws the pill on <paramref name="ci"/>, the control behind the button (so the label is drawn over it).</summary>
    private void Paint(Control ci)
    {
        var mode = GetDrawMode();
        bool off = Disabled;
        bool lit = !off && (mode is DrawMode.Hover or DrawMode.HoverPressed or DrawMode.Pressed || HasFocus());
        bool down = mode is DrawMode.Pressed or DrawMode.HoverPressed;
        float h = ci.Size.Y;
        var rect = new Rect2(Vector2.Zero, ci.Size);

        // The face: a dark gradient (a little green at the top left, as the mock-ups) in the app's own greys.
        var top = UiTheme.Control.Lerp(UiTheme.Accent, 0.07f);
        var bottom = UiTheme.Bar.Lerp(UiTheme.Accent, 0.025f);
        if (lit) { top = top.Lightened(0.14f); bottom = bottom.Lightened(0.10f); }
        if (down) { top = top.Darkened(0.2f); bottom = bottom.Darkened(0.2f); }
        float fade = off ? 0.55f : 1f;
        top = new Color(top, fade);
        bottom = new Color(bottom, fade);
        float border = lit ? 3f : 2.5f;

        var face = Pill(rect, border * 0.5f);
        var faceColors = new Color[face.Length];
        for (int i = 0; i < face.Length; i++) faceColors[i] = top.Lerp(bottom, Mathf.Clamp(face[i].Y / h, 0, 1));
        ci.DrawPolygon(face, faceColors);

        // A soft highlight just inside the border, strongest along the top.
        var inner = Pill(rect, border + 1.2f);
        var glow = new Color[inner.Length];
        for (int i = 0; i < inner.Length; i++) glow[i] = new Color(1, 1, 1, (off ? 0.03f : 0.09f) * (1 - Mathf.Clamp(inner[i].Y / h, 0, 1)));
        ci.DrawPolylineColors(Close(inner), Close(glow), 1.4f, true);

        // The border: bright green at the top, deeper at the bottom.
        var ring = Pill(rect, border * 0.5f);
        var ringColors = new Color[ring.Length];
        var ringTop = lit ? UiTheme.AccentHover.Lightened(0.15f) : UiTheme.AccentHover;
        var ringBottom = lit ? UiTheme.Accent : UiTheme.AccentPressed;
        for (int i = 0; i < ring.Length; i++)
        {
            var c = ringTop.Lerp(ringBottom, Mathf.Clamp(ring[i].Y / h, 0, 1));
            ringColors[i] = off ? new Color(UiTheme.Muted, 0.4f) : c;
        }
        ci.DrawPolylineColors(Close(ring), Close(ringColors), border, true);

        // The icon, the divider and (drawn by the button) the label.
        var iconColor = off ? new Color(UiTheme.Accent, 0.4f) : lit ? UiTheme.AccentHover : UiTheme.Accent;
        Glyphs.Draw(ci, _glyph, new Vector2(h * 0.84f, h / 2), h * 0.33f, iconColor, Mathf.Max(2.6f, h * 0.052f));
        ci.DrawLine(new Vector2(h * DividerAt, h * 0.26f), new Vector2(h * DividerAt, h * 0.74f), new Color(iconColor, off ? 0.25f : 0.85f), 2f, true);
    }

    /// <summary>The outline of a pill (fully rounded ends) shrunk by <paramref name="inset"/>, clockwise from the top left.</summary>
    private static Vector2[] Pill(Rect2 rect, float inset)
    {
        const int steps = 18;
        float r = rect.Size.Y / 2 - inset;
        float left = inset + r, right = rect.Size.X - inset - r, mid = rect.Size.Y / 2;
        var p = new List<Vector2>();
        for (int i = 0; i <= steps; i++)
        {
            float a = -Mathf.Pi / 2 + Mathf.Pi * i / steps;
            p.Add(new Vector2(right + Mathf.Cos(a) * r, mid + Mathf.Sin(a) * r));
        }
        for (int i = 0; i <= steps; i++)
        {
            float a = Mathf.Pi / 2 + Mathf.Pi * i / steps;
            p.Add(new Vector2(left + Mathf.Cos(a) * r, mid + Mathf.Sin(a) * r));
        }
        return p.ToArray();
    }

    private static T[] Close<T>(T[] points)
    {
        var closed = new T[points.Length + 1];
        points.CopyTo(closed, 0);
        closed[^1] = points[0];
        return closed;
    }
}

/// <summary>A see-through button with a drawn icon at its left and a label ("Quit", "Back").</summary>
public partial class GlyphButton : Button
{
    private readonly Glyph _glyph;

    public GlyphButton(string text, Glyph glyph, string tooltip)
    {
        _glyph = glyph;
        Text = text;
        TooltipText = tooltip;
        Alignment = HorizontalAlignment.Left;
        CustomMinimumSize = new Vector2(140, 52);
        AddThemeFontSizeOverride("font_size", 19);
        AddThemeFontOverride("font", UiTheme.Bold);
        // See-through until hovered (as the FlatButton look), with room at the left for the icon.
        StyleBoxFlat Look(Color bg)
        {
            var box = UiTheme.Box(bg, 8, 0, 8, 18);
            box.ContentMarginLeft = 50;
            return box;
        }
        var clear = new Color(1, 1, 1, 0);
        AddThemeStyleboxOverride("normal", Look(clear));
        AddThemeStyleboxOverride("hover", Look(new Color(1, 1, 1, 0.07f)));
        AddThemeStyleboxOverride("pressed", Look(new Color(1, 1, 1, 0.03f)));
        AddThemeStyleboxOverride("hover_pressed", Look(new Color(1, 1, 1, 0.03f)));
        AddThemeStyleboxOverride("disabled", Look(clear));
        var focus = Look(clear);
        focus.SetBorderWidthAll(2);
        focus.BorderColor = new Color(UiTheme.Accent, 0.6f);
        AddThemeStyleboxOverride("focus", focus);
        foreach (var name in new[] { "font_color", "font_pressed_color", "font_disabled_color" }) AddThemeColorOverride(name, UiTheme.Muted);
        foreach (var name in new[] { "font_hover_color", "font_focus_color", "font_hover_pressed_color" }) AddThemeColorOverride(name, UiTheme.Ink);
    }

    public override void _Draw()
    {
        var color = IsHovered() || HasFocus() ? UiTheme.Ink : UiTheme.Muted;
        Glyphs.Draw(this, _glyph, new Vector2(28, Size.Y / 2), 10, color, 2.5f);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseEnter || what == NotificationMouseExit || what == NotificationFocusEnter || what == NotificationFocusExit) QueueRedraw();
    }
}
