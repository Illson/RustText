//! Bounded, byte-offset based reading primitives for large log files.
//!
//! Every public position is a `u64` byte offset in the original file. Rendering
//! keeps only one bounded fragment per visible row; indexing and searching
//! operate in fixed-size blocks and never retain the file contents.

use std::{
    collections::VecDeque,
    fs::File,
    io::{self, Read, Seek, SeekFrom},
    path::Path,
    sync::atomic::AtomicBool,
};

use encoding_rs::{UTF_16BE, UTF_16LE, WINDOWS_1251};

use crate::text_format::TextEncoding;

mod index;
mod search;
mod time;

pub use index::{LineIndex, SparseLine};
pub use search::{LogSearchQuery, SearchHit, SearchLimits, SearchPage, search_page};

pub(super) const BLOCK_SIZE: usize = 64 * 1024;
const DEFAULT_ROW_BYTES: usize = 16 * 1024;
const MAX_PAGE_ROWS: usize = 4_096;
const MAX_ROW_BYTES: usize = 1024 * 1024;
const MAX_PAGE_BYTES: usize = 64 * 1024 * 1024;

#[derive(Debug)]
pub enum LogError {
    Io(io::Error),
    InvalidRegex(regex::Error),
    InvalidTimestamp,
    LineLimit { line_index: u64, limit: usize },
    Cancelled,
}

impl std::fmt::Display for LogError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(error) => write!(f, "Ошибка чтения лога: {error}"),
            Self::InvalidRegex(error) => write!(f, "Ошибка регулярного выражения: {error}"),
            Self::InvalidTimestamp => f.write_str(
                "Введите дату и время в формате ГГГГ-ММ-ДД ЧЧ:ММ:СС[.доли], например 2026-11-17 01:30:20.0850",
            ),
            Self::LineLimit { limit, .. } => {
                write!(f, "Строка превышает лимит поиска в {limit} байт")
            }
            Self::Cancelled => f.write_str("Поиск отменён"),
        }
    }
}

impl std::error::Error for LogError {}

impl From<io::Error> for LogError {
    fn from(value: io::Error) -> Self {
        Self::Io(value)
    }
}

#[derive(Clone, Copy, Debug)]
pub struct PageOptions {
    pub max_rows: usize,
    /// Maximum raw file bytes held for any one displayed fragment.
    pub max_row_bytes: usize,
}

