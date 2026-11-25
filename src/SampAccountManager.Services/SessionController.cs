using SampAccountManager.Core;
using SampAccountManager.Models;

namespace SampAccountManager.Services;

/// <summary>Sesiunea unui cont: proces Rust + masina de stari + jurnal individual + reconectare optionala.</summary>
public sealed class SessionController : IDisposable
{
    private readonly AppPaths _paths;
    private readonly LogService _logs;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string> _passwordProvider;
    private readonly object _gate = new();
    private HeadlessClientProcess? _proc;
    private bool _userStop;
    private int _reconnectAttempts;
    private CancellationTokenSource? _reconnectCts;

    public Guid AccountId { get; }
    public string Nickname { get; }
    public ServerEntry Server { get; private set; }
    public SessionStateMachine Machine { get; } = new();
    public List<string> LogLines { get; } = new();
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
    public string? ReconnectInfo { get; private set; }

    public event Action<SessionController>? Changed;
    public event Action<SessionController, string>? LogAdded;

    public SessionController(Account account, ServerEntry server, AppPaths paths, LogService logs,
        Func<AppSettings> settings, Func<string> passwordProvider)
    {
        AccountId = account.Id; Nickname = account.Nickname; Server = server;
        _paths = paths; _logs = logs; _settings = settings; _passwordProvider = passwordProvider;
    }

    public void Log(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        lock (LogLines)
        {
            LogLines.Add(stamped);
            if (LogLines.Count > 2000) LogLines.RemoveRange(0, 500);
        }
        _logs.Append("session-" + Nickname, stamped);
        LogAdded?.Invoke(this, stamped);
    }

    /// <summary>Porneste sesiunea. Intoarce null la succes sau mesajul de eroare.</summary>
    public string? Start(ServerEntry server)
    {
        lock (_gate)
        {
            if (Machine.IsActive) return "Sesiunea este deja activa.";
            var exe = _paths.FindHeadlessClient();
            if (exe == null)
            {
                Machine.BeginStart();
                Machine.ProcessExited();
                Log("EROARE: samp_headless_client.exe nu a fost gasit langa aplicatie.");
                Changed?.Invoke(this);
                return "Clientul headless (samp_headless_client.exe) nu a fost gasit.";
            }
            _reconnectCts?.Cancel();
            Server = server;
            _userStop = false;
            SessionId = Guid.NewGuid().ToString("N");
            Machine.BeginStart();
            ReconnectInfo = null;
            Log($"Pornire sesiune catre {server.Name} ({server.Host}:{server.Port}) ca {Nickname}");
            Changed?.Invoke(this);
            try
            {
                var p = new HeadlessClientProcess(exe, SessionId);
                p.EventReceived += OnEvent;
                p.InvalidLine += l => Log("linie necunoscuta de la client: " + Trim(l));
                p.Exited += () => OnProcessExited(p);
                _proc = p;
                p.Start();
                // parola e decriptata doar aici si trimisa pe stdin; nu se logheaza
                p.SendConnect(server.Host, server.Port, Nickname, _passwordProvider());
                Log($"Client headless pornit (PID {p.ProcessId}).");
                return null;
            }
            catch (Exception ex)
            {
                Machine.ProcessExited();
                Log("EROARE la pornirea clientului: " + ex.Message);
                Changed?.Invoke(this);
                return ex.Message;
            }
        }
    }

    public async Task StopAsync()
    {
        HeadlessClientProcess? p;
        lock (_gate)
        {
            _userStop = true;
            _reconnectCts?.Cancel();
            ReconnectInfo = null;
            p = _proc;
            if (p == null || !Machine.IsActive) { Changed?.Invoke(this); return; }
            Machine.BeginDisconnect();
        }
        Log("Deconectare ceruta de utilizator...");
        Changed?.Invoke(this);
        await p.StopAsync(TimeSpan.FromSeconds(5));
    }

