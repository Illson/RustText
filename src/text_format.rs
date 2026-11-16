use std::{borrow::Cow, io};

use encoding_rs::{UTF_16BE, UTF_16LE, WINDOWS_1251};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct TextFormat {
    pub encoding: TextEncoding,
    endings: LineEndings,
}

#[derive(Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum TextEncoding {
    #[default]
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Windows1251,
}

impl TextEncoding {
    pub fn label(self) -> &'static str {
        match self {
            Self::Utf8 => "UTF-8",
            Self::Utf8Bom => "UTF-8 BOM",
            Self::Utf16Le => "UTF-16 LE",
            Self::Utf16Be => "UTF-16 BE",
            Self::Windows1251 => "Windows-1251",
        }
    }
}

#[derive(Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
enum LineEndings {
    #[default]
    Lf,
    CrLf,
    Cr,
    Mixed,
}

impl LineEndings {
    fn detect(text: &str) -> Self {
        let mut bytes = text.bytes().peekable();
        let (mut lf, mut crlf, mut cr) = (false, false, false);

        while let Some(byte) = bytes.next() {
            match byte {
                b'\n' => lf = true,
                b'\r' if bytes.next_if_eq(&b'\n').is_some() => crlf = true,
                b'\r' => cr = true,
                _ => {}
            }
            if (lf as u8 + crlf as u8 + cr as u8) > 1 {
                return Self::Mixed;
            }
        }

        match (crlf, cr) {
            (true, _) => Self::CrLf,
            (_, true) => Self::Cr,
            _ => Self::Lf,
        }
    }
}

impl TextFormat {
    pub fn line_endings_label(self) -> &'static str {
        match self.endings {
            LineEndings::Lf => "LF",
            LineEndings::CrLf => "CRLF",
            LineEndings::Cr => "CR",
            LineEndings::Mixed => "Смешанные",
        }
    }

    pub(crate) fn decode(bytes: &[u8], windows_1251: bool) -> io::Result<(String, Self)> {
        let (text, encoding) = if windows_1251 {
            (
                WINDOWS_1251
                    .decode_without_bom_handling_and_without_replacement(bytes)
                    .ok_or_else(|| invalid_data("Некорректный текст Windows-1251"))?
                    .into_owned(),
                TextEncoding::Windows1251,
            )
        } else if let Some(content) = bytes.strip_prefix(&[0xFF, 0xFE]) {
            (decode_utf16(content, UTF_16LE)?, TextEncoding::Utf16Le)
        } else if let Some(content) = bytes.strip_prefix(&[0xFE, 0xFF]) {
            (decode_utf16(content, UTF_16BE)?, TextEncoding::Utf16Be)
        } else {
            let (content, encoding) = match bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
                Some(content) => (content, TextEncoding::Utf8Bom),
                None => (bytes, TextEncoding::Utf8),
            };
            let text = std::str::from_utf8(content).map_err(|_| {
                invalid_data("Ожидается UTF-8 или UTF-16 с BOM. Для Windows-1251 выберите кодировку при открытии")
            })?;
            (text.to_owned(), encoding)
        };

        if text.contains('\0') {
            return Err(invalid_data("Файл содержит нулевые символы"));
        }

        let endings = LineEndings::detect(&text);
        let text = if matches!(endings, LineEndings::Cr) {
            text.replace('\r', "\n")
        } else {
            text
        };
        Ok((text, Self { encoding, endings }))
    }

    pub(crate) fn encode(self, text: &str) -> io::Result<Vec<u8>> {
        let text = match self.endings {
            LineEndings::CrLf => {
                let mut normalized = String::with_capacity(text.len());
                for part in text.split_inclusive('\n') {
                    if let Some(line) = part.strip_suffix('\n') {
                        normalized.push_str(line);
                        if !line.ends_with('\r') {
                            normalized.push('\r');
                        }
                        normalized.push('\n');
                    } else {
                        normalized.push_str(part);
                    }
                }
                Cow::Owned(normalized)
            }
            LineEndings::Cr => Cow::Owned(text.replace("\r\n", "\n").replace('\n', "\r")),
            LineEndings::Lf | LineEndings::Mixed => Cow::Borrowed(text),
        };

        match self.encoding {
            TextEncoding::Utf8 => Ok(text.into_owned().into_bytes()),
            TextEncoding::Utf8Bom => {
                let mut encoded = vec![0xEF, 0xBB, 0xBF];
                encoded.extend_from_slice(text.as_bytes());
                Ok(encoded)
            }
            TextEncoding::Utf16Le | TextEncoding::Utf16Be => {
                let little_endian = matches!(self.encoding, TextEncoding::Utf16Le);
                let mut encoded = Vec::with_capacity(text.len().saturating_mul(2) + 2);
                encoded.extend(if little_endian {
                    [0xFF, 0xFE]
                } else {
                    [0xFE, 0xFF]
                });
                for code_unit in text.encode_utf16() {
                    encoded.extend(if little_endian {
                        code_unit.to_le_bytes()
                    } else {
                        code_unit.to_be_bytes()
                    });
                }
                Ok(encoded)
            }
            TextEncoding::Windows1251 => {
                let (encoded, _, had_errors) = WINDOWS_1251.encode(&text);
                if had_errors {
                    return Err(invalid_data("Часть символов отсутствует в Windows-1251"));
                }
                Ok(encoded.into_owned())
            }
        }
    }
}

fn decode_utf16(bytes: &[u8], encoding: &'static encoding_rs::Encoding) -> io::Result<String> {
    encoding
        .decode_without_bom_handling_and_without_replacement(bytes)
        .map(Cow::into_owned)
        .ok_or_else(|| invalid_data("Некорректный текст UTF-16"))
}

fn invalid_data(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

#[cfg(test)]
#[path = "../tests/unit/text_format.rs"]
mod tests;
