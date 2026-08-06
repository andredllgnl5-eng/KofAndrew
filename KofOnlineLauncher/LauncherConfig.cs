using System.Text.Json;
using System.IO;

namespace KofOnlineLauncher;

internal sealed class LauncherConfig
{
    public string GameDirectory { get; set; } = @"C:\Users\Abyss\OneDrive\Documentos\Server Koff\Ikemen KOF Online";
    public string ServerUrl { get; set; } = "http://127.0.0.1:5088";
    public string UpdateManifestUrl { get; set; } = "https://raw.githubusercontent.com/andredllgnl5-eng/KofAndrew-Updates/main/latest.json";

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "launcher-config.json");

    public static LauncherConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var initial = new LauncherConfig();
            initial.Save();
            return initial;
        }

        var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(ConfigPath)) ?? new LauncherConfig();
        if (!File.Exists(Path.Combine(config.GameDirectory, "Ikemen_GO.exe")))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "game");
            var development = @"C:\Users\Abyss\OneDrive\Documentos\Server Koff\Ikemen KOF Online";
            if (File.Exists(Path.Combine(bundled, "Ikemen_GO.exe"))) config.GameDirectory = bundled;
            else if (File.Exists(Path.Combine(development, "Ikemen_GO.exe"))) config.GameDirectory = development;
            config.Save();
        }
        return config;
    }

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
