//! Logica sesiunii SA-MP: conectare, ClientJoin, clasa/spawn si raportarea de evenimente reale.
//!
//! Principii:
//!  * Nicio stare nu este simulata: fiecare eveniment corespunde unui pachet primit de la server
//!    (sau unui pachet trimis de noi, marcat explicit ca atare, ex. SpawnRequested).
//!  * Nu se trimite identitate hardware falsificata (campul GPCI/auth din ClientJoin ramane gol),
//!    nu se raspunde la verificarile anti-cheat ale serverului si nu se emuleaza un client real.
//!  * "Online" apare doar dupa ce serverul a acceptat cererea de spawn si conexiunea a ramas
//!    activa (server -> client) cel putin ONLINE_AFTER.

use std::io::Write;
use std::net::SocketAddrV4;
use std::sync::mpsc::{Receiver, RecvTimeoutError};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use crate::bitstream::{BitStreamReader, BitStreamWriter};
use crate::json::Obj;
use crate::transport::{self, Command, Peer, RakEvent};
use crate::types::DisconnectReason;

const RPC_CLIENT_JOIN: u8 = 25;
const RPC_SPAWN: u8 = 52;
const RPC_SHOW_DIALOG: u8 = 61;
const RPC_DIALOG_RESPONSE: u8 = 62;
const RPC_SET_SPAWN_INFO: u8 = 68;
const RPC_CLIENT_MESSAGE: u8 = 93;
const RPC_SET_PLAYER_POS: u8 = 12;
const RPC_TOGGLE_SPECTATING: u8 = 124;
const RPC_REQUEST_CLASS: u8 = 128;
const RPC_REQUEST_SPAWN: u8 = 129;
const RPC_CONNECTION_REJECTED: u8 = 130;
const RPC_INIT_GAME: u8 = 139;

const SAMP_VERSION_0_3_7: u32 = 4057;
const CHALLENGE_XOR: u32 = 0xFD9;
const CLIENT_VERSION: &str = "0.3.7-R3";

const AUTH_GRACE: Duration = Duration::from_secs(3);
const SPAWN_RETRY: Duration = Duration::from_secs(4);
const MAX_SPAWN_ATTEMPTS: u32 = 3;
const ONLINE_AFTER: Duration = Duration::from_secs(5);
const AUTH_CONFIRM_TIMEOUT: Duration = Duration::from_secs(20);

pub struct Config {
    pub session_id: String,
    pub server: SocketAddrV4,
    pub nick: String,
    pub password: String,
    pub server_password: Option<String>,
}

fn now_ms() -> u128 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis()).unwrap_or(0)
}

pub struct Out {
    session_id: String,
    sink: Option<std::sync::Arc<std::sync::Mutex<Vec<String>>>>,
}

impl Out {
    pub fn new(session_id: &str) -> Self {
        Out { session_id: session_id.to_string(), sink: None }
    }
    #[cfg(test)]
    pub fn capture(session_id: &str) -> (Self, std::sync::Arc<std::sync::Mutex<Vec<String>>>) {
        let sink = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        (Out { session_id: session_id.to_string(), sink: Some(sink.clone()) }, sink)
    }
    pub fn event(&self, name: &str, data: Obj) {
        let line = Obj::new()
            .str("sessionId", &self.session_id)
            .str("event", name)
            .int("ts", now_ms() as i64)
            .raw("data", &data.build())
            .build();
        if let Some(sink) = &self.sink {
            sink.lock().unwrap().push(line);
            return;
        }
        let stdout = std::io::stdout();
        let mut h = stdout.lock();
        let _ = writeln!(h, "{line}");
        let _ = h.flush();
    }
    pub fn log(&self, level: &str, msg: &str) {
        self.event("Log", Obj::new().str("level", level).str("message", msg));
    }
}

fn str8_lossy(r: &mut BitStreamReader<'_>) -> Option<String> {
    let len = r.read_u8().ok()? as usize;
    let b = r.read_bytes(len).ok()?;
    Some(b.iter().map(|&c| c as char).collect())
}

