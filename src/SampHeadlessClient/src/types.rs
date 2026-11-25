//! Tipuri comune: erori, nivele de fiabilitate, motive de deconectare si cifrul de port SA-MP.
//! Tabelele si formatul sunt preluate din RakClient (WTFPL), vezi LICENSE-WTFPL-RakClient.

use std::fmt;

#[derive(Debug, PartialEq, Eq, Clone)]
pub enum ProtoError {
    Exhausted { needed: usize, available: usize },
    InvalidString,
    Malformed(&'static str),
}

impl fmt::Display for ProtoError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Exhausted { needed, available } => {
                write!(f, "bitstream epuizat: {needed} biti necesari, {available} disponibili")
            }
            Self::InvalidString => f.write_str("sir invalid in protocol"),
            Self::Malformed(m) => write!(f, "pachet malformat: {m}"),
        }
    }
}

impl std::error::Error for ProtoError {}

pub type Result<T> = std::result::Result<T, ProtoError>;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Reliability {
    Unreliable = 0,
    UnreliableSequenced = 1,
    Reliable = 2,
    ReliableOrdered = 3,
    ReliableSequenced = 4,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DisconnectReason {
    AttemptFailed,
    ServerFull,
    Banned,
    InvalidPassword,
    ClosedByServer,
    ConnectionLost,
    Rejected,
    Timeout,
    Local,
}

impl DisconnectReason {
    pub fn text(self) -> &'static str {
        match self {
            Self::AttemptFailed => "incercarea de conectare a esuat",
            Self::ServerFull => "server plin",
            Self::Banned => "conexiune refuzata: ban",
            Self::InvalidPassword => "parola de server invalida",
            Self::ClosedByServer => "conexiune inchisa de server",
            Self::ConnectionLost => "conexiune pierduta",
            Self::Rejected => "conexiune respinsa de server",
            Self::Timeout => "expirare (timeout)",
            Self::Local => "deconectat local",
        }
    }
    pub fn code(self) -> &'static str {
        match self {
            Self::AttemptFailed => "AttemptFailed",
            Self::ServerFull => "ServerFull",
            Self::Banned => "Banned",
            Self::InvalidPassword => "InvalidPassword",
            Self::ClosedByServer => "ClosedByServer",
            Self::ConnectionLost => "ConnectionLost",
            Self::Rejected => "Rejected",
            Self::Timeout => "Timeout",
            Self::Local => "Local",
        }
    }
}

/// Cifrul datagramelor SA-MP, cheiat pe portul serverului.
pub mod cipher {
    use super::{ProtoError, Result};
    use crate::tables::{INVERSE, MASK_BYTE, SUBSTITUTION};

    pub fn encrypt(data: &[u8], port: u16) -> Vec<u8> {
        let key = (port as u8) ^ 0xCC;
        let mut checksum = 0u8;
        for &b in data {
            checksum ^= b & MASK_BYTE;
        }
        let mut out = Vec::with_capacity(data.len() + 1);
        out.push(checksum);
        for (i, &b) in data.iter().enumerate() {
            let mut v = SUBSTITUTION[b as usize];
            if i & 1 == 1 {
                v ^= key;
            }
            out.push(v);
        }
        out
    }

    #[allow(dead_code)]
    pub fn decrypt(data: &[u8], port: u16) -> Result<Vec<u8>> {
        let (&checksum, body) = data.split_first().ok_or(ProtoError::Malformed("gol"))?;
        let key = (port as u8) ^ 0xCC;
        let mut plain = Vec::with_capacity(body.len());
        for (i, &b) in body.iter().enumerate() {
            let c = if i & 1 == 1 { b ^ key } else { b };
            plain.push(INVERSE[c as usize]);
        }
        let mut check = 0u8;
        for &b in &plain {
            check ^= b & MASK_BYTE;
        }
        if check != checksum {
            return Err(ProtoError::Malformed("checksum"));
        }
        Ok(plain)
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        #[test]
        fn golden_vector() {
            let plaintext = [
                0x00u8, 0x01, 0x02, 0x7F, 0x80, 0xFF, 0x10, 0x20, 0x33, 0xAB, 0xCD, 0xEF, 0x55,
                0xAA, 0x42, 0x99, 0x01, 0x02, 0x03, 0x04,
            ];
            let expected = [
                0xA8u8, 0x27, 0xC4, 0xFD, 0xE9, 0x75, 0x0A, 0x76, 0xB5, 0x8B, 0xBF, 0x3B, 0x74,
                0xB0, 0xFC, 0xA5, 0x5E, 0x69, 0x50, 0x87, 0xCD,
            ];
            assert_eq!(encrypt(&plaintext, 7777), expected);
            assert_eq!(decrypt(&expected, 7777).unwrap(), plaintext);
        }
        #[test]
        fn rejects_corrupt() {
            let mut ct = encrypt(&[1, 2, 3, 4], 7777);
            ct[0] ^= 0xFF;
            assert!(decrypt(&ct, 7777).is_err());
            assert!(decrypt(&[], 7777).is_err());
        }
    }
}
