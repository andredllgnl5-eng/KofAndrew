using System.Collections.Concurrent;

record CreateRoomRequest(string? RoomName, string? Nickname, int SpectatorCapacity = 4);
record JoinRoomRequest(string? Nickname, bool JoinQueue = false);
record RoomPlayerRequest(Guid PlayerId);
record QueueRoomRequest(Guid PlayerId, bool Join);
record MatchResultRequest(Guid ReporterId, Guid WinnerId, Guid LoserId);
record SendChatRequest(Guid PlayerId, string? Message);

sealed class RoomHubState : IDisposable
{
    private static readonly TimeSpan ParticipantTimeout = TimeSpan.FromSeconds(12);
    private readonly ConcurrentDictionary<Guid, RoomState> _rooms = new();
    private readonly Timer _cleanupTimer;

    public RoomHubState() => _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

    public object Create(CreateRoomRequest request)
    {
        var nickname = NormalizeNickname(request.Nickname);
        var roomName = (request.RoomName ?? "").Trim();
        if (roomName.Length is < 3 or > 28) throw new ArgumentException("O nome da sala deve ter entre 3 e 28 caracteres.");
        if (request.SpectatorCapacity is < 0 or > 4) throw new ArgumentException("A sala aceita de 0 a 4 espectadores.");

        var room = new RoomState(Guid.NewGuid(), roomName, request.SpectatorCapacity);
        var owner = room.Add(nickname, true);
        _rooms[room.Id] = room;
        return new { roomId = room.Id, playerId = owner.Id, room = room.Snapshot() };
    }

    public object Join(Guid roomId, JoinRoomRequest request)
    {
        var room = GetRoom(roomId);
        lock (room.Sync)
        {
            if (room.Participants.Count >= 2 + room.SpectatorCapacity) throw new InvalidOperationException("A sala está lotada.");
            var participant = room.Add(NormalizeNickname(request.Nickname), false);
            if (request.JoinQueue) room.Enqueue(participant.Id);
            return new { playerId = participant.Id, room = room.SnapshotUnsafe() };
        }
    }

    public object[] ListRooms() => _rooms.Values
        .Select(room => room.Summary())
        .OrderBy(room => room.Name)
        .Cast<object>()
        .ToArray();