fn latin1(b: &[u8]) -> String {
    b.iter().map(|&c| c as char).collect()
}

#[derive(PartialEq, Eq, Clone, Copy, Debug)]
enum Phase {
    Connecting,
    RakConnected,
    Joined,
    ClassSelected,
    SpawnRequested,
    SpawnConfirmed,
    Online,
}

struct Session<'a> {
    cfg: &'a Config,
    out: &'a Out,
    peer: Peer,
    phase: Phase,
    cookie_seen: bool,
    class_at: Option<Instant>,
    dialog_answered: bool,
    dialog_answered_at: Option<Instant>,
    auth_timeout_reported: bool,
    dialog_pending: bool,
    auth_started: bool,
    auth_completed: bool,
    spawn_info: Option<(f32, f32, f32)>,
    spawn_attempts: u32,
    last_spawn_request: Option<Instant>,
    spawn_confirmed_at: Option<Instant>,
    last_server_msg: Instant,
}

/// Ruleaza sesiunea pana la deconectare. Returneaza motivul final.
pub fn run(cfg: &Config, out: &Out, stdin_rx: &Receiver<crate::json::Object>) -> DisconnectReason {
    out.event("Connecting", Obj::new().str("host", &cfg.server.ip().to_string()).int("port", cfg.server.port() as i64));
    let peer = match transport::connect(cfg.server, cfg.server_password.clone()) {
        Ok(p) => p,
        Err(e) => {
            out.event("ConnectionError", Obj::new().str("code", "SocketError").str("message", &format!("socket UDP: {e}")));
            return DisconnectReason::AttemptFailed;
        }
    };
    let mut s = Session {
        cfg,
        out,
        peer,
        phase: Phase::Connecting,
        cookie_seen: false,
        class_at: None,
        dialog_answered: false,
        dialog_answered_at: None,
        auth_timeout_reported: false,
        dialog_pending: false,
        auth_started: false,
        auth_completed: false,
        spawn_info: None,
        spawn_attempts: 0,
        last_spawn_request: None,
        spawn_confirmed_at: None,
        last_server_msg: Instant::now(),
    };
    let reason = s.main_loop(stdin_rx);
    s.peer.join();
    reason
}

impl<'a> Session<'a> {
    fn rpc(&self, id: u8, payload: Vec<u8>) {
        let _ = self.peer.cmd.send(Command::Rpc { id, payload });
    }

    fn main_loop(&mut self, stdin_rx: &Receiver<crate::json::Object>) -> DisconnectReason {
        loop {
            // comenzi de la WPF
            while let Ok(cmd) = stdin_rx.try_recv() {
                if crate::json::get_str(&cmd, "cmd") == Some("disconnect") {
                    let _ = self.peer.cmd.send(Command::Disconnect);
                }
            }
            match self.peer.events.recv_timeout(Duration::from_millis(25)) {
                Ok(RakEvent::Disconnected(reason)) => {
                    self.on_disconnected(reason);
                    return reason;
                }
                Ok(ev) => self.on_event(ev),
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => {
                    self.on_disconnected(DisconnectReason::ConnectionLost);
                    return DisconnectReason::ConnectionLost;
                }
            }
            self.on_timers();
        }
    }

    fn on_disconnected(&mut self, reason: DisconnectReason) {
        let was_confirmed = matches!(self.phase, Phase::SpawnConfirmed | Phase::Online);
        let is_error = !matches!(reason, DisconnectReason::Local);
        if is_error {
            self.out.event(
                "ConnectionError",
                Obj::new()
                    .str("code", reason.code())
                    .str("message", reason.text())
                    .str("phase", &format!("{:?}", self.phase))
                    .bool("spawnWasConfirmed", was_confirmed),
            );
        }
        self.out.event("Disconnected", Obj::new().str("reason", reason.code()).str("message", reason.text()));
    }

