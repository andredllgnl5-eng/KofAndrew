using System.Diagnostics;
using System.IO.Compression;

var values = args.Chunk(2).Where(x => x.Length == 2 && x[0].StartsWith("--"))
    .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);
if (!values.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out var pid) ||
    !values.TryGetValue("--install", out var install) || !values.TryGetValue("--archive", out var archive) ||
    !values.TryGetValue("--launcher", out var launcher)) return;

try
{
    try { Process.GetProcessById(pid).WaitForExit(30000); } catch { }
    var installRoot = Path.GetFullPath(install);
    var staging = Path.Combine(Path.GetTempPath(), $"KofAndrew-Update-{Guid.NewGuid():N}");
    Directory.CreateDirectory(staging);
    ZipFile.ExtractToDirectory(archive, staging, true);
    foreach (var directory in Directory.GetDirectories(staging, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(staging, directory);
        Directory.CreateDirectory(Path.Combine(installRoot, relative));
    }
    foreach (var file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(staging, file);
        var destination = Path.GetFullPath(Path.Combine(installRoot, relative));
        if (!destination.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase)) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, true);
    }
    Directory.Delete(staging, true);
    File.Delete(archive);
    Process.Start(new ProcessStartInfo(Path.Combine(installRoot, launcher)) { WorkingDirectory = installRoot, UseShellExecute = true });
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(Path.GetTempPath(), "KofAndrew-update-error.txt"), ex.ToString());
}
