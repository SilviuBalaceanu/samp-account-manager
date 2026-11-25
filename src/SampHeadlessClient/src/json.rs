//! JSON minimal (fara dependente): parser pentru obiecte plate (comenzi de la WPF)
//! si un constructor pentru evenimente (JSON Lines catre WPF).

use std::collections::HashMap;

#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    Str(String),
    Num(f64),
    Bool(bool),
    Null,
}

pub type Object = HashMap<String, Value>;

pub fn escape(s: &str) -> String {
    let mut o = String::with_capacity(s.len() + 2);
    for c in s.chars() {
        match c {
            '"' => o.push_str("\\\""),
            '\\' => o.push_str("\\\\"),
            '\n' => o.push_str("\\n"),
            '\r' => o.push_str("\\r"),
            '\t' => o.push_str("\\t"),
            c if (c as u32) < 0x20 => o.push_str(&format!("\\u{:04x}", c as u32)),
            c => o.push(c),
        }
    }
    o
}

/// Constructor de obiect JSON pe o singura linie.
#[derive(Default)]
pub struct Obj {
    parts: Vec<String>,
}

impl Obj {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn str(mut self, k: &str, v: &str) -> Self {
        self.parts.push(format!("\"{}\":\"{}\"", escape(k), escape(v)));
        self
    }
    pub fn num(mut self, k: &str, v: f64) -> Self {
        if v.is_finite() {
            self.parts.push(format!("\"{}\":{}", escape(k), v));
        } else {
            self.parts.push(format!("\"{}\":null", escape(k)));
        }
        self
    }
    pub fn int(mut self, k: &str, v: i64) -> Self {
        self.parts.push(format!("\"{}\":{}", escape(k), v));
        self
    }
    pub fn bool(mut self, k: &str, v: bool) -> Self {
        self.parts.push(format!("\"{}\":{}", escape(k), v));
        self
    }
    pub fn raw(mut self, k: &str, json: &str) -> Self {
        self.parts.push(format!("\"{}\":{}", escape(k), json));
        self
    }
    pub fn build(self) -> String {
        format!("{{{}}}", self.parts.join(","))
    }
}

struct P<'a> {
    b: &'a [u8],
    i: usize,
}

