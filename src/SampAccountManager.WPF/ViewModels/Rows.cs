using System.Collections.ObjectModel;
using System.Windows.Input;
using SampAccountManager.Core;
using SampAccountManager.Models;
using SampAccountManager.Services;
using SampAccountManager.WPF.Infrastructure;

namespace SampAccountManager.WPF.ViewModels;

public static class StateText
{
    public static string Of(SessionState s) => s switch
    {
        SessionState.WaitingForSpawn => "Waiting for Spawn",
        SessionState.SpawnConfirmed => "Spawn Confirmed",
        _ => s.ToString(),
    };
}

public sealed class AccountRow : ObservableObject
{
    public Account Account { get; }
    private SessionState _state = SessionState.Offline;

    public AccountRow(Account a) => Account = a;

    public string Nickname => Account.Nickname;
    public string Notes => Account.Notes;
    public string LastUsedText => Account.LastUsedAt is { } d ? "Ultima utilizare: " + d.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "Neutilizat inca";
    public SessionState State { get => _state; private set { if (Set(ref _state, value)) OnPropertyChanged(nameof(StateLabel)); } }
    public string StateLabel => StateText.Of(_state);

    public void Refresh(SessionState? state = null)
    {
        if (state != null) State = state.Value;
        OnPropertyChanged(nameof(Nickname));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(LastUsedText));
    }
}

public sealed class ServerRow : ObservableObject
{
    public ServerEntry Server { get; }
    public ServerRow(ServerEntry s) => Server = s;
    public string Name => Server.Name;
    public string Address => $"{Server.Host}:{Server.Port}";
    public string Description => Server.Description;
    public ServerStatus Status => Server.Status;
    public string StatusText => Server.Status switch
    {
        ServerStatus.Online => $"Online · {Server.Players}/{Server.MaxPlayers} jucatori",
        ServerStatus.Offline => "Offline / nu raspunde",
        _ => "Necunoscut",
    };
    public string? HostName => Server.ReportedHostName;
    public bool IsDefault { get; set; }
    public string DefaultText => IsDefault ? "implicit" : "";

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Address)); OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(HostName));
        OnPropertyChanged(nameof(DefaultText));
    }
}

public sealed class SessionRow : ObservableObject
{
    private readonly AppServices _s;
    public SessionController Controller { get; }
    public ObservableCollection<string> Log { get; } = new();

    public SessionRow(AppServices s, SessionController c)
    {
        _s = s; Controller = c;
        lock (c.LogLines) foreach (var l in c.LogLines) Log.Add(l);
        ConnectCommand = new RelayCommand(Connect, () => CanConnect);
        DisconnectCommand = new RelayCommand(() => _ = Controller.StopAsync(), () => CanDisconnect);
        ReconnectCommand = new RelayCommand(Reconnect, () => Controller.Machine.State != SessionState.Disconnecting);
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ReconnectCommand { get; }

    public string Nickname => Controller.Nickname;
    public string ServerText => $"{Controller.Server.Name} ({Controller.Server.Host}:{Controller.Server.Port})";
    public SessionState State => Controller.Machine.State;
    public string StateLabel => StateText.Of(State);
    public bool CanConnect => !Controller.Machine.IsActive;
    public bool CanDisconnect => Controller.Machine.IsActive && State != SessionState.Disconnecting;

    public string ConnectedFor
    {
        get
        {
            var at = Controller.Machine.ConnectedAtUtc;
            if (at == null || !Controller.Machine.IsActive) return "-";
            var t = DateTime.UtcNow - at.Value;
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    public string PositionText
    {
        get
        {
            var m = Controller.Machine;
            return m.X == null ? "Pozitie necunoscuta (serverul nu a trimis-o inca)"
                : $"X {m.X:0.##}   Y {m.Y:0.##}   Z {m.Z:0.##}   (sursa: {m.PositionSource}, raportat de server)";
        }
    }

    public string WorldText => "Lume virtuala: indisponibila (serverul nu o trimite clientului)";
    public string ServerNameText => Controller.Machine.ServerHostName is { Length: > 0 } n ? "Server: " + n : "";
    public string Note
    {
        get
        {
            var m = Controller.Machine;
            if (m.LastError != null) return "Eroare: " + m.LastError;
            if (m.LastWarning != null) return "Atentie: " + m.LastWarning;
            return Controller.ReconnectInfo ?? "";
        }
    }

    public void Refresh()
    {
        foreach (var n in new[] { nameof(ServerText), nameof(State), nameof(StateLabel), nameof(CanConnect), nameof(CanDisconnect),
                     nameof(ConnectedFor), nameof(PositionText), nameof(ServerNameText), nameof(Note) })
            OnPropertyChanged(n);
        ((RelayCommand)ConnectCommand).Raise();
        ((RelayCommand)DisconnectCommand).Raise();
        ((RelayCommand)ReconnectCommand).Raise();
    }

    public void Tick() { OnPropertyChanged(nameof(ConnectedFor)); }

    public void AddLog(string line)
    {
        Log.Add(line);
        while (Log.Count > 1000) Log.RemoveAt(0);
    }

    private Account? FindAccount() => _s.Accounts.Items.FirstOrDefault(a => a.Id == Controller.AccountId);

    private void Connect()
    {
        var a = FindAccount();
        if (a == null) return;
        var err = _s.Sessions.Connect(a, Controller.Server);
        if (err != null) System.Windows.MessageBox.Show(err, "Conectare", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    private async void Reconnect()
    {
        var a = FindAccount();
        if (a == null) return;
        var err = await _s.Sessions.ReconnectAsync(a, Controller.Server);
        if (err != null) System.Windows.MessageBox.Show(err, "Reconectare", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }
}
