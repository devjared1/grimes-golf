using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// What the player can do without the mouse, from the keyboard, a wireless numpad, a gamepad or a presenter clicker.
/// Every input is mapped to one <see cref="Command"/> (<see cref="CommandFor"/>), so the same command does the same
/// thing whichever device sent it.
/// <list type="table">
/// <item><term>Hit / next hole</term><description>Space, numpad 5 or Enter, gamepad A, clicker "next" (Page Down)</description></item>
/// <item><term>Aim ¼° (course and range)</term><description>← →, numpad 4 6, D-pad left/right</description></item>
/// <item><term>Range: next / previous target flag</term><description>F / Shift+F, gamepad Y</description></item>
/// <item><term>Aim 2°</term><description>Shift + ← →, numpad 7 9, gamepad LB/RB</description></item>
/// <item><term>Landing view on / off</term><description>Z, numpad 1, gamepad left stick click</description></item>
/// <item><term>Power 1% / 5% (with a launch monitor: the aim 1 / 5 yd further or shorter)</term><description>↑ ↓, Shift + ↑ ↓</description></item>
/// <item><term>Club longer / shorter</term><description>numpad 8 2, D-pad up/down, Page Up/Down with Shift</description></item>
/// <item><term>Mulligan</term><description>U, numpad 0, gamepad X</description></item>
/// <item><term>Scorecard</term><description>S, numpad ., gamepad Y, clicker "back" (Page Up)</description></item>
/// <item><term>Next hole</term><description>N, numpad +</description></item>
/// <item><term>Menu</term><description>M, Esc (with nothing to close), gamepad Start</description></item>
/// <item><term>Camera</term><description>C, numpad -, gamepad right stick click</description></item>
/// <item><term>Settings</term><description>numpad *, gamepad Back/Select</description></item>
/// <item><term>Cancel / close</term><description>Esc, numpad /, gamepad B</description></item>
/// </list>
/// With a menu or panel open, the arrows / D-pad move between its buttons and Enter / Space / A presses.
/// </summary>
public partial class Main
{
    public enum Command
    {
        Hit, AimLeft, AimRight, AimLeftBig, AimRightBig, PowerUp, PowerDown, PowerUpBig, PowerDownBig, ClubLonger, ClubShorter,
        Mulligan, Scorecard, NextHole, Camera, Settings, Back, Menu, Demo, Drawer, Tuner, Replay, Flag, FlagBack, HeatMap, Grid, RestartHole,
        LandingView,
    }

