using System.Text;
using Godot;

namespace GrimesGolf.Game;

/// <summary>
/// Writes errors to <c>crash.log</c> in the app's data folder, so a problem a guest never sees still leaves a trace:
/// engine and script errors (including exceptions thrown in callbacks, which Godot reports and carries on from),
/// exceptions that escape a background task, and anything that takes the process down.
/// Repeats of the same error (e.g. one thrown every frame) are counted rather than written again; the file is
/// started afresh once it passes 1 MB.
/// </summary>
public partial class CrashLog : Logger
{
    private const long MaxBytes = 1 << 20;
    private static readonly object Gate = new();
    private static CrashLog? _instance;

    private readonly string _path = "";
    private string? _last;
    private int _repeats;

    public CrashLog() { }   // Godot needs one for script instances
    private CrashLog(string path) => _path = path;

    public static string? FilePath => _instance?._path;

    /// <summary>Starts logging to <paramref name="folder"/>/crash.log. Safe to call more than once.</summary>
    public static void Install(string folder)
    {
        if (_instance != null) return;
        Directory.CreateDirectory(folder);
        var log = _instance = new CrashLog(Path.Combine(folder, "crash.log"));
        OS.AddLogger(log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Write("Unhandled exception (the app closed)", e.ExceptionObject?.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write("Unobserved task exception", e.Exception.ToString());
            e.SetObserved();
        };
    }

    // Called by Godot from any thread. Must not print (that would come back here).
    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify,
                                   int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        if (errorType == (int)ErrorType.Warning) return;
        var detail = new StringBuilder();
        detail.Append(rationale.Length > 0 ? rationale : code);
        if (rationale.Length > 0 && code.Length > 0 && code != rationale) detail.Append("\n  ").Append(code);
        detail.Append($"\n  at {function} ({file}:{line})");
        foreach (var bt in scriptBacktraces) detail.Append('\n').Append(bt.Format());
        Write((ErrorType)errorType == ErrorType.Script ? "Script error" : "Error", detail.ToString());
    }

    public override void _LogMessage(string message, bool error)
    {
        if (error) Write("Error output", message.TrimEnd());
    }

    private void Write(string kind, string detail)
    {
        if (_path.Length == 0) return;
        lock (Gate)
        {
            try
            {
                if (detail == _last) { _repeats++; return; }
                var text = new StringBuilder();
                if (_repeats > 0) text.Append($"    (repeated {_repeats} more times)\n");
                _last = detail;
                _repeats = 0;
                text.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {kind}: {detail}\n");
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes) File.Delete(_path);
                File.AppendAllText(_path, text.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
