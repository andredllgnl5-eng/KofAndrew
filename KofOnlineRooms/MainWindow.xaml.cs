using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KofOnlineRooms;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly string _serverUrl;
    private Guid? _roomId;
    private Guid? _playerId;
    private bool _queued;
    private long _lastMessage;

    public MainWindow(string mode)
    {
        InitializeComponent();
        _serverUrl = LoadServerUrl();
        CreatePanel.Visibility = mode == "create" ? Visibility.Visible : Visibility.Collapsed;
        JoinPanel.Visibility = mode == "join" ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = mode == "create" ? "CONFIGURAR NOVA SALA" : "ESCOLHER UMA SALA";
        _timer.Tick += async (_, _) => await RefreshRoomAsync();
        Loaded += async (_, _) => { if (mode == "join") await LoadRoomsAsync(); };
        Closed += async (_, _) => await LeaveAsync();
    }

    private static string LoadServerUrl()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "launcher-config.json");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("ServerUrl").GetString()?.TrimEnd('/') ?? "http://26.152.187.43:5088";
        }
        catch { return "http://26.152.187.43:5088"; }
    }

    private async void CreateRoom_Click(object sender, RoutedEventArgs e)
    {
        var nickname = CreateNickname.Text.Trim();
        var roomName = RoomName.Text.Trim();
        var capacity = SpectatorCapacity.SelectedIndex;
        await ExecuteAsync(async () =>
        {
            using var response = await _http.PostAsJsonAsync($"{_serverUrl}/api/rooms", new { roomName, nickname, spectatorCapacity = capacity });
            await EnsureSuccess(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            EnterRoom(json.RootElement.GetProperty("roomId").GetGuid(), json.RootElement.GetProperty("playerId").GetGuid());
        });
    }

    private async void RefreshRooms_Click(object sender, RoutedEventArgs e) => await LoadRoomsAsync();

    private async Task LoadRoomsAsync()
    {
        await ExecuteAsync(async () =>
        {
            var rooms = await _http.GetFromJsonAsync<List<RoomListItem>>($"{_serverUrl}/api/rooms", JsonOptions()) ?? new();
            foreach (var room in rooms) room.Display = $"{room.Name}     {room.Participants}/{room.Capacity}     {(room.Status == "running" ? "EM LUTA" : "AGUARDANDO")}";
            RoomList.ItemsSource = rooms;
            StatusText.Text = rooms.Count == 0 ? "Nenhuma sala disponível." : $"{rooms.Count} sala(s) disponível(is).";
        });
    }

    private async void JoinRoom_Click(object sender, RoutedEventArgs e)
    {
        if (RoomList.SelectedItem is not RoomListItem room) { StatusText.Text = "Selecione uma sala."; return; }
        var nickname = JoinNickname.Text.Trim();
        await ExecuteAsync(async () =>
        {
            using var response = await _http.PostAsJsonAsync($"{_serverUrl}/api/rooms/{room.Id}/join", new { nickname, joinQueue = false });
            await EnsureSuccess(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            EnterRoom(room.Id, json.RootElement.GetProperty("playerId").GetGuid());
        });
    }

    private void EnterRoom(Guid roomId, Guid playerId)
    {
        _roomId = roomId; _playerId = playerId;
        EntryPanel.Visibility = Visibility.Collapsed;
        RoomPanel.Visibility = Visibility.Visible;
        _timer.Start();
        _ = RefreshRoomAsync();
    }

    private async Task RefreshRoomAsync()
    {
        if (_roomId is not Guid roomId || _playerId is not Guid playerId) return;
        try
        {
            await _http.PostAsJsonAsync($"{_serverUrl}/api/rooms/{roomId}/heartbeat", new { playerId });
            using var json = JsonDocument.Parse(await _http.GetStringAsync($"{_serverUrl}/api/rooms/{roomId}"));
            var root = json.RootElement;
            CurrentRoomName.Text = root.GetProperty("name").GetString() ?? "SALA";
            PlayerOne.Text = PlayerName(root, "playerOne"); PlayerTwo.Text = PlayerName(root, "playerTwo");
            QueueList.Items.Clear(); SpectatorList.Items.Clear();
            _queued = IsPlayer(root, "playerOne", playerId) || IsPlayer(root, "playerTwo", playerId);
            foreach (var item in root.GetProperty("queue").EnumerateArray())
            {
                var player = item.GetProperty("player");
                QueueList.Items.Add($"{item.GetProperty("position").GetInt32()}. {player.GetProperty("nickname").GetString()}");
                if (player.GetProperty("id").GetGuid() == playerId) _queued = true;
            }
            foreach (var player in root.GetProperty("spectators").EnumerateArray()) SpectatorList.Items.Add(player.GetProperty("nickname").GetString());
            QueueButton.Content = _queued ? "SAIR DA FILA" : "ENTRAR NA FILA";
            StartButton.Visibility = root.GetProperty("ownerId").GetGuid() == playerId ? Visibility.Visible : Visibility.Collapsed;
            StartButton.IsEnabled = root.GetProperty("playerOne").ValueKind != JsonValueKind.Null && root.GetProperty("playerTwo").ValueKind != JsonValueKind.Null && root.GetProperty("matchStatus").GetString() != "running";
            await LoadChatAsync(roomId);
        }
        catch { StatusText.Text = "Conexão com a sala perdida..."; }
    }

    private async Task LoadChatAsync(Guid roomId)
    {
        var messages = await _http.GetFromJsonAsync<List<ChatMessage>>($"{_serverUrl}/api/rooms/{roomId}/chat?after={_lastMessage}", JsonOptions()) ?? new();
        foreach (var message in messages)
        {
            ChatList.Items.Add(message.System ? $"• {message.Text}" : $"{message.Nickname}: {message.Text}");
            _lastMessage = Math.Max(_lastMessage, message.Sequence);
        }
        if (ChatList.Items.Count > 0) ChatList.ScrollIntoView(ChatList.Items[^1]);
    }

    private async void Queue_Click(object sender, RoutedEventArgs e)
    {
        if (_roomId is not Guid roomId || _playerId is not Guid playerId) return;
        await ExecuteAsync(async () => { using var response = await _http.PostAsJsonAsync($"{_serverUrl}/api/rooms/{roomId}/queue", new { playerId, join = !_queued }); await EnsureSuccess(response); });
        await RefreshRoomAsync();
    }

    private async void StartMatch_Click(object sender, RoutedEventArgs e)
    {
        if (_roomId is not Guid roomId || _playerId is not Guid playerId) return;
        await ExecuteAsync(async () => { using var response = await _http.PostAsJsonAsync($"{_serverUrl}/api/rooms/{roomId}/start", new { playerId }); await EnsureSuccess(response); });
        await RefreshRoomAsync();
    }

    private async void SendChat_Click(object sender, RoutedEventArgs e) => await SendChatAsync();
    private async void ChatInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SendChatAsync(); } }
    private async Task SendChatAsync()
    {
        if (_roomId is not Guid roomId || _playerId is not Guid playerId || string.IsNullOrWhiteSpace(ChatInput.Text)) return;
        var message = ChatInput.Text.Trim(); ChatInput.Clear();
        await ExecuteAsync(async () => { using var response = await _http.PostAsJsonAsync($"{_serverUrl}/api/rooms/{roomId}/chat", new { playerId, message }); await EnsureSuccess(response); });
        await LoadChatAsync(roomId);
    }

    private async Task LeaveAsync()
    {
        _timer.Stop();
        if (_roomId is Guid roomId && _playerId is Guid playerId)
            try { await _http.PostAsJsonAsync($"{_serverUrl}/api/rooms/{roomId}/leave", new { playerId }); } catch { }
    }

    private async void Close_Click(object sender, RoutedEventArgs e) { await LeaveAsync(); Close(); }
    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        try { using var json = JsonDocument.Parse(body); throw new InvalidOperationException(json.RootElement.GetProperty("error").GetString()); }
        catch (JsonException) { throw new InvalidOperationException($"Erro do servidor ({(int)response.StatusCode})."); }
    }
    private static string PlayerName(JsonElement root, string property) => root.GetProperty(property).ValueKind == JsonValueKind.Null ? "AGUARDANDO" : root.GetProperty(property).GetProperty("nickname").GetString() ?? "AGUARDANDO";
    private static bool IsPlayer(JsonElement root, string property, Guid id) => root.GetProperty(property).ValueKind != JsonValueKind.Null && root.GetProperty(property).GetProperty("id").GetGuid() == id;
    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };
}

sealed class RoomListItem { public Guid Id { get; set; } public string Name { get; set; } = ""; public int Participants { get; set; } public int Capacity { get; set; } public string Status { get; set; } = ""; public string Display { get; set; } = ""; }
sealed class ChatMessage { public long Sequence { get; set; } public string Nickname { get; set; } = ""; public string Text { get; set; } = ""; public bool System { get; set; } }
