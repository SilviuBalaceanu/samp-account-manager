using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SampAccountManager.Core;
using SampAccountManager.Models;

namespace SampAccountManager.Services;

/// <summary>Un proces samp_headless_client = o sesiune. Comunicare JSON Lines pe stdin/stdout, fara fereastra.</summary>
public sealed class HeadlessClientProcess : IDisposable
{
    private readonly Process _process;
    private readonly object _writeGate = new();
    private bool _disposed;

    public string SessionId { get; }
    public event Action<HeadlessEvent>? EventReceived;
    public event Action<string>? InvalidLine;
    public event Action? Exited;

    public HeadlessClientProcess(string exePath, string sessionId)
    {
        SessionId = sessionId;
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--session-id");
        psi.ArgumentList.Add(sessionId);
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (HeadlessEventParser.TryParse(e.Data, out var ev) && ev != null) EventReceived?.Invoke(ev);
            else InvalidLine?.Invoke(e.Data);
        };
        _process.ErrorDataReceived += (_, e) => { /* stderr nefolosit; consumat ca sa nu blocheze procesul */ };
        _process.Exited += (_, _) => Exited?.Invoke();
    }

    public int? ProcessId { get; private set; }

    public void Start()
    {
        _process.Start();
        ProcessId = _process.Id;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch { return true; } }
    }

    private void Send(object payload)
    {
        lock (_writeGate)
        {
            try
            {
                if (HasExited) return;
                _process.StandardInput.WriteLine(JsonSerializer.Serialize(payload));
                _process.StandardInput.Flush();
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }
    }

    public void SendConnect(string host, int port, string nick, string password) =>
        Send(new Dictionary<string, object> { ["cmd"] = "connect", ["host"] = host, ["port"] = port, ["nick"] = nick, ["password"] = password });

    public void SendDisconnect() => Send(new Dictionary<string, object> { ["cmd"] = "disconnect" });

    /// <summary>Opreste procesul: cere deconectare, asteapta, apoi forteaza daca e nevoie (fara procese ramase).</summary>
    public async Task StopAsync(TimeSpan graceful)
    {
        if (HasExited) return;
        SendDisconnect();
        var deadline = DateTime.UtcNow + graceful;
        while (!HasExited && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!HasExited)
        {
            try { _process.StandardInput.Close(); } catch { }
            var d2 = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (!HasExited && DateTime.UtcNow < d2) await Task.Delay(50);
        }
        if (!HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    public void Kill()
    {
        try { if (!HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        _process.Dispose();
    }
}
