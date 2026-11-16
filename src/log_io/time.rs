use std::{
    io::{BufReader, Read, Seek, SeekFrom},
    sync::atomic::{AtomicBool, Ordering},
};

use crate::text_format::TextEncoding;

use super::{
    BLOCK_SIZE, LogError, LogReader, align_offset, decode, read_fragment,
    search::read_buffered_line, unit_size,
};

const MAX_BACKWARD_PROBE_BYTES: u64 = 1024 * 1024;
const MAX_TIMESTAMP_LINE_BYTES: usize = 1024 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
struct Timestamp {
    year: u16,
    month: u8,
    day: u8,
    hour: u8,
    minute: u8,
    second: u8,
    nanosecond: u32,
}

#[derive(Clone, Debug)]
struct DatedLine {
    start: u64,
    end: u64,
    timestamp: Timestamp,
}

#[derive(Debug, PartialEq, Eq)]
struct SearchOutcome {
    offset: Option<u64>,
    probes: usize,
    used_fallback: bool,
}

enum ProbeResult {
    Dated(DatedLine),
    Unsupported,
}

impl LogReader {
    /// Finds the first dated physical line at or after `target`.
    ///
    /// Timestamps must be chronologically ordered for binary search. Missing
    /// timestamps, long probe lines, or inconsistent sampled timestamps use
    /// a cancellable forward scan from `start_offset`.
    pub fn seek_timestamp(
        &mut self,
        start_offset: u64,
        target: &str,
        cancel: &AtomicBool,
    ) -> Result<Option<u64>, LogError> {
        Ok(search(self, start_offset, target, cancel)?.offset)
    }
}

fn search(
    reader: &mut LogReader,
    start_offset: u64,
    query: &str,
    cancel: &AtomicBool,
) -> Result<SearchOutcome, LogError> {
    if cancel.load(Ordering::Relaxed) {
        return Err(LogError::Cancelled);
    }
    let target = parse_query_timestamp(query).ok_or(LogError::InvalidTimestamp)?;
    let start = align_offset(start_offset.min(reader.size), reader.encoding);
    if start >= reader.size {
        return Ok(SearchOutcome {
            offset: None,
            probes: 0,
            used_fallback: false,
        });
    }

    let mut probes = Vec::new();
    let first = match probe_line(reader, start, start)? {
        ProbeResult::Dated(line) => line,
        ProbeResult::Unsupported => return forward_scan(reader, start, target, cancel, 0),
    };
    probes.push(first.clone());
    if cancel.load(Ordering::Relaxed) {
        return Err(LogError::Cancelled);
    }
    let Some(last_start) = last_line_start(reader, start)? else {
        return forward_scan(reader, start, target, cancel, 1);
    };
    let last = match probe_line(reader, last_start, start)? {
        ProbeResult::Dated(line) => line,
        ProbeResult::Unsupported => return forward_scan(reader, start, target, cancel, 1),
    };
    if !insert_monotonic_probe(&mut probes, last.clone()) {
        return forward_scan(reader, start, target, cancel, 2);
    }
    if first.timestamp >= target {
        return Ok(SearchOutcome {
            offset: Some(first.start),
            probes: 2,
            used_fallback: false,
        });
    }
    if last.timestamp < target {
        return Ok(SearchOutcome {
            offset: None,
            probes: 2,
            used_fallback: false,
        });
    }

    let mut low = first.end;
    let mut high = last.start;
    let mut best = Some(last.start);
    let mut probe_count = 2;
    while low < high {
        if cancel.load(Ordering::Relaxed) {
            return Err(LogError::Cancelled);
        }
        let midpoint = low + (high - low) / 2;
        let line = match probe_line(reader, midpoint, start)? {
            ProbeResult::Dated(line) => line,
            ProbeResult::Unsupported => {
                return forward_scan(reader, start, target, cancel, probe_count);
            }
        };
        probe_count += 1;
        if line.start < low || !insert_monotonic_probe(&mut probes, line.clone()) {
            return forward_scan(reader, start, target, cancel, probe_count);
        }
        if line.timestamp >= target {
            best = Some(line.start);
            high = line.start;
        } else {
            low = line.end;
        }
    }

    Ok(SearchOutcome {
        offset: best,
        probes: probe_count,
        used_fallback: false,
    })
}

fn insert_monotonic_probe(probes: &mut Vec<DatedLine>, line: DatedLine) -> bool {
    let index = probes.partition_point(|probe| probe.start < line.start);
    if probes
        .get(index)
        .is_some_and(|probe| probe.start == line.start)
    {
        return probes[index].timestamp == line.timestamp;
    }
    if index > 0 && probes[index - 1].timestamp > line.timestamp {
        return false;
    }
    if probes
        .get(index)
        .is_some_and(|probe| line.timestamp > probe.timestamp)
    {
        return false;
    }
    probes.insert(index, line);
    true
}

