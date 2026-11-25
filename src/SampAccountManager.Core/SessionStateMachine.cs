using SampAccountManager.Models;

namespace SampAccountManager.Core;

/// <summary>
/// Starea reala a unei sesiuni, derivata EXCLUSIV din evenimentele clientului headless.
/// "Online" este acceptat doar daca sesiunea a trecut prin SpawnConfirmed (spawn acceptat de server).
/// </summary>
public sealed class SessionStateMachine
{
    public SessionState State { get; private set; } = SessionState.Offline;
    public string? LastError { get; private set; }
    public string? LastWarning { get; private set; }
    public string? ServerHostName { get; private set; }
    public int? PlayerId { get; private set; }
    public double? X { get; private set; }
    public double? Y { get; private set; }
    public double? Z { get; private set; }
    public string? PositionSource { get; private set; }
    /// <summary>Momentul in care serverul a acceptat conexiunea RakNet (UTC).</summary>
    public DateTime? ConnectedAtUtc { get; private set; }
    public string? DisconnectReason { get; private set; }

    public bool IsActive => State is SessionState.Connecting or SessionState.Connected or SessionState.Authenticating
        or SessionState.WaitingForSpawn or SessionState.SpawnConfirmed or SessionState.Online or SessionState.Disconnecting;

    /// <summary>True doar cand serverul a confirmat spawn-ul si conexiunea e activa.</summary>
    public bool IsConfirmedActive => State is SessionState.SpawnConfirmed or SessionState.Online;

    public void BeginStart()
    {
        State = SessionState.Connecting;
        LastError = null; LastWarning = null; ServerHostName = null; PlayerId = null;
        X = Y = Z = null; PositionSource = null; ConnectedAtUtc = null; DisconnectReason = null;
    }

    public void BeginDisconnect()
    {
        if (IsActive) State = SessionState.Disconnecting;
    }

    /// <summary>Procesul clientului s-a oprit. Daca nu a raportat Disconnected, e o eroare.</summary>
    public void ProcessExited()
    {
        if (State == SessionState.Disconnecting)
        {
            State = SessionState.Disconnected;
        }
        else if (IsActive)
        {
            LastError ??= "Clientul headless s-a oprit neasteptat.";
            State = SessionState.Error;
        }
    }

    public void Apply(HeadlessEvent e)
    {
        // dupa o stare terminala ignoram evenimente intarziate
        if (State is SessionState.Offline or SessionState.Disconnected or SessionState.Error) return;

        switch (e.Event)
        {
            case "ClientStarted":
            case "Connecting":
                if (State == SessionState.Connecting) { /* ramane Connecting */ }
                break;

            case "Connected":
                if (State == SessionState.Connecting)
                {
                    State = SessionState.Connected;
                    ConnectedAtUtc = DateTime.UtcNow;
                }
                PlayerId = (int?)e.GetDouble("playerId") ?? PlayerId;
                break;

            case "Joined":
                ServerHostName = e.GetString("hostName") ?? ServerHostName;
                PlayerId = (int?)e.GetDouble("playerId") ?? PlayerId;
                break;

            case "AuthenticationStarted":
                if (State is SessionState.Connected or SessionState.Connecting) State = SessionState.Authenticating;
                break;

            case "AuthenticationCompleted":
                if (State == SessionState.Authenticating) State = SessionState.WaitingForSpawn;
                break;

            case "SpawnRequested":
                if (State is SessionState.Connected or SessionState.Authenticating or SessionState.WaitingForSpawn)
                    State = SessionState.WaitingForSpawn;
                break;

            case "SpawnConfirmed":
                if (State == SessionState.WaitingForSpawn) State = SessionState.SpawnConfirmed;
                break;

            case "Online":
                // regula centrala: Online doar dupa SpawnConfirmed
                if (State == SessionState.SpawnConfirmed) State = SessionState.Online;
                break;

            case "PositionUpdate":
                X = e.GetDouble("x"); Y = e.GetDouble("y"); Z = e.GetDouble("z");
                PositionSource = e.GetString("source");
                break;

            case "ConnectionError":
            {
                var code = e.GetString("code");
                var msg = e.GetString("message") ?? code ?? "eroare necunoscuta";
                if (code == "AuthenticationNotConfirmed")
                {
                    LastWarning = msg; // nu inchide sesiunea, dar nu permite sa para reusita
                }
                else
                {
                    LastError = msg;
                    if (State != SessionState.Disconnecting) State = SessionState.Error;
                }
                break;
            }

            case "Disconnected":
                DisconnectReason = e.GetString("message") ?? e.GetString("reason");
                State = LastError != null && State != SessionState.Disconnecting ? SessionState.Error : SessionState.Disconnected;
                break;
        }
    }
}
