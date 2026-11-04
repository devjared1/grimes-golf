namespace GrimesGolf.Core.Square;

/// <summary>Transport to the launch monitor (BLE on Windows; faked in tests).</summary>
public interface ISquareLink
{
    /// <summary>Raised for every raw EVT notification. A zero-length payload means the link is being torn down.</summary>
    event Action<byte[]>? DataReceived;

    /// <summary>Raised when the underlying connection drops.</summary>
    event Action? Disconnected;

    Task ConnectAsync(CancellationToken ct);

    /// <summary>Writes one 9-byte command to the CMD characteristic.</summary>
    Task WriteAsync(byte[] command, CancellationToken ct);

    Task DisconnectAsync();
}
