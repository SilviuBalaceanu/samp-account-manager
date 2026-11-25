using System.Text.Json;

namespace SampAccountManager.Models;

public enum SessionState
{
    Offline,
    Connecting,
    Connected,
    Authenticating,
    WaitingForSpawn,
    SpawnConfirmed,
    Online,
    Disconnecting,
    Disconnected,
    Error
}

public enum ServerStatus { Unknown, Online, Offline }

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nickname { get; set; } = "";
    /// <summary>Parola protejata (DPAPI, utilizatorul curent). Nu se stocheaza niciodata in clar.</summary>
    public string ProtectedPassword { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

public sealed class ServerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 7777;
    public string Description { get; set; } = "";
    public ServerStatus Status { get; set; } = ServerStatus.Unknown;
    public string? ReportedHostName { get; set; }
    public int? Players { get; set; }
    public int? MaxPlayers { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
}

public sealed class AppSettings
{
    public bool AutoStartWithWindows { get; set; }
    public bool AutoSave { get; set; } = true;
    public string? LogFolder { get; set; }
    public Guid? DefaultServerId { get; set; }
    public Guid? LastSelectedAccountId { get; set; }
    public Guid? LastUsedAccountId { get; set; }
    public bool ReconnectEnabled { get; set; }
    public int ReconnectIntervalSeconds { get; set; } = 30;
    public int MaxReconnectAttempts { get; set; } = 3;
}

/// <summary>Eveniment real primit de la clientul Rust (o linie JSON).</summary>
public sealed class HeadlessEvent
{
    public string SessionId { get; set; } = "";
    public string Event { get; set; } = "";
    public long Ts { get; set; }
    public JsonElement Data { get; set; }

    public string? GetString(string name) =>
        Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public double? GetDouble(string name) =>
        Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    public bool? GetBool(string name) =>
        Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;
}
