//! Transport RakNet 3.x / SA-MP pe UDP, sincron (un thread per sesiune).
//! Handshake: interogare `SAMP`, open-connection (cookie), connection-request,
//! raspuns la ID_AUTH_KEY, ID_NEW_INCOMING_CONNECTION. Adaptat dupa RakClient (WTFPL).
//! Nu se trimite niciun fel de identitate falsificata: nu exista proxy, nu exista GPCI generat.

use std::net::{SocketAddr, SocketAddrV4, UdpSocket};
use std::sync::mpsc::{self, Receiver, Sender, TryRecvError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use crate::bitstream::BitStreamWriter;
use crate::reliability::ReliabilityLayer;
use crate::types::{cipher, DisconnectReason, Reliability};

const TICK: Duration = Duration::from_millis(20);
const HANDSHAKE_RETRY: Duration = Duration::from_millis(500);
const CONNECT_TIMEOUT: Duration = Duration::from_secs(12);
const KEEPALIVE: Duration = Duration::from_secs(8);
const PEER_TIMEOUT: Duration = Duration::from_secs(15);
const QUERY_FALLBACK: Duration = Duration::from_millis(400);
const SAMP_PETARDED: u16 = 0x6969;

const ID_OPEN_CONNECTION_REQUEST: u8 = 24;
const ID_OPEN_CONNECTION_REPLY: u8 = 25;
const ID_OPEN_CONNECTION_COOKIE: u8 = 26;
const ID_CONNECTION_REQUEST: u8 = 11;
const ID_AUTH_KEY: u8 = 12;
const ID_NEW_INCOMING_CONNECTION: u8 = 30;
const ID_CONNECTION_REQUEST_ACCEPTED: u8 = 34;
const ID_DISCONNECTION_NOTIFICATION: u8 = 32;
const ID_CONNECTION_LOST: u8 = 33;
const ID_NO_FREE_INCOMING_CONNECTIONS: u8 = 31;
const ID_CONNECTION_BANNED: u8 = 36;
const ID_INVALID_PASSWORD: u8 = 37;
const ID_CONNECTION_ATTEMPT_FAILED: u8 = 29;
const ID_PONG: u8 = 39;
const ID_INTERNAL_PING: u8 = 6;
const ID_CONNECTED_PONG: u8 = 9;
const ID_RPC: u8 = 20;
const ID_TIMESTAMP: u8 = 40;

pub enum Command {
    Rpc { id: u8, payload: Vec<u8> },
    /// Pachet brut (ex. sincronizare), cu fiabilitate si canal.
    Send { data: Vec<u8>, reliability: Reliability, channel: u8 },
    Disconnect,
}

#[derive(Debug)]
pub enum RakEvent {
    Connected { body: Vec<u8> },
    Rpc { id: u8, payload: Vec<u8> },
    Packet { data: Vec<u8> },
    Disconnected(DisconnectReason),
}

pub struct Peer {
    pub cmd: Sender<Command>,
    pub events: Receiver<RakEvent>,
    handle: Option<JoinHandle<()>>,
}

impl Peer {
    /// Asteapta terminarea thread-ului de transport (fara procese/thread-uri ramase).
    pub fn join(&mut self) {
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

pub fn connect(server: SocketAddrV4, password: Option<String>) -> std::io::Result<Peer> {
    let socket = UdpSocket::bind(("0.0.0.0", 0))?;
    socket.set_read_timeout(Some(TICK))?;
    socket.connect(SocketAddr::V4(server))?;
    let (cmd_tx, cmd_rx) = mpsc::channel();
    let (evt_tx, evt_rx) = mpsc::channel();
    let handle = thread::Builder::new()
        .name("rak-transport".into())
        .spawn(move || {
            let mut t = Task::new(socket, server, password, cmd_rx, evt_tx);
            t.run();
        })?;
    Ok(Peer { cmd: cmd_tx, events: evt_rx, handle: Some(handle) })
}

#[derive(PartialEq, Eq, Clone, Copy)]
enum Phase {
    OpenRequest,
    ConnectionRequest,
    Connected,
}

struct Task {
    socket: UdpSocket,
    server: SocketAddrV4,
    password: Option<String>,
    cmd_rx: Receiver<Command>,
    evt_tx: Sender<RakEvent>,
    rel: ReliabilityLayer,
    phase: Phase,
    cookie: Vec<u8>,
    deadline: Instant,
    started: Instant,
    last_handshake: Instant,
    last_keepalive: Instant,
    last_recv: Instant,
    last_tick: Instant,
    open_sent: bool,
    query_sent_at: Instant,
    stop: bool,
}

impl Task {
    fn new(
        socket: UdpSocket,
        server: SocketAddrV4,
        password: Option<String>,
        cmd_rx: Receiver<Command>,
        evt_tx: Sender<RakEvent>,
    ) -> Self {
        let now = Instant::now();
        Task {
            socket,
            server,
            password,
            cmd_rx,
            evt_tx,
            rel: ReliabilityLayer::new(),
            phase: Phase::OpenRequest,
            cookie: Vec::new(),
            deadline: now + CONNECT_TIMEOUT,
            started: now,
            last_handshake: now,
            last_keepalive: now,
            last_recv: now,
            last_tick: now,
            open_sent: false,
            query_sent_at: now,
            stop: false,
        }
    }

    fn emit(&mut self, e: RakEvent) {
        if self.evt_tx.send(e).is_err() {
            self.stop = true;
        }
    }

    fn finish(&mut self, reason: DisconnectReason) {
        self.emit(RakEvent::Disconnected(reason));
        self.stop = true;
    }

    fn run(&mut self) {
        let mut buf = vec![0u8; 65535];
        self.send_query();
        while !self.stop {
            match self.socket.recv(&mut buf) {
                Ok(n) if n > 0 => {
                    let d = buf[..n].to_vec();
                    self.on_datagram(&d);
                }
                Ok(_) => {}
                Err(e) => match e.kind() {
                    std::io::ErrorKind::WouldBlock
                    | std::io::ErrorKind::TimedOut
                    | std::io::ErrorKind::ConnectionReset
                    | std::io::ErrorKind::ConnectionRefused => {}
                    _ => {
                        self.finish(DisconnectReason::ConnectionLost);
                        break;
                    }
                },
            }
            loop {
                match self.cmd_rx.try_recv() {
                    Ok(c) => self.on_command(c),
                    Err(TryRecvError::Empty) => break,
                    Err(TryRecvError::Disconnected) => {
                        self.stop = true;
                        break;
                    }
                }
            }
            if self.stop {
                break;
            }
            if self.last_tick.elapsed() >= TICK {
                self.last_tick = Instant::now();
                self.on_tick();
            }
        }
    }

    fn send_raw(&self, bytes: &[u8]) {
        let _ = self.socket.send(bytes);
    }

    fn send_offline(&self, plaintext: &[u8]) {
        self.send_raw(&cipher::encrypt(plaintext, self.server.port()));
    }

    fn send_query(&mut self) {
        self.query_sent_at = Instant::now();
        let mut q = Vec::with_capacity(11);
        q.extend_from_slice(b"SAMP");
        q.extend_from_slice(&self.server.ip().octets());
        q.extend_from_slice(&self.server.port().to_le_bytes());
        q.push(b'i');
        self.send_raw(&q);
    }

    fn flush(&mut self) {
        let now = Instant::now();
        for d in self.rel.update(now) {
            self.send_raw(&cipher::encrypt(&d, self.server.port()));
        }
    }

    fn send_open_request(&mut self) {
        self.last_handshake = Instant::now();
        let mut msg = vec![ID_OPEN_CONNECTION_REQUEST, 0, 0];
        if self.cookie.len() == 2 {
            let cookie = u16::from_le_bytes([self.cookie[0], self.cookie[1]]);
            msg[1..3].copy_from_slice(&(cookie ^ SAMP_PETARDED).to_le_bytes());
        }
        self.open_sent = true;
        self.send_offline(&msg);
    }

    fn send_connection_request(&mut self) {
        if self.phase == Phase::Connected {
            return;
        }
        self.phase = Phase::ConnectionRequest;
        self.last_handshake = Instant::now();
        let mut p = vec![ID_CONNECTION_REQUEST];
        if let Some(pw) = &self.password {
            p.extend_from_slice(pw.as_bytes());
        }
        self.rel.enqueue(&p, Reliability::Reliable, 0);
        self.flush();
    }

    fn send_new_incoming(&mut self) {
        let mut msg = vec![ID_NEW_INCOMING_CONNECTION];
        msg.extend_from_slice(&self.server.ip().octets());
        msg.extend_from_slice(&self.server.port().to_le_bytes());
        self.rel.enqueue(&msg, Reliability::Reliable, 0);
        self.flush();
    }

    fn on_command(&mut self, c: Command) {
        match c {
            Command::Rpc { id, payload } => {
                let frame = build_rpc(id, &payload);
                self.rel.enqueue(&frame, Reliability::ReliableOrdered, 0);
                self.flush();
            }
            Command::Send { data, reliability, channel } => {
                self.rel.enqueue(&data, reliability, channel);
                self.flush();
            }
            Command::Disconnect => {
                self.rel.enqueue(&[ID_DISCONNECTION_NOTIFICATION], Reliability::Reliable, 0);
                self.flush();
                self.finish(DisconnectReason::Local);
            }
        }
    }

    fn on_tick(&mut self) {
        let now = Instant::now();
        if self.phase != Phase::Connected {
            if now >= self.deadline {
                self.finish(DisconnectReason::Timeout);
                return;
            }
            if !self.open_sent && now.duration_since(self.query_sent_at) >= QUERY_FALLBACK {
                self.send_open_request();
            } else if self.open_sent
                && self.phase == Phase::OpenRequest
                && now.duration_since(self.last_handshake) >= HANDSHAKE_RETRY
            {
                self.send_open_request();
            }
        } else {
            if now.duration_since(self.last_recv) >= PEER_TIMEOUT {
                self.finish(DisconnectReason::Timeout);
                return;
            }
            if now.duration_since(self.last_keepalive) >= KEEPALIVE {
                self.last_keepalive = now;
                self.rel.enqueue(&[ID_TIMESTAMP], Reliability::Unreliable, 0);
            }
        }
        self.flush();
    }

    fn on_datagram(&mut self, raw: &[u8]) {
        if raw.starts_with(b"SAMP") {
            if !self.open_sent {
                self.send_open_request();
            }
            return;
        }
        let first = raw.first().copied().unwrap_or(0);
        if self.phase != Phase::Connected && is_offline_id(first) {
            self.handle_offline(raw);
            return;
        }
        let now = Instant::now();
        let delivered = match self.rel.on_receive(raw, now) {
            Ok(d) => d,
            Err(_) => return,
        };
        self.last_recv = now;
        for m in delivered {
            self.handle_message(m);
            if self.stop {
                return;
            }
        }
    }

    fn handle_offline(&mut self, raw: &[u8]) {
        match raw[0] {
            ID_OPEN_CONNECTION_COOKIE => {
                self.cookie = raw[1..].to_vec();
                self.send_open_request();
            }
            ID_OPEN_CONNECTION_REPLY => self.send_connection_request(),
            ID_PONG => {}
            other => {
                if let Some(r) = disconnect_reason(other) {
                    self.finish(r);
                }
            }
        }
    }

    fn handle_auth_key(&mut self, m: &[u8]) {
        // Provocarea de autentificare a conexiunii SA-MP/open.mp: [12][len][send\0]; raspuns [12][len][recv].
        let Some(&len) = m.get(1) else { return };
        let send_len = (len as usize).saturating_sub(1);
        let Some(send) = m.get(2..2 + send_len) else { return };
        let Ok(send) = std::str::from_utf8(send) else { return };
        if let Some((_, recv)) = crate::auth_table::AUTH_TABLE.iter().find(|(s, _)| *s == send) {
            let mut resp = vec![ID_AUTH_KEY, recv.len() as u8];
            resp.extend_from_slice(recv.as_bytes());
            self.rel.enqueue(&resp, Reliability::Reliable, 0);
        }
    }

    fn handle_message(&mut self, m: Vec<u8>) {
        let Some(&id) = m.first() else { return };
        if let Some(r) = disconnect_reason(id) {
            self.finish(r);
            return;
        }
        match id {
            ID_CONNECTION_REQUEST_ACCEPTED => {
                if self.phase != Phase::Connected {
                    self.phase = Phase::Connected;
                    self.last_keepalive = Instant::now();
                    self.send_new_incoming();
                    self.emit(RakEvent::Connected { body: m[1..].to_vec() });
                }
            }
            ID_AUTH_KEY => self.handle_auth_key(&m),
            ID_TIMESTAMP | ID_RPC => {
                if let Some((rid, payload)) = parse_rpc(&m) {
                    self.emit(RakEvent::Rpc { id: rid, payload });
                }
            }
            ID_INTERNAL_PING => {
                if let Some(echo) = m.get(1..5) {
                    let local = self.started.elapsed().as_millis() as u32;
                    let mut pong = vec![ID_CONNECTED_PONG];
                    pong.extend_from_slice(echo);
                    pong.extend_from_slice(&local.to_le_bytes());
                    self.rel.enqueue(&pong, Reliability::Unreliable, 0);
                }
            }
            _ => self.emit(RakEvent::Packet { data: m }),
        }
    }
}

fn is_offline_id(id: u8) -> bool {
    matches!(
        id,
        ID_OPEN_CONNECTION_REPLY
            | ID_OPEN_CONNECTION_COOKIE
            | ID_PONG
            | ID_NO_FREE_INCOMING_CONNECTIONS
            | ID_CONNECTION_BANNED
            | ID_CONNECTION_ATTEMPT_FAILED
    )
}

fn disconnect_reason(id: u8) -> Option<DisconnectReason> {
    match id {
        ID_DISCONNECTION_NOTIFICATION => Some(DisconnectReason::ClosedByServer),
        ID_CONNECTION_LOST => Some(DisconnectReason::ConnectionLost),
        ID_NO_FREE_INCOMING_CONNECTIONS => Some(DisconnectReason::ServerFull),
        ID_CONNECTION_BANNED => Some(DisconnectReason::Banned),
        ID_INVALID_PASSWORD => Some(DisconnectReason::InvalidPassword),
        ID_CONNECTION_ATTEMPT_FAILED => Some(DisconnectReason::AttemptFailed),
        _ => None,
    }
}

pub fn build_rpc(unique_id: u8, payload: &[u8]) -> Vec<u8> {
    let mut w = BitStreamWriter::new();
    w.write_u8(ID_RPC);
    w.write_u8(unique_id);
    w.write_compressed_u32((payload.len() as u32).saturating_mul(8));
    w.write_bytes(payload);
    w.into_bytes()
}

pub fn parse_rpc(message: &[u8]) -> Option<(u8, Vec<u8>)> {
    use crate::bitstream::BitStreamReader;
    let mut r = BitStreamReader::new(message);
    let mut id = r.read_u8().ok()?;
    if id == ID_TIMESTAMP {
        let _ = r.read_u32().ok()?;
        id = r.read_u8().ok()?;
    }
    if id != ID_RPC {
        return None;
    }
    let unique = r.read_u8().ok()?;
    let bits = r.read_compressed_u32().ok()? as usize;
    let payload = r.read_bits_bytes(bits).ok()?;
    Some((unique, payload))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rpc_roundtrip() {
        let f = build_rpc(25, &[1, 2, 3, 4, 5]);
        assert_eq!(parse_rpc(&f), Some((25, vec![1, 2, 3, 4, 5])));
        let f = build_rpc(7, &[]);
        assert_eq!(parse_rpc(&f), Some((7, vec![])));
        assert!(parse_rpc(&[34, 0, 0]).is_none());
    }

    #[test]
    fn disconnect_ids() {
        assert_eq!(disconnect_reason(ID_CONNECTION_BANNED), Some(DisconnectReason::Banned));
        assert_eq!(disconnect_reason(200), None);
    }
}
