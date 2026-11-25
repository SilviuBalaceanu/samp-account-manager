using SampAccountManager.Models;

namespace SampAccountManager.Services;

/// <summary>Gestioneaza sesiunile individuale (cate una per cont). Impiedica conectarea dubla a aceluiasi cont.</summary>
public sealed class SessionManager : IDisposable
{
    private readonly AppPaths _paths;
    private readonly LogService _logs;
    private readonly SettingsService _settings;
    private readonly AccountRepository _accounts;
    private readonly Dictionary<Guid, SessionController> _sessions = new();
    private readonly object _gate = new();

    public event Action<SessionController>? SessionChanged;
    public event Action<SessionController, string>? SessionLog;

    public SessionManager(AppPaths paths, LogService logs, SettingsService settings, AccountRepository accounts)
    {
        _paths = paths; _logs = logs; _settings = settings; _accounts = accounts;
    }

    public IReadOnlyList<SessionController> Sessions
    {
        get { lock (_gate) return _sessions.Values.ToList(); }
    }

    public SessionController GetOrCreate(Account account, ServerEntry server)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(account.Id, out var existing)) return existing;
            var c = new SessionController(account, server, _paths, _logs, () => _settings.Settings,
                () => _accounts.GetPassword(account));
            c.Changed += s => SessionChanged?.Invoke(s);
            c.LogAdded += (s, l) => SessionLog?.Invoke(s, l);
            _sessions[account.Id] = c;
            return c;
        }
    }

    /// <summary>Conectare. Refuza daca contul (sau acelasi nickname) are deja o sesiune activa.</summary>
    public string? Connect(Account account, ServerEntry server)
    {
        lock (_gate)
        {
            if (_sessions.Values.Any(s => s.Machine.IsActive &&
                    (s.AccountId == account.Id || s.Nickname.Equals(account.Nickname, StringComparison.OrdinalIgnoreCase))))
                return $"Contul {account.Nickname} are deja o sesiune activa; deconecteaza-l intai.";
        }
        var session = GetOrCreate(account, server);
        session.ResetReconnectCounter();
        var err = session.Start(server);
        if (err == null) _accounts.MarkUsed(account);
        return err;
    }

    public Task DisconnectAsync(Guid accountId)
    {
        SessionController? s;
        lock (_gate) _sessions.TryGetValue(accountId, out s);
        return s?.StopAsync() ?? Task.CompletedTask;
    }

    public async Task<string?> ReconnectAsync(Account account, ServerEntry server)
    {
        await DisconnectAsync(account.Id);
        return Connect(account, server);
    }

    public async Task DisconnectAllAsync()
    {
        List<SessionController> all;
        lock (_gate) all = _sessions.Values.ToList();
        await Task.WhenAll(all.Select(s => s.StopAsync()));
    }

    public int ConfirmedOnlineCount
    {
        get { lock (_gate) return _sessions.Values.Count(s => s.Machine.IsConfirmedActive); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }
}