    fn on_timers(&mut self) {
        let now = Instant::now();
        // dupa selectia clasei: asteptam o scurta fereastra pentru un dialog de login inainte de a cere spawn
        if self.phase == Phase::ClassSelected {
            let grace_over = self.class_at.map_or(false, |t| now.duration_since(t) >= AUTH_GRACE);
            let ready = if self.dialog_pending {
                false
            } else if self.dialog_answered {
                self.auth_completed
            } else {
                grace_over
            };
            if ready {
                self.request_spawn();
            }
        }
        if self.dialog_answered && !self.auth_completed && !self.auth_timeout_reported {
            if self.dialog_answered_at.map_or(false, |t| now.duration_since(t) >= AUTH_CONFIRM_TIMEOUT) {
                self.auth_timeout_reported = true;
                self.out.event(
                    "ConnectionError",
                    Obj::new()
                        .str("code", "AuthenticationNotConfirmed")
                        .str("message", "serverul nu a confirmat autentificarea (parola gresita sau flux necunoscut)"),
                );
            }
        }
        if self.phase == Phase::SpawnRequested {
            if let Some(t) = self.last_spawn_request {
                if now.duration_since(t) >= SPAWN_RETRY {
                    if self.spawn_attempts >= MAX_SPAWN_ATTEMPTS {
                        self.out.event(
                            "ConnectionError",
                            Obj::new()
                                .str("code", "SpawnNotConfirmed")
                                .str("message", "serverul nu a confirmat cererea de spawn dupa mai multe incercari"),
                        );
                        let _ = self.peer.cmd.send(Command::Disconnect);
                        self.last_spawn_request = None;
                    } else {
                        self.request_spawn();
                    }
                }
            }
        }
        if self.phase == Phase::SpawnConfirmed {
            if let Some(t) = self.spawn_confirmed_at {
                if now.duration_since(t) >= ONLINE_AFTER && now.duration_since(self.last_server_msg) < Duration::from_secs(10) {
                    self.phase = Phase::Online;
                    self.out.event(
                        "Online",
                        Obj::new().str("basis", "spawn acceptat de server si conexiune activa dupa 5 s"),
                    );
                }
            }
        }
    }

    fn request_spawn(&mut self) {
        self.spawn_attempts += 1;
        self.last_spawn_request = Some(Instant::now());
        self.phase = Phase::SpawnRequested;
        self.rpc(RPC_REQUEST_SPAWN, Vec::new());
        self.out.event("SpawnRequested", Obj::new().int("attempt", self.spawn_attempts as i64));
    }

    fn on_event(&mut self, ev: RakEvent) {
        self.last_server_msg = Instant::now();
        match ev {
            RakEvent::Connected { body } => self.on_connected(&body),
            RakEvent::Rpc { id, payload } => self.on_rpc(id, &payload),
            RakEvent::Packet { .. } => {}
            RakEvent::Disconnected(_) => {}
        }
    }

    fn on_connected(&mut self, body: &[u8]) {
        let mut r = BitStreamReader::new(body);
        let parsed = (|| {
            r.read_u32().ok()?;
            r.read_u16().ok()?;
            let pid = r.read_u16().ok()?;
            let cookie = r.read_u32().ok()?;
            Some((pid, cookie))
        })();
        let Some((player_id, cookie)) = parsed else {
            self.out.event("ConnectionError", Obj::new().str("code", "BadHandshake").str("message", "raspuns de conexiune ilizibil"));
            let _ = self.peer.cmd.send(Command::Disconnect);
            return;
        };
        self.cookie_seen = true;
        self.phase = Phase::RakConnected;
        self.out.event("Connected", Obj::new().int("playerId", player_id as i64).str("note", "handshake RakNet acceptat de server"));
        // ClientJoin: campul de autentificare hardware (GPCI) ramane gol: nu falsificam identitatea clientului.
        let mut w = BitStreamWriter::new();
        w.write_u32(SAMP_VERSION_0_3_7);
        w.write_u8(0);
        w.write_str8(&self.cfg.nick);
        w.write_u32(cookie ^ CHALLENGE_XOR);
        w.write_str8("");
        w.write_str8(CLIENT_VERSION);
        self.rpc(RPC_CLIENT_JOIN, w.into_bytes());
    }

