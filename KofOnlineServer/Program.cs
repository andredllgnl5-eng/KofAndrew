using System.Threading;
using System.Diagnostics;
using System.Text.Json;

using var singleInstance = new Mutex(true, @"Local\KOFFCommunityServer", out var isFirstInstance);
if (!isFirstInstance)
{
    Console.Title = "KOFF Community Server";
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("============================================================");
    Console.WriteLine("  O SERVIDOR KOFF JA ESTA EM EXECUCAO");
    Console.WriteLine("============================================================");
    Console.ResetColor();
    Console.WriteLine("  Use a janela do servidor que ja estava aberta.");
    Console.WriteLine("  Nao e necessario iniciar outro servidor.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5088");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.None);
builder.Services.AddSingleton<ArenaState>();
builder.Services.AddSingleton<RoomHubState>();

var app = builder.Build();

var parentPid = ReadParentPid(args);
if (parentPid is int monitoredParent)
{
    _ = Task.Run(async () =>
    {
        while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await Task.Delay(1000);
            try
            {
                using var parent = Process.GetProcessById(monitoredParent);
                if (!parent.HasExited) continue;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine("  Terminal fechado. Encerrando o servidor KOFF...");
            Console.ResetColor();
            Environment.Exit(0);
        }
    });
}

app.MapGet("/api/status", () => Results.Ok(new { online = true, name = "KOFF Community Server", version = "0.1.0" }));
app.MapGet("/api/arena", (ArenaState arena) => Results.Ok(arena.Snapshot()));

app.MapPost("/api/players/join", (JoinRequest request, ArenaState arena) =>
{
    var name = (request.Nickname ?? "").Trim();
    if (name.Length is < 3 or > 18) return Results.BadRequest(new { error = "O apelido deve ter entre 3 e 18 caracteres." });
    return Results.Ok(arena.Join(name));
});

app.MapPost("/api/queue/join", (PlayerRequest request, ArenaState arena) =>
    arena.JoinQueue(request.PlayerId) ? Results.Ok(arena.Snapshot()) : Results.NotFound(new { error = "Jogador não encontrado." }));

app.MapPost("/api/queue/leave", (PlayerRequest request, ArenaState arena) =>
    arena.LeaveQueue(request.PlayerId) ? Results.Ok(arena.Snapshot()) : Results.NotFound(new { error = "Jogador não encontrado." }));

app.MapPost("/api/players/ready", (PlayerRequest request, ArenaState arena) =>
{
    if (!arena.ToggleReady(request.PlayerId, out _))
        return Results.BadRequest(new { error = "Apenas Player 1 ou Player 2 pode ficar pronto." });

    if (arena.BothReady && !arena.MatchRunning) arena.MatchStarted();
    return Results.Ok(arena.Snapshot());
});

app.MapPost("/api/players/leave", (PlayerRequest request, ArenaState arena) =>
    arena.Leave(request.PlayerId) ? Results.Ok(arena.Snapshot()) : Results.NotFound(new { error = "Jogador não encontrado." }));

app.MapPost("/api/admin/advance", (ArenaState arena) => Results.Ok(arena.AdvanceQueue()));

// API v2: salas 1x1, fila, espectadores, chat e migracao automatica do dono.
app.MapGet("/api/rooms", (RoomHubState rooms) => Results.Ok(rooms.ListRooms()));
app.MapGet("/api/rooms/{roomId:guid}", (Guid roomId, RoomHubState rooms) =>
    rooms.TrySnapshot(roomId, out var snapshot) ? Results.Ok(snapshot) : Results.NotFound(new { error = "Sala não encontrada." }));
