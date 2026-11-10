using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using GrimesGolf.Bluetooth;
using GrimesGolf.Core;
using GrimesGolf.Core.Course;
using GrimesGolf.Core.Square;

namespace GrimesGolf.ShotRecorder;

/// <summary>
/// Records shots while they're hit in GSPro, flies each through our physics, and lines our numbers up against
/// GSPro's (typed in after each shot). By default it sits between Square's GSPro connector and GSPro: the connector
/// is pointed at this tool's port, and every byte is passed on to GSPro untouched while the Open Connect messages are
/// recorded. (Listening in on the Bluetooth link beside the connector doesn't work: Windows won't share it.)
/// <c>--drive</c> connects to the monitor over Bluetooth and arms it itself instead, recording the raw packets, for
/// when nothing else is connected.
/// </summary>
public static class Program
{
    private static readonly object Gate = new();
    private const int DefaultRelayPort = 9210, GsProPort = 921;

    public static async Task<int> Main(string[] args)
    {
        var opts = Options.Parse(args);
        string[] known = { "drive", "verbose", "tuning", "port", "gspro", "out", "note", "club", "lh", "address", "only" };
        foreach (var key in opts.Named.Keys.Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase)))
            Console.Error.WriteLine($"Unknown option --{key} (ignored). Options: {string.Join(" ", known.Select(k => "--" + k))}");
        try
        {
            return opts.Verb switch
            {
                "record" => await RecordAsync(opts),
                "compare" => Compare(opts),
                "fit" => FitTuning(opts),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or SocketException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine($"""
            Grimes Golf shot recorder: record shots hit in GSPro and compare our physics with GSPro's.

              record [--port={DefaultRelayPort}] [--gspro=127.0.0.1:{GsProPort}] [--tuning=file|defaults] [--out=folder] [--note=text]
                  Start this, then point Square's GSPro connector at 127.0.0.1 port {DefaultRelayPort} (its custom IP
                  setting) and connect it. Everything is passed on to GSPro, and each shot is recorded.
                  After each shot, type GSPro's numbers: carry total apex(ft) offline, e.g. 152 168 85 R4.

              record --drive [--club=I7] [--lh] [--address=XX:XX:XX:XX:XX:XX] [--verbose] [...]
                  Connects to the monitor over Bluetooth and arms it itself, recording the raw packets (close Square's
                  connector first; nothing goes to GSPro).

              compare [capture folder or packets.jsonl] [--tuning=file|defaults]
                  Flies a capture again with the given tuning (by default the game's start-up tuning) and compares
                  it with the GSPro numbers saved with it. With no capture given, the latest one.

              fit [capture folders...] [--tuning=file|defaults] [--only=air|ground|green]
                  Searches the flight settings, then the fairway's and green's bounce and roll, for the values that
                  match GSPro's numbers best, starting from the given tuning. Where GSPro plays unlike the real thing
                  its numbers aren't copied: full wedges check (fairway) or spin back (green), irons release 4-5 yd. Writes fitted-tuning.json beside the (first) capture;
                  nothing else is changed. Try it with compare --tuning=..., or load it in the game's tuner.

            Captures go to %APPDATA%\Square Range 3D\captures\<date_time>\ (packets.jsonl, gspro.csv, comparison.csv).
            """);
        return 2;
    }

    // ---- record ------------------------------------------------------------------------------

    private static async Task<int> RecordAsync(Options opts)
    {
        bool drive = opts.Has("drive"), verbose = opts.Has("verbose");
        var (tuning, tuningName) = LoadTuning(opts.Get("tuning"));
        ActivePhysics.Apply(tuning);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Settle the connection details before making a capture folder.
        int port = int.TryParse(opts.Get("port"), out int p) ? p : DefaultRelayPort;
        var gspro = ParseEndpoint(opts.Get("gspro"));
        TcpListener? listener = null;
        ulong address = 0;
        if (drive) address = ParseAddress(opts.Get("address")) ?? LastMonitor() ?? await ScanAsync(cts.Token);
        else
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); }
            catch (SocketException ex) { throw new IOException($"Can't listen on port {port} ({ex.SocketErrorCode}). Is the recorder already running? Try --port=9211.", ex); }
        }

        string folder = opts.Get("out") ?? Path.Combine(DataFolder, "captures", $"{DateTime.Now:yyyy-MM-dd_HHmmss}");
        Directory.CreateDirectory(folder);
        string refsPath = Path.Combine(folder, "gspro.csv");
        if (!File.Exists(refsPath)) File.WriteAllText(refsPath, ReferenceShot.CsvHeader + Environment.NewLine);
        var refs = ReferenceShot.ReadCsv(refsPath);

        using var capture = new PacketCaptureWriter(Path.Combine(folder, "packets.jsonl"),
            new CaptureHeader(DateTimeOffset.Now, drive ? "drive" : "gspro-relay", opts.Get("note")));
        capture.Note(DateTimeOffset.Now, $"tuning: {tuningName}");
        Say($"Capture: {folder}");
        Say($"Physics: {tuningName}");

        var packets = new ShotTracker();
        var messages = new OpenConnectShotTracker();
        IReadOnlyList<CapturedShot> Shots() => drive ? packets.Shots : messages.Shots;
        int shown = 0;
        void Show(CapturedShot? s)
        {
            if (s == null) return;
            if (s.Number > shown)
            {
                shown = s.Number;
                var c = ComparedShot.Fly(s, null);
                Console.WriteLine();
                Console.WriteLine(ComparisonReport.TableHeader);
                Console.WriteLine(ComparisonReport.Row(c));
                Console.WriteLine(c.IsPutt ? "  (putt: not compared)" : $"  GSPro for shot {s.Number}? carry total apex(ft) offline  >");
            }
            else if (verbose) Console.WriteLine($"  shot {s.Number}: club {s.ClubCode ?? "?"}{(s.Club is { } k ? $", path {k.PathDeg:0.0}° face {k.FaceDeg:0.0}° attack {k.AttackDeg:0.0}° loft {k.DynamicLoftDeg:0.0}°" : "")}");
        }
        void OnPacket(bool outgoing, byte[] data)
        {
            var pk = new CapturedPacket(DateTimeOffset.Now, outgoing, data);
            capture.Write(pk);
            lock (Gate) Show(packets.Feed(pk));
        }
        void OnMessage(CapturedMessage m)
        {
            capture.Write(m);
            if (verbose) Say($"{(m.ToGsPro ? "→ GSPro" : "← GSPro")} {m.Json}");
            lock (Gate) Show(messages.Feed(m));
        }
        void Log(string m)
        {
            if (!verbose && (m.StartsWith("Sensor raw") || m.StartsWith("Shot raw") || m.StartsWith("Monitor club"))) return;
            Say(m);
        }

        MonitorSession? session = null;
        string club = (opts.Get("club") ?? "DR").ToUpperInvariant();
        bool lh = opts.Has("lh");
        Task work;
        if (drive)
        {
            string addressText = new SquareAdvertisement("", address, 0, DateTime.Now).AddressText;
            work = DriveAsync(address, addressText, club, lh, OnPacket, Log, capture, s => session = s, cts.Token);
            Say($"Driving the monitor at {addressText} ({club}{(lh ? ", LH" : "")}). Type 'help' for commands.");
        }
        else
        {
            work = RelayAsync(listener!, gspro, OnMessage, Log, capture, cts.Token);
            Say($"In Square's connector, set the GSPro address to 127.0.0.1 port {port} and connect. Shots go on to GSPro at {gspro}.");
            Say("Type 'help' for commands.");
        }

        while (!cts.IsCancellationRequested)
        {
            string? line;
            try { line = await Task.Run(Console.ReadLine).WaitAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            switch (line.Split(' ')[0].ToLowerInvariant())
            {
                case "q" or "quit" or "exit":
                    cts.Cancel();
                    continue;
                case "help" or "?":
                    Say("""
                        152 168 85 R4    GSPro's carry, total, apex (feet; 28y for yards) and offline for the last shot
                        #3 152 168 85 R4 the same for shot 3 (to fix or fill in an earlier one); '-' leaves one out
                        list             every shot so far, and the averages
                        note <text>      a note in the capture
                        club <code>      (--drive) set the monitor's club, e.g. club I7
                        q                stop, and write comparison.csv
                        """);
                    continue;
                case "list" or "l":
                    lock (Gate) PrintAll(Compare(Shots(), refs));
                    continue;
                case "note":
                    capture.Note(DateTimeOffset.Now, line[4..].Trim());
                    Say("Noted.");
                    continue;
                case "club":
                    if (!drive) { Say("Change the club in GSPro (or Square's connector); it shows up here by itself."); continue; }
                    if (session == null) { Say("Not connected yet."); continue; }
                    club = line[4..].Trim().ToUpperInvariant();
                    if (!SquareClubMap.TryGet(club, out _)) { Say($"Unknown club {club}. Codes: {string.Join(" ", ClubPresets.All.Select(c => c.Code))}"); continue; }
                    capture.Note(DateTimeOffset.Now, $"club {club}");
                    try { await session.SelectClubAsync(club, lh); Say($"Club set to {club}."); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Say($"Club change failed: {ex.Message}"); }
                    continue;
            }

            int shot, count;
            string values = line;
            lock (Gate) shot = count = Shots().Count;
            if (line.StartsWith('#'))
            {
                var head = line[1..].Split(' ', 2);
                if (!int.TryParse(head[0], out shot) || head.Length < 2) { Say("Type #<shot> and the numbers, e.g. #3 152 168 85 R4."); continue; }
                values = head[1];
            }
            if (shot < 1 || shot > count) { Say(shot < 1 ? "No shot yet." : $"There's no shot {shot} yet."); continue; }
            if (!ReferenceShot.TryParse(shot, values, out var r, out var err)) { Say($"Didn't get that: {err}."); continue; }
            File.AppendAllText(refsPath, r.ToCsv() + Environment.NewLine);
            lock (Gate)
            {
                refs[shot] = r;
                Console.WriteLine(ComparisonReport.Row(ComparedShot.Fly(Shots()[shot - 1], r)));
            }
        }

        session?.Stop();
        listener?.Stop();
        try { await work.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* closing anyway */ }

        List<ComparedShot> all;
        lock (Gate) all = Compare(Shots(), refs);
        if (all.Count > 0)
        {
            ComparisonReport.WriteCsv(Path.Combine(folder, "comparison.csv"), all, tuningName);
            PrintAll(all);
        }
        Say($"{all.Count} shot(s) recorded in {folder}");
        return 0;
    }

    /// <summary>
    /// Takes the connector's connections and passes each on to GSPro, both ways, byte for byte: the bytes are forwarded
    /// before anything is made of them, so GSPro sees exactly what the connector sent. Messages are recorded on the side.
    /// </summary>
    private static async Task RelayAsync(TcpListener listener, IPEndPoint gspro, Action<CapturedMessage> onMessage, Action<string> log,
                                         PacketCaptureWriter capture, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var connector = await listener.AcceptTcpClientAsync(ct);
                _ = RelayOneAsync(connector, gspro, onMessage, log, capture, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException ex) when (ct.IsCancellationRequested || ex.SocketErrorCode == SocketError.OperationAborted) { }
    }

    private static async Task RelayOneAsync(TcpClient connector, IPEndPoint gspro, Action<CapturedMessage> onMessage, Action<string> log,
                                            PacketCaptureWriter capture, CancellationToken ct)
    {
        using (connector)
        using (var game = new TcpClient())
        {
            connector.NoDelay = true;
            game.NoDelay = true;
            log("Square's connector connected.");
            try { await game.ConnectAsync(gspro, ct); }
            catch (SocketException ex)
            {
                log($"Couldn't reach GSPro at {gspro} ({ex.SocketErrorCode}). Is GSPro running, with its Open Connect API on? Reconnect the connector to try again.");
                capture.Note(DateTimeOffset.Now, $"GSPro unreachable: {ex.SocketErrorCode}");
                return;
            }
            catch (OperationCanceledException) { return; }
            capture.Note(DateTimeOffset.Now, $"relaying to GSPro at {gspro}");
            log("Passing it on to GSPro. Hit away.");

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var up = PumpAsync(connector.GetStream(), game.GetStream(), toGsPro: true, onMessage, stop.Token);
            var down = PumpAsync(game.GetStream(), connector.GetStream(), toGsPro: false, onMessage, stop.Token);
            var first = await Task.WhenAny(up, down);
            stop.Cancel();
            string who = first == up ? "Square's connector" : "GSPro";
            capture.Note(DateTimeOffset.Now, $"{who} closed the connection");
            if (!ct.IsCancellationRequested) log($"{who} closed the connection. Waiting for the connector to connect again.");
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, bool toGsPro, Action<CapturedMessage> onMessage, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var framer = new JsonObjectFramer();
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, ct)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                int c = decoder.GetChars(buffer, 0, n, chars, 0);
                foreach (var json in framer.Feed(new string(chars, 0, c)))
                {
                    try { onMessage(new CapturedMessage(DateTimeOffset.Now, toGsPro, json)); }
                    catch (Exception ex) { Console.Error.WriteLine($"Couldn't record a message: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    /// <summary>Connects and arms the monitor like the game does, recording both directions. Reconnects if it drops.</summary>
    private static async Task DriveAsync(ulong address, string addressText, string club, bool lh, Action<bool, byte[]> onPacket,
                                         Action<string> log, PacketCaptureWriter capture, Action<MonitorSession?> current, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var s = new MonitorSession(address, log);
            s.Packet += onPacket;
            s.InfoRead += info => capture.Note(DateTimeOffset.Now, $"driving {addressText}: {info.Description}, serial {info.Serial}, firmware {info.FirmwareRaw}");
            s.Ended += reason => { capture.Note(DateTimeOffset.Now, reason); log(reason); };
            current(s);
            using (ct.Register(s.Stop)) await s.RunAsync(club, lh);
            current(null);
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // ---- compare -----------------------------------------------------------------------------

    private static int Compare(Options opts)
    {
        string path = opts.Positional.FirstOrDefault() ?? LatestCapture()
                      ?? throw new IOException("No captures yet; record some shots first.");
        string file = Directory.Exists(path) ? Path.Combine(path, "packets.jsonl") : path;
        if (!File.Exists(file)) throw new IOException($"No capture at {file}");
        string folder = Path.GetDirectoryName(Path.GetFullPath(file))!;

        var (tuning, tuningName) = LoadTuning(opts.Get("tuning"));
        ActivePhysics.Apply(tuning);
        var cap = PacketCapture.Read(file);
        var shots = cap.Shots();
        var refs = ReferenceShot.ReadCsv(Path.Combine(folder, "gspro.csv"));

        Say($"Capture: {file}");
        if (cap.Header is { } h) Say($"Recorded {h.Started:yyyy-MM-dd HH:mm} ({h.Mode}){(h.Note is { } n ? $": {n}" : "")}");
        Say($"Physics: {tuningName}");
        Say($"{cap.Packets.Count} packets, {cap.Messages.Count} GSPro messages, {shots.Count} shots, GSPro numbers for {refs.Count}");

        var all = Compare(shots, refs);
        if (all.Count == 0) return 0;
        PrintAll(all);
        string csv = Path.Combine(folder, "comparison.csv");
        ComparisonReport.WriteCsv(csv, all, tuningName);
        Say($"Written to {csv}");
        return 0;
    }

    // ---- fit ---------------------------------------------------------------------------------

    private static int FitTuning(Options opts)
    {
        var paths = opts.Positional.Count > 0 ? opts.Positional.ToList()
            : new List<string> { LatestCapture() ?? throw new IOException("No captures yet; record some shots first.") };
        var shots = new List<(CapturedShot Shot, ReferenceShot Ref)>();
        foreach (var path in paths)
        {
            string file = Directory.Exists(path) ? Path.Combine(path, "packets.jsonl") : path;
            if (!File.Exists(file)) throw new IOException($"No capture at {file}");
            var refs = ReferenceShot.ReadCsv(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "gspro.csv"));
            var found = PacketCapture.Read(file).Shots().Where(s => s.ClubCode != "PT" && refs.ContainsKey(s.Number)).Select(s => (s, refs[s.Number])).ToList();
            Say($"{file}: {found.Count} shot(s) with GSPro numbers");
            shots.AddRange(found);
        }
        if (shots.Count < 3) throw new IOException("Need at least 3 shots with GSPro numbers to fit anything.");
        string outFolder = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;

        var (start, startName) = LoadTuning(opts.Get("tuning"));
        Say($"Starting from: {startName}");
        List<ComparedShot> FlyAll(PhysicsTuning t)
        {
            ActivePhysics.Apply(t);
            return shots.Select(x => ComparedShot.Fly(x.Shot, x.Ref)).ToList();
        }
        string? only = opts.Get("only")?.ToLowerInvariant();

        var tuning = start;
        if (only is null or "air")
        {
            Say($"Fitting the flight (carry and apex). Error before: {Fit.F(Fit.AirError(FlyAll(tuning)))}");
            tuning = Fit.Search(tuning, Fit.AirKeys, t => Fit.AirError(FlyAll(t)));
            Say($"  after: {Fit.F(Fit.AirError(FlyAll(tuning)))}");
        }
        List<(ComparedShot, double)> OnGreen(PhysicsTuning t) => FlyAll(t).Select(c => (c, Fit.RunOut(c.Launch.Fly(Lie.Green)))).ToList();
        if (only is null or "ground")
        {
            Say($"Fitting the fairway bounce and roll (run-out). Error before: {Fit.F(Fit.GroundError(FlyAll(tuning)))}");
            tuning = Fit.Search(tuning, Fit.GroundKeys, t => Fit.GroundError(FlyAll(t)));
            Say($"  after: {Fit.F(Fit.GroundError(FlyAll(tuning)))}");
        }
        if (only is null or "ground" or "green")
        {
            Say($"Fitting the green's bounce and spin (run-out). Error before: {Fit.F(Fit.GreenError(OnGreen(tuning)))}");
            tuning = Fit.Search(tuning, Fit.GreenKeys, t => Fit.GreenError(OnGreen(t)));
            Say($"  after: {Fit.F(Fit.GreenError(OnGreen(tuning)))}");
        }

        Console.WriteLine();
        Console.WriteLine("Changed:");
        Console.WriteLine(tuning.ChangesFrom(start).Any() ? Fit.Changes(start, tuning) : "  nothing (the starting values fit best)");

        var before = FlyAll(start);
        var beforeGreen = before.Select(c => Fit.RunOut(c.Launch.Fly(Lie.Green))).ToList();
        var afterFair = FlyAll(tuning);
        var afterGreen = afterFair.Select(c => Fit.RunOut(c.Launch.Fly(Lie.Green))).ToList();
        Console.WriteLine();
        Console.WriteLine($"Run-out after landing (yd; negative = spun back). * = full wedge (≥{Fit.SpinBackRpm:0} rpm).");
        Console.WriteLine("  #  club   spin │ fairway: target  before → after │ green: target  before → after");
        for (int i = 0; i < afterFair.Count; i++)
        {
            var c = afterFair[i];
            string fairTarget = Fit.FairwayTarget(c)?.ToString()
                ?? (c.Reference is { CarryYds: { } rc, TotalYds: { } rt } ? $"GSPro {rt - rc:+0.0;-0.0}" : "-");
            string greenTarget = Fit.GreenTarget(c)?.ToString() ?? "-";
            Console.WriteLine($"{c.Shot.Number,3}  {c.Shot.ClubCode,-4}{(Fit.IsFullWedge(c) ? "*" : " ")}{c.Launch.SpinRpm,6:0} │ " +
                              $"{fairTarget,15} {Fit.RunOut(before[i].Ours),7:+0.0;-0.0} → {Fit.RunOut(c.Ours),-6:+0.0;-0.0} │ " +
                              $"{greenTarget,13} {beforeGreen[i],7:+0.0;-0.0} → {afterGreen[i]:+0.0;-0.0}");
        }

        Console.WriteLine();
        Console.WriteLine("Before:");
        Console.WriteLine(ComparisonReport.Summary(before));
        var after = afterFair;
        Console.WriteLine();
        Console.WriteLine("After:");
        PrintAll(after);

        tuning = tuning.Named($"Fitted to GSPro {DateTime.Now:yyyy-MM-dd}");
        string outFile = Path.Combine(outFolder, "fitted-tuning.json");
        File.WriteAllText(outFile, tuning.ToJson());
        Say($"Written to {outFile} (your own tuning is unchanged)");
        return 0;
    }

    private static List<ComparedShot> Compare(IEnumerable<CapturedShot> shots, IReadOnlyDictionary<int, ReferenceShot> refs) =>
        shots.Select(s => ComparedShot.Fly(s, refs.GetValueOrDefault(s.Number))).ToList();

    private static void PrintAll(IReadOnlyList<ComparedShot> all)
    {
        Console.WriteLine();
        Console.WriteLine(ComparisonReport.TableHeader);
        foreach (var c in all) Console.WriteLine(ComparisonReport.Row(c));
        Console.WriteLine();
        Console.WriteLine("Δ = GSPro minus ours (positive: GSPro went further / higher)");
        Console.WriteLine(ComparisonReport.Summary(all));
    }

    // ---- set-up ------------------------------------------------------------------------------

    /// <summary>The game's data folder (Godot's user folder for the app).</summary>
    private static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Square Range 3D");

    private static string? LatestCapture()
    {
        string dir = Path.Combine(DataFolder, "captures");
        return Directory.Exists(dir) ? Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "packets.jsonl"))).Order().LastOrDefault() : null;
    }

    /// <summary>
    /// The tuning to fly shots with: a file, "defaults", or (by default) the game's start-up tuning, found in the Godot
    /// project (<c>src/GrimesGolf.Godot/tuning/startup.json</c>) above this tool, or in <c>tuning</c> beside it.
    /// </summary>
    private static (PhysicsTuning Tuning, string Name) LoadTuning(string? arg)
    {
        if (string.Equals(arg, "defaults", StringComparison.OrdinalIgnoreCase)) return (PhysicsTuning.Defaults, "defaults");
        string? path = arg;
        if (path == null)
        {
            var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "tuning", "startup.json") };
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                candidates.Add(Path.Combine(d.FullName, "src", "GrimesGolf.Godot", "tuning", "startup.json"));
            path = candidates.FirstOrDefault(File.Exists);
            if (path == null) return (PhysicsTuning.Defaults, "defaults (no start-up tuning found)");
        }
        if (!File.Exists(path)) throw new IOException($"No tuning file at {path}");
        var t = PhysicsTuning.FromJson(File.ReadAllText(path));
        return (t, $"{t.Name} ({Path.GetFullPath(path)}, {t.ChangedCount} changed from the defaults)");
    }

    /// <summary>"127.0.0.1:921", "host:port" or just a port; GSPro on this PC by default.</summary>
    private static IPEndPoint ParseEndpoint(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new IPEndPoint(IPAddress.Loopback, GsProPort);
        if (int.TryParse(s, out int portOnly)) return new IPEndPoint(IPAddress.Loopback, portOnly);
        int colon = s.LastIndexOf(':');
        string host = colon > 0 ? s[..colon] : s;
        int port = colon > 0 && int.TryParse(s[(colon + 1)..], out int pp) ? pp : GsProPort;
        var ip = IPAddress.TryParse(host, out var a) ? a
            : Dns.GetHostAddresses(host).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? throw new ArgumentException($"Can't find {host}");
        return new IPEndPoint(ip, port);
    }

    /// <summary>The monitor the game connected to last (its settings), for <c>--drive</c>.</summary>
    private static ulong? LastMonitor()
    {
        try
        {
            string path = Path.Combine(DataFolder, "settings.json");
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path))?["LastMonitor"] is JsonValue v && v.TryGetValue<ulong>(out var a) ? a : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>"F0:12:34:56:78:9A" or a plain number.</summary>
    private static ulong? ParseAddress(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string hex = s.Replace(":", "").Replace("-", "");
        if (s.Contains(':') || s.Contains('-'))
            return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ? a : throw new ArgumentException($"Bad address {s}");
        return ulong.TryParse(s, out var n) ? n : throw new ArgumentException($"Bad address {s}");
    }

    private static async Task<ulong> ScanAsync(CancellationToken ct)
    {
        Say("Scanning for a Square monitor (30 s)...");
        using var scanner = new SquareBleScanner();
        var found = new TaskCompletionSource<SquareAdvertisement>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.DeviceSeen += a => found.TrySetResult(a);
        scanner.Start();
        try
        {
            var a = await found.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Say($"Found {a.Name} ({a.AddressText}, {a.Rssi} dBm)");
            return a.Address;
        }
        catch (TimeoutException) { throw new IOException("No Square monitor found. Is it on, and nothing else connected to it?"); }
    }

    private static void Say(string m)
    {
        lock (Gate) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
    }
}

/// <summary>A verb, <c>--key=value</c> / <c>--flag</c> options, and anything else as positional arguments.</summary>
internal sealed record Options(string Verb, Dictionary<string, string?> Named, List<string> Positional)
{
    public static Options Parse(string[] args)
    {
        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var pos = new List<string>();
        foreach (var a in args.Skip(1))
        {
            if (!a.StartsWith("--")) { pos.Add(a); continue; }
            int eq = a.IndexOf('=');
            if (eq < 0) named[a[2..]] = null; else named[a[2..eq]] = a[(eq + 1)..];
        }
        return new Options(args.FirstOrDefault()?.ToLowerInvariant() ?? "", named, pos);
    }

    public bool Has(string key) => Named.ContainsKey(key);
    public string? Get(string key) => Named.GetValueOrDefault(key);
}
