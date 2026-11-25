using System.Windows;
using SampAccountManager.Models;
using SampAccountManager.Services;
using SampAccountManager.WPF.Infrastructure;

namespace SampAccountManager.WPF.ViewModels;

public sealed class NavItem
{
    public NavItem(string title, ObservableObject page) { Title = title; Page = page; }
    public string Title { get; }
    public ObservableObject Page { get; }
}

public sealed class ShellViewModel : ObservableObject
{
    private NavItem _selectedNav;

    public AppServices Services { get; }
    public DashboardViewModel Dashboard { get; }
    public AccountsViewModel Accounts { get; }
    public ServersViewModel Servers { get; }
    public SessionsViewModel Sessions { get; }
    public SettingsViewModel Settings { get; }
    public LogsViewModel Logs { get; }
    public List<NavItem> Nav { get; }

    public ShellViewModel(AppServices s)
    {
        Services = s;
        Dashboard = new DashboardViewModel(this, s);
        Accounts = new AccountsViewModel(this, s);
        Servers = new ServersViewModel(this, s);
        Sessions = new SessionsViewModel(s);
        Settings = new SettingsViewModel(this, s);
        Logs = new LogsViewModel(s);
        Nav = new List<NavItem>
        {
            new("Dashboard", Dashboard), new("Accounts", Accounts), new("Servers", Servers),
            new("Sessions", Sessions), new("Settings", Settings), new("Logs", Logs),
        };
        _selectedNav = Nav[0];

        s.Sessions.SessionChanged += c => UiThread.Post(() =>
        {
            Sessions.OnChanged(c);
            Accounts.RefreshState(c);
            Dashboard.Refresh();
        });
        s.Sessions.SessionLog += (c, line) => UiThread.Post(() =>
        {
            Sessions.OnLog(c, line);
            Logs.Add(c.Nickname, line);
            if (line.Contains("SPAWN CONFIRMAT") || line.Contains("ONLINE:") || line.Contains("EROARE") || line.Contains("Deconectat"))
                s.AddActivity($"{c.Nickname}: {line[11..]}");
        });
        s.AddActivity("Aplicatie pornita");
        _ = Servers.RefreshAllAsync();
        Ready = true;
        Dashboard.Refresh();
    }

    public bool Ready { get; private set; }

    public NavItem SelectedNav { get => _selectedNav; set { if (Set(ref _selectedNav, value)) OnPropertyChanged(nameof(CurrentPage)); } }
    public ObservableObject CurrentPage => _selectedNav.Page;

    /// <summary>Serverul folosit la CONNECT: cel selectat in pagina Servers, altfel cel implicit.</summary>
    public ServerEntry? EffectiveServer => Servers.Selected?.Server ?? Services.Servers.Default;

    public bool HasDirtyData => Services.Accounts.Dirty || Services.Servers.Dirty || Services.Settings.Dirty;

    public void ReloadAll()
    {
        Accounts.Reload();
        Servers.Reload();
        Settings.Reload();
        Dashboard.Refresh();
    }
}
