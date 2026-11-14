using GrimesGolf.Core;
using GrimesGolf.Core.Square;

namespace GrimesGolf.Game;

/// <summary>
/// Replaying a capture on the range: shots recorded with the shot recorder (<c>GrimesGolf.ShotRecorder</c>, beside
/// GSPro) flown one by one with the physics as it is now, with GSPro's numbers for each shot shown beside ours once
/// the ball stops. N or HIT SHOT flies the next one, Shift+N goes back one; F5 (with the tuner open) flies the same
/// one again, so a tuning change can be watched on the shot it was made for. Replayed shots aren't saved.
/// </summary>
public partial class Main
{
    private sealed record LoadedCapture(string Folder, string Title, List<CapturedShot> Shots, Dictionary<int, ReferenceShot> GsPro);

    private LoadedCapture? _capture;
    private int _captureIndex = -1;
    /// <summary>The captured shot behind the last range shot (null once a shot from anywhere else is hit).</summary>
    private (RangeShot Range, CapturedShot Shot)? _captureLast;
    /// <summary>The captured shot in the air, whose comparison shows once the ball stops.</summary>
    private (CapturedShot Shot, FlightResult Ours)? _capturePending;
    /// <summary>True while a captured shot is being played (so it isn't saved as one of the player's shots).</summary>
    private bool _playingCapture;

    private string CapturesFolder => Path.Combine(_dataFolder, "captures");

    /// <summary>The capture folders, newest first (those with a capture file).</summary>
    private List<string> CaptureFolders() =>
        Directory.Exists(CapturesFolder)
            ? Directory.GetDirectories(CapturesFolder).Where(d => File.Exists(Path.Combine(d, "packets.jsonl"))).OrderDescending().ToList()
            : new List<string>();

    /// <summary>The range menu's Replay a Capture: the latest captures to pick from.</summary>
    private void OpenCaptureList()
    {
        var items = new List<MenuItem>();
        foreach (var folder in CaptureFolders().Take(8))
        {
            if (ReadCapture(folder) is not { } c || c.Shots.Count == 0) continue;
            items.Add(new(c.Title, () => LoadCapture(folder), $"{c.Shots.Count} shots, GSPro numbers for {c.GsPro.Count}"));
        }
        if (items.Count == 0)
        {
            _hud.Toast("No captures with shots yet: record some with the shot recorder", UiTheme.Warn);
            return;
        }
        if (_capture != null) items.Add(new("Stop Replaying", StopCapture, "Back to hitting your own shots"));
        items.Add(new("Cancel", null));
        _hud.Overlay.Open("Replay a capture",
            "Shots recorded beside GSPro, flown with your current physics. N or HIT SHOT for the next shot, Shift+N for the one before.", items);
    }

    private static LoadedCapture? ReadCapture(string folder)
    {
        try
        {
            var cap = PacketCapture.Read(Path.Combine(folder, "packets.jsonl"));
            var shots = cap.Shots().Where(s => s.ClubCode != "PT").ToList();
            var gspro = ReferenceShot.ReadCsv(Path.Combine(folder, "gspro.csv"));
            var started = cap.Header?.Started ?? Directory.GetCreationTime(folder);
            string title = $"{started:ddd d MMM, HH:mm}{(cap.Header?.Note is { Length: > 0 } n ? $"  ·  {n}" : "")}  ·  {shots.Count} shots";
            return new LoadedCapture(folder, title, shots, gspro);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void LoadCapture(string folder)
    {
        if (ReadCapture(folder) is not { Shots.Count: > 0 } c) { _hud.Toast("Couldn't read that capture", UiTheme.Danger); return; }
        _capture = c;
        _captureIndex = -1;
        Log($"Replaying capture {folder}: {c.Shots.Count} shots, GSPro numbers for {c.GsPro.Count}.");
        _hud.SetRangeNote($"Capture loaded: {c.Shots.Count} shots  ·  N or HIT SHOT for the first");
        _hud.Toast($"{c.Shots.Count} captured shots loaded", UiTheme.Accent);
    }

    private void StopCapture()
    {
        if (_capture == null) return;
        _capture = null;
        _captureIndex = -1;
        _captureLast = null;
        _capturePending = null;
        _hud.SetRangeNote(null);
    }

    /// <summary>Flies the captured shot <paramref name="step"/> on from the last one (false: no capture loaded).</summary>
    private bool PlayCapture(int step)
    {
        if (_capture is not { } c || _courseMode) return false;
        int next = _captureIndex + step;
        if (next >= c.Shots.Count) { _hud.Toast("That was the last captured shot (Shift+N goes back)", UiTheme.Ink); return true; }
        if (next < 0) { _hud.Toast("That's the first captured shot", UiTheme.Ink); return true; }
        _captureIndex = next;
        var s = c.Shots[next];
        if (s.ClubCode != null) _hud.SetClub(s.ClubCode);
        FlyCaptured(s, RangeShot.FromSquare(s.Ball, s.Club));
        return true;
    }

    private void FlyCaptured(CapturedShot s, RangeShot shot)
    {
        _playingCapture = true;
        try { ShowShot(shot, replay: true); }
        finally { _playingCapture = false; }
        _captureLast = (shot, s);
        if (_rangeResult is { } r && ReferenceEquals(r.Shot, shot)) _capturePending = (s, r.Flight);
        _hud.SetRangeNote($"Captured shot {s.Number} of {_capture?.Shots.Count}  ·  {s.ClubCode ?? "?"}  ·  in the air…");
    }

    /// <summary>F5 on a captured shot: flies it again (with the tuning as it is now), GSPro's numbers and all.</summary>
    private bool ReplayCaptured(RangeShot last)
    {
        if (_captureLast is not { } cl || !ReferenceEquals(cl.Range, last) || _courseMode) return false;
        FlyCaptured(cl.Shot, last);
        return true;
    }

    /// <summary>Once the ball has stopped: GSPro's numbers for the captured shot beside ours.</summary>
    private void ShowCaptureResult()
    {
        if (_capturePending is not { } p || _capture is not { } c) return;
        _capturePending = null;
        var f = p.Ours;
        string text = $"Captured shot {p.Shot.Number} of {c.Shots.Count}  ·  {p.Shot.ClubCode ?? "?"}\n" +
                      $"Ours:    {f.CarryYards:0} carry  ·  {f.TotalYards:0} total  ·  {f.ApexYards * 3:0} ft apex  ·  {RangeShot.Side(f.LateralYards, "0")}";
        if (c.GsPro.GetValueOrDefault(p.Shot.Number) is { } g)
        {
            string D(double? theirs, double mine) => theirs is { } t ? Math.Round(t - mine).ToString("+0;-0;0") : "-";
            text += $"\nGSPro:  {N(g.CarryYds)} carry  ·  {N(g.TotalYds)} total  ·  {N(g.ApexFt)} ft apex  ·  {(g.OfflineYds is { } o ? RangeShot.Side(o, "0") : "-")}" +
                    $"\nGSPro is {D(g.CarryYds, f.CarryYards)} carry, {D(g.TotalYds, f.TotalYards)} total, {D(g.ApexFt, f.ApexYards * 3)} ft apex";
        }
        else text += "\n(no GSPro numbers for this shot)";
        _hud.SetRangeNote(text + "\nN next  ·  Shift+N back  ·  F5 flies it again (tuner open)");
        Log(text.Replace("\n", "  "));
    }

    private static string N(double? v) => v?.ToString("0") ?? "-";
}
