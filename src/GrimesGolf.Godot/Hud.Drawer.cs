using Godot;
using GrimesGolf.Core;

namespace GrimesGolf.Game;

/// <summary>The launch monitor drawer (devices, connection, device details, battery, handedness, log) and the status chip.</summary>
public partial class Hud
{
    private void BuildDrawer()
    {
        _drawer = new PanelContainer { Visible = false };
        _drawer.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(UiTheme.Bar, 1), 0, 1, 18));   // opaque: it covers the shot data
        _drawer.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
        _drawer.OffsetTop = TopBarHeight;
        _drawer.OffsetRight = 380;
        AddChild(_drawer);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        _drawer.AddChild(v);

        var head = new HBoxContainer();
        var title = UiTheme.Text("Launch monitor", 18, UiTheme.Ink, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        var close = new Button { Text = "✕", ThemeTypeVariation = "FlatButton", TooltipText = "Close  [L]" };
        close.Pressed += ToggleDrawer;
        head.AddChild(close);
        v.AddChild(head);
        _status = UiTheme.Text("Not connected", 13, UiTheme.Muted);
        v.AddChild(_status);

        var devHead = new HBoxContainer();
        var devCaption = UiTheme.Caption("Devices nearby");
        devCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        devHead.AddChild(devCaption);
        _scan = new Button { Text = "Scan", ThemeTypeVariation = "FlatButton" };
        _scan.Pressed += () => ScanPressed?.Invoke();
        devHead.AddChild(_scan);
        v.AddChild(devHead);

        // No type-to-search, so keyboard shortcuts still work while the list has focus.
        _devices = new ItemList { CustomMinimumSize = new Vector2(0, 100), AllowSearch = false };
        _devices.ItemSelected += _ => UpdateButtons();
        _devices.ItemActivated += _ => { if (!_connect.Disabled) _connect.EmitSignal(BaseButton.SignalName.Pressed); };
        v.AddChild(_devices);
        var hint = UiTheme.Text("Close the Square phone app first, and don't pair the monitor in Windows settings.", 12, UiTheme.Muted);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(hint);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _connect = new Button { Text = "Connect", Disabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, ThemeTypeVariation = "PrimaryButton" };
        _connect.Pressed += () => { if (SelectedDevice is { } d) ConnectPressed?.Invoke(d.Address); };
        _disconnect = new Button { Text = "Disconnect", Disabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _disconnect.Pressed += () => DisconnectPressed?.Invoke();
        buttons.AddChild(_connect);
        buttons.AddChild(_disconnect);
        v.AddChild(buttons);

        v.AddChild(UiTheme.Caption("Device"));
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 3);
        foreach (var key in StatKeys)
        {
            grid.AddChild(UiTheme.Text(key, 13, UiTheme.Muted));
            var val = UiTheme.Text("—", 13, UiTheme.Ink);
            val.ClipText = true;
            val.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            grid.AddChild(val);
            _stats[key] = val;
        }
        v.AddChild(grid);

        v.AddChild(UiTheme.Caption("Battery"));
        _battery = new ProgressBar { MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 20) };
        _batteryText = UiTheme.Text("—", 12, UiTheme.Ink);
        _batteryText.HorizontalAlignment = HorizontalAlignment.Center;
        _batteryText.VerticalAlignment = VerticalAlignment.Center;
        _batteryText.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _battery.AddChild(_batteryText);
        v.AddChild(_battery);

        _leftHanded = new CheckBox { Text = "Left-handed golfer" };
        _leftHanded.Toggled += _ => ClubChanged?.Invoke();
        v.AddChild(_leftHanded);

        v.AddChild(UiTheme.Caption("Log"));
        _log = new RichTextLabel { ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        _log.AddThemeFontSizeOverride("normal_font_size", 11);
        _log.AddThemeColorOverride("default_color", UiTheme.Muted);
        v.AddChild(_log);
    }

    /// <summary>Opens or closes the launch monitor drawer (over everything but the settings, so it works from the main menu too).</summary>
    public void ToggleDrawer()
    {
        _drawer.Visible = !_drawer.Visible;
        if (!_drawer.Visible) return;
        _drawer.MoveToFront();
        _settings.MoveToFront();
    }

    // ---- monitor -----------------------------------------------------------------------------

    public void SetStatus(string text) => _status.Text = text;
    public void SetStat(string key, string value) => _stats[key].Text = value;
    public void SetScanning(bool scanning) => _scan.Text = scanning ? "Stop" : "Scan";

    public void ResetStats(bool keepShots = true)
    {
        foreach (var (k, v) in _stats) if (!keepShots || k != "Shots") v.Text = "—";
        _battery.Value = 0;
        _batteryText.Text = "—";
        _batteryLevel = null;
        UpdateChip(UiTheme.Muted);
    }

    public void SetBattery(int level, string charge)
    {
        _battery.Value = level;
        var fill = level <= 15 ? UiTheme.Danger : level <= 35 ? UiTheme.Warn : UiTheme.Accent;
        _battery.AddThemeStyleboxOverride("fill", UiTheme.Box(fill, 4, 0, 0));
        _batteryText.Text = $"{level}%  ·  {charge}";
        _batteryLevel = level;
        UpdateChip(_chipColor);
    }

    /// <summary>Monitor readiness in the top bar ("Ready — hit!", "Place a ball", ...).</summary>
    public void SetReady(string text, Color color)
    {
        _readyText = text;
        UpdateChip(color);
    }

    private Color _chipColor = UiTheme.Muted;

    private void UpdateChip(Color accent)
    {
        _chipColor = accent;
        _monitorChip.Text = ChipText();
        foreach (var (state, bg) in new[] { ("normal", UiTheme.Control), ("hover", UiTheme.ControlHover), ("pressed", UiTheme.ControlPressed) })
        {
            var box = UiTheme.Box(bg, 8, 0, 8, 14);
            box.BorderWidthLeft = 5;
            box.BorderColor = accent;
            _monitorChip.AddThemeStyleboxOverride(state, box);
        }
        if (MainMenu?.IsInsideTree() == true) MainMenu.SetMonitorStatus(ChipText(), accent);
    }

    private string ChipText() => _batteryLevel is { } b ? $"{_readyText}   ·   {b}%" : _readyText;

    public void SetConnected(bool connected)
    {
        _connected = connected;
        UpdateButtons();
    }

    public void Log(string line) => _log.AddText(line + "\n");

    /// <summary>Re-renders the monitor list, keeping the selection.</summary>
    public void SetDevices(IReadOnlyList<DeviceEntry> devices)
    {
        ulong? selected = SelectedDevice?.Address;
        _shownDevices = devices.ToList();
        _devices.Clear();
        for (int i = 0; i < _shownDevices.Count; i++)
        {
            var d = _shownDevices[i];
            bool stale = (DateTime.Now - d.SeenAt).TotalSeconds > 10;
            int idx = _devices.AddItem($"{d.Name}   ·   {d.Rssi} dBm");
            _devices.SetItemTooltip(idx, d.AddressText);
            if (stale) _devices.SetItemCustomFgColor(idx, UiTheme.Muted);
            if (d.Address == selected) _devices.Select(idx);
        }
        if (selected == null && _shownDevices.Count > 0) _devices.Select(0);
        UpdateButtons();
    }

    public DeviceEntry? SelectedDevice
    {
        get
        {
            var sel = _devices.GetSelectedItems();
            return sel.Length > 0 && sel[0] < _shownDevices.Count ? _shownDevices[sel[0]] : null;
        }
    }

    private void UpdateButtons()
    {
        _connect.Disabled = _connected || SelectedDevice == null;
        _disconnect.Disabled = !_connected;
        _scan.Disabled = _connected;
    }
}