    private void OnEvent(HeadlessEvent e)
    {
        lock (_gate)
        {
            if (e.SessionId != SessionId) return; // evenimente doar pentru sesiunea curenta
            Machine.Apply(e);
        }
        Log(Describe(e));
        Changed?.Invoke(this);
        if (Machine.State == SessionState.Error && _proc != null && !_proc.HasExited)
        {
            // eroare terminala: nu lasam procesul in fundal
            var p = _proc;
            _ = Task.Run(() => p.StopAsync(TimeSpan.FromSeconds(3)));
        }
    }

    private void OnProcessExited(HeadlessClientProcess p)
    {
        bool schedule;
        lock (_gate)
        {
            if (!ReferenceEquals(p, _proc)) return;
            var before = Machine.State;
            Machine.ProcessExited();
            if (before != Machine.State) Log($"Procesul clientului s-a oprit (stare: {Machine.State}).");
            schedule = !_userStop && ShouldReconnect();
        }
        Changed?.Invoke(this);
        if (schedule) ScheduleReconnect();
    }

    private bool ShouldReconnect()
    {
        var s = _settings();
        if (!s.ReconnectEnabled) return false;
        if (_reconnectAttempts >= Math.Max(0, s.MaxReconnectAttempts)) return false;
        var reason = Machine.DisconnectReason ?? "";
        var err = Machine.LastError ?? "";
        // nu insistam dupa ban, parola de server gresita sau respingere explicita
        if (reason.Contains("ban", StringComparison.OrdinalIgnoreCase) || err.Contains("ban", StringComparison.OrdinalIgnoreCase)) return false;
        if (err.Contains("respins", StringComparison.OrdinalIgnoreCase) || err.Contains("parola de server", StringComparison.OrdinalIgnoreCase)) return false;
        return Machine.State is SessionState.Error or SessionState.Disconnected;
    }

    private void ScheduleReconnect()
    {
        var s = _settings();
        _reconnectAttempts++;
        var delay = TimeSpan.FromSeconds(Math.Max(5, s.ReconnectIntervalSeconds));
        _reconnectCts?.Cancel();
        var cts = _reconnectCts = new CancellationTokenSource();
        ReconnectInfo = $"Reconectare {_reconnectAttempts}/{s.MaxReconnectAttempts} in {delay.TotalSeconds:0}s";
        Log(ReconnectInfo);
        Changed?.Invoke(this);
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token); }
            catch (TaskCanceledException) { return; }
            Start(Server);
        });
    }

    public void ResetReconnectCounter() => _reconnectAttempts = 0;

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "..." : s;

    public static string Describe(HeadlessEvent e) => e.Event switch
    {
        "ClientStarted" => "Client headless pornit",
        "Connecting" => $"Conectare la {e.GetString("host")}:{e.GetDouble("port")}",
        "Connected" => $"Conexiune RakNet acceptata de server (id jucator {e.GetDouble("playerId")})",
        "Joined" => $"Serverul a acceptat intrarea in joc: \"{e.GetString("hostName")}\"",
        "DialogShown" => $"Dialog primit de la server: \"{e.GetString("title")}\"",
        "AuthenticationStarted" => $"Autentificare: raspuns trimis la dialogul \"{e.GetString("title")}\"",
        "AuthenticationCompleted" => "Autentificare incheiata (dedusa din raspunsul serverului)",
        "SpawnRequested" => $"Cerere de spawn trimisa (incercarea {e.GetDouble("attempt")})",
        "SpawnConfirmed" => "SPAWN CONFIRMAT: serverul a acceptat cererea de spawn",
        "Online" => "ONLINE: spawn acceptat si conexiune activa",
        "PositionUpdate" => $"Pozitie raportata de server ({e.GetString("source")}): X={e.GetDouble("x"):0.##} Y={e.GetDouble("y"):0.##} Z={e.GetDouble("z"):0.##}",
        "ServerMessage" => "Server: " + e.GetString("text"),
        "Log" => $"[{e.GetString("level")}] {e.GetString("message")}",
        "ConnectionError" => $"EROARE ({e.GetString("code")}): {e.GetString("message")}",
        "Disconnected" => $"Deconectat: {e.GetString("message") ?? e.GetString("reason")}",
        _ => "Eveniment: " + e.Event,
    };

    public void Dispose()
    {
        _reconnectCts?.Cancel();
        _proc?.Dispose();
    }
}
