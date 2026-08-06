using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO;

namespace KofOnlineLauncher;

public partial class OnlineWindow : Window
{
    private readonly string _serverUrl;
    private readonly LauncherConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Guid? _playerId;
    private bool _queued;
    private bool _ready;
    private string _nickname = "";
    private Guid? _launchedMatchId;

    internal OnlineWindow(LauncherConfig config)
    {
        InitializeComponent();
        _config = config;
        _serverUrl = config.ServerUrl.TrimEnd('/');
        _timer.Tick += async (_, _) => await RefreshArenaAsync();
        Loaded += async (_, _) => { _timer.Start(); await RefreshArenaAsync(); };
        Closed += async (_, _) =>
        {
            _timer.Stop();
            if (_playerId is Guid id)
                try { await _http.PostAsJsonAsync($"{_serverUrl}/api/players/leave", new { playerId = id }); } catch { }
        };
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var nickname = NicknameBox.Text.Trim();
        if (nickname.Length < 3) { MessageBox.Show("Use um apelido com pelo menos 3 caracteres."); return; }
        try
        {
            using var response = await _http.PostAsJsonAsync($"{_serverUrl}/api/players/join", new { nickname });
            if (!response.IsSuccessStatusCode) { MessageBox.Show("Não foi possível entrar com esse apelido."); return; }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            _playerId = json.RootElement.GetProperty("playerId").GetGuid();
            _nickname = nickname;
            LoginPanel.Visibility = Visibility.Collapsed;
            QueueButton.IsEnabled = true;
            MyStatusText.Text = $"{nickname}, você está assistindo. Entre na fila para lutar.";
            await RefreshArenaAsync();
        }
        catch (Exception ex) { MessageBox.Show($"Servidor indisponível: {ex.Message}"); }
    }

    private async void Queue_Click(object sender, RoutedEventArgs e)
    {
        if (_playerId is not Guid id) return;
        try
        {
            var endpoint = _queued ? "leave" : "join";
            await _http.PostAsJsonAsync($"{_serverUrl}/api/queue/{endpoint}", new { playerId = id });
            _queued = !_queued;
            QueueButton.Content = _queued ? "SAIR DA FILA" : "ENTRAR NA FILA";
            await RefreshArenaAsync();
        }
        catch { MessageBox.Show("Não foi possível atualizar sua posição na fila."); }
    }

    private async void Ready_Click(object sender, RoutedEventArgs e)
    {
        if (_playerId is not Guid id) return;
        try
        {
            using var response = await _http.PostAsJsonAsync($"{_serverUrl}/api/players/ready", new { playerId = id });
            response.EnsureSuccessStatusCode();
            await RefreshArenaAsync();
        }
        catch { MessageBox.Show("Não foi possível atualizar o estado PRONTO."); }
    }