fn forward_scan(
    reader: &mut LogReader,
    start: u64,
    target: Timestamp,
    cancel: &AtomicBool,
    probes: usize,
) -> Result<SearchOutcome, LogError> {
    let mut cursor = start;
    let mut buffered = BufReader::with_capacity(BLOCK_SIZE * 2, reader.file.try_clone()?);
    buffered.seek(SeekFrom::Start(start))?;
    while cursor < reader.size {
        if cancel.load(Ordering::Relaxed) {
            return Err(LogError::Cancelled);
        }
        let fragment = read_buffered_line(
            &mut buffered,
            cursor,
            reader.size,
            MAX_TIMESTAMP_LINE_BYTES,
            reader.encoding,
        )?;
        if fragment.end <= cursor {
            break;
        }
        let text = decode(&fragment.bytes, reader.encoding, cursor == 0);
        if parse_timestamp(&text).is_some_and(|timestamp| timestamp >= target) {
            return Ok(SearchOutcome {
                offset: Some(cursor),
                probes,
                used_fallback: true,
            });
        }
        cursor = fragment.end;
        let mut line_continues = fragment.limit_reached;
        while line_continues {
            if cancel.load(Ordering::Relaxed) {
                return Err(LogError::Cancelled);
            }
            let next = read_buffered_line(
                &mut buffered,
                cursor,
                reader.size,
                MAX_TIMESTAMP_LINE_BYTES,
                reader.encoding,
            )?;
            if next.end <= cursor {
                break;
            }
            cursor = next.end;
            line_continues = next.limit_reached;
        }
    }
    Ok(SearchOutcome {
        offset: None,
        probes,
        used_fallback: true,
    })
}

fn probe_line(
    reader: &mut LogReader,
    probe: u64,
    minimum_start: u64,
) -> Result<ProbeResult, LogError> {
    let start = match line_start_at_or_before(reader, probe, minimum_start)? {
        Some(start) => start,
        None => return Ok(ProbeResult::Unsupported),
    };
    let fragment = read_fragment(
        &mut reader.file,
        start,
        reader.size,
        MAX_TIMESTAMP_LINE_BYTES,
        reader.encoding,
    )?;
    if fragment.end <= start
        || fragment.limit_reached
        || (!fragment.terminated && fragment.end < reader.size)
    {
        return Ok(ProbeResult::Unsupported);
    }
    let text = decode(&fragment.bytes, reader.encoding, start == 0);
    let Some(timestamp) = parse_timestamp(&text) else {
        return Ok(ProbeResult::Unsupported);
    };
    Ok(ProbeResult::Dated(DatedLine {
        start,
        end: fragment.end,
        timestamp,
    }))
}

fn last_line_start(reader: &mut LogReader, minimum_start: u64) -> Result<Option<u64>, LogError> {
    let unit = unit_size(reader.encoding) as u64;
    let mut probe = reader.size.saturating_sub(unit);
    probe = align_offset(probe, reader.encoding);
    line_start_at_or_before(reader, probe, minimum_start)
}

fn line_start_at_or_before(
    reader: &mut LogReader,
    probe: u64,
    minimum_start: u64,
) -> Result<Option<u64>, LogError> {
    let unit = unit_size(reader.encoding) as u64;
    let probe = align_offset(probe.min(reader.size), reader.encoding);
    if probe <= minimum_start {
        return Ok(Some(minimum_start));
    }
    let scan_start = align_offset(
        probe.saturating_sub(MAX_BACKWARD_PROBE_BYTES),
        reader.encoding,
    );
    let length = (probe - scan_start) as usize;
    let mut bytes = vec![0; length];
    reader.file.seek(SeekFrom::Start(scan_start))?;
    reader.file.read_exact(&mut bytes)?;
    let mut index = bytes.len();
    while index >= unit as usize {
        index -= unit as usize;
        let code_unit = read_code_unit(&bytes[index..index + unit as usize], reader.encoding);
        let absolute = scan_start + index as u64;
        if code_unit == 10 {
            return Ok(Some((absolute + unit).max(minimum_start)));
        }
        if code_unit == 13 {
            let next = absolute + unit;
            if next == probe {
                if code_unit_at_file(&mut reader.file, probe, reader.encoding)? == Some(10) {
                    continue;
                }
                return Ok(Some(next.max(minimum_start)));
            }
            if next + unit <= probe
                && code_unit_at(&bytes, index + unit as usize, reader.encoding) == Some(10)
            {
                return Ok(Some((next + unit).max(minimum_start)));
            }
            return Ok(Some(next.max(minimum_start)));
        }
    }
    if scan_start <= minimum_start {
        Ok(Some(minimum_start))
    } else {
        Ok(None)
    }
}

