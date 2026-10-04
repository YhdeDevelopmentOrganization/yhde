using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YHDE.Server.Framing;
using YHDE.Server.Framing.Messages;

// YHDE load test. Simulated editors behave like the Godot add-on: presence
// updates (cursor, selection, view) up to 15 times a second and committed
// edits, spread over teams, each team in its own branch. It measures how long
// a teammate waits to see a cursor move and an edit land.
//
// WARNING: edits are real operations. Point it at a test server, or pass
// --ops-per-sec 0 against a server with real projects.
//
//   dotnet run -c Release -- --url wss://host/ws --key KEY --teams 10 --team-size 5
var options = Options.Parse(args);
if (options is null) return 2;

if (!options.Ramp)
{
    var single = await Load.RunAsync(options, options.Teams, options.TeamSize, quiet: false);
    var json = JsonSerializer.Serialize(single.Report, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (options.JsonOut is not null) await File.WriteAllTextAsync(options.JsonOut, json);
    Console.WriteLine();
    Console.WriteLine(Load.Header);
    Console.WriteLine(single.Row(null));
    return 0;
}

// Ramp: more editors step by step until teammates would notice the delay.
Console.WriteLine($"Ramp test against {options.Url}: teams of {options.TeamSize}, cursor {options.PresenceHz}/s, " +
                  $"one edit every {(options.OpsPerSec > 0 ? (1 / options.OpsPerSec).ToString("0.#", CultureInfo.InvariantCulture) + " s" : "never")}.");
Console.WriteLine("Measuring the network alone first (2 editors)…");
var baseline = await Load.RunAsync(options, 1, 2, quiet: true);
Console.WriteLine($"Network baseline: cursor {baseline.CursorP95:0} ms, edit {baseline.EditP95:0} ms (p95). Later rows are compared with this.");
Console.WriteLine();
Console.WriteLine(Load.Header);
var reports = new List<object> { baseline.Report };
var smooth = 0;
var working = 0;
foreach (var teams in new[] { 4, 10, 20, 40, 60, 80, 100, 130, 160, 200, 250, 300, 400 })
{
    if (teams > options.MaxTeams) break;
    if (teams > options.Branches.Count)
    {
        Console.WriteLine($"Stopping: only {options.Branches.Count} test branches (make more with ./yhde test-branches N).");
        break;
    }
    await Task.Delay(TimeSpan.FromSeconds(5)); // let the server settle between steps
    var step = await Load.RunAsync(options, teams, options.TeamSize, quiet: true);
    reports.Add(step.Report);
    var verdict = step.Verdict(baseline);
    Console.WriteLine(step.Row(verdict));
    if (verdict == "smooth") smooth = step.Editors;
    if (verdict != "too slow") working = step.Editors;
    if (verdict == "too slow") break;
}
Console.WriteLine();
Console.WriteLine(smooth > 0
    ? $"Result: {smooth} editors online at once felt instant; up to {working} still worked well."
    : "Result: even the smallest step was slow: check the network (baseline) and the server.");
if (options.JsonOut is not null)
    await File.WriteAllTextAsync(options.JsonOut, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static class Load
{
    public const string Header = " editors | cursor p95 | edit saved p95 | disconnects | this PC cpu | verdict";

    public sealed record Result(int Editors, double CursorP95, double EditP95, int Lost, double PcCpu, object Report)
    {
        public string Verdict(Result baseline)
        {
            var cursor = CursorP95 - baseline.CursorP95;
            var edit = EditP95 - baseline.EditP95;
            if (Lost > 0 || cursor > 150 || edit > 250) return "too slow";
            if (cursor > 50 || edit > 100) return "noticeable";
            return "smooth";
        }

        public string Row(string? verdict) => string.Create(CultureInfo.InvariantCulture,
            $" {Editors,7} | {Ms(CursorP95),10} | {Ms(EditP95),14} | {Lost,11} | {PcCpu,9:0} % | {verdict ?? "-"}");

        private static string Ms(double v) => double.IsNaN(v) ? "-" : v.ToString("0", CultureInfo.InvariantCulture) + " ms";
    }

    // Ctrl+C ends the measuring early; a second Ctrl+C quits at once.
    public static readonly CancellationTokenSource Interrupt = CreateInterrupt();

    private static CancellationTokenSource CreateInterrupt()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }

    public static async Task<Result> RunAsync(Options options, int teams, int teamSize, bool quiet)
    {
        var clock = Stopwatch.StartNew();
        var metrics = new Metrics();
        var clients = new List<SimClient>();
        for (var t = 0; t < teams; t++)
        {
            for (var m = 0; m < teamSize; m++)
            {
                clients.Add(new SimClient(options, options.Branches[t % options.Branches.Count], t, m, clock, metrics));
            }
        }

        if (!quiet) Console.Error.WriteLine($"Connecting {clients.Count} editors ({teams} team(s) × {teamSize})…");
        var connectErrors = 0;
        using (var gate = new SemaphoreSlim(32))
        {
            await Task.WhenAll(clients.Select(async c =>
            {
                await gate.WaitAsync();
                try
                {
                    await c.ConnectAsync();
                }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref connectErrors) <= 3) Console.Error.WriteLine($"connect failed: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        var live = clients.Where(c => c.Connected).ToList();
        if (!quiet) Console.Error.WriteLine($"{live.Count} connected, {connectErrors} failed. Warming up {options.Warmup}s, measuring {options.Duration}s…");

        using var stop = new CancellationTokenSource();
        var runs = live.Select(c => c.RunAsync(stop.Token)).ToList();
        await Task.Delay(TimeSpan.FromSeconds(options.Warmup));
        if (options.DemoScene is not null)
        {
            var found = live.Count > 0 ? live[0].KnownScenes() : [];
            foreach (var scene in options.DemoScenes)
                Console.Error.WriteLine(found.Contains(scene)
                    ? $"  {scene}: found"
                    : $"  {scene}: not in the project yet. Open it in Godot (signed in to YHDE) and the bots go there too.");
            Console.Error.WriteLine($"{live.Count} bots are in, {Math.Min(options.DemoEditors, live.Count)} of them editing. Join now: they stay {options.Duration / 60.0:0.#} minutes. Ctrl+C ends it early and removes their boxes.");
        }
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wallBefore = clock.Elapsed;
        metrics.Start(clock.Elapsed.TotalMilliseconds);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Duration), Interrupt.Token);
        }
        catch (OperationCanceledException) { }
        metrics.Stop(clock.Elapsed.TotalMilliseconds);
        process.Refresh();
        var pcCpu = 100.0 * (process.TotalProcessorTime - cpuBefore).TotalSeconds /
                    (clock.Elapsed - wallBefore).TotalSeconds / Environment.ProcessorCount;
        stop.Cancel();
        try { await Task.WhenAll(runs); } catch (OperationCanceledException) { }
        var dropped = live.Count(c => !c.Connected);
        await Task.WhenAll(live.Select(c => c.CloseAsync()));

        var report = metrics.Report(options, teams, teamSize, clients.Count, live.Count, connectErrors, dropped);
        return new Result(clients.Count, metrics.PresenceP95, metrics.CommitP95, connectErrors + dropped, pcCpu, report);
    }
}

sealed class Options
{
    public string Url = "ws://127.0.0.1:5000/ws";
    public string? Key;
    public Guid Project = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
    public List<Guid> Branches = [Guid.Parse("ffffffff-0000-0000-0000-000000000002")];
    public int Teams = 1;
    public int TeamSize = 5;
    public double PresenceHz = 15;
    public double OpsPerSec = 1;
    public int Warmup = 5;
    public int Duration = 20;
    public bool Insecure;
    public string? JsonOut;
    public bool Ramp;
    public int MaxTeams = 400;
    // Demo (--demo): bots with visible cursors in the given scenes; a few of them drag boxes.
    public string? DemoScene;
    public string[] DemoScenes = [];
    public double DemoX, DemoY, DemoRadius = 400;
    public int DemoEditors = 8;
    public bool Watch;
    public double DemoEditSeconds = 4;

    private static List<Guid> ParseBranches(string text) =>
        text.Split([',', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        bool presenceSet = false, opsSet = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--url": o.Url = Next(); break;
                case "--key": o.Key = Next(); break;
                case "--project": o.Project = Guid.Parse(Next()); break;
                case "--branches": o.Branches = ParseBranches(Next()); break;
                case "--branches-file": o.Branches = ParseBranches(File.ReadAllText(Next())); break;
                case "--teams": o.Teams = int.Parse(Next()); break;
                case "--team-size": o.TeamSize = int.Parse(Next()); break;
                case "--presence-hz": o.PresenceHz = double.Parse(Next(), CultureInfo.InvariantCulture); presenceSet = true; break;
                case "--ops-per-sec": o.OpsPerSec = double.Parse(Next(), CultureInfo.InvariantCulture); opsSet = true; break;
                case "--ramp": o.Ramp = true; break;
                case "--max-teams": o.MaxTeams = int.Parse(Next()); break;
                case "--warmup": o.Warmup = int.Parse(Next()); break;
                case "--duration": o.Duration = int.Parse(Next()); break;
                case "--insecure": o.Insecure = true; break;
                case "--json": o.JsonOut = Next(); break;
                case "--demo":
                    o.DemoScene = Next();
                    o.DemoScenes = o.DemoScene.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--demo-editors": o.DemoEditors = int.Parse(Next()); break;
                case "--watch": o.Watch = true; o.PresenceHz = 0; o.OpsPerSec = 0; presenceSet = opsSet = true; break;
                case "--demo-edit-seconds": o.DemoEditSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--area":
                    var a = Next().Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    (o.DemoX, o.DemoY, o.DemoRadius) = (a[0], a[1], a.Length > 2 ? a[2] : o.DemoRadius);
                    break;
                default:
                    Console.Error.WriteLine("""
                        YHDE load test
                          --url ws(s)://host/ws   server (default ws://127.0.0.1:5000/ws)
                          --key KEY               access key
                          --teams N               teams, each in its own branch (default 1)
                          --team-size N           editors per team (default 5)
                          --branches a,b,…        branch ids to spread teams over (default: the seeded branch)
                          --branches-file FILE    the same, read from a file (./yhde test-branches N > FILE)
                          --presence-hz F         presence updates per editor per second (default 15)
                          --ops-per-sec F         committed edits per editor per second (default 1; 0 = none)
                          --warmup S --duration S seconds (default 5 / 20)
                          --ramp                  step up the number of teams until it gets slow and print a table
                                                  (defaults to typical use: cursor 5/s, an edit every 5 s)
                          --max-teams N           stop the ramp here (default 400)
                          --insecure              accept any TLS certificate (local testing)
                          --json FILE             also write the report here
                          --demo a.tscn,b.tscn    visible bots for a recording, named Bot 1…N, for 30 minutes:
                                                  cursors wander smoothly and move between the scenes
                                                  (2D or 3D; open each in Godot once first)
                          --demo-editors N        how many of them own a box and drag it (default 8)
                          --watch                 one silent editor that prints, every second, the presence
                                                  it receives and how much of it carries a drag
                          --demo-edit-seconds S   pause between an editing bot's drags, roughly (default 4)
                          --area X,Y,R            where they wander in 2D (default 0,0,400); in 3D the
                                                  same area in metres / 100 around the origin
                        """);
                    return null;
            }
        }
        if (o.Ramp)
        {
            if (!presenceSet) o.PresenceHz = 5;
            if (!opsSet) o.OpsPerSec = 0.2;
            if (o.Duration == 20) o.Duration = 15;
        }
        if (o.DemoScene is not null)
        {
            if (!presenceSet) o.PresenceHz = 5;
            if (!opsSet) o.OpsPerSec = 0;
            if (o.Duration == 20) o.Duration = 1800;
        }
        return o;
    }
}

sealed class Metrics
{
    private volatile bool _on;
    private double _start;
    private double _end;
    private readonly ConcurrentQueue<double> _presence = new();
    private readonly ConcurrentQueue<double> _commit = new();
    private readonly ConcurrentQueue<double> _delivery = new();
    private long _presenceSent, _opsSent, _received, _bytesReceived, _rejected, _sendErrors;

    public bool On => _on;
    public void Start(double now) { _start = now; _on = true; }
    public void Stop(double now) { _end = now; _on = false; }

    public void Presence(double ms) { if (_on) _presence.Enqueue(ms); }
    public void Commit(double ms) { if (_on) _commit.Enqueue(ms); }
    public void Delivery(double ms) { if (_on) _delivery.Enqueue(ms); }
    public void PresenceSent() { if (_on) Interlocked.Increment(ref _presenceSent); }
    public void OpSent() { if (_on) Interlocked.Increment(ref _opsSent); }
    public void Received(int bytes)
    {
        if (!_on) return;
        Interlocked.Increment(ref _received);
        Interlocked.Add(ref _bytesReceived, bytes);
    }
    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void SendError() => Interlocked.Increment(ref _sendErrors);

    private static object Summary(IEnumerable<double> values)
    {
        var v = values.ToArray();
        Array.Sort(v);
        double P(double q) => v.Length == 0 ? 0 : v[Math.Min(v.Length - 1, (int)(q * v.Length))];
        return new
        {
            count = v.Length,
            p50_ms = Math.Round(P(0.50), 1),
            p95_ms = Math.Round(P(0.95), 1),
            p99_ms = Math.Round(P(0.99), 1),
            max_ms = Math.Round(v.Length == 0 ? 0 : v[^1], 1),
        };
    }

    private static double P95(IEnumerable<double> values)
    {
        var v = values.ToArray();
        if (v.Length == 0) return double.NaN;
        Array.Sort(v);
        return v[Math.Min(v.Length - 1, (int)(0.95 * v.Length))];
    }

    public double PresenceP95 => P95(_presence);
    public double CommitP95 => P95(_commit);

    public object Report(Options o, int teams, int teamSize, int editors, int connected, int connectErrors, int dropped)
    {
        var seconds = Math.Max(0.001, (_end - _start) / 1000.0);
        return new
        {
            editors,
            teams,
            team_size = teamSize,
            presence_hz = o.PresenceHz,
            ops_per_sec = o.OpsPerSec,
            connected,
            connect_errors = connectErrors,
            dropped,
            rejected = _rejected,
            send_errors = _sendErrors,
            presence_sent_per_s = Math.Round(_presenceSent / seconds),
            ops_sent_per_s = Math.Round(_opsSent / seconds, 1),
            messages_received_per_s = Math.Round(_received / seconds),
            mbit_received_per_s = Math.Round(_bytesReceived * 8 / seconds / 1e6, 2),
            presence_latency = Summary(_presence),
            commit_latency = Summary(_commit),
            edit_delivery_latency = Summary(_delivery),
        };
    }
}

sealed class SimClient(Options o, Guid branch, int team, int member, Stopwatch clock, Metrics metrics)
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<Guid, double> _pending = new();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Random _rng = new(team * 1000 + member);
    private readonly Guid[] _targets = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
    private Task? _receive;
    private long _lastSeq;

    public bool Connected => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync()
    {
        if (o.Key is not null) _ws.Options.SetRequestHeader("Authorization", "Bearer " + o.Key);
        if (o.Insecure) _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _ws.ConnectAsync(new Uri(o.Url), timeout.Token);
        _receive = Task.Run(ReceiveLoopAsync);
        await SendAsync(MessageType.Hello, Channel.System, new HelloPayload { ProtocolVersion = 1, Capabilities = ["ops", "presence"] });
        // Start at the head: this editor is already up to date.
        await SendAsync(MessageType.Subscribe, Channel.Ops, new SubscribePayload
        {
            ProjectId = o.Project.ToByteArray(),
            BranchId = branch.ToByteArray(),
            // Demo bots read the whole log to learn each scene's root node.
            LastAckedSeq = o.DemoScene is not null ? 0 : long.MaxValue / 2,
        });
        var done = await Task.WhenAny(_subscribed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (done != _subscribed.Task) throw new TimeoutException("no SyncState");
    }

    public async Task RunAsync(CancellationToken stop)
    {
        var hz = o.PresenceHz > 0 ? o.PresenceHz : 20;
        if (o.DemoScene is not null && Editing) hz = Math.Max(hz, DragHz);
        var period = TimeSpan.FromSeconds(1.0 / hz);
        await Task.Delay(TimeSpan.FromMilliseconds(_rng.NextDouble() * period.TotalMilliseconds), stop).ConfigureAwait(false);
        using var timer = new PeriodicTimer(period);
        var opChance = o.OpsPerSec / hz;
        var nextAck = 0.0;
        try
        {
            while (await timer.WaitForNextTickAsync(stop) && Connected)
            {
                var now = clock.Elapsed.TotalMilliseconds;
                if (o.Watch && now >= _watchPrintAt)
                {
                    _watchPrintAt = now + 1000;
                    var (n, d, sample) = (Interlocked.Exchange(ref _watchEntries, 0), Interlocked.Exchange(ref _watchDrags, 0), _watchSample);
                    _watchSample = "";
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  presence {n}/s, with a drag {d}/s{(sample.Length > 0 ? "   e.g. " + sample : "")}");
                }
                if (o.DemoScene is not null) await DemoTickAsync(now);
                if (o.PresenceHz > 0 && (_drag is not null || now >= _nextPresenceAt || o.DemoScene is null))
                {
                    _nextPresenceAt = now + 1000 / o.PresenceHz;
                    await SendPresenceAsync(now);
                }
                if (opChance > 0 && _rng.NextDouble() < opChance) await SendOpAsync(now);
                if (now >= nextAck)
                {
                    nextAck = now + 1000;
                    var seq = Interlocked.Read(ref _lastSeq);
                    if (seq > 0) await SendAsync(MessageType.Ack, Channel.Ops, new AckPayload { Seq = seq }, seqOrAck: seq);
                }
            }
        }
        catch (OperationCanceledException) { }
        // Demo bots delete the boxes they still have.
        foreach (var n in _mine.ToList()) await DeleteNodeAsync(n);
    }

    private Task SendPresenceAsync(double now)
    {
        metrics.PresenceSent();
        if (o.DemoScene is not null) return SendDemoPresenceAsync(now);
        var x = _rng.NextDouble() * 1920;
        var y = _rng.NextDouble() * 1080;
        // About the size of what the plugin sends: selection, cursor, view.
        var state = string.Create(CultureInfo.InvariantCulture,
            $"{{\"t\":{now:F3},\"sel\":[\"{_targets[0]}\",\"{_targets[1]}\"],\"ptr\":[{x:F2},{y:F2}],\"view\":{{\"z\":1.25,\"o\":[{x / 3:F2},{y / 3:F2}]}},\"drags\":[],\"screen\":\"2D\"}}");
        return SendAsync(MessageType.PresenceUpdate, Channel.Presence, new PresenceUpdatePayload
        {
            DisplayName = $"Load {team}-{member}",
            Scene = "res://levels/main.tscn",
            Tool = "2D",
            StateJson = state,
        });
    }

    // Each bot's cursor follows its own loop around the area. "cur" is a 2D
    // world position, as the add-on sends it.
    private readonly double _p1 = new Random(team * 7919 + member).NextDouble() * Math.Tau, _p2 = new Random(member * 104729 + team).NextDouble() * Math.Tau;
    private readonly double _speed = 0.15 + new Random(member * 31 + team).NextDouble() * 0.25;

    private Task SendDemoPresenceAsync(double now)
    {
        var (x, y) = Loop(now);
        var scene = _scene.Length > 0 ? _scene : o.DemoScenes[0];
        var is3D = _roots.TryGetValue(scene, out var known) ? known.Is3D : scene.Contains("3d", StringComparison.OrdinalIgnoreCase);
        var focus = _focus is { } f && now < _focusUntil && f.Scene == scene ? f : null;
        var sel = focus is null ? "" : $"\"{WireText(focus.Id)}\"";
        var dragging = _drag is { } d && d.Node.Scene == scene ? d : null;
        var (gx, gy, gz) = dragging is null ? (0.0, 0.0, 0.0) : dragging.At(now);
        string state;
        if (is3D)
        {
            // The loop on the ground plane, in metres; on the box while editing it.
            var (cx, cy, cz) = dragging is not null ? (gx, gy + 0.5, gz)
                : focus is null ? ((x - o.DemoX) / 100, 0.0, (y - o.DemoY) / 100) : (focus.X, focus.Y + 0.5, focus.Z);
            var drag = dragging is null ? "" : string.Create(CultureInfo.InvariantCulture,
                $",\"drag\":[[\"{WireText(dragging.Node.Id)}\",\"t3\",1,0,0,0,1,0,0,0,1,{gx:F3},{gy:F3},{gz:F3}]]");
            state = string.Create(CultureInfo.InvariantCulture, $"{{\"sel\":[{sel}],\"cur\":[{cx:F3},{cy:F3},{cz:F3}]{drag}}}");
        }
        else
        {
            var (cx, cy) = dragging is not null ? (gx + BoxSize / 2, gy + BoxSize / 2)
                : focus is null ? (x, y) : (focus.X + BoxSize / 2, focus.Y + BoxSize / 2);
            var drag = dragging is null ? "" : string.Create(CultureInfo.InvariantCulture,
                $",\"drag\":[[\"{WireText(dragging.Node.Id)}\",\"t2\",1,0,0,1,{gx:F1},{gy:F1},{BoxSize:F1},{BoxSize:F1}]]");
            state = string.Create(CultureInfo.InvariantCulture,
                $"{{\"sel\":[{sel}],\"cur\":[{cx:F1},{cy:F1}],\"view\":[{o.DemoX:F1},{o.DemoY:F1},1.0]{drag}}}");
        }
        return SendAsync(MessageType.PresenceUpdate, Channel.Presence, new PresenceUpdatePayload
        {
            DisplayName = $"Bot {BotNumber}",
            Scene = scene,
            Tool = is3D ? "3D" : "2D",
            StateJson = state,
        });
    }

    // Where this bot's cursor is on its loop, in 2D scene units.
    private (double X, double Y) Loop(double now)
    {
        var t = now / 1000 * _speed;
        var r = o.DemoRadius;
        return (o.DemoX + r * (0.65 * Math.Sin(t + _p1) + 0.35 * Math.Sin(2.3 * t + _p2)),
                o.DemoY + r * 0.6 * (0.65 * Math.Cos(1.3 * t + _p2) + 0.35 * Math.Sin(1.7 * t + _p1)));
    }

    // Demo edits, in the add-on's op format (scene_document.cpp, variant_codec.cpp).
    // Each editing bot keeps one box under its scene's root and drags it around.
    private const double BoxSize = 64;
    private static readonly double[][] Palette =
        [[0.949, 0.306, 0.118], [0.102, 0.737, 0.996], [0.039, 0.812, 0.514], [0.635, 0.349, 1.0], [1.0, 0.78, 0.0]];

    private sealed class DemoNode
    {
        public byte[] Id = [];
        public string Scene = "";
        public bool Is3D;
        public double X, Y, Z;
    }

    private readonly ConcurrentDictionary<string, (string Root, bool Is3D)> _roots = new();
    private readonly ConcurrentDictionary<string, bool> _seen = new();
    private readonly List<DemoNode> _mine = [];
    private string _scene = "";
    private double _nextSceneAt, _nextEditAt;
    private DemoNode? _focus;
    private double _focusUntil;

    // A drag in progress. Presence carries the box's position while it lasts;
    // the move is committed as one ChangeProperty at the end.
    private sealed class Drag
    {
        public required DemoNode Node;
        public double FromX, FromY, FromZ, ToX, ToY, ToZ, BendX, BendZ, Start, End;

        public (double, double, double) At(double now)
        {
            var t = Math.Clamp((now - Start) / (End - Start), 0, 1);
            t = t * t * (3 - 2 * t); // smoothstep
            // Quadratic curve through an offset midpoint.
            var u = 1 - t;
            double Curve(double a, double b, double bend) => u * u * a + 2 * u * t * ((a + b) / 2 + bend) + t * t * b;
            return Node.Is3D
                ? (Curve(FromX, ToX, BendX), FromY, Curve(FromZ, ToZ, BendZ))
                : (Curve(FromX, ToX, BendX), Curve(FromY, ToY, BendZ), 0);
        }
    }

    private Drag? _drag;
    private double _watchPrintAt;
    private int _watchEntries, _watchDrags;
    private string _watchSample = "";
    private const double DragHz = 20;
    private double _nextPresenceAt;
    private int _made;

    private int BotNumber => team * o.TeamSize + member + 1;
    private bool Editing => BotNumber <= o.DemoEditors;

    public HashSet<string> KnownScenes() => [.. _roots.Keys];

    // The add-on's ids (core/uuid.cpp, scene_document.cpp): a scene's root is
    // the name-based id of "node:." in the namespace of "scene:<path>", unless
    // the log says otherwise.
    private static readonly byte[] YhdeNamespace = Convert.FromHexString("5f0e6c1a9a554f3e8b0b79d2a1c3e4f5");

    private static byte[] FromName(byte[] ns, string name)
    {
        var digest = SHA1.HashData([.. ns, .. Encoding.UTF8.GetBytes(name)]);
        var id = digest[..16];
        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return id;
    }

    public static byte[] DerivedRoot(string scene) => FromName(FromName(YhdeNamespace, "scene:" + scene), "node:.");

    // The add-on's text for an id in its own byte order (core/uuid.cpp).
    private static string Hex(byte[] id)
    {
        var h = Convert.ToHexString(id).ToLowerInvariant();
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
    }

    // The add-on's text for an id as it travels on the wire (TargetId):
    // Uuid::to_wire swaps the first fields, like .NET's Guid.
    private static string WireText(byte[] wire) => new Guid(wire).ToString();

    // A scene's root is the node created with no parent.
    private void Learn(CommittedOpPayload op)
    {
        if (o.DemoScene is null) return;
        try
        {
            using var doc = JsonDocument.Parse(op.PayloadJson);
            var r = doc.RootElement;
            if (!r.TryGetProperty("s", out var s) || s.GetString() is not { } scene || !o.DemoScenes.Contains(scene)) return;
            if (op.Type == "CreateNode" && r.TryGetProperty("p", out var p) && p.GetString() is "")
            {
                // A root made node by node: its own id and class.
                var cls = r.TryGetProperty("c", out var c) ? c.GetString() ?? "" : "";
                _roots[scene] = (WireText(op.TargetId), cls.EndsWith("3D", StringComparison.Ordinal));
            }
            else if (_seen.TryAdd(scene, true) && !_roots.ContainsKey(scene))
            {
                // A scene made as a file: the derived root; 2D or 3D by its name.
                _roots[scene] = (Hex(DerivedRoot(scene)), scene.Contains("3d", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (JsonException) { }
    }

    private async Task DemoTickAsync(double now)
    {
        if (now >= _nextSceneAt)
        {
            var found = o.DemoScenes.Where(_roots.ContainsKey).ToArray();
            if (found.Length > 0)
            {
                var home = o.DemoScenes[(BotNumber - 1) % o.DemoScenes.Length];
                _scene = Editing && found.Contains(home) ? home : found[_rng.Next(found.Length)];
            }
            _nextSceneAt = now + 15000 + _rng.NextDouble() * 25000;
        }
        if (_drag is { } d && now >= d.End)
        {
            _drag = null;
            await FinishDragAsync(d, now);
        }
        if (_drag is not null || !Editing || !_roots.ContainsKey(_scene) || now < _nextEditAt) return;
        _nextEditAt = now + o.DemoEditSeconds * 1000 * (0.3 + _rng.NextDouble() * 0.5);
        var mine = _mine.FirstOrDefault(n => n.Scene == _scene);
        if (mine is null)
        {
            if (_mine.Count == 0) await CreateNodeAsync(now);
        }
        else if (_rng.NextDouble() < 0.05) await DeleteNodeAsync(mine); // now and then: gone, and made again later
        else await MoveNodeAsync(mine, now);
    }

    private Task SubmitAsync(string type, byte[] target, string payload)
    {
        var reference = Guid.NewGuid().ToByteArray();
        return SendAsync(MessageType.SubmitOp, Channel.Ops, new SubmitOpPayload
        {
            OpId = Guid.NewGuid().ToByteArray(),
            Type = type,
            TargetId = target,
            PayloadJson = Encoding.UTF8.GetBytes(payload),
            ClientOpRef = reference,
            ParentSeq = Interlocked.Read(ref _lastSeq),
        }, reference);
    }

    private static string Position(DemoNode n) => n.Is3D
        ? string.Create(CultureInfo.InvariantCulture, $"[\"v3\",{n.X:F3},{n.Y:F3},{n.Z:F3}]")
        : string.Create(CultureInfo.InvariantCulture, $"[\"v2\",{n.X:F1},{n.Y:F1}]");

    private async Task CreateNodeAsync(double now)
    {
        var (root, is3D) = _roots[_scene];
        var (x, y) = Loop(now);
        var n = new DemoNode { Id = RandomNumberGenerator.GetBytes(16), Scene = _scene, Is3D = is3D };
        if (is3D) (n.X, n.Y, n.Z) = ((x - o.DemoX) / 100, 0.5, (y - o.DemoY) / 100);
        else (n.X, n.Y) = (x - BoxSize / 2, y - BoxSize / 2);
        var c = Palette[(BotNumber + _made) % Palette.Length];
        var name = $"Bot{BotNumber}Box{++_made}";
        var props = is3D
            ? $"[\"position\",{Position(n)}]"
            : string.Create(CultureInfo.InvariantCulture,
                $"[\"position\",{Position(n)},\"size\",[\"v2\",{BoxSize:F1},{BoxSize:F1}],\"color\",[\"c\",{c[0]:F3},{c[1]:F3},{c[2]:F3},1.0]]");
        var payload = $"{{\"s\":\"{_scene}\",\"p\":\"{root}\",\"n\":\"{name}\",\"c\":\"{(is3D ? "CSGBox3D" : "ColorRect")}\",\"props\":{props}}}";
        _mine.Add(n);
        (_focus, _focusUntil) = (n, now + 1500);
        await SubmitAsync("CreateNode", n.Id, payload);
    }

    private Task MoveNodeAsync(DemoNode n, double now)
    {
        var d = new Drag { Node = n, FromX = n.X, FromY = n.Y, FromZ = n.Z, Start = now, End = now + 2500 + _rng.NextDouble() * 1500 };
        double Spread(double r) => (_rng.NextDouble() * 2 - 1) * r;
        if (n.Is3D)
        {
            var r = o.DemoRadius / 100;
            (d.ToX, d.ToY, d.ToZ) = (Spread(r), n.Y, Spread(r * 0.6));
            (d.BendX, d.BendZ) = (Spread(r * 0.3), Spread(r * 0.3));
        }
        else
        {
            (d.ToX, d.ToY, d.ToZ) = (o.DemoX + Spread(o.DemoRadius) - BoxSize / 2, o.DemoY + Spread(o.DemoRadius * 0.6) - BoxSize / 2, 0);
            (d.BendX, d.BendZ) = (Spread(o.DemoRadius * 0.3), Spread(o.DemoRadius * 0.3));
        }
        _drag = d;
        (_focus, _focusUntil) = (n, d.End + 600);
        return Task.CompletedTask;
    }

    private async Task FinishDragAsync(Drag d, double now)
    {
        var n = d.Node;
        if (!_mine.Contains(n)) return;
        var old = Position(n);
        (n.X, n.Y, n.Z) = (d.ToX, d.ToY, d.ToZ);
        (_focus, _focusUntil) = (n, now + 600);
        await SubmitAsync("ChangeProperty", n.Id, $"{{\"s\":\"{n.Scene}\",\"k\":\"position\",\"v\":{Position(n)},\"o\":{old}}}");
    }

    private async Task DeleteNodeAsync(DemoNode n)
    {
        if (_drag?.Node == n) _drag = null;
        _mine.Remove(n);
        if (_focus == n) _focus = null;
        await SubmitAsync("DeleteNode", n.Id, $"{{\"s\":\"{n.Scene}\"}}");
    }

    private Task SendOpAsync(double now)
    {
        metrics.OpSent();
        var reference = Guid.NewGuid();
        _pending[reference] = now;
        var target = _targets[_rng.Next(_targets.Length)];
        var payload = string.Create(CultureInfo.InvariantCulture,
            $"{{\"s\":\"res://levels/main.tscn\",\"k\":\"position\",\"v\":[\"Vector2\",{_rng.NextDouble() * 500:F3},{_rng.NextDouble() * 500:F3}],\"o\":[\"Vector2\",0,0],\"lt\":{now:F3}}}");
        return SendAsync(MessageType.SubmitOp, Channel.Ops, new SubmitOpPayload
        {
            OpId = Guid.NewGuid().ToByteArray(),
            Type = "ChangeProperty",
            TargetId = target.ToByteArray(),
            PayloadJson = Encoding.UTF8.GetBytes(payload),
            ClientOpRef = reference.ToByteArray(),
            ParentSeq = Interlocked.Read(ref _lastSeq),
        }, reference.ToByteArray());
    }

    private async Task SendAsync<T>(MessageType type, Channel channel, T payload, byte[]? reference = null, long seqOrAck = 0)
    {
        var frame = new Frame
        {
            MsgType = type,
            Channel = channel,
            ClientOpRef = reference ?? [],
            SeqOrAck = seqOrAck,
            Payload = Codec.EncodePayload(payload),
        };
        var wire = Codec.Encode(frame);
        await _send.WaitAsync();
        try
        {
            if (_ws.State == WebSocketState.Open) await _ws.SendAsync(wire, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (Exception)
        {
            metrics.SendError();
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                if (message.Length < 5) continue;
                metrics.Received((int)message.Length);
                Handle(message.GetBuffer().AsMemory(4, (int)message.Length - 4));
            }
        }
        catch (Exception)
        {
            // Closed or reset; Connected reports it.
        }
    }

    private void Handle(ReadOnlyMemory<byte> bytes)
    {
        var frame = Codec.Decode(bytes);
        var now = clock.Elapsed.TotalMilliseconds;
        switch (frame.MsgType)
        {
            case MessageType.SyncState:
            {
                var s = Codec.DecodePayload<SyncStatePayload>(frame.Payload);
                foreach (var tail in s.TailOps) Learn(tail);
                if (s.HeadSeq > Interlocked.Read(ref _lastSeq)) Interlocked.Exchange(ref _lastSeq, s.HeadSeq);
                if (!s.HasMore) _subscribed.TrySetResult();
                break;
            }
            case MessageType.OpCommitted:
            {
                var op = Codec.DecodePayload<CommittedOpPayload>(frame.Payload);
                Learn(op);
                if (op.Seq > Interlocked.Read(ref _lastSeq)) Interlocked.Exchange(ref _lastSeq, op.Seq);
                var reference = new Guid(op.ClientOpRef);
                if (_pending.TryRemove(reference, out var sent))
                {
                    metrics.Commit(now - sent);
                }
                else if (Stamp(op.PayloadJson, "\"lt\":"u8) is { } lt)
                {
                    metrics.Delivery(now - lt);
                }
                break;
            }
            case MessageType.PresenceState:
            {
                var p = Codec.DecodePayload<PresenceStatePayload>(frame.Payload);
                if (o.Watch)
                {
                    foreach (var e in p.Entries)
                    {
                        Interlocked.Increment(ref _watchEntries);
                        if (!e.StateJson.Contains("\"drag\"")) continue;
                        Interlocked.Increment(ref _watchDrags);
                        var at = e.StateJson.IndexOf("\"drag\"", StringComparison.Ordinal);
                        _watchSample = $"{e.DisplayName} in {e.Scene} ({e.Tool}): {e.StateJson[at..Math.Min(e.StateJson.Length, at + 110)]}";
                    }
                }
                foreach (var e in p.Entries)
                {
                    if (Stamp(Encoding.UTF8.GetBytes(e.StateJson), "\"t\":"u8) is { } t) metrics.Presence(now - t);
                }
                break;
            }
            case MessageType.OpRejected:
                metrics.Rejected();
                break;
        }
    }

    private static double? Stamp(ReadOnlySpan<byte> json, ReadOnlySpan<byte> key)
    {
        var at = json.IndexOf(key);
        if (at < 0) return null;
        var rest = json[(at + key.Length)..];
        var end = rest.IndexOfAny((byte)',', (byte)'}');
        if (end < 0) return null;
        return double.TryParse(Encoding.ASCII.GetString(rest[..end]), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
        }
        catch (Exception) { }
        _ws.Dispose();
    }
}
