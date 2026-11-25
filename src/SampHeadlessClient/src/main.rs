//! SampHeadlessClient: client SA-MP fara grafica. Un proces = o sesiune.
//! Protocol WPF <-> Rust: JSON Lines. stdin: comenzi, stdout: evenimente. stderr: nefolosit.
//! Comenzi: {"cmd":"connect","host":"..","port":7777,"nick":"..","password":"..","serverPassword":".."}
//!          {"cmd":"disconnect"}

mod auth_table;
mod bitstream;
mod client;
mod json;
mod reliability;
mod tables;
mod transport;
mod types;

use std::io::BufRead;
use std::net::{SocketAddrV4, ToSocketAddrs};
use std::sync::mpsc;
use std::time::Duration;

use json::{get_num, get_str, Obj};

fn session_id_from_args() -> String {
    let a: Vec<String> = std::env::args().collect();
    for i in 0..a.len() {
        if a[i] == "--session-id" {
            if let Some(v) = a.get(i + 1) {
                return v.clone();
            }
        }
    }
    "unknown".to_string()
}

fn main() {
    let sid = session_id_from_args();
    let out = client::Out::new(&sid);
    out.event("ClientStarted", Obj::new().int("pid", std::process::id() as i64).str("version", env!("CARGO_PKG_VERSION")));

    // thread de citire stdin; la EOF (WPF a murit) trimitem disconnect, ca sa nu ramana procese orfane
    let (tx, rx) = mpsc::channel();
    std::thread::spawn(move || {
        let stdin = std::io::stdin();
        for line in stdin.lock().lines() {
            let Ok(line) = line else { break };
            if line.trim().is_empty() {
                continue;
            }
            match json::parse_object(&line) {
                Ok(o) => {
                    if tx.send(o).is_err() {
                        return;
                    }
                }
                Err(_) => {}
            }
        }
        let mut o = json::Object::new();
        o.insert("cmd".into(), json::Value::Str("disconnect".into()));
        o.insert("eof".into(), json::Value::Bool(true));
        let _ = tx.send(o);
    });

    // asteptam comanda connect
    let cfg = loop {
        match rx.recv_timeout(Duration::from_secs(30)) {
            Ok(cmd) => match get_str(&cmd, "cmd") {
                Some("connect") => {
                    let host = get_str(&cmd, "host").unwrap_or("");
                    let port = get_num(&cmd, "port").unwrap_or(7777.0) as u16;
                    let nick = get_str(&cmd, "nick").unwrap_or("").to_string();
                    if nick.is_empty() || nick.len() > 24 {
                        out.event("ConnectionError", Obj::new().str("code", "InvalidNick").str("message", "nickname gol sau mai lung de 24 caractere"));
                        out.event("Disconnected", Obj::new().str("reason", "Local").str("message", "configuratie invalida"));
                        return;
                    }
                    let addr: Option<SocketAddrV4> = (host, port).to_socket_addrs().ok().and_then(|it| {
                        it.filter_map(|a| match a {
                            std::net::SocketAddr::V4(v4) => Some(v4),
                            _ => None,
                        })
                        .next()
                    });
                    let Some(server) = addr else {
                        out.event("ConnectionError", Obj::new().str("code", "DnsError").str("message", &format!("nu pot rezolva {host}")));
                        out.event("Disconnected", Obj::new().str("reason", "AttemptFailed").str("message", "DNS esuat"));
                        return;
                    };
                    break client::Config {
                        session_id: sid.clone(),
                        server,
                        nick,
                        password: get_str(&cmd, "password").unwrap_or("").to_string(),
                        server_password: get_str(&cmd, "serverPassword").filter(|s| !s.is_empty()).map(|s| s.to_string()),
                    };
                }
                Some("disconnect") => {
                    out.event("Disconnected", Obj::new().str("reason", "Local").str("message", "deconectat inainte de conectare"));
                    return;
                }
                _ => {}
            },
            Err(_) => {
                out.event("ConnectionError", Obj::new().str("code", "NoCommand").str("message", "nu a venit nicio comanda connect"));
                return;
            }
        }
    };

    client::run(&cfg, &out, &rx);
}
