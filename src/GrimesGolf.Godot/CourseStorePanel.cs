using Godot;
using GrimesGolf.Core.Course;

namespace GrimesGolf.Game;

/// <summary>How a course in the store stands on this PC.</summary>
/// <param name="Removable">It was downloaded (one that came with the app stays).</param>
public sealed record StoreRow(CatalogCourse Course, int? InstalledVersion, bool Removable);

/// <summary>Get More Courses, from New Game: the courses that can be downloaded, one at a time.</summary>
public partial class CourseStorePanel : Control
{
    public event Action<CatalogCourse>? DownloadPressed;
    public event Action<CatalogCourse>? RemovePressed;
    public event Action? CancelDownload;
    public event Action? Closed;
    public event Action? RetryPressed;

    private Label _status = null!;
    private VBoxContainer _rows = null!;
    private ProgressBar _progress = null!;
    private Button _close = null!, _retry = null!, _cancel = null!;
    private readonly List<Button> _rowButtons = new();
    private bool _busy;

    public bool IsOpen => Visible;
    public bool Busy => _busy;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 1), 16, 1, 24));
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);

        var v = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) };
        v.AddThemeConstantOverride("separation", 12);
        panel.AddChild(v);
        v.AddChild(UiTheme.Text("Get More Courses", 24, UiTheme.Ink, bold: true));
        _status = UiTheme.Text("", 15, UiTheme.Muted);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_status);
        _progress = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = true, Visible = false };
        v.AddChild(_progress);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, CustomMinimumSize = new Vector2(0, 380) };
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_rows);
        v.AddChild(scroll);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _retry = new Button { Text = "Try Again", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(150, 48), Visible = false };
        _retry.Pressed += () => RetryPressed?.Invoke();
        buttons.AddChild(_retry);
        _cancel = new Button { Text = "Cancel Download", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 48), Visible = false };
        _cancel.Pressed += () => CancelDownload?.Invoke();
        buttons.AddChild(_cancel);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _close = new Button { Text = "Done", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(120, 48), TooltipText = "Back to New Game  [Esc, gamepad B]" };
        _close.Pressed += Close;
        buttons.AddChild(_close);
        v.AddChild(buttons);
    }

    /// <summary>Opens it, waiting for the list (<see cref="ShowRows"/> or <see cref="ShowProblem"/>).</summary>
    public void Open()
    {
        Visible = true;
        _busy = false;
        _progress.Visible = false;
        _cancel.Visible = false;
        _retry.Visible = false;
        _close.Disabled = false;
        ClearRows();
        _status.Text = "Getting the list of courses…";
        _close.GrabFocus();
    }

    /// <summary>Closes it, unless a download is running (cancel that first: it's only one press).</summary>
    public void Close()
    {
        if (_busy) return;
        Visible = false;
        Closed?.Invoke();
    }

    public void ShowProblem(string text)
    {
        _status.Text = text;
        _retry.Visible = true;
        _retry.GrabFocus();
    }

    public void ShowRows(IReadOnlyList<StoreRow> rows, string? message = null)
    {
        _busy = false;
        _progress.Visible = false;
        _cancel.Visible = false;
        _retry.Visible = false;
        _close.Disabled = false;
        _status.Text = message ?? (rows.Count == 0 ? "There are no courses to download yet." : "Downloaded courses appear on the New Game list.");
        ClearRows();
        Button? first = null;
        foreach (var row in rows)
        {
            var button = AddRow(row);
            first ??= button;
        }
        (first ?? _close).GrabFocus();
    }

    /// <summary>A download is running: everything but cancelling it waits.</summary>
    public void ShowDownloading(string title)
    {
        _busy = true;
        _status.Text = $"Downloading {title}…";
        _progress.Value = 0;
        _progress.Visible = true;
        _cancel.Visible = true;
        _close.Disabled = true;
        _retry.Visible = false;
        foreach (var b in _rowButtons) b.Disabled = true;
        _cancel.GrabFocus();
    }

    public void SetProgress(double fraction)
    {
        _progress.Value = fraction;
        // Past the download it's unpacked and checked, which takes a few seconds with nothing to show for it.
        if (fraction >= 1) _status.Text = "Checking the course…";
    }

    private Button AddRow(StoreRow row)
    {
        var c = row.Course;
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 10, 1, 12));
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 2);
        text.AddChild(UiTheme.Text(c.Title, 19, UiTheme.Ink, bold: true));
        text.AddChild(UiTheme.Text($"{c.Holes} holes  ·  Par {c.Par}  ·  {c.Yards:#,0} yd  ·  {c.Bytes / 1e6:0.#} MB", 14, UiTheme.Accent));
        if (c.Description.Length > 0)
        {
            var about = UiTheme.Text(c.Description, 14, UiTheme.Muted);
            about.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.AddChild(about);
        }
        h.AddChild(text);

        bool installed = row.InstalledVersion >= c.Version;
        var main = new Button
        {
            Text = installed ? "Installed" : row.InstalledVersion != null ? "Update" : "Download",
            ThemeTypeVariation = installed ? "FlatButton" : "PrimaryButton",
            Disabled = installed,
            CustomMinimumSize = new Vector2(130, 44),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        main.Pressed += () => DownloadPressed?.Invoke(c);
        h.AddChild(main);
        _rowButtons.Add(main);
        if (row.Removable)
        {
            var remove = new Button { Text = "Remove", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 44), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            remove.Pressed += () => RemovePressed?.Invoke(c);
            h.AddChild(remove);
            _rowButtons.Add(remove);
        }
        box.AddChild(h);
        _rows.AddChild(box);
        return main;
    }

    private void ClearRows()
    {
        foreach (var c in _rows.GetChildren()) { _rows.RemoveChild(c); c.QueueFree(); }
        _rowButtons.Clear();
    }
}
