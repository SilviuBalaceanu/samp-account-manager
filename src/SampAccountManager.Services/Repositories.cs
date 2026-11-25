using SampAccountManager.Core;
using SampAccountManager.Models;

namespace SampAccountManager.Services;

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    public AppSettings Settings { get; private set; }
    public bool Dirty { get; private set; }
    public event Action? Changed;

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
        Settings = JsonFileStore.Load(paths.SettingsFile, () => new AppSettings());
    }

    public string LogFolder => string.IsNullOrWhiteSpace(Settings.LogFolder) ? _paths.DefaultLogsDir : Settings.LogFolder!;

    public void Touch()
    {
        Dirty = true;
        if (Settings.AutoSave) Save();
        Changed?.Invoke();
    }

    public void Save()
    {
        JsonFileStore.Save(_paths.SettingsFile, Settings);
        Dirty = false;
    }

    public void Reload()
    {
        Settings = JsonFileStore.Load(_paths.SettingsFile, () => new AppSettings());
        Dirty = false;
        Changed?.Invoke();
    }
}

public sealed class AccountRepository
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly IPasswordProtector _protector;
    public List<Account> Items { get; private set; }
    public bool Dirty { get; private set; }

    public AccountRepository(AppPaths paths, SettingsService settings, IPasswordProtector protector)
    {
        _paths = paths; _settings = settings; _protector = protector;
        Items = JsonFileStore.Load(paths.AccountsFile, () => new List<Account>());
    }

    public string? Validate(string nickname, Guid? editingId)
    {
        nickname = nickname.Trim();
        if (nickname.Length is < 3 or > 24) return "Nickname-ul trebuie sa aiba intre 3 si 24 de caractere.";
        if (Items.Any(a => a.Id != editingId && a.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)))
            return "Exista deja un cont cu acest nickname.";
        return null;
    }

    public Account Add(string nickname, string password, string notes)
    {
        var a = new Account { Nickname = nickname.Trim(), ProtectedPassword = _protector.Protect(password), Notes = notes };
        Items.Add(a);
        Persist();
        return a;
    }

    /// <summary>Parola goala la editare = pastreaza parola existenta.</summary>
    public void Update(Account account, string nickname, string? newPassword, string notes)
    {
        account.Nickname = nickname.Trim();
        account.Notes = notes;
        if (!string.IsNullOrEmpty(newPassword)) account.ProtectedPassword = _protector.Protect(newPassword);
        Persist();
    }

    public void Remove(Account account)
    {
        Items.Remove(account);
        Persist();
    }

    public string GetPassword(Account account) => _protector.Unprotect(account.ProtectedPassword);

    public void MarkUsed(Account account)
    {
        account.LastUsedAt = DateTime.UtcNow;
        _settings.Settings.LastUsedAccountId = account.Id;
        _settings.Touch();
        Persist();
    }

    public void Persist()
    {
        Dirty = true;
        if (_settings.Settings.AutoSave) Save();
    }

    public void Save()
    {
        JsonFileStore.Save(_paths.AccountsFile, Items);
        Dirty = false;
    }

    public void Reload()
    {
        Items = JsonFileStore.Load(_paths.AccountsFile, () => new List<Account>());
        Dirty = false;
    }
}

public sealed class ServerRepository
{
    public const string DefaultName = "B-Zone";
    public const string DefaultHost = "rpg.b-zone.ro";
    public const int DefaultPort = 7777;

    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    public List<ServerEntry> Items { get; private set; }
    public bool Dirty { get; private set; }

    public ServerRepository(AppPaths paths, SettingsService settings)
    {
        _paths = paths; _settings = settings;
        Items = JsonFileStore.Load(paths.ServersFile, () => new List<ServerEntry>());
        EnsureDefault();
    }

    /// <summary>B-Zone este mereu prezent implicit (se adauga daca lipseste, cand lista e goala sau nu il contine).</summary>
    private void EnsureDefault()
    {
        if (Items.Any(s => s.Host.Equals(DefaultHost, StringComparison.OrdinalIgnoreCase) && s.Port == DefaultPort)) return;
        var s = new ServerEntry { Name = DefaultName, Host = DefaultHost, Port = DefaultPort, Description = "Server implicit B-Zone" };
        Items.Insert(0, s);
        if (_settings.Settings.DefaultServerId == null)
        {
            _settings.Settings.DefaultServerId = s.Id;
            _settings.Touch();
        }
        Persist();
    }

    public string? Validate(string name, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Numele este obligatoriu.";
        if (string.IsNullOrWhiteSpace(host)) return "IP-ul / host-ul este obligatoriu.";
        if (port is < 1 or > 65535) return "Portul trebuie sa fie intre 1 si 65535.";
        return null;
    }

    public ServerEntry Add(string name, string host, int port, string description)
    {
        var s = new ServerEntry { Name = name.Trim(), Host = host.Trim(), Port = port, Description = description };
        Items.Add(s);
        Persist();
        return s;
    }

    public void Update(ServerEntry s, string name, string host, int port, string description)
    {
        s.Name = name.Trim(); s.Host = host.Trim(); s.Port = port; s.Description = description;
        s.Status = ServerStatus.Unknown;
        Persist();
    }

    public void Remove(ServerEntry s)
    {
        Items.Remove(s);
        if (_settings.Settings.DefaultServerId == s.Id)
        {
            _settings.Settings.DefaultServerId = Items.FirstOrDefault()?.Id;
            _settings.Touch();
        }
        Persist();
    }

    public ServerEntry? Default =>
        Items.FirstOrDefault(s => s.Id == _settings.Settings.DefaultServerId) ?? Items.FirstOrDefault();

    public void Persist()
    {
        Dirty = true;
        if (_settings.Settings.AutoSave) Save();
    }

    public void Save()
    {
        JsonFileStore.Save(_paths.ServersFile, Items);
        Dirty = false;
    }

    public void Reload()
    {
        Items = JsonFileStore.Load(_paths.ServersFile, () => new List<ServerEntry>());
        EnsureDefault();
        Dirty = false;
    }
}