    private async Task RefreshArenaAsync()
    {
        try
        {
            using var json = JsonDocument.Parse(await _http.GetStringAsync($"{_serverUrl}/api/arena"));
            var root = json.RootElement;
            PlayerOneText.Text = ReadPlayer(root, "playerOne");
            PlayerTwoText.Text = ReadPlayer(root, "playerTwo");
            QueueList.Items.Clear();
            var myPosition = 0;
            foreach (var item in root.GetProperty("queue").EnumerateArray())
            {
                QueueList.Items.Add($"{item.GetProperty("position").GetInt32()}. {item.GetProperty("player").GetProperty("nickname").GetString()}");
                if (_playerId is Guid id && item.GetProperty("player").GetProperty("id").GetGuid() == id)
                    myPosition = item.GetProperty("position").GetInt32();
            }
            var count = root.GetProperty("spectatorCount").GetInt32();
            SpectatorText.Text = count == 1 ? "1 espectador" : $"{count} espectadores";

            if (_playerId is Guid playerId)
            {
                var isPlayerOne = HasPlayerId(root, "playerOne", playerId);
                var isPlayerTwo = HasPlayerId(root, "playerTwo", playerId);
                var activePlayer = isPlayerOne ? root.GetProperty("playerOne") : isPlayerTwo ? root.GetProperty("playerTwo") : default;
                _ready = (isPlayerOne || isPlayerTwo) && activePlayer.GetProperty("ready").GetBoolean();
                _queued = isPlayerOne || isPlayerTwo || myPosition > 0;
                QueueButton.Content = _queued ? (isPlayerOne || isPlayerTwo ? "SAIR DA PARTIDA" : "SAIR DA FILA") : "ENTRAR NA FILA";
                ReadyButton.Visibility = isPlayerOne || isPlayerTwo ? Visibility.Visible : Visibility.Collapsed;
                ReadyButton.IsEnabled = isPlayerOne || isPlayerTwo;
                ReadyButton.Content = _ready ? "CANCELAR PRONTO" : "ESTOU PRONTO";
                ReadyButton.Background = new System.Windows.Media.SolidColorBrush(_ready
                    ? System.Windows.Media.Color.FromRgb(23, 117, 61)
                    : System.Windows.Media.Color.FromRgb(177, 122, 19));
                var matchStatus = root.GetProperty("matchStatus").GetString();
                var matchActive = matchStatus is "ready" or "running";
                MatchReadyPanel.Visibility = matchActive ? Visibility.Visible : Visibility.Collapsed;
                MatchReadyText.Text = matchStatus == "running" ? "▶ JOGO INICIADO" : "✓ OS DOIS ESTÃO PRONTOS";
                ReadyButton.IsEnabled = (isPlayerOne || isPlayerTwo) && matchStatus != "running";
                MyStatusText.Text = isPlayerOne
                    ? $"{_nickname}, você é o PLAYER 1 desta partida{(_ready ? " e está PRONTO." : ".")}" 
                    : isPlayerTwo
                        ? $"{_nickname}, você é o PLAYER 2 desta partida{(_ready ? " e está PRONTO." : ".")}" 
                        : myPosition > 0
                            ? $"{_nickname}, você está na posição {myPosition} da fila."
                            : $"{_nickname}, você está assistindo. Entre na fila para lutar.";

                if (matchStatus == "running" && (isPlayerOne || isPlayerTwo) &&
                    root.TryGetProperty("matchId", out var matchValue) && matchValue.ValueKind == JsonValueKind.String)
                {
                    var matchId = matchValue.GetGuid();
                    if (_launchedMatchId != matchId)
                    {
                        _launchedMatchId = matchId;
                        LaunchIkemenNetplay(isPlayerOne, root.GetProperty("hostAddress").GetString() ?? "127.0.0.1");
                    }
                }
            }
        }
        catch { SpectatorText.Text = "Conexão perdida"; }
    }

    private void LaunchIkemenNetplay(bool isHost, string hostAddress)
    {
        var executable = Path.Combine(_config.GameDirectory, "Ikemen_GO.exe");
        if (!File.Exists(executable))
        {
            MessageBox.Show("Ikemen_GO.exe não encontrado. Abra Configurações e selecione a edição KOF Online.");
            return;
        }

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _config.GameDirectory,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-koffnetplay");
        start.ArgumentList.Add(isHost ? "host" : "join");
        if (!isHost)
        {
            start.ArgumentList.Add("-koffhost");
            start.ArgumentList.Add(hostAddress);
        }
        Process.Start(start);
        MyStatusText.Text = isHost
            ? "Abrindo IKEMEN como HOST na porta 7500..."
            : $"Conectando o IKEMEN ao host {hostAddress}:7500...";
    }

    private static string ReadPlayer(JsonElement root, string property)
    {
        var value = root.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null ? "AGUARDANDO" : value.GetProperty("nickname").GetString() ?? "AGUARDANDO";
    }

    private static bool HasPlayerId(JsonElement root, string property, Guid id)
    {
        var value = root.GetProperty(property);
        return value.ValueKind != JsonValueKind.Null && value.GetProperty("id").GetGuid() == id;
    }
}
