using System.Text.Json;
using System.IO;

namespace KofOnlineLauncher;

internal sealed class LauncherConfig
{
    public string GameDirectory { get; set; } = "";
    public string ServerUrl { get; set; } = "http://26.152.187.43:5088";
    public string UpdateManifestUrl { get; set; } = "https://raw.githubusercontent.com/andredllgnl5-eng/KofAndrew-Updates/main/latest.json";

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "launcher-config.json");

    public static LauncherConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var initial = new LauncherConfig();
            initial.ResolveGameDirectory();
            initial.Save();
            return initial;
        }

        var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(ConfigPath)) ?? new LauncherConfig();
        config.ResolveGameDirectory();
        config.Save();
        return config;
    }

    private void ResolveGameDirectory()
    {
        if (File.Exists(Path.Combine(GameDirectory, "Ikemen_GO.exe"))) return;
        var bundled = Path.Combine(AppContext.BaseDirectory, "game");
        if (File.Exists(Path.Combine(bundled, "Ikemen_GO.exe"))) GameDirectory = bundled;
    }

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
