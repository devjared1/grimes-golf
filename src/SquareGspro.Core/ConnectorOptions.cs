using System.Text.Json;

namespace SquareGspro.Core;

public sealed class ConnectorOptions
{
    public string GsProHost { get; set; } = "127.0.0.1";
    public int GsProPort { get; set; } = 921;

    /// <summary>Identifier sent to GSPro as DeviceID.</summary>
    public string DeviceId { get; set; } = "SquareGolf-GSPro-Connector";

    /// <summary>BLE advertised-name prefix to scan for. The Omni advertises as "SquareGolf(XXXX)".</summary>
    public string DeviceNamePrefix { get; set; } = "SquareGolf";

    /// <summary>Optional exact advertised name (e.g. "SquareGolf(A1B2)") when several monitors are nearby.</summary>
    public string? DeviceName { get; set; }

    public int ScanTimeoutSeconds { get; set; } = 30;
    public int HeartbeatSeconds { get; set; } = 5;

    /// <summary>How long to wait for club data after a ball-flight packet before sending ball data alone.</summary>
    public int ClubMetricsTimeoutMs { get; set; } = 800;

    public bool SendClubData { get; set; } = true;
    public bool AdvancedSpin { get; set; } = true;

    /// <summary>Club/handedness used until GSPro reports the real ones. GSPro codes: DR, W3, I7, PW, SW, PT ...</summary>
    public string DefaultClub { get; set; } = "I7";
    public string DefaultHanded { get; set; } = "RH";

    public static ConnectorOptions Load(string path, Action<string> log)
    {
        if (!File.Exists(path)) { log($"No {Path.GetFileName(path)} found; using defaults."); return new(); }
        try
        {
            return JsonSerializer.Deserialize<ConnectorOptions>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? new();
        }
        catch (JsonException ex)
        {
            log($"Could not read {path}: {ex.Message}. Using defaults.");
            return new();
        }
    }
}
