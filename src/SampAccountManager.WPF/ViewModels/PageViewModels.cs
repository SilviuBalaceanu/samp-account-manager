using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SampAccountManager.Models;
using SampAccountManager.Services;
using SampAccountManager.WPF.Infrastructure;
using SampAccountManager.WPF.Views;

namespace SampAccountManager.WPF.ViewModels;

// ------------------------------------------------------------------ Accounts
public sealed class AccountsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly AppServices _s;
    private string _search = "";
    private AccountRow? _selected;

    public ObservableCollection<AccountRow> Rows { get; } = new();
    public ICollectionView View { get; }

    public AccountsViewModel(ShellViewModel shell, AppServices s)
    {
        _shell = shell; _s = s;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is AccountRow r && (_search.Length == 0 || r.Nickname.Contains(_search, StringComparison.OrdinalIgnoreCase));
        AddCommand = new RelayCommand(Add);
        EditCommand = new RelayCommand(Edit, () => Selected != null);
        DeleteCommand = new RelayCommand(Delete, () => Selected != null);
        ConnectCommand = new RelayCommand(Connect, () => Selected != null);
        DisconnectCommand = new RelayCommand(() => { if (Selected != null) _ = _s.Sessions.DisconnectAsync(Selected.Account.Id); }, () => Selected != null);
        ReconnectCommand = new RelayCommand(Reconnect, () => Selected != null);
        Reload();
    }

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ReconnectCommand { get; }

    public string Search { get => _search; set { if (Set(ref _search, value)) View.Refresh(); } }

    public AccountRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var c in new[] { EditCommand, DeleteCommand, ConnectCommand, DisconnectCommand, ReconnectCommand })
                ((RelayCommand)c).Raise();
            if (value != null && _s.Settings.Settings.LastSelectedAccountId != value.Account.Id)
            {
                _s.Settings.Settings.LastSelectedAccountId = value.Account.Id;
                _s.Settings.Touch();
            }
            _shell.Dashboard.Refresh();
        }
    }

    public void Reload()
    {
        Rows.Clear();
        foreach (var a in _s.Accounts.Items.OrderBy(a => a.Nickname, StringComparer.OrdinalIgnoreCase)) Rows.Add(new AccountRow(a));
        var last = _s.Settings.Settings.LastSelectedAccountId;
        Selected = Rows.FirstOrDefault(r => r.Account.Id == last) ?? Rows.FirstOrDefault();
    }

    public void RefreshState(SessionController c)
    {
        var row = Rows.FirstOrDefault(r => r.Account.Id == c.AccountId);
        row?.Refresh(c.Machine.State);
    }

    private void Add()
    {
        var dlg = new AccountDialog(null, (nick, pass, id) =>
            _s.Accounts.Validate(nick, id) ?? (pass.Length == 0 ? "Parola este obligatorie la adaugare." : null));
        if (dlg.ShowDialog() != true) return;
        var acc = _s.Accounts.Add(dlg.Nickname, dlg.Password, dlg.Notes);
        var row = new AccountRow(acc);
        Rows.Add(row);
        Selected = row;
        _s.AddActivity($"Cont adaugat: {acc.Nickname}");
        _shell.Dashboard.Refresh();
    }

    private void Edit()
    {
        if (Selected == null) return;
        var acc = Selected.Account;
        var dlg = new AccountDialog(acc, (nick, _, id) => _s.Accounts.Validate(nick, id));
        if (dlg.ShowDialog() != true) return;
        _s.Accounts.Update(acc, dlg.Nickname, dlg.Password, dlg.Notes);
        Selected.Refresh();
        View.Refresh();
        _s.AddActivity($"Cont modificat: {acc.Nickname}");
    }

    private void Delete()
    {
        if (Selected == null) return;
        var acc = Selected.Account;
        if (_s.Sessions.Sessions.Any(x => x.AccountId == acc.Id && x.Machine.IsActive))
        {
            MessageBox.Show("Contul are o sesiune activa. Deconecteaza-l intai.", "Stergere cont", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"Stergi contul {acc.Nickname}?", "Stergere cont", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var row = Selected;
        _s.Accounts.Remove(acc);
        Rows.Remove(row);
        _s.AddActivity($"Cont sters: {acc.Nickname}");
        _shell.Dashboard.Refresh();
    }

    private void Connect()
    {
        if (Selected == null) return;
        var server = _shell.EffectiveServer;
        if (server == null) { MessageBox.Show("Nu exista niciun server configurat.", "Conectare"); return; }
        var err = _s.Sessions.Connect(Selected.Account, server);
        if (err != null) MessageBox.Show(err, "Conectare", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
        {
            Selected.Refresh();
            _s.AddActivity($"CONNECT {Selected.Nickname} -> {server.Name}");
            _shell.Dashboard.Refresh();
        }
    }

    private async void Reconnect()
    {
        if (Selected == null) return;
        var server = _shell.EffectiveServer;
        if (server == null) return;
        var err = await _s.Sessions.ReconnectAsync(Selected.Account, server);
        if (err != null) MessageBox.Show(err, "Reconectare", MessageBoxButton.OK, MessageBoxImage.Warning);
        else _s.AddActivity($"RECONNECT {Selected.Nickname} -> {server.Name}");
    }
}

// ------------------------------------------------------------------ Servers
public sealed class ServersViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly AppServices _s;
    private ServerRow? _selected;

    public ObservableCollection<ServerRow> Rows { get; } = new();

    public ServersViewModel(ShellViewModel shell, AppServices s)
    {
        _shell = shell; _s = s;
        AddCommand = new RelayCommand(Add);
        EditCommand = new RelayCommand(Edit, () => Selected != null);
        DeleteCommand = new RelayCommand(Delete, () => Selected != null && Rows.Count > 1);
        DefaultCommand = new RelayCommand(SetDefault, () => Selected != null);
        RefreshCommand = new RelayCommand(() => _ = RefreshAllAsync());
        Reload();
    }

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DefaultCommand { get; }
    public ICommand RefreshCommand { get; }

    public ServerRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var c in new[] { EditCommand, DeleteCommand, DefaultCommand }) ((RelayCommand)c).Raise();
            _shell.Dashboard.Refresh();
        }
    }

    public void Reload()
    {
        Rows.Clear();
        foreach (var sv in _s.Servers.Items) Rows.Add(new ServerRow(sv));
        MarkDefault();
        Selected = Rows.FirstOrDefault(r => r.Server.Id == _s.Servers.Default?.Id) ?? Rows.FirstOrDefault();
    }

    private void MarkDefault()
    {
        var id = _s.Servers.Default?.Id;
        foreach (var r in Rows) { r.IsDefault = r.Server.Id == id; r.Refresh(); }
    }

    private void Add()
    {
        var dlg = new ServerDialog(null, (n, h, p) => _s.Servers.Validate(n, h, p));
        if (dlg.ShowDialog() != true) return;
        var sv = _s.Servers.Add(dlg.ServerName, dlg.Host, dlg.Port, dlg.Description);
        var row = new ServerRow(sv);
        Rows.Add(row);
        Selected = row;
        _s.AddActivity($"Server adaugat: {sv.Name}");
        _ = RefreshOneAsync(row);
    }

    private void Edit()
    {
        if (Selected == null) return;
        var dlg = new ServerDialog(Selected.Server, (n, h, p) => _s.Servers.Validate(n, h, p));
        if (dlg.ShowDialog() != true) return;
        _s.Servers.Update(Selected.Server, dlg.ServerName, dlg.Host, dlg.Port, dlg.Description);
        Selected.Refresh();
        _s.AddActivity($"Server modificat: {Selected.Name}");
        _ = RefreshOneAsync(Selected);
    }

    private void Delete()
    {
        if (Selected == null || Rows.Count <= 1) return;
        if (_s.Sessions.Sessions.Any(x => x.Machine.IsActive && x.Server.Id == Selected.Server.Id))
        {
            MessageBox.Show("Exista sesiuni active pe acest server.", "Stergere server", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"Stergi serverul {Selected.Name}?", "Stergere server", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var row = Selected;
        _s.Servers.Remove(row.Server);
        Rows.Remove(row);
        Selected = Rows.FirstOrDefault();
        MarkDefault();
        _s.AddActivity($"Server sters: {row.Name}");
    }

    private void SetDefault()
    {
        if (Selected == null) return;
        _s.Settings.Settings.DefaultServerId = Selected.Server.Id;
        _s.Settings.Touch();
        MarkDefault();
        _shell.Settings.Reload();
        _s.AddActivity($"Server implicit: {Selected.Name}");
    }

    public async Task RefreshOneAsync(ServerRow row)
    {
        await ServerQueryService.RefreshAsync(row.Server);
        UiThread.Post(() => { row.Refresh(); _s.Servers.Persist(); _shell.Dashboard.Refresh(); });
    }

    public async Task RefreshAllAsync()
    {
        await Task.WhenAll(Rows.ToList().Select(RefreshOneAsync));
    }
}

// ------------------------------------------------------------------ Sessions
public sealed class SessionsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private SessionRow? _selected;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<SessionRow> Rows { get; } = new();

    public SessionsViewModel(AppServices s)
    {
        _s = s;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { foreach (var r in Rows) r.Tick(); };
        _timer.Start();
        DisconnectAllCommand = new RelayCommand(async () => await _s.Sessions.DisconnectAllAsync());
    }

    public ICommand DisconnectAllCommand { get; }

    public SessionRow? Selected { get => _selected; set => Set(ref _selected, value); }

    private SessionRow RowFor(SessionController c)
    {
        var row = Rows.FirstOrDefault(r => ReferenceEquals(r.Controller, c));
        if (row == null)
        {
            row = new SessionRow(_s, c);
            Rows.Add(row);
            Selected ??= row;
        }
        return row;
    }

    public void OnChanged(SessionController c) => RowFor(c).Refresh();
    public void OnLog(SessionController c, string line) => RowFor(c).AddLog(line);
}

