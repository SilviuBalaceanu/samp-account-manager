using SampAccountManager.Core;
using SampAccountManager.Models;
using Xunit;

namespace SampAccountManager.Tests;

public class SessionStateMachineTests
{
    private static HeadlessEvent Ev(string name, string dataJson = "{}")
    {
        var line = "{\"sessionId\":\"s\",\"event\":\"" + name + "\",\"ts\":1,\"data\":" + dataJson + "}";
        Assert.True(HeadlessEventParser.TryParse(line, out var ev));
        return ev!;
    }

    private static SessionStateMachine Started()
    {
        var m = new SessionStateMachine();
        m.BeginStart();
        return m;
    }

    [Fact]
    public void Online_is_ignored_without_spawn_confirmation()
    {
        var m = Started();
        m.Apply(Ev("Connected", "{\"playerId\":3}"));
        m.Apply(Ev("Online"));
        Assert.Equal(SessionState.Connected, m.State);
        Assert.False(m.IsConfirmedActive);
    }

    [Fact]
    public void Full_flow_reaches_online_only_through_spawn_confirmed()
    {
        var m = Started();
        m.Apply(Ev("ClientStarted"));
        m.Apply(Ev("Connected", "{\"playerId\":3}"));
        Assert.Equal(SessionState.Connected, m.State);
        m.Apply(Ev("AuthenticationStarted"));
        Assert.Equal(SessionState.Authenticating, m.State);
        m.Apply(Ev("AuthenticationCompleted"));
        Assert.Equal(SessionState.WaitingForSpawn, m.State);
        m.Apply(Ev("SpawnRequested"));
        Assert.Equal(SessionState.WaitingForSpawn, m.State);
        m.Apply(Ev("SpawnConfirmed"));
        Assert.Equal(SessionState.SpawnConfirmed, m.State);
        m.Apply(Ev("Online"));
        Assert.Equal(SessionState.Online, m.State);
        Assert.True(m.IsConfirmedActive);
    }

    [Fact]
    public void Spawn_confirmed_without_requesting_spawn_is_ignored()
    {
        var m = Started();
        m.Apply(Ev("Connected"));
        m.Apply(Ev("SpawnConfirmed"));
        Assert.Equal(SessionState.Connected, m.State);
    }

    [Fact]
    public void Connection_error_is_terminal_and_late_events_are_ignored()
    {
        var m = Started();
        m.Apply(Ev("Connected"));
        m.Apply(Ev("ConnectionError", "{\"code\":\"Banned\",\"message\":\"ban\"}"));
        Assert.Equal(SessionState.Error, m.State);
        m.Apply(Ev("Online"));
        m.Apply(Ev("Disconnected", "{\"reason\":\"Banned\"}"));
        Assert.Equal(SessionState.Error, m.State);
        Assert.Equal("ban", m.LastError);
    }

    [Fact]
    public void Authentication_not_confirmed_is_a_warning_not_a_success()
    {
        var m = Started();
        m.Apply(Ev("Connected"));
        m.Apply(Ev("AuthenticationStarted"));
        m.Apply(Ev("ConnectionError", "{\"code\":\"AuthenticationNotConfirmed\",\"message\":\"nu\"}"));
        Assert.Equal(SessionState.Authenticating, m.State);
        Assert.Equal("nu", m.LastWarning);
    }

    [Fact]
    public void Process_exit_while_active_is_error_but_while_disconnecting_is_disconnected()
    {
        var a = Started();
        a.Apply(Ev("Connected"));
        a.ProcessExited();
        Assert.Equal(SessionState.Error, a.State);

        var b = Started();
        b.Apply(Ev("Connected"));
        b.BeginDisconnect();
        Assert.Equal(SessionState.Disconnecting, b.State);
        b.ProcessExited();
        Assert.Equal(SessionState.Disconnected, b.State);
    }

    [Fact]
    public void Position_is_stored_as_reported_by_server()
    {
        var m = Started();
        m.Apply(Ev("Connected"));
        m.Apply(Ev("PositionUpdate", "{\"source\":\"SetPlayerPos\",\"x\":1.5,\"y\":-2,\"z\":10,\"virtualWorld\":null}"));
        Assert.Equal(1.5, m.X);
        Assert.Equal(-2, m.Y);
        Assert.Equal("SetPlayerPos", m.PositionSource);
    }

    [Fact]
    public void Parser_rejects_garbage()
    {
        Assert.False(HeadlessEventParser.TryParse("nu e json", out _));
        Assert.False(HeadlessEventParser.TryParse("{\"x\":1}", out _));
        Assert.False(HeadlessEventParser.TryParse("", out _));
    }
}
