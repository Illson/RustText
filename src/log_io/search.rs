use std::{
    fs::File,
    io::{self, BufRead, BufReader, Read, Seek, SeekFrom},
    path::Path,
    sync::atomic::{AtomicBool, Ordering},
};

use regex::{Regex, RegexBuilder};

use super::{
    BLOCK_SIZE, Fragment, LogError, LogReader, align_offset, decode, find_delimiter,
    minimum_fragment_bytes, trim_utf8_boundary, unit_size,
};
use crate::text_format::TextEncoding;

const DEFAULT_ROW_BYTES: usize = 16 * 1024;
const DEFAULT_SEARCH_LINE_BYTES: usize = 8 * 1024 * 1024;
const DEFAULT_SEARCH_BYTES: u64 = 512 * 1024 * 1024;
const MAX_SEARCH_LINE_BYTES: usize = 16 * 1024 * 1024;
const MAX_SEARCH_RESULTS: usize = 10_000;
const MAX_PREVIEW_BYTES: usize = 64 * 1024;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SearchHit {
    /// Zero-based physical line index from this search page's start.
    pub line_index: u64,
    pub line_start: u64,
    pub line_end: u64,
    /// Bounded, decoded preview of the matching line.
    pub preview: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SearchPage {
    pub hits: Vec<SearchHit>,
    pub next_offset: u64,
    pub next_line_index: u64,
    pub scanned_bytes: u64,
    pub complete: bool,
    /// Retry offset for an incomplete UTF-8 tail. This may preserve the caller's original
    /// arbitrary byte start when that start caused the current physical line to be skipped.
    pub pending_line_start: Option<u64>,
    /// File length observed while this page was searched.
    pub file_size: u64,
    /// Resume at `next_offset` to continue after a bounded batch.
    pub work_limit_reached: bool,
}

#[derive(Clone, Copy, Debug)]
pub struct SearchLimits {
    pub max_scanned_bytes: u64,
    pub max_line_bytes: usize,
    pub preview_bytes: usize,
}

impl Default for SearchLimits {
    fn default() -> Self {
        Self {
            max_scanned_bytes: DEFAULT_SEARCH_BYTES,
            max_line_bytes: DEFAULT_SEARCH_LINE_BYTES,
            preview_bytes: DEFAULT_ROW_BYTES,
        }
    }
}

#[derive(Clone, Debug)]
pub struct LogSearchQuery {
    pub pattern: String,
    pub regex: bool,
    pub case_sensitive: bool,
    pub whole_word: bool,
}

impl LogSearchQuery {
    pub fn literal(text: impl Into<String>, case_sensitive: bool) -> Self {
        Self {
            pattern: text.into(),
            regex: false,
            case_sensitive,
            whole_word: false,
        }
    }

    pub fn regex(pattern: impl Into<String>, case_sensitive: bool) -> Self {
        Self {
            pattern: pattern.into(),
            regex: true,
            case_sensitive,
            whole_word: false,
        }
    }

    fn compile(&self) -> Result<Regex, LogError> {
        if self.pattern.is_empty() {
            return Err(LogError::InvalidRegex(regex::Error::Syntax(
                "пустой запрос".into(),
            )));
        }
        let mut pattern = if self.regex {
            self.pattern.clone()
        } else {
            regex::escape(&self.pattern)
        };
        if self.whole_word {
            pattern = format!(r"\b(?:{pattern})\b");
        }
        RegexBuilder::new(&pattern)
            .case_insensitive(!self.case_sensitive)
            .build()
            .map_err(LogError::InvalidRegex)
    }
}

/// Streams physical lines until a result page fills or the work limit is
/// reached. A reached limit is explicit in `SearchPage::work_limit_reached`,
/// and the returned offset is safe to resume. `cancel` can be shared with a UI.
pub fn search_page(
    path: impl AsRef<Path>,
    encoding_override: Option<TextEncoding>,
    query: &LogSearchQuery,
    start_offset: u64,
    max_results: usize,
    limits: SearchLimits,
    cancel: &AtomicBool,
) -> Result<SearchPage, LogError> {
    let regex = query.compile()?;
    let mut reader = LogReader::open(path, encoding_override)?;
    let size = reader.size;
    let mut cursor = align_offset(start_offset.min(size), reader.encoding);
    let max_line_bytes = limits.max_line_bytes.clamp(
        minimum_fragment_bytes(reader.encoding),
        MAX_SEARCH_LINE_BYTES,
    );
    let mut stream = BufReader::with_capacity(BLOCK_SIZE * 2, reader.file.try_clone()?);
    stream.seek(SeekFrom::Start(cursor))?;
    let mut pending_line_start = None;
    let mut line_index = 0;
    // Search resume offsets are line boundaries. For a user supplied arbitrary
    // byte position, consume only the partial line at that position.
    if cursor > 0 && !is_line_boundary(&mut reader.file, cursor, reader.encoding)? {
        let arbitrary_start = cursor;
        let partial =
            read_buffered_line(&mut stream, cursor, size, max_line_bytes, reader.encoding)?;
        if partial.limit_reached {
            return Err(LogError::LineLimit {
                line_index,
                limit: max_line_bytes,
            });
        }
        if partial.incomplete_tail {
            pending_line_start = Some(arbitrary_start);
        }
        cursor = partial.end;
        line_index += u64::from(partial.terminated);
    }
    let page_start = cursor;
    let result_limit = max_results.min(MAX_SEARCH_RESULTS);
    let preview_bytes = limits.preview_bytes.min(MAX_PREVIEW_BYTES);
    let mut hits = Vec::new();
    let mut work_limit_reached = false;
    while pending_line_start.is_none() && cursor < size && hits.len() < result_limit {
        if cancel.load(Ordering::Relaxed) {
            return Err(LogError::Cancelled);
        }
        let fragment =
            read_buffered_line(&mut stream, cursor, size, max_line_bytes, reader.encoding)?;
        if fragment.end <= cursor {
            if fragment.incomplete_tail {
                pending_line_start = Some(cursor);
            }
            break;
        }
        if fragment.limit_reached {
            return Err(LogError::LineLimit {
                line_index,
                limit: max_line_bytes,
            });
        }
        let line = decode(&fragment.bytes, reader.encoding, cursor == 0);
        if regex.is_match(&line) {
            let preview = truncate_utf8(&line, preview_bytes);
            hits.push(SearchHit {
                line_index,
                line_start: cursor,
                line_end: fragment.end,
                preview,
            });
        }
        if fragment.incomplete_tail {
            pending_line_start = Some(cursor);
        }
        cursor = fragment.end;
        line_index += u64::from(fragment.terminated);
        if pending_line_start.is_some() {
            break;
        }
        if cursor - page_start >= limits.max_scanned_bytes
            && cursor < size
            && hits.len() < result_limit
        {
            work_limit_reached = true;
            break;
        }
    }
    Ok(SearchPage {
        hits,
        next_offset: pending_line_start.unwrap_or(cursor),
        next_line_index: line_index,
        scanned_bytes: cursor - page_start,
        complete: cursor >= size && pending_line_start.is_none(),
        pending_line_start,
        file_size: size,
        work_limit_reached,
    })
}

pub(super) fn read_buffered_line(
    reader: &mut BufReader<File>,
    start: u64,
    file_size: u64,
    max_bytes: usize,
    encoding: TextEncoding,
) -> io::Result<Fragment> {
    let cap = max_bytes.max(minimum_fragment_bytes(encoding));
    let unit = unit_size(encoding);
    let mut bytes = Vec::with_capacity(cap.min(BLOCK_SIZE));
    let mut cursor = start;
    while cursor < file_size && bytes.len() < cap {
        let available = reader.fill_buf()?;
        if available.is_empty() {
            break;
        }
        let usable = if unit == 2 {
            available.len() - available.len() % unit
        } else {
            available.len()
        };
        if usable == 0 {
            let trailing = available[0];
            bytes.push(trailing);
            reader.consume(1);
            cursor += 1;
            continue;
        }
        if let Some((index, delimiter_len)) = find_delimiter(&available[..usable], encoding) {
            let remaining = cap - bytes.len();
            if index > remaining {
                let take = if unit == 2 {
                    remaining - remaining % unit
                } else {
                    remaining
                };
                bytes.extend_from_slice(&available[..take]);
                reader.consume(take);
                cursor += take as u64;
                return Ok(Fragment {
                    bytes,
                    end: cursor,
                    terminated: false,
                    limit_reached: true,
                    incomplete_tail: false,
                });
            }
            let boundary_cr = delimiter_len == unit
                && index + unit == usable
                && is_cr_unit(&available[index..index + unit], encoding);
            bytes.extend_from_slice(&available[..index]);
            let mut consumed = index + delimiter_len;
            reader.consume(consumed);
            cursor += consumed as u64;
            if boundary_cr && cursor < file_size {
                let next = reader.fill_buf()?;
                let is_lf = if unit == 1 {
                    next.first() == Some(&b'\n')
                } else if next.len() >= 2 {
                    is_lf_unit(&next[..2], encoding)
                } else {
                    false
                };
                if is_lf {
                    consumed = unit;
                    reader.consume(consumed);
                    cursor += consumed as u64;
                }
            }
            return Ok(Fragment {
                bytes,
                end: cursor,
                terminated: true,
                limit_reached: false,
                incomplete_tail: false,
            });
        }
        let take = (cap - bytes.len()).min(usable);
        let take = if unit == 2 { take - take % unit } else { take };
        if take == 0 {
            break;
        }
        bytes.extend_from_slice(&available[..take]);
        reader.consume(take);
        cursor += take as u64;
    }
    let raw_cursor = cursor;
    if matches!(encoding, TextEncoding::Utf8 | TextEncoding::Utf8Bom) {
        trim_utf8_boundary(&mut bytes);
        cursor = start + bytes.len() as u64;
        reader.seek(SeekFrom::Start(cursor))?;
    }
    let limit_reached = raw_cursor.saturating_sub(start) >= cap as u64 && raw_cursor < file_size;
    Ok(Fragment {
        bytes,
        end: cursor,
        terminated: false,
        limit_reached,
        incomplete_tail: raw_cursor == file_size && cursor < raw_cursor,
    })
}

fn is_cr_unit(bytes: &[u8], encoding: TextEncoding) -> bool {
    if bytes.len() == 1 {
        return bytes[0] == b'\r';
    }
    match encoding {
        TextEncoding::Utf16Be => bytes == [0, 13],
        _ => bytes == [13, 0],
    }
}

fn is_lf_unit(bytes: &[u8], encoding: TextEncoding) -> bool {
    match encoding {
        TextEncoding::Utf16Be => bytes == [0, 10],
        _ => bytes == [10, 0],
    }
}

fn is_line_boundary(file: &mut File, offset: u64, encoding: TextEncoding) -> io::Result<bool> {
    let unit = unit_size(encoding);
    let at = offset.saturating_sub(unit as u64);
    let mut bytes = [0_u8; 2];
    file.seek(SeekFrom::Start(at))?;
    let count = file.read(&mut bytes[..unit])?;
    if count != unit {
        return Ok(false);
    }
    Ok(if unit == 1 {
        bytes[0] == b'\n' || bytes[0] == b'\r'
    } else if matches!(encoding, TextEncoding::Utf16Be) {
        let value = u16::from_be_bytes(bytes);
        value == 10 || value == 13
    } else {
        let value = u16::from_le_bytes(bytes);
        value == 10 || value == 13
    })
}

fn truncate_utf8(text: &str, max_bytes: usize) -> String {
    if text.len() <= max_bytes {
        return text.to_owned();
    }
    let mut end = max_bytes.min(text.len());
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    text[..end].to_owned()
}