impl<'a> P<'a> {
    fn ws(&mut self) {
        while self.i < self.b.len() && self.b[self.i].is_ascii_whitespace() {
            self.i += 1;
        }
    }
    fn eat(&mut self, c: u8) -> Result<(), String> {
        self.ws();
        if self.b.get(self.i) == Some(&c) {
            self.i += 1;
            Ok(())
        } else {
            Err(format!("asteptat '{}' la pozitia {}", c as char, self.i))
        }
    }
    fn string(&mut self) -> Result<String, String> {
        self.eat(b'"')?;
        let mut out: Vec<u8> = Vec::new();
        loop {
            let c = *self.b.get(self.i).ok_or("sir neterminat")?;
            self.i += 1;
            match c {
                b'"' => break,
                b'\\' => {
                    let e = *self.b.get(self.i).ok_or("escape incomplet")?;
                    self.i += 1;
                    match e {
                        b'"' => out.push(b'"'),
                        b'\\' => out.push(b'\\'),
                        b'/' => out.push(b'/'),
                        b'n' => out.push(b'\n'),
                        b'r' => out.push(b'\r'),
                        b't' => out.push(b'\t'),
                        b'b' => out.push(8),
                        b'f' => out.push(12),
                        b'u' => {
                            let hex = self.b.get(self.i..self.i + 4).ok_or("\\u incomplet")?;
                            let hex = std::str::from_utf8(hex).map_err(|_| "\\u invalid")?;
                            let mut cp = u32::from_str_radix(hex, 16).map_err(|_| "\\u invalid")?;
                            self.i += 4;
                            if (0xD800..0xDC00).contains(&cp) && self.b.get(self.i) == Some(&b'\\') {
                                if let Some(h2) = self.b.get(self.i + 2..self.i + 6) {
                                    if let Ok(lo) = u32::from_str_radix(
                                        std::str::from_utf8(h2).unwrap_or("zz"),
                                        16,
                                    ) {
                                        if (0xDC00..0xE000).contains(&lo) {
                                            cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                            self.i += 6;
                                        }
                                    }
                                }
                            }
                            let ch = char::from_u32(cp).unwrap_or('\u{FFFD}');
                            let mut buf = [0u8; 4];
                            out.extend_from_slice(ch.encode_utf8(&mut buf).as_bytes());
                        }
                        _ => return Err("escape necunoscut".into()),
                    }
                }
                c => out.push(c),
            }
        }
        String::from_utf8(out).map_err(|_| "utf8 invalid".into())
    }
    fn value(&mut self) -> Result<Value, String> {
        self.ws();
        match self.b.get(self.i) {
            Some(b'"') => Ok(Value::Str(self.string()?)),
            Some(b't') if self.b[self.i..].starts_with(b"true") => {
                self.i += 4;
                Ok(Value::Bool(true))
            }
            Some(b'f') if self.b[self.i..].starts_with(b"false") => {
                self.i += 5;
                Ok(Value::Bool(false))
            }
            Some(b'n') if self.b[self.i..].starts_with(b"null") => {
                self.i += 4;
                Ok(Value::Null)
            }
            Some(c) if c.is_ascii_digit() || *c == b'-' => {
                let st = self.i;
                while self.i < self.b.len()
                    && matches!(self.b[self.i], b'0'..=b'9' | b'-' | b'+' | b'.' | b'e' | b'E')
                {
                    self.i += 1;
                }
                std::str::from_utf8(&self.b[st..self.i])
                    .ok()
                    .and_then(|s| s.parse::<f64>().ok())
                    .map(Value::Num)
                    .ok_or_else(|| "numar invalid".to_string())
            }
            _ => Err(format!("valoare neasteptata la {}", self.i)),
        }
    }
}

pub fn parse_object(line: &str) -> Result<Object, String> {
    let mut p = P { b: line.as_bytes(), i: 0 };
    p.eat(b'{')?;
    let mut map = Object::new();
    p.ws();
    if p.b.get(p.i) == Some(&b'}') {
        return Ok(map);
    }
    loop {
        let k = p.string()?;
        p.eat(b':')?;
        let v = p.value()?;
        map.insert(k, v);
        p.ws();
        match p.b.get(p.i) {
            Some(b',') => p.i += 1,
            Some(b'}') => break,
            _ => return Err("asteptat ',' sau '}'".into()),
        }
    }
    Ok(map)
}

pub fn get_str<'a>(o: &'a Object, k: &str) -> Option<&'a str> {
    match o.get(k) {
        Some(Value::Str(s)) => Some(s),
        _ => None,
    }
}

pub fn get_num(o: &Object, k: &str) -> Option<f64> {
    match o.get(k) {
        Some(Value::Num(n)) => Some(*n),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_command() {
        let o = parse_object(r#"{"cmd":"connect","port":7777,"nick":"A\"b","x":true}"#).unwrap();
        assert_eq!(get_str(&o, "cmd"), Some("connect"));
        assert_eq!(get_num(&o, "port"), Some(7777.0));
        assert_eq!(get_str(&o, "nick"), Some("A\"b"));
    }

    #[test]
    fn unicode_escape() {
        let o = parse_object(r#"{"p":"ă😀"}"#).unwrap();
        assert_eq!(get_str(&o, "p"), Some("ă😀"));
    }

    #[test]
    fn rejects_garbage() {
        assert!(parse_object("nu e json").is_err());
        assert!(parse_object(r#"{"a":}"#).is_err());
    }

    #[test]
    fn builder_escapes() {
        let s = Obj::new().str("a", "x\"y\n").int("n", 3).bool("b", true).build();
        assert_eq!(s, r#"{"a":"x\"y\n","n":3,"b":true}"#);
        assert!(parse_object(&s).is_ok());
    }
}