app.MapPost("/api/rooms", (CreateRoomRequest request, RoomHubState rooms) =>
{
    try { return Results.Ok(rooms.Create(request)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/rooms/{roomId:guid}/join", (Guid roomId, JoinRoomRequest request, RoomHubState rooms) =>
{
    try { return Results.Ok(rooms.Join(roomId, request)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/rooms/{roomId:guid}/heartbeat", (Guid roomId, RoomPlayerRequest request, RoomHubState rooms) =>
    rooms.Heartbeat(roomId, request.PlayerId) ? Results.Ok() : Results.NotFound(new { error = "Participante não encontrado." }));
app.MapPost("/api/rooms/{roomId:guid}/leave", (Guid roomId, RoomPlayerRequest request, RoomHubState rooms) =>
    rooms.Leave(roomId, request.PlayerId) ? Results.Ok() : Results.NotFound(new { error = "Participante não encontrado." }));
app.MapPost("/api/rooms/{roomId:guid}/queue", (Guid roomId, QueueRoomRequest request, RoomHubState rooms) =>
{
    try { return Results.Ok(rooms.SetQueue(roomId, request.PlayerId, request.Join)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/rooms/{roomId:guid}/start", (Guid roomId, RoomPlayerRequest request, RoomHubState rooms) =>
{
    try { return Results.Ok(rooms.Start(roomId, request.PlayerId)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
});
app.MapPost("/api/rooms/{roomId:guid}/result", (Guid roomId, MatchResultRequest request, RoomHubState rooms) =>
{
    try { return Results.Ok(rooms.ReportResult(roomId, request)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/rooms/{roomId:guid}/chat", (Guid roomId, long? after, RoomHubState rooms) =>
    rooms.TryMessages(roomId, after ?? 0, out var messages) ? Results.Ok(messages) : Results.NotFound(new { error = "Sala não encontrada." }));
app.MapPost("/api/rooms/{roomId:guid}/chat", (Guid roomId, SendChatRequest request, RoomHubState rooms) =>
{
    try { return Results.Ok(rooms.SendMessage(roomId, request)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

try
{
    await app.StartAsync();
    Console.Title = "KOFF Community Server";
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("============================================================");
    Console.WriteLine("                 KOFF COMMUNITY SERVER");
    Console.WriteLine("============================================================");
    Console.ResetColor();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("  STATUS:  SERVIDOR ONLINE");
    Console.ResetColor();
    Console.WriteLine("  LOCAL:   http://127.0.0.1:5088");
    Console.WriteLine("  REDE:    http://0.0.0.0:5088");
    Console.WriteLine("  ARENA:   Arena KOFF #01");
    Console.WriteLine();
    Console.WriteLine("  Abra o launcher e clique em JOGAR ONLINE.");
    Console.WriteLine("  Nao feche esta janela enquanto o servidor estiver em uso.");
    Console.WriteLine("============================================================");
    Console.WriteLine();
    await app.WaitForShutdownAsync();
}
catch (IOException)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("============================================================");
    Console.WriteLine("  A PORTA 5088 JA ESTA SENDO USADA");
    Console.WriteLine("============================================================");
    Console.ResetColor();
    Console.WriteLine("  Provavelmente outro servidor KOFF ja esta aberto.");
    Console.WriteLine("  Feche o servidor antigo antes de iniciar uma nova versao.");
}

static int? ReadParentPid(string[] arguments)
{
    for (var i = 0; i < arguments.Length - 1; i++)
        if (arguments[i].Equals("--parent-pid", StringComparison.OrdinalIgnoreCase) && int.TryParse(arguments[i + 1], out var pid))
            return pid;
    return null;
}

record JoinRequest(string? Nickname);
record PlayerRequest(Guid PlayerId);

sealed class ArenaState
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Player> _players = new();
    private readonly List<Guid> _queue = new();
    private Guid? _playerOne;
    private Guid? _playerTwo;
    private readonly HashSet<Guid> _ready = new();
    private bool _matchRunning;
    private Guid? _matchId;
    private string? _launchError;

    public bool BothReady
    {
        get { lock (_sync) return _playerOne is Guid p1 && _playerTwo is Guid p2 && _ready.Contains(p1) && _ready.Contains(p2); }
    }

    public bool MatchRunning
    {
        get { lock (_sync) return _matchRunning; }
    }

    public object Join(string nickname)
    {
        lock (_sync)
        {
            var player = new Player(Guid.NewGuid(), nickname, DateTimeOffset.UtcNow);
            _players[player.Id] = player;
            return new { playerId = player.Id, arena = SnapshotUnsafe() };
        }
    }

    public bool JoinQueue(Guid id)
    {
        lock (_sync)
        {
            if (!_players.ContainsKey(id)) return false;
            if (_playerOne == id || _playerTwo == id || _queue.Contains(id)) return true;
            if (_playerOne is null) _playerOne = id;
            else if (_playerTwo is null) _playerTwo = id;
            else _queue.Add(id);
            return true;
        }
    }

    public bool LeaveQueue(Guid id)
    {
        lock (_sync)
        {
            if (!_players.ContainsKey(id)) return false;
            _queue.Remove(id);
            _ready.Remove(id);
            if (_playerOne == id) _playerOne = TakeNext();
            if (_playerTwo == id) _playerTwo = TakeNext();
            return true;
        }
    }

    public bool Leave(Guid id)
    {
        lock (_sync)
        {
            if (!_players.Remove(id)) return false;
            _queue.Remove(id);
            _ready.Remove(id);
            if (_playerOne == id) _playerOne = TakeNext();
            if (_playerTwo == id) _playerTwo = TakeNext();
            return true;
        }
    }

    public object AdvanceQueue()
    {
        lock (_sync)
        {
            if (_playerTwo is Guid oldPlayerTwo && _players.ContainsKey(oldPlayerTwo)) _queue.Add(oldPlayerTwo);
            _ready.Clear();
            _playerTwo = TakeNext();
            return SnapshotUnsafe();
        }
    }

    public bool ToggleReady(Guid id, out object snapshot)
    {
        lock (_sync)
        {
            if (_playerOne != id && _playerTwo != id)
            {
                snapshot = SnapshotUnsafe();
                return false;
            }

            if (_matchRunning)
            {
                snapshot = SnapshotUnsafe();
                return true;
            }

            if (!_ready.Add(id)) _ready.Remove(id);
            _launchError = null;
            snapshot = SnapshotUnsafe();
            return true;
        }
    }

    public void MatchStarted()
    {
        lock (_sync) { _matchRunning = true; _matchId = Guid.NewGuid(); _launchError = null; }
    }

    public void MatchStopped()
    {
        lock (_sync) { _matchRunning = false; _matchId = null; _ready.Clear(); }
    }

    public void SetLaunchError(string error)
    {
        lock (_sync) { _launchError = error; _ready.Clear(); }
    }

    public object Snapshot()
    {
        lock (_sync) return SnapshotUnsafe();
    }

    private Guid? TakeNext()
    {
        if (_queue.Count == 0) return null;
        var next = _queue[0];
        _queue.RemoveAt(0);
        return next;
    }

    private object SnapshotUnsafe() => new
    {
        roomName = "Arena KOFF #01",
        playerOne = GetPlayer(_playerOne),
        playerTwo = GetPlayer(_playerTwo),
        queue = _queue.Select((id, index) => new { position = index + 1, player = GetPlayer(id) }).ToArray(),
        spectatorCount = Math.Max(0, _players.Count - _queue.Count - (_playerOne is null ? 0 : 1) - (_playerTwo is null ? 0 : 1)),
        rule = "Ganhador permanece",
        matchStatus = _matchRunning
            ? "running"
            : _playerOne is not null && _playerTwo is not null && _ready.Contains(_playerOne.Value) && _ready.Contains(_playerTwo.Value)
                ? "ready"
                : "waiting",
        matchId = _matchId,
        hostAddress = "26.152.187.43",
        netplayPort = 7500,
        launchError = _launchError
    };

    private object? GetPlayer(Guid? id) => id is Guid value && _players.TryGetValue(value, out var player)
        ? new { id = player.Id, nickname = player.Nickname, ready = _ready.Contains(player.Id) }
        : null;
}

record Player(Guid Id, string Nickname, DateTimeOffset JoinedAt);