    /// <summary>The command for an input event (null if it isn't one, or is a release or key repeat that shouldn't repeat).</summary>
    public static Command? CommandFor(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true } k:
            {
                bool shift = k.ShiftPressed;
                // Aim and power repeat while held; everything else fires once per press.
                Command? held = k.Keycode switch
                {
                    Key.Left => shift ? Command.AimLeftBig : Command.AimLeft,
                    Key.Right => shift ? Command.AimRightBig : Command.AimRight,
                    Key.Up => shift ? Command.PowerUpBig : Command.PowerUp,
                    Key.Down => shift ? Command.PowerDownBig : Command.PowerDown,
                    Key.Kp4 => Command.AimLeft, Key.Kp6 => Command.AimRight,
                    Key.Kp7 => Command.AimLeftBig, Key.Kp9 => Command.AimRightBig,
                    _ => null,
                };
                if (held != null) return held;
                if (k.Echo) return null;
                return k.Keycode switch
                {
                    Key.Space or Key.Kp5 or Key.KpEnter => Command.Hit,
                    Key.Pagedown => shift ? Command.ClubShorter : Command.Hit,       // presenter clicker: "next"
                    Key.Pageup => shift ? Command.ClubLonger : Command.Scorecard,    // presenter clicker: "back"
                    Key.Kp8 => Command.ClubLonger, Key.Kp2 => Command.ClubShorter,
                    Key.U or Key.Kp0 => Command.Mulligan,
                    Key.S or Key.KpPeriod => Command.Scorecard,
                    Key.N or Key.KpAdd => Command.NextHole,
                    Key.C or Key.KpSubtract => Command.Camera,
                    Key.KpMultiply => Command.Settings,
                    Key.Escape or Key.KpDivide => Command.Back,
                    Key.M => Command.Menu,
                    Key.D => Command.Demo,
                    Key.L => Command.Drawer,
                    Key.T => Command.Tuner,
                    Key.F5 => Command.Replay,
                    Key.F => shift ? Command.FlagBack : Command.Flag,
                    Key.H => Command.HeatMap,
                    Key.G => Command.Grid,
                    Key.R => Command.RestartHole,
                    Key.Z or Key.Kp1 => Command.LandingView,
                    _ => null,
                };
            }
            case InputEventJoypadButton { Pressed: true } b:
                return b.ButtonIndex switch
                {
                    JoyButton.A => Command.Hit,
                    JoyButton.B => Command.Back,
                    JoyButton.X => Command.Mulligan,
                    JoyButton.Y => Command.Scorecard,
                    JoyButton.DpadLeft => Command.AimLeft,
                    JoyButton.DpadRight => Command.AimRight,
                    JoyButton.LeftShoulder => Command.AimLeftBig,
                    JoyButton.RightShoulder => Command.AimRightBig,
                    JoyButton.DpadUp => Command.ClubLonger,
                    JoyButton.DpadDown => Command.ClubShorter,
                    JoyButton.Start => Command.Menu,
                    JoyButton.Back => Command.Settings,
                    JoyButton.RightStick => Command.Camera,
                    JoyButton.LeftStick => Command.LandingView,
                    _ => null,
                };
            default:
                return null;
        }
    }

    // Handled in _Input (before the GUI) so focused buttons/lists don't swallow the arrow keys and gamepad buttons.
    public override void _Input(InputEvent e)
    {
        // A course is loading: nothing to do until it's ready.
        if (_loading) { GetViewport().SetInputAsHandled(); return; }
        // The home player panel (it can open over the settings): like the settings, the UI has everything but closing it.
        if (_hud.Home.IsOpen)
        {
            if (CommandFor(e) is Command.Back) { _hud.Home.Close(); GetViewport().SetInputAsHandled(); }
            return;
        }
        // Get More Courses: the same (Esc / B closes it, once any download is finished or cancelled).
        if (_hud.Store.IsOpen)
        {
            if (CommandFor(e) is Command.Back) { _hud.Store.Close(); GetViewport().SetInputAsHandled(); }
            return;
        }
        // History: the same.
        if (_hud.History.IsOpen)
        {
            if (CommandFor(e) is Command.Back) { _hud.History.Close(); GetViewport().SetInputAsHandled(); }
            return;
        }
        // Settings open, or typing in a text box: let the UI have everything (gamepad and arrows move between fields).
        if (_hud.SettingsOpen)
        {
            if (CommandFor(e) is Command.Back or Command.Settings) { _hud.CloseSettings(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (GetViewport().GuiGetFocusOwner() is LineEdit line)
        {
            // New Game's player list: Esc / B still goes back a page from a name box.
            if (CommandFor(e) is Command.Back && _hud.MainMenu.OnSubPage)
            {
                line.ReleaseFocus();
                _hud.MainMenu.Back();
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        // A menu or panel: it gets the input, apart from closing it (and the drawer, which can open over the main menu).
        if (_hud.ModalOpen)
        {
            var command = CommandFor(e);
            if (command is Command.Back or Command.Menu && CloseTopPanel()) GetViewport().SetInputAsHandled();
            else if (command is Command.Drawer or Command.Settings && _hud.MainMenu.IsOpen && Do(command.Value)) GetViewport().SetInputAsHandled();
            return;
        }
        if (CommandFor(e) is not { } c) return;
        if (Do(c)) GetViewport().SetInputAsHandled();
    }

    /// <summary>Esc / B with a panel open: closes the top one (false if there's nothing it closes, e.g. the main menu's home page).</summary>
    private bool CloseTopPanel()
    {
        if (_hud.Alignment.IsOpen) _hud.Alignment.Cancel();
        else if (_hud.DrawerOpen) _hud.ToggleDrawer();
        else if (_hud.Players.IsOpen) _hud.Players.Close();
        else if (_hud.Overlay.IsOpen) _hud.Overlay.Close();
        else if (_hud.MainMenu.OnSubPage) _hud.MainMenu.Back();
        else if (_hud.Online.IsOpen && !_hud.Online.InLobby) { _hud.Online.Close(); ShowMainMenu(); }
        else return false;
        return true;
    }

    /// <summary>Carries out a command; false if it doesn't apply right now (the input then goes on to the UI).</summary>
    private bool Do(Command c)
    {
        bool course = _courseMode;
        switch (c)
        {
            case Command.AimLeft or Command.AimRight or Command.AimLeftBig or Command.AimRightBig:
                double degrees = c switch { Command.AimLeft => -Hud.AimStep, Command.AimRight => Hud.AimStep, Command.AimLeftBig => -Hud.AimStepBig, _ => Hud.AimStepBig };
                if (course) Aim(degrees); else AimRange(degrees);
                return true;
            case Command.LandingView: ToggleLandingView(); return true;
            case Command.PowerUp or Command.PowerDown or Command.PowerUpBig or Command.PowerDownBig:
                NudgeAim(c switch { Command.PowerUp => 1, Command.PowerDown => -1, Command.PowerUpBig => 5, _ => -5 });
                return true;
            case Command.Hit: HitPress(); return true;
            case Command.Back:
                if (_camera.Flying) _camera.SkipFlyover();
                else if (_hud.ScorecardOpen) _hud.ShowScorecard(false);
                else if (_hud.DrawerOpen) _hud.ToggleDrawer();
                else OpenGameMenu();
                return true;
            case Command.Menu: OpenGameMenu(); return true;
            case Command.ClubLonger or Command.ClubShorter:
                _hud.StepClub(c == Command.ClubLonger ? -1 : 1);
                return true;
            case Command.Demo:
                if (course && _match is { HoleComplete: true } && _outcome == null) NextHole(); else Demo();
                return true;
            // Online: no tuner, replay or restarting the hole (the host's PC plays every shot with the host's physics).
            case Command.Tuner or Command.Replay or Command.RestartHole when _online != null && course: return true;
            case Command.Camera: ToggleCamera(); return true;
            case Command.Drawer: _hud.ToggleDrawer(); return true;
            case Command.Settings: OpenSettings(); return true;
            case Command.Tuner: _tuner.Toggle(); return true;
            // Only with the tuner open: a presenter clicker's "slideshow" button sends F5.
            case Command.Replay when _tuner.Visible: Replay(); return true;
            case Command.Scorecard when course: _hud.ToggleScorecard(); return true;
            // On the range, the scorecard and flag keys pick the target flag instead.
            case Command.Scorecard or Command.Flag when !course: CycleRangeTarget(1); return true;
            case Command.FlagBack when !course: CycleRangeTarget(-1); return true;
            case Command.NextHole when course: NextHole(); return true;
            case Command.Mulligan when course: Mulligan(); return true;
            case Command.Flag when course: ToggleFlag(); return true;
            case Command.HeatMap when course: SetGreenOverlay(!_heatMap, _grid); return true;
            case Command.Grid when course: SetGreenOverlay(_heatMap, !_grid); return true;
            case Command.RestartHole when course: RestartHole(); return true;
            default: return false;
        }
    }

    /// <summary>Everyone back to the tee of this hole.</summary>
    private void RestartHole()
    {
        if (_match != null && _courseMode && _outcome == null) StartHole(_match.HoleIndex);
    }
}