// ------------------------------------------------------------------ Logs
public sealed class LogsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private string _filter = "";

    public ObservableCollection<string> Lines { get; } = new();
    public ICollectionView View { get; }

    public LogsViewModel(AppServices s)
    {
        _s = s;
        View = CollectionViewSource.GetDefaultView(Lines);
        View.Filter = o => o is string l && (_filter.Length == 0 || l.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        ClearCommand = new RelayCommand(() => Lines.Clear());
        OpenFolderCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _s.Settings.LogFolder + "\"") { UseShellExecute = true }); }
            catch { }
        });
    }

    public ICommand ClearCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) View.Refresh(); } }

    public void Add(string nick, string line)
    {
        Lines.Add($"{nick,-14} {line}");
        while (Lines.Count > 5000) Lines.RemoveAt(0);
    }
}

// ------------------------------------------------------------------ Dashboard
public sealed class DashboardViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly AppServices _s;

    public DashboardViewModel(ShellViewModel shell, AppServices s) { _shell = shell; _s = s; }

    public ObservableCollection<string> Activity => _s.Activity;
    public int TotalAccounts => _s.Accounts.Items.Count;
    public int ConfirmedSessions => _s.Sessions.ConfirmedOnlineCount;
    public string SelectedAccount => _shell.Accounts.Selected?.Nickname ?? "-";
    public string LastUsedAccount
    {
        get
        {
            var id = _s.Settings.Settings.LastUsedAccountId;
            return _s.Accounts.Items.FirstOrDefault(a => a.Id == id)?.Nickname ?? "-";
        }
    }
    public string SelectedServer => _shell.EffectiveServer is { } sv ? $"{sv.Name} ({sv.Host}:{sv.Port})" : "-";
    public string ConnectionSummary
    {
        get
        {
            var all = _s.Sessions.Sessions;
            if (all.Count == 0) return "Nicio sesiune pornita";
            var groups = all.GroupBy(x => x.Machine.State).Select(g => $"{g.Count()} {StateText.Of(g.Key)}");
            return string.Join("  ·  ", groups);
        }
    }

    public void Refresh()
    {
        if (!_shell.Ready) return;
        foreach (var n in new[] { nameof(TotalAccounts), nameof(ConfirmedSessions), nameof(SelectedAccount), nameof(LastUsedAccount),
                     nameof(SelectedServer), nameof(ConnectionSummary) })
            OnPropertyChanged(n);
    }
}