fn code_unit_at_file(
    file: &mut std::fs::File,
    offset: u64,
    encoding: TextEncoding,
) -> Result<Option<u16>, LogError> {
    let unit = unit_size(encoding);
    let mut bytes = [0_u8; 2];
    file.seek(SeekFrom::Start(offset))?;
    match file.read_exact(&mut bytes[..unit]) {
        Ok(()) => Ok(Some(read_code_unit(&bytes[..unit], encoding))),
        Err(error) if error.kind() == std::io::ErrorKind::UnexpectedEof => Ok(None),
        Err(error) => Err(error.into()),
    }
}

fn code_unit_at(bytes: &[u8], index: usize, encoding: TextEncoding) -> Option<u16> {
    let unit = unit_size(encoding);
    bytes
        .get(index..index + unit)
        .map(|bytes| read_code_unit(bytes, encoding))
}

fn read_code_unit(bytes: &[u8], encoding: TextEncoding) -> u16 {
    if unit_size(encoding) == 1 {
        bytes[0] as u16
    } else if matches!(encoding, TextEncoding::Utf16Be) {
        u16::from_be_bytes([bytes[0], bytes[1]])
    } else {
        u16::from_le_bytes([bytes[0], bytes[1]])
    }
}

fn parse_timestamp(text: &str) -> Option<Timestamp> {
    let bytes = text.as_bytes();
    for start in 0..bytes.len().saturating_sub(18) {
        if start > 0 && bytes[start - 1].is_ascii_digit() {
            continue;
        }
        if let Some(timestamp) = parse_timestamp_at(text, start) {
            return Some(timestamp);
        }
    }
    None
}

fn parse_query_timestamp(text: &str) -> Option<Timestamp> {
    let text = text.trim();
    let timestamp = parse_timestamp_at(text, 0)?;
    (timestamp_end(text.as_bytes())? == text.len()).then_some(timestamp)
}

fn timestamp_end(bytes: &[u8]) -> Option<usize> {
    if bytes.len() < 19 {
        return None;
    }
    if bytes.get(19) != Some(&b'.') {
        return Some(19);
    }
    let mut end = 20;
    while bytes.get(end).is_some_and(u8::is_ascii_digit) {
        end += 1;
    }
    (1..=9).contains(&(end - 20)).then_some(end)
}

fn parse_timestamp_at(text: &str, start: usize) -> Option<Timestamp> {
    let bytes = text.as_bytes();
    let value = bytes.get(start..)?;
    if value.len() < 19
        || !value[0..4].iter().all(u8::is_ascii_digit)
        || value[4] != b'-'
        || !value[5..7].iter().all(u8::is_ascii_digit)
        || value[7] != b'-'
        || !value[8..10].iter().all(u8::is_ascii_digit)
        || !matches!(value[10], b' ' | b'T')
        || !value[11..13].iter().all(u8::is_ascii_digit)
        || value[13] != b':'
        || !value[14..16].iter().all(u8::is_ascii_digit)
        || value[16] != b':'
        || !value[17..19].iter().all(u8::is_ascii_digit)
    {
        return None;
    }

    let mut cursor = 19;
    let mut nanosecond = 0_u32;
    if value.get(cursor) == Some(&b'.') {
        cursor += 1;
        let fraction_start = cursor;
        while value.get(cursor).is_some_and(u8::is_ascii_digit) {
            cursor += 1;
        }
        let fraction_length = cursor - fraction_start;
        if !(1..=9).contains(&fraction_length) {
            return None;
        }
        for digit in &value[fraction_start..cursor] {
            nanosecond = nanosecond * 10 + (*digit - b'0') as u32;
        }
        for _ in fraction_length..9 {
            nanosecond *= 10;
        }
    }

    let year = decimal(&value[0..4])?;
    let month = decimal(&value[5..7])? as u8;
    let day = decimal(&value[8..10])? as u8;
    let hour = decimal(&value[11..13])? as u8;
    let minute = decimal(&value[14..16])? as u8;
    let second = decimal(&value[17..19])? as u8;
    if year == 0
        || month == 0
        || month > 12
        || day == 0
        || day > days_in_month(year, month)
        || hour > 23
        || minute > 59
        || second > 60
    {
        return None;
    }
    Some(Timestamp {
        year,
        month,
        day,
        hour,
        minute,
        second,
        nanosecond,
    })
}

fn decimal(bytes: &[u8]) -> Option<u16> {
    bytes.iter().try_fold(0_u16, |value, byte| {
        byte.is_ascii_digit()
            .then(|| value * 10 + (*byte - b'0') as u16)
    })
}

fn days_in_month(year: u16, month: u8) -> u8 {
    match month {
        2 if year.is_multiple_of(4) && (!year.is_multiple_of(100) || year.is_multiple_of(400)) => {
            29
        }
        2 => 28,
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    }
}

#[cfg(test)]
#[path = "../../tests/unit/log_time.rs"]
mod tests;