    fn on_rpc(&mut self, id: u8, p: &[u8]) {
        match id {
            RPC_INIT_GAME => {
                // 104 biti setari, u16 id jucator, 275 biti setari, str8 hostname
                let mut r = BitStreamReader::new(p);
                let parsed = (|| {
                    r.skip_bits(104).ok()?;
                    let pid = r.read_u16().ok()?;
                    r.skip_bits(275).ok()?;
                    let host = str8_lossy(&mut r)?;
                    Some((pid, host))
                })();
                let (pid, host) = parsed.unwrap_or((0, String::new()));
                self.phase = Phase::Joined;
                self.out.event(
                    "Joined",
                    Obj::new().int("playerId", pid as i64).str("hostName", &host).bool("serverConfirmed", true),
                );
                let mut w = BitStreamWriter::new();
                w.write_i32(0);
                self.rpc(RPC_REQUEST_CLASS, w.into_bytes());
            }
            RPC_REQUEST_CLASS => {
                let allowed = p.first().copied().unwrap_or(0) != 0;
                if allowed && p.len() >= 47 {
                    let x = f32::from_le_bytes([p[7], p[8], p[9], p[10]]);
                    let y = f32::from_le_bytes([p[11], p[12], p[13], p[14]]);
                    let z = f32::from_le_bytes([p[15], p[16], p[17], p[18]]);
                    self.spawn_info.get_or_insert((x, y, z));
                }
                if self.phase == Phase::Joined {
                    self.phase = Phase::ClassSelected;
                    self.class_at = Some(Instant::now());
                }
                self.out.log("info", &format!("clasa: server allowed={allowed}"));
            }
            RPC_SHOW_DIALOG => self.on_dialog(p),
            RPC_SET_SPAWN_INFO => {
                if p.len() >= 18 {
                    let x = f32::from_le_bytes([p[6], p[7], p[8], p[9]]);
                    let y = f32::from_le_bytes([p[10], p[11], p[12], p[13]]);
                    let z = f32::from_le_bytes([p[14], p[15], p[16], p[17]]);
                    self.spawn_info = Some((x, y, z));
                    self.position("SetSpawnInfo", x, y, z);
                }
                self.mark_auth_completed("SetSpawnInfo");
            }
            RPC_TOGGLE_SPECTATING => {
                let on = p.first().copied().unwrap_or(1) != 0;
                if !on {
                    self.mark_auth_completed("TogglePlayerSpectating(0)");
                }
            }
            RPC_REQUEST_SPAWN => {
                let allow = p.first().copied().unwrap_or(0);
                if self.phase == Phase::SpawnRequested && allow != 0 {
                    self.phase = Phase::SpawnConfirmed;
                    self.spawn_confirmed_at = Some(Instant::now());
                    self.rpc(RPC_SPAWN, Vec::new());
                    self.out.event(
                        "SpawnConfirmed",
                        Obj::new().int("allow", allow as i64).str("basis", "RPC RequestSpawn acceptat explicit de server"),
                    );
                    if let Some((x, y, z)) = self.spawn_info {
                        self.position("SpawnInfo", x, y, z);
                    }
                } else if allow == 0 {
                    self.out.log("warn", "serverul a refuzat cererea de spawn (allow=0)");
                }
            }
            RPC_SET_PLAYER_POS => {
                if p.len() >= 12 {
                    let f = |o: usize| f32::from_le_bytes([p[o], p[o + 1], p[o + 2], p[o + 3]]);
                    self.position("SetPlayerPos", f(0), f(4), f(8));
                }
            }
            RPC_CLIENT_MESSAGE => {
                if p.len() >= 8 {
                    let len = u32::from_le_bytes([p[4], p[5], p[6], p[7]]) as usize;
                    if let Some(t) = p.get(8..8 + len.min(p.len() - 8)) {
                        self.out.event("ServerMessage", Obj::new().str("text", &latin1(t)));
                    }
                }
            }
            RPC_CONNECTION_REJECTED => {
                self.out.event(
                    "ConnectionError",
                    Obj::new()
                        .str("code", "ConnectionRejected")
                        .str("message", "serverul a respins conexiunea (ex. nume invalid/duplicat, versiune sau validare client)")
                        .int("reason", p.first().copied().unwrap_or(0) as i64),
                );
            }
            _ => {}
        }
    }