// ------------------------------------------------------------------ Settings
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly AppServices _s;
    private string _status = "";

    public SettingsViewModel(ShellViewModel shell, AppServices s)
    {
        _shell = shell; _s = s;
        BrowseLogsCommand = new RelayCommand(BrowseLogs);
        BackupCommand = new RelayCommand(Backup);
        RestoreCommand = new RelayCommand(Restore);
        SaveNowCommand = new RelayCommand(SaveNow);
        ResetLogFolderCommand = new RelayCommand(() => { _s.Settings.Settings.LogFolder = null; _s.Settings.Touch(); OnPropertyChanged(nameof(LogFolder)); });
    }

    public ICommand BrowseLogsCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand SaveNowCommand { get; }
    public ICommand ResetLogFolderCommand { get; }

    private AppSettings M => _s.Settings.Settings;

    public bool AutoStart
    {
        get => M.AutoStartWithWindows;
        set
        {
            if (M.AutoStartWithWindows == value) return;
            try
            {
                StartupRegistration.Set(value, Environment.ProcessPath ?? "");
                M.AutoStartWithWindows = value;
                _s.Settings.Touch();
            }
            catch (Exception ex) { Status = "Nu am putut modifica pornirea automata: " + ex.Message; }
            OnPropertyChanged();
        }
    }

    public bool AutoSave
    {
        get => M.AutoSave;
        set { if (M.AutoSave == value) return; M.AutoSave = value; _s.Settings.Touch(); if (value) SaveNow(); OnPropertyChanged(); OnPropertyChanged(nameof(ManualSaveVisible)); }
    }

    public bool ManualSaveVisible => !M.AutoSave;
    public string LogFolder => _s.Settings.LogFolder;

    public ObservableCollection<ServerRow> ServerChoices => _shell.Servers.Rows;
    public ServerRow? DefaultServer
    {
        get => _shell.Servers.Rows.FirstOrDefault(r => r.Server.Id == _s.Servers.Default?.Id);
        set
        {
            if (value == null) return;
            M.DefaultServerId = value.Server.Id;
            _s.Settings.Touch();
            foreach (var r in _shell.Servers.Rows) { r.IsDefault = r.Server.Id == value.Server.Id; r.Refresh(); }
            OnPropertyChanged();
            _shell.Dashboard.Refresh();
        }
    }

    public bool ReconnectEnabled { get => M.ReconnectEnabled; set { M.ReconnectEnabled = value; _s.Settings.Touch(); OnPropertyChanged(); } }
    public int ReconnectInterval
    {
        get => M.ReconnectIntervalSeconds;
        set { M.ReconnectIntervalSeconds = Math.Clamp(value, 5, 3600); _s.Settings.Touch(); OnPropertyChanged(); }
    }
    public int MaxReconnectAttempts
    {
        get => M.MaxReconnectAttempts;
        set { M.MaxReconnectAttempts = Math.Clamp(value, 0, 20); _s.Settings.Touch(); OnPropertyChanged(); }
    }

    public string Status { get => _status; set => Set(ref _status, value); }

    public void Reload()
    {
        foreach (var n in new[] { nameof(AutoStart), nameof(AutoSave), nameof(ManualSaveVisible), nameof(LogFolder), nameof(DefaultServer),
                     nameof(ReconnectEnabled), nameof(ReconnectInterval), nameof(MaxReconnectAttempts) })
            OnPropertyChanged(n);
    }

    private void BrowseLogs()
    {
        var dlg = new OpenFolderDialog { Title = "Alege folderul pentru loguri", InitialDirectory = _s.Settings.LogFolder };
        if (dlg.ShowDialog() != true) return;
        M.LogFolder = dlg.FolderName;
        _s.Settings.Touch();
        OnPropertyChanged(nameof(LogFolder));
    }

    private void SaveNow()
    {
        try
        {
            _s.Settings.Save(); _s.Accounts.Save(); _s.Servers.Save();
            Status = "Salvat la " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) { Status = "Salvare esuata: " + ex.Message; }
    }

    private void Backup()
    {
        try
        {
            SaveNow();
            var path = _s.Backup.CreateBackup();
            Status = "Backup creat: " + path;
            _s.AddActivity("Backup creat");
        }
        catch (Exception ex) { Status = "Backup esuat: " + ex.Message; }
    }

    private void Restore()
    {
        if (_s.Sessions.Sessions.Any(x => x.Machine.IsActive))
        {
            MessageBox.Show("Deconecteaza toate sesiunile inainte de restaurare.", "Restaurare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new OpenFileDialog { Filter = "Backup (*.zip)|*.zip", InitialDirectory = _s.Paths.BackupsDir, Title = "Alege backup-ul" };
        if (dlg.ShowDialog() != true) return;
        if (MessageBox.Show("Restaurarea inlocuieste conturile, serverele si setarile curente (se face un backup automat inainte). Continui?",
                "Restaurare", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _s.Backup.Restore(dlg.FileName);
            _s.Settings.Reload(); _s.Accounts.Reload(); _s.Servers.Reload();
            _shell.ReloadAll();
            Status = "Restaurat din " + System.IO.Path.GetFileName(dlg.FileName) +
                     ". Parolele sunt protejate pe utilizatorul Windows care a facut backup-ul.";
            _s.AddActivity("Backup restaurat");
        }
        catch (Exception ex) { Status = "Restaurare esuata: " + ex.Message; }
    }
}