    public bool TrySnapshot(Guid roomId, out object snapshot)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) { snapshot = new { }; return false; }
        snapshot = room.Snapshot();
        return true;
    }

    public bool Heartbeat(Guid roomId, Guid playerId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return false;
        lock (room.Sync)
        {
            if (!room.Participants.TryGetValue(playerId, out var player)) return false;
            player.LastSeen = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public bool Leave(Guid roomId, Guid playerId)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return false;
        bool removed;
        lock (room.Sync) removed = room.Remove(playerId, "saiu da sala");
        RemoveRoomIfEmpty(room);
        return removed;
    }

    public object SetQueue(Guid roomId, Guid playerId, bool join)
    {
        var room = GetRoom(roomId);
        lock (room.Sync)
        {
            if (!room.Participants.ContainsKey(playerId)) throw new KeyNotFoundException("Participante não encontrado.");
            if (room.MatchRunning && (room.PlayerOne == playerId || room.PlayerTwo == playerId))
                throw new InvalidOperationException("Não é possível sair da fila durante uma luta.");
            if (join) room.Enqueue(playerId); else room.Dequeue(playerId);
            room.FillFighterSlots();
            return room.SnapshotUnsafe();
        }
    }

    public object Start(Guid roomId, Guid ownerId)
    {
        var room = GetRoom(roomId);
        lock (room.Sync)
        {
            if (room.OwnerId != ownerId) throw new UnauthorizedAccessException("Somente o dono da sala pode iniciar.");
            room.FillFighterSlots();
            if (room.PlayerOne is null || room.PlayerTwo is null)
                throw new InvalidOperationException("É necessário o dono e pelo menos mais 1 jogador na fila.");
            if (room.MatchRunning) return room.SnapshotUnsafe();
            room.MatchRunning = true;
            room.MatchId = Guid.NewGuid();
            room.AddSystemMessage("Partida iniciada pelo dono da sala.");
            return room.SnapshotUnsafe();
        }
    }

    public object ReportResult(Guid roomId, MatchResultRequest request)
    {
        var room = GetRoom(roomId);
        lock (room.Sync)
        {
            if (request.ReporterId != room.PlayerOne && request.ReporterId != room.PlayerTwo)
                throw new InvalidOperationException("Somente os jogadores podem registrar o resultado.");
            if (!room.MatchRunning || request.WinnerId == request.LoserId ||
                !new[] { room.PlayerOne, room.PlayerTwo }.Contains(request.WinnerId) ||
                !new[] { room.PlayerOne, room.PlayerTwo }.Contains(request.LoserId))
                throw new InvalidOperationException("Resultado inválido para a partida atual.");

            room.MatchRunning = false;
            room.MatchId = null;
            room.PlayerOne = request.WinnerId;
            room.PlayerTwo = null;
            room.Dequeue(request.WinnerId);
            room.Dequeue(request.LoserId);
            room.Queue.Add(request.LoserId); // perdeu: final da fila
            room.FillFighterSlots();          // ganhou: permanece
            room.AddSystemMessage($"{room.NameOf(request.WinnerId)} venceu e permanece. {room.NameOf(request.LoserId)} foi para o fim da fila.");
            return room.SnapshotUnsafe();
        }
    }

    public object SendMessage(Guid roomId, SendChatRequest request)
    {
        var room = GetRoom(roomId);
        lock (room.Sync)
        {
            if (!room.Participants.TryGetValue(request.PlayerId, out var player)) throw new KeyNotFoundException("Participante não encontrado.");
            var text = (request.Message ?? "").Trim();
            if (text.Length is < 1 or > 180) throw new ArgumentException("A mensagem deve ter entre 1 e 180 caracteres.");
            player.LastSeen = DateTimeOffset.UtcNow;
            return room.AddMessage(player.Id, player.Nickname, text, false);
        }
    }

    public bool TryMessages(Guid roomId, long after, out object messages)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) { messages = Array.Empty<object>(); return false; }
        lock (room.Sync) messages = room.Messages.Where(message => message.Sequence > after).ToArray();
        return true;
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - ParticipantTimeout;
        foreach (var room in _rooms.Values)
        {
            lock (room.Sync)
            {
                foreach (var id in room.Participants.Values.Where(player => player.LastSeen < cutoff).Select(player => player.Id).ToArray())
                    room.Remove(id, "perdeu a conexão");
            }
            RemoveRoomIfEmpty(room);
        }
    }

    private void RemoveRoomIfEmpty(RoomState room)
    {
        lock (room.Sync) if (room.Participants.Count == 0) _rooms.TryRemove(room.Id, out _);
    }

    private RoomState GetRoom(Guid roomId) => _rooms.TryGetValue(roomId, out var room)
        ? room
        : throw new KeyNotFoundException("Sala não encontrada.");

    private static string NormalizeNickname(string? nickname)
    {
        var value = (nickname ?? "").Trim();
        if (value.Length is < 3 or > 18) throw new ArgumentException("O apelido deve ter entre 3 e 18 caracteres.");
        return value;
    }

    public void Dispose() => _cleanupTimer.Dispose();
}

sealed class RoomState
{
    private static readonly Random Random = new();
    public object Sync { get; } = new();
    public Guid Id { get; }
    public string Name { get; }
    public int SpectatorCapacity { get; }
    public Dictionary<Guid, RoomParticipant> Participants { get; } = new();
    public List<Guid> Queue { get; } = new();
    public List<RoomChatMessage> Messages { get; } = new();
    public Guid OwnerId { get; private set; }
    public Guid? PlayerOne { get; set; }
    public Guid? PlayerTwo { get; set; }
    public bool MatchRunning { get; set; }
    public Guid? MatchId { get; set; }
    private long _messageSequence;

    public RoomState(Guid id, string name, int spectatorCapacity) => (Id, Name, SpectatorCapacity) = (id, name, spectatorCapacity);

    public RoomParticipant Add(string nickname, bool owner)
    {
        var participant = new RoomParticipant(Guid.NewGuid(), nickname, DateTimeOffset.UtcNow);
        Participants[participant.Id] = participant;
        if (owner)
        {
            OwnerId = participant.Id;
            PlayerOne = participant.Id;
            AddSystemMessage($"{nickname} criou a sala.");
        }
        else AddSystemMessage($"{nickname} entrou na sala.");
        return participant;
    }