impl Default for PageOptions {
    fn default() -> Self {
        Self {
            max_rows: 500,
            max_row_bytes: DEFAULT_ROW_BYTES,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LogRow {
    /// Start of this fragment in the source file.
    pub start: u64,
    /// First byte after the fragment, including a line ending when present.
    pub end: u64,
    pub text: String,
    /// True if more bytes from the same physical line follow.
    pub continues: bool,
    /// True if this fragment ends at a physical line ending.
    pub terminated: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Page {
    pub rows: Vec<LogRow>,
    pub next_offset: u64,
    pub file_size: u64,
    pub eof: bool,
}

/// A short-lived reader. It deliberately owns no decoded copy of the file.
pub struct LogReader {
    file: File,
    encoding: TextEncoding,
    size: u64,
    index: LineIndex,
}

impl LogReader {
    /// `encoding_override` is `Some` for a user-selected encoding; otherwise a
    /// UTF BOM is detected and BOM-less files are treated as UTF-8.
    pub fn open(
        path: impl AsRef<Path>,
        encoding_override: Option<TextEncoding>,
    ) -> Result<Self, LogError> {
        let mut file = File::open(path)?;
        let size = file.metadata()?.len();
        let encoding = match encoding_override {
            Some(encoding) => encoding,
            None => detect_encoding(&mut file, size)?,
        };
        file.seek(SeekFrom::Start(0))?;
        Ok(Self {
            file,
            encoding,
            size,
            index: LineIndex::default(),
        })
    }

    pub fn file(&self) -> &File {
        &self.file
    }

    pub fn encoding(&self) -> TextEncoding {
        self.encoding
    }

    pub fn size(&self) -> u64 {
        self.size
    }

    /// Refreshes the length observed through this file handle after append or
    /// truncation; replacement detection remains the owning layer's job.
    pub fn refresh_size(&mut self) -> Result<u64, LogError> {
        self.size = self.file.metadata()?.len();
        Ok(self.size)
    }

    pub fn line_index(&self) -> &LineIndex {
        &self.index
    }

    /// Restore an index only after the owning layer verified the file identity.
    pub fn set_line_index(&mut self, index: LineIndex) {
        self.index = index;
    }

    pub fn seek_start(&self) -> u64 {
        0
    }

    /// Maps a percentage to an approximate byte position without reading the
    /// file. The caller can render from the returned byte position immediately.
    pub fn seek_percent(&self, percent: f64) -> u64 {
        let fraction = (percent / 100.0).clamp(0.0, 1.0);
        (self.size as f64 * fraction) as u64
    }

    /// Reads only enough data to render the requested viewport. Long physical
    /// lines are exposed as multiple rows carrying their source offsets.
    pub fn render_page(&mut self, offset: u64, options: PageOptions) -> Result<Page, LogError> {
        self.render_page_until(offset, self.size, options)
    }

    pub(crate) fn render_page_until(
        &mut self,
        offset: u64,
        limit: u64,
        options: PageOptions,
    ) -> Result<Page, LogError> {
        self.read_page_until(offset, limit, options, false)
    }

    fn read_page_until(
        &mut self,
        offset: u64,
        limit: u64,
        options: PageOptions,
        keep_tail: bool,
    ) -> Result<Page, LogError> {
        let limit = limit.min(self.size);
        if align_offset(limit, self.encoding) <= self.encoding.bom_len() {
            return Ok(Page {
                rows: Vec::new(),
                next_offset: limit,
                file_size: self.size,
                eof: limit >= self.size,
            });
        }
        let mut cursor = align_offset(offset.min(limit), self.encoding);
        let row_bytes = options
            .max_row_bytes
            .clamp(minimum_fragment_bytes(self.encoding), MAX_ROW_BYTES);
        let row_limit = options
            .max_rows
            .min(MAX_PAGE_ROWS)
            .min((MAX_PAGE_BYTES / row_bytes).max(1));
        let mut rows = VecDeque::with_capacity(row_limit);
        let is_utf8 = matches!(self.encoding, TextEncoding::Utf8 | TextEncoding::Utf8Bom);
        let mut incomplete_utf8_tail = false;
        while cursor < limit && row_limit > 0 && (keep_tail || rows.len() < row_limit) {
            let start = cursor;
            let fragment = read_fragment(&mut self.file, cursor, limit, row_bytes, self.encoding)?;
            incomplete_utf8_tail = is_utf8 && limit == self.size && fragment.incomplete_tail;
            if fragment.end <= cursor {
                break;
            }
            cursor = fragment.end;
            if rows.len() == row_limit {
                rows.pop_front();
            }
            rows.push_back(LogRow {
                start,
                end: cursor,
                text: decode(&fragment.bytes, self.encoding, start == 0),
                continues: !fragment.terminated && cursor < self.size,
                terminated: fragment.terminated,
            });
            if incomplete_utf8_tail {
                break;
            }
        }
        Ok(Page {
            rows: rows.into(),
            next_offset: cursor,
            file_size: self.size,
            eof: cursor >= self.size || incomplete_utf8_tail,
        })
    }

    /// Renders a bounded viewport ending at `offset`. The backward scan is
    /// capped by `max_scan_bytes`; a very long physical line may therefore
    /// begin in the middle and is represented as a continuation fragment.
    pub fn page_before(
        &mut self,
        offset: u64,
        options: PageOptions,
        max_scan_bytes: u64,
    ) -> Result<Page, LogError> {
        let target = align_offset(offset.min(self.size), self.encoding);
        let start = previous_page_start(
            &mut self.file,
            target,
            options.max_rows.max(1),
            max_scan_bytes,
            options
                .max_row_bytes
                .clamp(minimum_fragment_bytes(self.encoding), MAX_ROW_BYTES),
            self.encoding,
        )?;
        self.read_page_until(start, target, options, true)
    }

    /// Advances the sparse index by at most `max_bytes`; useful for background
    /// indexing and for extending the index after the file grows.
    pub fn advance_index(&mut self, max_bytes: u64, cancel: &AtomicBool) -> Result<u64, LogError> {
        let through = self
            .size
            .min(self.index.scanned_to().saturating_add(max_bytes));
        self.index
            .advance(&mut self.file, self.encoding, through, cancel)
    }

    /// Returns the line start at or before the supplied byte offset. If the
    /// sparse index is empty/initial, scans forward from the closest sample.
    pub fn line_start_at(&mut self, offset: u64) -> Result<u64, LogError> {
        let target = align_offset(offset.min(self.size), self.encoding);
        let entries = self.index.entries();
        let sample = entries
            .get(..entries.partition_point(|entry| entry.offset <= target))
            .and_then(|entries| entries.last())
            .copied()
            .unwrap_or(SparseLine { line: 1, offset: 0 });
        let mut cursor = sample.offset;
        let mut result = sample.offset;
        let mut buffer = vec![0_u8; BLOCK_SIZE];
        while cursor < target {
            let take = (target - cursor).min(buffer.len() as u64) as usize;
            self.file.seek(SeekFrom::Start(cursor))?;
            let count = self.file.read(&mut buffer[..take])?;
            if count == 0 {
                break;
            }
            for delimiter_end in delimiter_ends(&buffer[..count], self.encoding, cursor) {
                let after = cursor + delimiter_end as u64;
                if after <= target {
                    result = after;
                }
            }
            cursor += count as u64;
        }
        Ok(result)
    }

    /// Returns the 1-based physical line containing `offset` when the sparse
    /// index has already scanned through it. The remaining scan starts at the
    /// nearest sample, keeping viewport queries bounded as indexing progresses.
    pub fn line_number_at(&mut self, offset: u64) -> Result<Option<u64>, LogError> {
        let target = align_offset(offset.min(self.size), self.encoding);
        if target > self.index.scanned_to() {
            return Ok(None);
        }
        if target >= self.index.last_line_start() {
            return Ok(Some(self.index.line_count()));
        }

        let entries = self.index.entries();
        let sample = entries
            .get(..entries.partition_point(|entry| entry.offset <= target))
            .and_then(|entries| entries.last())
            .copied()
            .unwrap_or(SparseLine { line: 1, offset: 0 });
        let mut cursor = sample.offset;
        let mut line = sample.line;
        let mut buffer = vec![0_u8; BLOCK_SIZE + unit_size(self.encoding) * 2];
        while cursor < target {
            let core_length = (target - cursor).min(BLOCK_SIZE as u64) as usize;
            let core_end = cursor + core_length as u64;
            let lookahead = (self.size - cursor - core_length as u64)
                .min((unit_size(self.encoding) * 2) as u64) as usize;
            self.file.seek(SeekFrom::Start(cursor))?;
            let count = core_length + lookahead;
            self.file.read_exact(&mut buffer[..count])?;
            for delimiter_end in delimiter_ends(&buffer[..count], self.encoding, cursor) {
                let after = cursor + delimiter_end as u64;
                if after > cursor && after <= core_end && after <= target {
                    line += 1;
                }
            }
            cursor += core_length as u64;
        }
        Ok(Some(line))
    }

    /// Scrolls to the containing physical line, useful for a search result.
    pub fn page_at_line(&mut self, offset: u64, options: PageOptions) -> Result<Page, LogError> {
        let start = self.line_start_at(offset)?;
        self.render_page(start, options)
    }
}

#[derive(Debug)]
pub(super) struct Fragment {
    pub(super) bytes: Vec<u8>,
    pub(super) end: u64,
    pub(super) terminated: bool,
    pub(super) limit_reached: bool,
    pub(super) incomplete_tail: bool,
}

fn read_fragment(
    file: &mut File,
    start: u64,
    file_size: u64,
    max_bytes: usize,
    encoding: TextEncoding,
) -> io::Result<Fragment> {
    let cap = max_bytes.max(minimum_fragment_bytes(encoding));
    let unit = unit_size(encoding);
    let delimiter_lookahead = unit * 2;
    let mut bytes = Vec::with_capacity(cap.min(BLOCK_SIZE));
    let mut cursor = start;
    let mut buffer = vec![
        0_u8;
        BLOCK_SIZE
            .min(cap)
            .saturating_add(delimiter_lookahead)
            .max(2)
    ];
    while cursor < file_size && bytes.len() < cap {
        let remaining = cap - bytes.len();
        let take = (file_size - cursor)
            .min(remaining.saturating_add(delimiter_lookahead) as u64)
            .min(buffer.len() as u64) as usize;
        file.seek(SeekFrom::Start(cursor))?;
        let count = file.read(&mut buffer[..take])?;
        if count == 0 {
            break;
        }
        let usable = if unit == 2 { count - count % 2 } else { count };
        if usable == 0 {
            bytes.extend_from_slice(&buffer[..count]);
            cursor += count as u64;
            continue;
        }
        if let Some((index, delimiter_len)) = find_delimiter(&buffer[..usable], encoding) {
            if index > remaining {
                let mut consumed = if unit == 2 {
                    remaining - remaining % 2
                } else {
                    remaining
                };
                consumed = avoid_utf16_surrogate_split(&buffer[..usable], consumed, encoding);
                bytes.extend_from_slice(&buffer[..consumed]);
                cursor += consumed as u64;
                if matches!(encoding, TextEncoding::Utf8 | TextEncoding::Utf8Bom) {
                    trim_utf8_boundary(&mut bytes);
                    cursor = start + bytes.len() as u64;
                }
                return Ok(Fragment {
                    bytes,
                    end: cursor,
                    terminated: false,
                    limit_reached: false,
                    incomplete_tail: false,
                });
            }
            bytes.extend_from_slice(&buffer[..index]);
            cursor += (index + delimiter_len) as u64;
            return Ok(Fragment {
                bytes,
                end: cursor,
                terminated: true,
                limit_reached: false,
                incomplete_tail: false,
            });
        }
        let consumed = usable.min(remaining);
        let mut consumed = if unit == 2 {
            consumed - consumed % 2
        } else {
            consumed
        };
        consumed = avoid_utf16_surrogate_split(&buffer[..usable], consumed, encoding);
        if consumed == 0 {
            break;
        }
        bytes.extend_from_slice(&buffer[..consumed]);
        cursor += consumed as u64;
    }
    let raw_cursor = cursor;
    if matches!(encoding, TextEncoding::Utf8 | TextEncoding::Utf8Bom) {
        trim_utf8_boundary(&mut bytes);
        cursor = start + bytes.len() as u64;
    } else if unit_size(encoding) == 2 && bytes.len() % 2 != 0 {
        bytes.pop();
        cursor -= 1;
    }
    let limit_reached = cursor < file_size && bytes.len() >= cap;
    Ok(Fragment {
        bytes,
        end: cursor,
        terminated: false,
        limit_reached,
        incomplete_tail: raw_cursor == file_size && cursor < raw_cursor,
    })
}

fn avoid_utf16_surrogate_split(bytes: &[u8], consumed: usize, encoding: TextEncoding) -> usize {
    if !matches!(encoding, TextEncoding::Utf16Le | TextEncoding::Utf16Be)
        || consumed < 2
        || bytes.len() < consumed + 2
    {
        return consumed;
    }
    let code_unit = |pair: &[u8]| match encoding {
        TextEncoding::Utf16Be => u16::from_be_bytes([pair[0], pair[1]]),
        _ => u16::from_le_bytes([pair[0], pair[1]]),
    };
    let last = code_unit(&bytes[consumed - 2..consumed]);
    let next = code_unit(&bytes[consumed..consumed + 2]);
    if (0xd800..=0xdbff).contains(&last) && (0xdc00..=0xdfff).contains(&next) {
        consumed - 2
    } else {
        consumed
    }
}

pub(super) fn find_delimiter(bytes: &[u8], encoding: TextEncoding) -> Option<(usize, usize)> {
    if unit_size(encoding) == 1 {
        let index = bytes
            .iter()
            .position(|byte| *byte == b'\n' || *byte == b'\r')?;
        let delimiter_len = if bytes[index] == b'\r' && bytes.get(index + 1) == Some(&b'\n') {
            2
        } else {
            1
        };
        return Some((index, delimiter_len));
    }
    for index in (0..bytes.len().saturating_sub(1)).step_by(2) {
        let unit = match encoding {
            TextEncoding::Utf16Be => u16::from_be_bytes([bytes[index], bytes[index + 1]]),
            _ => u16::from_le_bytes([bytes[index], bytes[index + 1]]),
        };
        if unit == 10 || unit == 13 {
            let next = bytes.get(index + 2..index + 4);
            let crlf = unit == 13
                && next.is_some_and(|next| match encoding {
                    TextEncoding::Utf16Be => next == [0, 10],
                    _ => next == [10, 0],
                });
            return Some((index, if crlf { 4 } else { 2 }));
        }
    }
    None
}

pub(super) fn delimiter_ends(
    bytes: &[u8],
    encoding: TextEncoding,
    absolute_start: u64,
) -> Vec<usize> {
    let mut result = Vec::new();
    let unit = unit_size(encoding);
    let mut index = 0;
    while index + unit <= bytes.len() {
        if let Some((delimiter, length)) = find_delimiter(&bytes[index..], encoding) {
            let end = index + delimiter + length;
            result.push(end);
            index = end;
        } else {
            break;
        }
    }
    let _ = absolute_start;
    result
}

fn previous_page_start(
    file: &mut File,
    target: u64,
    row_count: usize,
    max_scan_bytes: u64,
    row_bytes: usize,
    encoding: TextEncoding,
) -> io::Result<u64> {
    if target == 0 {
        return Ok(0);
    }
    let unit = unit_size(encoding) as u64;
    let ends_at_line_boundary = offset_ends_line(file, target, encoding)?;
    let required_breaks = row_count.saturating_add(usize::from(ends_at_line_boundary));
    let mut scan_start = target.saturating_sub(max_scan_bytes);
    scan_start -= scan_start % unit;
    let viewport_bytes = (row_count.min(MAX_PAGE_ROWS) as u64)
        .saturating_mul(row_bytes.min(MAX_ROW_BYTES) as u64)
        .min(MAX_PAGE_BYTES as u64)
        .max(unit);
    let byte_fallback = align_offset(target.saturating_sub(viewport_bytes), encoding);
    let mut cursor = target;
    let mut found = 0;
    let mut buffer = vec![0_u8; BLOCK_SIZE];
    while cursor > scan_start && found < required_breaks {
        let block_start = cursor.saturating_sub(buffer.len() as u64).max(scan_start);
        let length = (cursor - block_start) as usize;
        file.seek(SeekFrom::Start(block_start))?;
        let count = file.read(&mut buffer[..length])?;
        if count == 0 {
            break;
        }
        let bytes = &buffer[..count];
        if unit == 1 {
            for index in (0..bytes.len()).rev() {
                if bytes[index] == b'\n' {
                    found += 1;
                    if found >= required_breaks {
                        let candidate = block_start + index as u64 + 1;
                        return Ok(if target.saturating_sub(candidate) > viewport_bytes {
                            byte_fallback
                        } else {
                            candidate
                        });
                    }
                }
            }
        } else {
            let mut index = bytes.len().saturating_sub(2) & !1;
            loop {
                if index + 1 >= bytes.len() {
                    if index < 2 {
                        break;
                    }
                    index -= 2;
                    continue;
                }
                let value = if matches!(encoding, TextEncoding::Utf16Be) {
                    u16::from_be_bytes([bytes[index], bytes[index + 1]])
                } else {
                    u16::from_le_bytes([bytes[index], bytes[index + 1]])
                };
                if value == 10 {
                    found += 1;
                    if found >= required_breaks {
                        let candidate = block_start + index as u64 + 2;
                        return Ok(if target.saturating_sub(candidate) > viewport_bytes {
                            byte_fallback
                        } else {
                            candidate
                        });
                    }
                }
                if index < 2 {
                    break;
                }
                index -= 2;
            }
        }
        cursor = block_start;
    }
    Ok(byte_fallback)
}

fn offset_ends_line(file: &mut File, offset: u64, encoding: TextEncoding) -> io::Result<bool> {
    let unit = unit_size(encoding);
    if offset < unit as u64 {
        return Ok(false);
    }
    let mut bytes = [0_u8; 2];
    file.seek(SeekFrom::Start(offset - unit as u64))?;
    if file.read(&mut bytes[..unit])? != unit {
        return Ok(false);
    }
    Ok(if unit == 1 {
        bytes[0] == b'\n' || bytes[0] == b'\r'
    } else if matches!(encoding, TextEncoding::Utf16Be) {
        matches!(u16::from_be_bytes(bytes), 10 | 13)
    } else {
        matches!(u16::from_le_bytes(bytes), 10 | 13)
    })
}

fn detect_encoding(file: &mut File, size: u64) -> io::Result<TextEncoding> {
    let mut bom = [0_u8; 3];
    let count = file.read(&mut bom[..size.min(3) as usize])?;
    Ok(if count >= 3 && bom == [0xef, 0xbb, 0xbf] {
        TextEncoding::Utf8Bom
    } else if count >= 2 && bom[..2] == [0xff, 0xfe] {
        TextEncoding::Utf16Le
    } else if count >= 2 && bom[..2] == [0xfe, 0xff] {
        TextEncoding::Utf16Be
    } else {
        TextEncoding::Utf8
    })
}

pub(super) fn unit_size(encoding: TextEncoding) -> usize {
    if matches!(encoding, TextEncoding::Utf16Le | TextEncoding::Utf16Be) {
        2
    } else {
        1
    }
}

pub(super) fn minimum_fragment_bytes(encoding: TextEncoding) -> usize {
    if matches!(encoding, TextEncoding::Utf8 | TextEncoding::Utf8Bom) {
        4
    } else {
        unit_size(encoding)
    }
}

pub(crate) fn align_offset(offset: u64, encoding: TextEncoding) -> u64 {
    if unit_size(encoding) == 2 {
        offset - offset % 2
    } else {
        offset
    }
}

pub(super) fn decode(bytes: &[u8], encoding: TextEncoding, at_file_start: bool) -> String {
    let bytes = if at_file_start {
        match encoding {
            TextEncoding::Utf8Bom => bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes),
            TextEncoding::Utf16Le => bytes.strip_prefix(&[0xff, 0xfe]).unwrap_or(bytes),
            TextEncoding::Utf16Be => bytes.strip_prefix(&[0xfe, 0xff]).unwrap_or(bytes),
            _ => bytes,
        }
    } else {
        bytes
    };
    match encoding {
        TextEncoding::Utf8 | TextEncoding::Utf8Bom => String::from_utf8_lossy(bytes).into_owned(),
        TextEncoding::Utf16Le => UTF_16LE.decode_without_bom_handling(bytes).0.into_owned(),
        TextEncoding::Utf16Be => UTF_16BE.decode_without_bom_handling(bytes).0.into_owned(),
        TextEncoding::Windows1251 => WINDOWS_1251
            .decode_without_bom_handling(bytes)
            .0
            .into_owned(),
    }
}

pub(super) fn trim_utf8_boundary(bytes: &mut Vec<u8>) {
    if bytes.is_empty() {
        return;
    }
    let mut lead_index = bytes.len() - 1;
    while lead_index > 0 && bytes[lead_index] & 0xc0 == 0x80 {
        lead_index -= 1;
    }
    let lead_byte = bytes[lead_index];
    let expected = match bytes[lead_index] {
        0xc2..=0xdf => 2,
        0xe0..=0xef => 3,
        0xf0..=0xf4 => 4,
        _ => 1,
    };
    let present = bytes.len() - lead_index;
    if expected > present && (0xc2..=0xf4).contains(&lead_byte) {
        bytes.truncate(lead_index);
    }
}

#[cfg(test)]
#[path = "../tests/unit/log_io.rs"]
mod tests;