    fn position(&self, source: &str, x: f32, y: f32, z: f32) {
        self.out.event(
            "PositionUpdate",
            Obj::new()
                .str("source", source)
                .num("x", x as f64)
                .num("y", y as f64)
                .num("z", z as f64)
                .raw("virtualWorld", "null"),
        );
    }

    fn mark_auth_completed(&mut self, basis: &str) {
        if self.dialog_answered && !self.auth_completed {
            self.auth_completed = true;
            self.dialog_pending = false;
            self.out.event(
                "AuthenticationCompleted",
                Obj::new().bool("inferred", true).str("basis", &format!("dupa raspunsul la dialog, serverul a trimis {basis}")),
            );
        }
    }

    fn on_dialog(&mut self, p: &[u8]) {
        let mut r = BitStreamReader::new(p);
        let parsed = (|| {
            let id = r.read_u16().ok()?;
            let style = r.read_u8().ok()?;
            let title = str8_lossy(&mut r)?;
            Some((id, style, title))
        })();
        let Some((id, style, title)) = parsed else { return };
        self.out.event("DialogShown", Obj::new().int("dialogId", id as i64).int("style", style as i64).str("title", &title));
        // Autentificare normala: raspundem o singura data la primul dialog de tip parola (stil 3).
        if style == 3 && !self.dialog_answered && !self.cfg.password.is_empty() {
            self.dialog_pending = true;
            self.auth_started = true;
            self.out.event("AuthenticationStarted", Obj::new().int("dialogId", id as i64).str("title", &title));
            let pw = self.cfg.password.as_bytes();
            let len = pw.len().min(255);
            let mut w = BitStreamWriter::new();
            w.write_u16(id);
            w.write_u8(1);
            w.write_u16(0xFFFF);
            w.write_u8(len as u8);
            w.write_bytes(&pw[..len]);
            self.rpc(RPC_DIALOG_RESPONSE, w.into_bytes());
            self.dialog_answered = true;
            self.dialog_answered_at = Some(Instant::now());
            // parola nu este logata; asteptam semnalul serverului pentru a marca autentificarea ca terminata
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::reliability::ReliabilityLayer;
    use crate::transport::{build_rpc, parse_rpc};
    use crate::types::{cipher, Reliability};
    use std::net::UdpSocket;
    use std::sync::{mpsc, Arc, Mutex};

    #[derive(Default, Debug)]
    struct ServerSeen {
        join_nick: Option<String>,
        dialog_reply: Option<String>,
        spawn_rpc: bool,
        disconnect: bool,
        request_spawn_before_login: bool,
    }

    /// Server simulat (doar pentru test), vorbeste acelasi format de fire cu clientul.
    fn mock_server(with_dialog: bool, stop: Arc<Mutex<bool>>) -> (u16, Arc<Mutex<ServerSeen>>, std::thread::JoinHandle<()>) {
        let sock = UdpSocket::bind("127.0.0.1:0").unwrap();
        sock.set_read_timeout(Some(Duration::from_millis(10))).unwrap();
        let port = sock.local_addr().unwrap().port();
        let seen = Arc::new(Mutex::new(ServerSeen::default()));
        let seen2 = seen.clone();
        let h = std::thread::spawn(move || {
            let mut rel = ReliabilityLayer::new();
            let mut peer: Option<std::net::SocketAddr> = None;
            let mut buf = vec![0u8; 65535];
            let mut logged_in = !with_dialog;
            while !*stop.lock().unwrap() {
                if let Ok((n, from)) = sock.recv_from(&mut buf) {
                    peer = Some(from);
                    let d = &buf[..n];
                    if d.starts_with(b"SAMP") {
                        let _ = sock.send_to(b"SAMP-reply", from);
                    } else if let Ok(plain) = cipher::decrypt(d, port) {
                        if plain.first() == Some(&24) {
                            let _ = sock.send_to(&[25, 0], from);
                        } else if let Ok(msgs) = rel.on_receive(&plain, Instant::now()) {
                            for m in msgs {
                                match m.first().copied() {
                                    Some(11) => {
                                        let mut b = vec![34u8];
                                        b.extend_from_slice(&[127, 0, 0, 1]);
                                        b.extend_from_slice(&port.to_le_bytes());
                                        b.extend_from_slice(&7u16.to_le_bytes());
                                        b.extend_from_slice(&0x1234u32.to_le_bytes());
                                        rel.enqueue(&b, Reliability::Reliable, 0);
                                    }
                                    Some(32) => seen2.lock().unwrap().disconnect = true,
                                    Some(20) => {
                                        if let Some((id, p)) = parse_rpc(&m) {
                                            match id {
                                                25 => {
                                                    let mut r = BitStreamReader::new(&p);
                                                    r.read_u32().unwrap();
                                                    r.read_u8().unwrap();
                                                    let nick = str8_lossy(&mut r).unwrap();
                                                    let ch = r.read_u32().unwrap();
                                                    assert_eq!(ch, 0x1234 ^ CHALLENGE_XOR);
                                                    let gpci = str8_lossy(&mut r).unwrap();
                                                    assert_eq!(gpci, "", "GPCI nu trebuie falsificat");
                                                    seen2.lock().unwrap().join_nick = Some(nick);
                                                    let mut w = BitStreamWriter::new();
                                                    w.write_zero_bits(104);
                                                    w.write_u16(7);
                                                    w.write_zero_bits(275);
                                                    w.write_str8("Server de test");
                                                    rel.enqueue(&build_rpc(139, &w.into_bytes()), Reliability::ReliableOrdered, 0);
                                                }
                                                128 => {
                                                    let mut blob = vec![1u8];
                                                    blob.extend_from_slice(&[0u8; 46]);
                                                    rel.enqueue(&build_rpc(128, &blob), Reliability::ReliableOrdered, 0);
                                                    if with_dialog {
                                                        let mut w = BitStreamWriter::new();
                                                        w.write_u16(1);
                                                        w.write_u8(3);
                                                        w.write_str8("Login");
                                                        w.write_str8("OK");
                                                        w.write_str8("Iesire");
                                                        rel.enqueue(&build_rpc(61, &w.into_bytes()), Reliability::ReliableOrdered, 0);
                                                    }
                                                }
                                                62 => {
                                                    let mut r = BitStreamReader::new(&p);
                                                    r.read_u16().unwrap();
                                                    r.read_u8().unwrap();
                                                    r.read_u16().unwrap();
                                                    let len = r.read_u8().unwrap() as usize;
                                                    let txt = String::from_utf8(r.read_bytes(len).unwrap()).unwrap();
                                                    seen2.lock().unwrap().dialog_reply = Some(txt);
                                                    logged_in = true;
                                                    let mut w = BitStreamWriter::new();
                                                    w.write_u8(0);
                                                    w.write_i32(0);
                                                    w.write_u8(0);
                                                    w.write_f32(1958.5);
                                                    w.write_f32(-1743.25);
                                                    w.write_f32(13.5);
                                                    w.write_f32(0.0);
                                                    w.write_zero_bits(6 * 32);
                                                    rel.enqueue(&build_rpc(68, &w.into_bytes()), Reliability::ReliableOrdered, 0);
                                                }
                                                129 => {
                                                    if !logged_in {
                                                        seen2.lock().unwrap().request_spawn_before_login = true;
                                                    }
                                                    rel.enqueue(&build_rpc(129, &[2]), Reliability::ReliableOrdered, 0);
                                                }
                                                52 => seen2.lock().unwrap().spawn_rpc = true,
                                                _ => {}
                                            }
                                        }
                                    }
                                    _ => {}
                                }
                            }
                        }
                    }
                }
                if let Some(p) = peer {
                    for d in rel.update(Instant::now()) {
                        let _ = sock.send_to(&d, p);
                    }
                }
            }
        });
        (port, seen, h)
    }

    fn run_scenario(with_dialog: bool) -> (Vec<String>, Arc<Mutex<ServerSeen>>) {
        let stop = Arc::new(Mutex::new(false));
        let (port, seen, h) = mock_server(with_dialog, stop.clone());
        let (out, sink) = Out::capture("t1");
        let cfg = Config {
            session_id: "t1".into(),
            server: SocketAddrV4::new(std::net::Ipv4Addr::LOCALHOST, port),
            nick: "TestNick".into(),
            password: "parola-secreta".into(),
            server_password: None,
        };
        let (tx, rx) = mpsc::channel();
        let sink2 = sink.clone();
        let ctl = std::thread::spawn(move || {
            let start = Instant::now();
            while start.elapsed() < Duration::from_secs(25) {
                if sink2.lock().unwrap().iter().any(|l| l.contains("\"event\":\"Online\"")) {
                    break;
                }
                std::thread::sleep(Duration::from_millis(50));
            }
            let mut o = crate::json::Object::new();
            o.insert("cmd".into(), crate::json::Value::Str("disconnect".into()));
            let _ = tx.send(o);
        });
        let reason = run(&cfg, &out, &rx);
        ctl.join().unwrap();
        std::thread::sleep(Duration::from_millis(200));
        *stop.lock().unwrap() = true;
        h.join().unwrap();
        assert_eq!(reason, DisconnectReason::Local);
        let lines = sink.lock().unwrap().clone();
        (lines, seen)
    }

    #[test]
    fn full_flow_without_login_dialog() {
        let (lines, seen) = run_scenario(false);
        let ev: Vec<String> = lines
            .iter()
            .filter_map(|l| l.split("\"event\":\"").nth(1).and_then(|r| r.split('"').next()).map(|s| s.to_string()))
            .collect();
        for e in ["Connecting", "Connected", "Joined", "SpawnRequested", "SpawnConfirmed", "Online", "Disconnected"] {
            assert!(ev.iter().any(|x| x == e), "lipseste {e}: {ev:?}");
        }
        assert!(ev.iter().position(|x| x == "SpawnConfirmed") < ev.iter().position(|x| x == "Online"));
        let s = seen.lock().unwrap();
        assert_eq!(s.join_nick.as_deref(), Some("TestNick"));
        assert!(s.spawn_rpc && s.disconnect);
        assert!(!lines.iter().any(|l| l.contains("parola-secreta")));
    }

    #[test]
    fn full_flow_with_login_dialog_waits_for_server() {
        let (lines, seen) = run_scenario(true);
        let ev: Vec<String> = lines
            .iter()
            .filter_map(|l| l.split("\"event\":\"").nth(1).and_then(|r| r.split('"').next()).map(|s| s.to_string()))
            .collect();
        let pos = |n: &str| ev.iter().position(|x| x == n).unwrap_or_else(|| panic!("lipseste {n}: {ev:?}"));
        assert!(pos("AuthenticationStarted") < pos("AuthenticationCompleted"));
        assert!(pos("AuthenticationCompleted") < pos("SpawnRequested"));
        assert!(pos("SpawnRequested") < pos("SpawnConfirmed"));
        assert!(pos("SpawnConfirmed") < pos("Online"));
        let s = seen.lock().unwrap();
        assert_eq!(s.dialog_reply.as_deref(), Some("parola-secreta"));
        assert!(!s.request_spawn_before_login);
        assert!(lines.iter().any(|l| l.contains("\"event\":\"PositionUpdate\"") && l.contains("1958.5")));
        assert!(!lines.iter().any(|l| l.contains("parola-secreta")), "parola a ajuns in evenimente");
    }
}
