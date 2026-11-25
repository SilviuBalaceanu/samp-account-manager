using System.Collections.ObjectModel;
using SampAccountManager.Services;
using SampAccountManager.WPF.Infrastructure;

namespace SampAccountManager.WPF.ViewModels;

/// <summary>Radacina de compozitie: toate serviciile aplicatiei.</summary>
public sealed class AppServices
{
    public AppPaths Paths { get; }
    public SettingsService Settings { get; }
    public AccountRepository Accounts { get; }
    public ServerRepository Servers { get; }
    public LogService Logs { get; }
    public BackupService Backup { get; }
    public SessionManager Sessions { get; }
    public ObservableCollection<string> Activity { get; } = new();

    public AppServices()
    {
        Paths = new AppPaths();
        Settings = new SettingsService(Paths);
        var protector = new DpapiPasswordProtector();
        Accounts = new AccountRepository(Paths, Settings, protector);
        Servers = new ServerRepository(Paths, Settings);
        Logs = new LogService(Settings);
        Backup = new BackupService(Paths);
        Sessions = new SessionManager(Paths, Logs, Settings, Accounts);
    }

    public void AddActivity(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        UiThread.Post(() =>
        {
            Activity.Insert(0, line);
            while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1);
        });
        Logs.Append("activity", line);
    }
}