    public bool Remove(Guid id, string reason)
    {
        if (!Participants.Remove(id, out var leaving)) return false;
        Queue.Remove(id);
        var wasFighter = PlayerOne == id || PlayerTwo == id;
        if (PlayerOne == id) PlayerOne = null;
        if (PlayerTwo == id) PlayerTwo = null;
        var ownerLeft = OwnerId == id;
        if (ownerLeft && Participants.Count > 0)
        {
            OwnerId = Participants.Keys.ElementAt(Random.Next(Participants.Count));
            AddSystemMessage($"{NameOf(OwnerId)} agora é o dono da sala.");
        }
        if (wasFighter && MatchRunning)
        {
            MatchRunning = false;
            MatchId = null;
            AddSystemMessage("A luta foi interrompida. O novo dono pode reiniciar quando houver dois jogadores.");
        }
        FillFighterSlots();
        AddSystemMessage($"{leaving.Nickname} {reason}.");
        return true;
    }

    public void Enqueue(Guid id)
    {
        if (PlayerOne == id || PlayerTwo == id || Queue.Contains(id)) return;
        Queue.Add(id);
        FillFighterSlots();
    }

    public void Dequeue(Guid id)
    {
        Queue.Remove(id);
        if (!MatchRunning)
        {
            if (PlayerOne == id) PlayerOne = null;
            if (PlayerTwo == id) PlayerTwo = null;
        }
    }

    public void FillFighterSlots()
    {
        if (MatchRunning) return;
        if (PlayerOne is null && Queue.Count > 0) { PlayerOne = Queue[0]; Queue.RemoveAt(0); }
        if (PlayerTwo is null && Queue.Count > 0) { PlayerTwo = Queue[0]; Queue.RemoveAt(0); }
    }

    public RoomChatMessage AddMessage(Guid? playerId, string nickname, string text, bool system)
    {
        var message = new RoomChatMessage(++_messageSequence, playerId, nickname, text, system, DateTimeOffset.UtcNow);
        Messages.Add(message);
        if (Messages.Count > 100) Messages.RemoveRange(0, Messages.Count - 100);
        return message;
    }

    public void AddSystemMessage(string text) => AddMessage(null, "SISTEMA", text, true);
    public string NameOf(Guid id) => Participants.TryGetValue(id, out var player) ? player.Nickname : "Jogador";

    public object Snapshot()
    {
        lock (Sync) return SnapshotUnsafe();
    }

    public RoomSummary Summary()
    {
        lock (Sync) return new RoomSummary(Id, Name, Participants.Count, 2 + SpectatorCapacity, MatchRunning ? "running" : "waiting");
    }

    public object SnapshotUnsafe() => new
    {
        id = Id,
        name = Name,
        ownerId = OwnerId,
        capacity = new { players = 2, spectators = SpectatorCapacity, total = 2 + SpectatorCapacity },
        playerOne = Player(PlayerOne),
        playerTwo = Player(PlayerTwo),
        queue = Queue.Select((id, index) => new { position = index + 1, player = Player(id) }).ToArray(),
        spectators = Participants.Values.Where(player => player.Id != PlayerOne && player.Id != PlayerTwo && !Queue.Contains(player.Id))
            .Select(player => Player(player.Id)).ToArray(),
        matchStatus = MatchRunning ? "running" : "waiting",
        matchId = MatchId,
        lastMessageSequence = _messageSequence
    };

    private object? Player(Guid? id) => id is Guid value && Participants.TryGetValue(value, out var player)
        ? new { id = player.Id, nickname = player.Nickname, owner = player.Id == OwnerId }
        : null;
}

sealed class RoomParticipant
{
    public Guid Id { get; }
    public string Nickname { get; }
    public DateTimeOffset LastSeen { get; set; }
    public RoomParticipant(Guid id, string nickname, DateTimeOffset lastSeen) => (Id, Nickname, LastSeen) = (id, nickname, lastSeen);
}

record RoomChatMessage(long Sequence, Guid? PlayerId, string Nickname, string Text, bool System, DateTimeOffset SentAt);
record RoomSummary(Guid Id, string Name, int Participants, int Capacity, string Status);
