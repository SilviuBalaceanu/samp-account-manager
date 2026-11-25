namespace SampAccountManager.Services;

/// <summary>Foldere locale: data/, logs/, backups/ langa aplicatie (sau in LocalAppData daca folderul nu e scriibil).</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public string BackupsDir => Path.Combine(Root, "backups");
    public string DefaultLogsDir => Path.Combine(Root, "logs");
    public string AccountsFile => Path.Combine(DataDir, "accounts.json");
    public string ServersFile => Path.Combine(DataDir, "servers.json");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");

    public AppPaths(string? root = null)
    {
        Root = root ?? ChooseRoot();
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(DefaultLogsDir);
    }

    private static string ChooseRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(baseDir, ".write-test");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return baseDir;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SampAccountManager");
        }
    }

    /// <summary>Calea executabilei clientului Rust (langa aplicatie sau in subfolderul headless).</summary>
    public string? FindHeadlessClient()
    {
        var name = OperatingSystem.IsWindows() ? "samp_headless_client.exe" : "samp_headless_client";
        foreach (var p in new[]
        {
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(AppContext.BaseDirectory, "headless", name),
        })
            if (File.Exists(p)) return p;
        return null;
    }
}
