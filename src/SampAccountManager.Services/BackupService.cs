using System.IO.Compression;

namespace SampAccountManager.Services;

/// <summary>Backup/restaurare pentru conturi, servere si setari. Parolele raman protejate prin DPAPI (valabile doar pe acelasi utilizator Windows).</summary>
public sealed class BackupService
{
    private readonly AppPaths _paths;
    public BackupService(AppPaths paths) => _paths = paths;

    public string CreateBackup(string? targetZip = null)
    {
        var path = targetZip ?? Path.Combine(_paths.BackupsDir, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var f in new[] { _paths.AccountsFile, _paths.ServersFile, _paths.SettingsFile })
            if (File.Exists(f)) zip.CreateEntryFromFile(f, Path.GetFileName(f));
        return path;
    }

    /// <summary>Restaureaza dintr-un zip. Inainte face un backup automat al starii curente.</summary>
    public void Restore(string zipPath)
    {
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var allowed = new[] { "accounts.json", "servers.json", "settings.json" };
            if (!zip.Entries.Any(e => allowed.Contains(e.Name))) throw new InvalidDataException("Arhiva nu contine fisiere de date valide.");
        }
        CreateBackup(Path.Combine(_paths.BackupsDir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip"));
        using var z = ZipFile.OpenRead(zipPath);
        foreach (var e in z.Entries)
        {
            if (e.Name is not ("accounts.json" or "servers.json" or "settings.json")) continue; // evitam path traversal
            e.ExtractToFile(Path.Combine(_paths.DataDir, e.Name), overwrite: true);
        }
    }
}
