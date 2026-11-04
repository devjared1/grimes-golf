using Godot;
using GrimesGolf.Core.Course;
using GrimesGolf.Online.Protocol;

namespace GrimesGolf.Game;

/// <summary>
/// Play Online: host a lobby or join one with its 5-character code, then the lobby itself (the code to give friends,
/// who's in, whose launch monitor is live, the course and settings, Start for the host). See <c>Main.Online.cs</c>.
/// </summary>
public partial class OnlinePanel : Control
{
    public event Action<string>? HostPressed;
    public event Action<string, string>? JoinPressed;
    public event Action? RejoinPressed, ResumeHostPressed, StartPressed, LeavePressed, BackPressed;
    public event Action<int>? CourseChosen;
    public event Action<int, int>? SeatMoved;

    private VBoxContainer _home = null!, _lobby = null!, _seats = null!;
    private LineEdit _relay = null!, _code = null!;
    private Button _rejoin = null!, _resumeHost = null!, _start = null!;
    private Label _note = null!, _lobbyCode = null!, _lobbyStatus = null!, _setup = null!, _whyNot = null!, _courseLabel = null!;
    private OptionButton _course = null!;
    private bool _host;

    public bool IsOpen => Visible;
    public bool InLobby => Visible && _lobby.Visible;

    public override void _Ready()
    {
        Visible = false;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var bg = new ColorRect { Color = new Color(0.04f, 0.05f, 0.06f, 0.94f), MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var centre = new CenterContainer();
        centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        var card = new PanelContainer { CustomMinimumSize = new Vector2(760, 0) };
        card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Card, 14, 1, 28));
        centre.AddChild(card);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        card.AddChild(column);
        BuildHome(column);
        BuildLobby(column);
    }

    private void BuildHome(VBoxContainer column)
    {
        _home = new VBoxContainer();
        _home.AddThemeConstantOverride("separation", 10);
        column.AddChild(_home);
        _home.AddChild(UiTheme.Text("Play Online", 34, UiTheme.Ink, bold: true));
        var intro = UiTheme.Text("Play a round with friends at their own simulators. Everyone needs a Square launch monitor and this same version of the app.", 15, UiTheme.Muted);
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _home.AddChild(intro);

        _home.AddChild(UiTheme.Caption("Relay"));
        _relay = new LineEdit { PlaceholderText = "wss://your-relay.fly.dev/ws", CustomMinimumSize = new Vector2(0, 44) };
        _home.AddChild(_relay);

        var host = new Button { Text = "Host a Lobby", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(0, 54), TooltipText = "You pick the course and settings, and start once everyone's in" };
        host.AddThemeFontSizeOverride("font_size", 20);
        host.Pressed += () => HostPressed?.Invoke(_relay.Text.Trim());
        _home.AddChild(host);
        _resumeHost = new Button { Text = "Carry On Your Hosted Round", CustomMinimumSize = new Vector2(0, 48), Visible = false };
        _resumeHost.Pressed += () => ResumeHostPressed?.Invoke();
        _home.AddChild(_resumeHost);

        _home.AddChild(new HSeparator());
        _home.AddChild(UiTheme.Caption("Join with a code"));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _code = new LineEdit { PlaceholderText = "ABCDE", MaxLength = 5, CustomMinimumSize = new Vector2(200, 54), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _code.AddThemeFontSizeOverride("font_size", 28);
        _code.TextChanged += t => { int c = _code.CaretColumn; _code.Text = t.ToUpperInvariant(); _code.CaretColumn = c; };
        _code.TextSubmitted += _ => Join();
        row.AddChild(_code);
        var join = new Button { Text = "Join", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(160, 54) };
        join.AddThemeFontSizeOverride("font_size", 20);
        join.Pressed += Join;
        row.AddChild(join);
        _home.AddChild(row);
        _rejoin = new Button { Text = "Rejoin", CustomMinimumSize = new Vector2(0, 48), Visible = false };
        _rejoin.Pressed += () => RejoinPressed?.Invoke();
        _home.AddChild(_rejoin);

        _note = UiTheme.Text("", 15, UiTheme.Warn);
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _home.AddChild(_note);
        var back = new Button { Text = "Back", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 48), SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
        back.Pressed += () => BackPressed?.Invoke();
        _home.AddChild(back);
    }

    private void Join() => JoinPressed?.Invoke(_relay.Text.Trim(), _code.Text.Trim().ToUpperInvariant());

    private void BuildLobby(VBoxContainer column)
    {
        _lobby = new VBoxContainer { Visible = false };
        _lobby.AddThemeConstantOverride("separation", 10);
        column.AddChild(_lobby);
        _lobbyCode = UiTheme.Text("", 40, UiTheme.Accent, bold: true);
        _lobby.AddChild(_lobbyCode);
        _lobbyStatus = UiTheme.Text("", 15, UiTheme.Muted);
        _lobbyStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _lobby.AddChild(_lobbyStatus);

        _lobby.AddChild(UiTheme.Caption("Course"));
        _course = new OptionButton { CustomMinimumSize = new Vector2(0, 44) };
        foreach (var e in CourseLibrary.All) _course.AddItem(e.Name);
        _course.ItemSelected += i => CourseChosen?.Invoke((int)i);
        _lobby.AddChild(_course);
        _courseLabel = UiTheme.Text("", 20, UiTheme.Ink, bold: true);
        _lobby.AddChild(_courseLabel);
        _setup = UiTheme.Text("", 15, UiTheme.Muted);
        _setup.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _lobby.AddChild(_setup);

        _lobby.AddChild(UiTheme.Caption("Players (in teeing order; with teams, 1 & 2 play 3 & 4)"));
        _seats = new VBoxContainer();
        _seats.AddThemeConstantOverride("separation", 6);
        _lobby.AddChild(_seats);

        _whyNot = UiTheme.Text("", 15, UiTheme.Warn);
        _whyNot.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _lobby.AddChild(_whyNot);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        _start = new Button { Text = "Start the Round", ThemeTypeVariation = "PrimaryButton", CustomMinimumSize = new Vector2(240, 54) };
        _start.AddThemeFontSizeOverride("font_size", 20);
        _start.Pressed += () => StartPressed?.Invoke();
        buttons.AddChild(_start);
        buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var leave = new Button { Text = "Leave", ThemeTypeVariation = "FlatButton", CustomMinimumSize = new Vector2(0, 54) };
        leave.Pressed += () => LeavePressed?.Invoke();
        buttons.AddChild(leave);
        _lobby.AddChild(buttons);
    }

    /// <summary>The first page: host or join. <paramref name="rejoin"/> and <paramref name="resumeHost"/> describe a round to go back to (null = none).</summary>
    public void OpenHome(string relay, string? rejoin, string? resumeHost, string? note = null)
    {
        _relay.Text = relay;
        _rejoin.Visible = rejoin != null;
        _rejoin.Text = rejoin ?? "";
        _resumeHost.Visible = resumeHost != null;
        _resumeHost.Text = resumeHost ?? "";
        _note.Text = note ?? "";
        _home.Visible = true;
        _lobby.Visible = false;
        Visible = true;
        _code.GrabFocus();
    }

    public void SetNote(string text) => _note.Text = text;

    /// <summary>The lobby page while connecting (before the relay has given a code).</summary>
    public void OpenLobby(bool host, string status)
    {
        _host = host;
        _home.Visible = false;
        _lobby.Visible = true;
        Visible = true;
        _lobbyCode.Text = host ? "Opening a lobby…" : "Joining…";
        _lobbyStatus.Text = status;
        _course.Visible = host;
        _courseLabel.Visible = !host;
        _start.Visible = host;
        _whyNot.Text = "";
        foreach (var c in _seats.GetChildren()) c.QueueFree();
    }

    public void SetStatus(string status) => _lobbyStatus.Text = status;

    public void SetLobby(LobbyState s, int courseIndex)
    {
        _lobbyCode.Text = s.Code.Length > 0 ? $"Lobby  {s.Code}" : _lobbyCode.Text;
        _lobbyStatus.Text = _host ? "Give your friends this code. They pick Play Online, then Join." : "In the lobby. The host picks the course and starts the round.";
        if (courseIndex >= 0) _course.Select(courseIndex);
        _courseLabel.Text = s.Course ?? "Course not picked yet";
        _setup.Text = $"{s.Setup.Summary(s.Seats.Length)}  ·  {Rules(s.Rules)}";
        foreach (var c in _seats.GetChildren()) c.QueueFree();
        for (int i = 0; i < s.Seats.Length; i++)
        {
            var seat = s.Seats[i];
            var pc = s.Pcs.FirstOrDefault(p => p.Pc == seat.Pc);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.AddChild(new ColorRect { Color = UiTheme.PlayerColor(i), CustomMinimumSize = new Vector2(10, 32) });
            row.AddChild(UiTheme.Text($"{i + 1}. {seat.Name}{(seat.LeftHanded ? " (LH)" : "")}", 20, UiTheme.Ink, bold: true));
            row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            string where = pc == null ? "" : pc.Host ? "host" : pc.Connected ? "connected" : "not connected";
            string monitor = pc == null ? "" : pc.MonitorLive ? "monitor ready" : "no monitor";
            row.AddChild(UiTheme.Text($"{where}  ·  {monitor}", 15, pc is { Connected: true, MonitorLive: true } ? UiTheme.Accent : UiTheme.Warn));
            if (_host && i > 0)
            {
                int index = i;
                var up = new Button { Text = "▲", TooltipText = "Move up", CustomMinimumSize = new Vector2(44, 36) };
                up.Pressed += () => SeatMoved?.Invoke(index, index - 1);
                row.AddChild(up);
            }
            _seats.AddChild(row);
        }
        _whyNot.Text = s.CanStart ? "" : s.WhyNot ?? "";
        _start.Disabled = !s.CanStart;
    }

    private static string Rules(HouseRules r) =>
        $"{(r.Mulligans > 0 ? $"{r.Mulligans} mulligan{(r.Mulligans == 1 ? "" : "s")}" : "no mulligans")}, " +
        $"{(r.GimmeMetres > 0 ? $"gimmes inside {r.GimmeMetres * 3.281:0.#} ft" : "hole everything out")}{(r.PickUpAtDoublePar ? ", pick up at double par" : "")}";

    public void Close()
    {
        Visible = false;
        GetViewport()?.GuiReleaseFocus();
    }
}
