using SquareGspro.Bluetooth;
using SquareGspro.Core;
using SquareGspro.Core.GsPro;

static void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {m}");

Console.Title = "Square Golf -> GSPro Connector";
Log("Square Golf -> GSPro connector (unofficial). Ctrl+C to quit.");

var options = ConnectorOptions.Load(Path.Combine(AppContext.BaseDirectory, "connector.json"), Log);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var gsPro = new GsProClient(options.GsProHost, options.GsProPort, Log);
var bridge = new ShotBridge(options, gsPro, Log);
var gsProTask = gsPro.RunAsync(cts.Token);

while (!cts.IsCancellationRequested)
{
    var link = new SquareBleLink(options, Log);
    try
    {
        await bridge.RunSessionAsync(link, cts.Token);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
    catch (Exception ex)
    {
        Log($"Square connection problem: {ex.Message}");
    }

    try { await Task.Delay(3000, cts.Token); }
    catch (OperationCanceledException) { break; }
}

cts.Cancel();
try { await gsProTask; } catch (OperationCanceledException) { }
Log("Stopped.");
