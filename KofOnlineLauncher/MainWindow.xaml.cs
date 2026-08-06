using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net.Http.Headers;

namespace KofOnlineLauncher;

public partial class MainWindow : Window
{
    private LauncherConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private const string CurrentVersionFile = "client-version.json";

    public MainWindow()
    {
        InitializeComponent();
        _config = LauncherConfig.Load();
        RefreshGameStatus();
        Loaded += async (_, _) => await CheckUpdatesThenServerAsync();
    }

    private async Task CheckUpdatesThenServerAsync()
    {
        OnlineButton.IsEnabled = false;
        try
        {
            GameStatus.Text = "Consultando atualizações oficiais…";
            using var updateHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            updateHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KofAndrewLauncher", "1.0"));
            var separator = _config.UpdateManifestUrl.Contains('?') ? "&" : "?";
            var manifestUrl = $"{_config.UpdateManifestUrl}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var manifestJson = (await updateHttp.GetStringAsync(manifestUrl)).TrimStart('\uFEFF');
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestJson, UpdateJson.Options)
                ?? throw new InvalidDataException("Manifesto de atualização inválido.");
            VersionText.Text = $"v{manifest.Version}";

            var valid = await VerifyFilesAsync(manifest);
            if (!valid)
            {
                await DownloadAndInstallAsync(updateHttp, manifest);
                return;
            }

            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, CurrentVersionFile), manifestJson);
            GameStatus.Text = $"✓ Jogo atualizado — v{manifest.Version}";
            GameStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 220, 139));
            await CheckServerAsync();
        }
        catch (Exception ex)
        {
            GameStatus.Text = $"Não foi possível verificar a atualização: {ex.Message}";
            GameStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 105, 105));
            ServerStatus.Text = "Atualização não verificada";
            OnlineButton.IsEnabled = false;
        }
    }

    private async Task<bool> VerifyFilesAsync(UpdateManifest manifest)
    {
        UpdateProgress.Visibility = Visibility.Visible;
        var total = Math.Max(1, manifest.Files.Count);
        for (var index = 0; index < manifest.Files.Count; index++)
        {
            var entry = manifest.Files[index];
            var installRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(installRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length != entry.Size) return false;
            GameStatus.Text = $"Verificando arquivos… {index + 1}/{total}";
            await using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(await sha.ComputeHashAsync(stream));
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            UpdateProgress.Value = (index + 1) * 100d / total;
        }
        UpdateProgress.Visibility = Visibility.Collapsed;
        return true;
    }

    private async Task DownloadAndInstallAsync(HttpClient http, UpdateManifest manifest)
    {
        var updater = Path.Combine(AppContext.BaseDirectory, "KOF Updater.exe");
        if (!File.Exists(updater)) throw new FileNotFoundException("KOF Updater.exe não encontrado.");
        GameStatus.Text = $"Baixando atualização v{manifest.Version}…";
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        var archive = Path.Combine(Path.GetTempPath(), $"KofAndrew-{manifest.Version}-{Guid.NewGuid():N}.zip");
        using var response = await http.GetAsync(manifest.PackageUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var destination = File.Create(archive))
        {
            var buffer = new byte[1024 * 1024];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                if (total > 0) UpdateProgress.Value = received * 100d / total.Value;
                GameStatus.Text = total > 0
                    ? $"Baixando atualização… {received / 1024 / 1024} de {total.Value / 1024 / 1024} MB"
                    : $"Baixando atualização… {received / 1024 / 1024} MB";
            }
        }
        await using (var stream = File.OpenRead(archive))
        {
            using var sha = SHA256.Create();
            var packageHash = Convert.ToHexString(await sha.ComputeHashAsync(stream));
            if (!packageHash.Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(archive);
                throw new InvalidDataException("O pacote baixado falhou na verificação de segurança.");
            }
        }
        var temporaryUpdater = Path.Combine(Path.GetTempPath(), $"KOF-Updater-{Guid.NewGuid():N}.exe");
        File.Copy(updater, temporaryUpdater, true);
        Process.Start(new ProcessStartInfo(temporaryUpdater)
        {
            UseShellExecute = false,
            ArgumentList =
            {
                "--pid", Environment.ProcessId.ToString(),
                "--install", AppContext.BaseDirectory,
                "--archive", archive,
                "--launcher", "KOF Online.exe"
            }
        });
        Application.Current.Shutdown();
    }

    private async Task CheckServerAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{_config.ServerUrl.TrimEnd('/')}/api/status");
            response.EnsureSuccessStatusCode();
            ServerStatus.Text = "KOFF Community Server online";
            ServerDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 221, 127));
            OnlineButton.IsEnabled = true;
            OnlineButton.ToolTip = "Entrar na Arena KOFF";
        }
        catch
        {
            ServerStatus.Text = "Servidor ainda não iniciado";
            ServerDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(130, 130, 135));
            OnlineButton.IsEnabled = false;
        }
    }

    private void PlayOnline_Click(object sender, RoutedEventArgs e)
    {
        new OnlineWindow(_config) { Owner = this }.ShowDialog();
    }

    private string GameExecutable => Path.Combine(_config.GameDirectory, "Ikemen_GO.exe");

    private void RefreshGameStatus()
    {
        GameStatus.Text = File.Exists(GameExecutable)
            ? $"✓ IKEMEN Online encontrado — {_config.GameDirectory}"
            : $"IKEMEN Online não encontrado — {_config.GameDirectory}";
        GameStatus.Foreground = File.Exists(GameExecutable)
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 220, 139))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 105, 105));
    }

    private void PlayOffline_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(GameExecutable))
        {
            MessageBox.Show("Selecione a pasta que contém o Ikemen_GO.exe em Configurações.", "KOF Online", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(GameExecutable) { WorkingDirectory = _config.GameDirectory, UseShellExecute = true });
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione o Ikemen_GO.exe do KOF Online",
            InitialDirectory = _config.GameDirectory,
            Filter = "IKEMEN (Ikemen_GO.exe)|Ikemen_GO.exe|Executáveis (*.exe)|*.exe",
            FileName = "Ikemen_GO.exe"
        };
        if (dialog.ShowDialog(this) != true) return;

        if (!File.Exists(dialog.FileName) || !string.Equals(Path.GetFileName(dialog.FileName), "Ikemen_GO.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("A pasta selecionada não contém Ikemen_GO.exe.", "Pasta inválida", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _config.GameDirectory = Path.GetDirectoryName(dialog.FileName)!;
        _config.Save();
        RefreshGameStatus();
    }
}

internal sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public List<UpdateFile> Files { get; set; } = new();
}

internal sealed class UpdateFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

internal static class UpdateJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
