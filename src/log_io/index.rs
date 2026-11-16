use std::{
    fs::File,
    io::{Read, Seek, SeekFrom},
    sync::atomic::{AtomicBool, Ordering},
};

use super::{BLOCK_SIZE, LogError, delimiter_ends, unit_size};
use crate::text_format::TextEncoding;

const INDEX_SAMPLE_BYTES: u64 = 64 * 1024;
const MAX_INDEX_ENTRIES: usize = 131_072;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SparseLine {
    pub line: u64,
    pub offset: u64,
}

/// Incremental bounded index. It can be serialized by the owner as the public
/// entry list plus `scanned_to`/`line_count`; callers should discard it when
/// file identity or sampled contents change.
#[derive(Clone, Debug)]
pub struct LineIndex {
    entries: Vec<SparseLine>,
    scanned_to: u64,
    line_count: u64,
    last_line_start: u64,
    next_sample: u64,
    sample_stride: u64,
}

impl Default for LineIndex {
    fn default() -> Self {
        Self {
            entries: vec![SparseLine { line: 1, offset: 0 }],
            scanned_to: 0,
            line_count: 1,
            last_line_start: 0,
            next_sample: INDEX_SAMPLE_BYTES,
            sample_stride: INDEX_SAMPLE_BYTES,
        }
    }
}

impl LineIndex {
    pub fn entries(&self) -> &[SparseLine] {
        &self.entries
    }

    pub fn scanned_to(&self) -> u64 {
        self.scanned_to
    }

    pub fn line_count(&self) -> u64 {
        self.line_count
    }

    pub(crate) fn last_line_start(&self) -> u64 {
        self.last_line_start
    }

    /// Counts physical rows in the indexed prefix, excluding an empty trailing
    /// row after a final delimiter and content that consists only of a BOM.
    pub(crate) fn physical_line_count(&self, content_start: u64) -> u64 {
        if self.scanned_to <= content_start {
            return 0;
        }
        self.line_count
            .saturating_sub(u64::from(self.last_line_start == self.scanned_to))
    }

    pub(crate) fn advance(
        &mut self,
        file: &mut File,
        encoding: TextEncoding,
        through: u64,
        cancel: &AtomicBool,
    ) -> Result<u64, LogError> {
        let starting_position = self.scanned_to;
        let unit = unit_size(encoding) as u64;
        let through = through - through % unit;
        let mut buffer = vec![0_u8; BLOCK_SIZE];
        while self.scanned_to < through {
            if cancel.load(Ordering::Relaxed) {
                return Err(LogError::Cancelled);
            }
            let cursor = self.scanned_to;
            let merges_trailing_cr = self.last_line_start == cursor
                && cursor >= unit
                && has_carriage_return_at(file, cursor - unit, encoding)?
                && has_line_feed_at(file, cursor, encoding)?;
            // Include a small overlap so a CRLF split at a block boundary is
            // recognized as one delimiter and its LF is not counted twice.
            let overlap = (unit_size(encoding) * 2) as u64;
            let block_start = cursor.saturating_sub(overlap);
            let skip_before = (cursor - block_start) as usize;
            let lookahead = unit_size(encoding);
            let core_capacity = buffer.len().saturating_sub(skip_before + lookahead) as u64;
            let advance_limit = (through - cursor).min(core_capacity);
            let file_capacity = file
                .metadata()?
                .len()
                .saturating_sub(block_start)
                .min(skip_before as u64 + advance_limit + lookahead as u64)
                as usize;
            file.seek(SeekFrom::Start(block_start))?;
            let count = file.read(&mut buffer[..file_capacity])?;
            if count <= skip_before {
                break;
            }
            let available_bytes = (count - skip_before).min(advance_limit as usize);
            let advance_bytes = available_bytes - available_bytes % unit as usize;
            if advance_bytes == 0 {
                break;
            }
            let bytes = &buffer[..count];
            for delimiter_end in delimiter_ends(bytes, encoding, block_start) {
                let line_offset = block_start + delimiter_end as u64;
                if line_offset <= cursor || line_offset > cursor + advance_bytes as u64 {
                    continue;
                }
                if merges_trailing_cr && line_offset == cursor + unit {
                    self.last_line_start = line_offset;
                    let sample_at_cursor = self.entries.last_mut().is_some_and(|sample| {
                        if sample.offset == cursor {
                            sample.offset = line_offset;
                            true
                        } else {
                            false
                        }
                    });
                    if sample_at_cursor {
                        self.next_sample = self.next_sample.saturating_add(unit);
                    } else {
                        self.sample_line(line_offset);
                    }
                    continue;
                }
                self.line_count += 1;
                self.last_line_start = line_offset;
                self.sample_line(line_offset);
            }
            self.scanned_to = cursor + advance_bytes as u64;
        }
        Ok(self.scanned_to - starting_position)
    }

    fn sample_line(&mut self, line_offset: u64) {
        if line_offset < self.next_sample {
            return;
        }
        if self.entries.len() >= MAX_INDEX_ENTRIES {
            let old_len = self.entries.len();
            self.entries = self
                .entries
                .iter()
                .enumerate()
                .filter(|(index, _)| index % 2 == 0 || index + 1 == old_len)
                .map(|(_, entry)| *entry)
                .collect();
            self.sample_stride = self.sample_stride.saturating_mul(2);
        } else {
            self.entries.push(SparseLine {
                line: self.line_count,
                offset: line_offset,
            });
        }
        self.next_sample = line_offset.saturating_add(self.sample_stride);
    }
}

fn has_carriage_return_at(
    file: &mut File,
    offset: u64,
    encoding: TextEncoding,
) -> Result<bool, LogError> {
    has_code_unit_at(file, offset, encoding, 13)
}

fn has_line_feed_at(
    file: &mut File,
    offset: u64,
    encoding: TextEncoding,
) -> Result<bool, LogError> {
    has_code_unit_at(file, offset, encoding, 10)
}

fn has_code_unit_at(
    file: &mut File,
    offset: u64,
    encoding: TextEncoding,
    expected: u16,
) -> Result<bool, LogError> {
    let unit = unit_size(encoding);
    let mut bytes = [0_u8; 2];
    file.seek(SeekFrom::Start(offset))?;
    file.read_exact(&mut bytes[..unit])?;
    Ok(if unit == 1 {
        bytes[0] == expected as u8
    } else if matches!(encoding, TextEncoding::Utf16Be) {
        u16::from_be_bytes(bytes) == expected
    } else {
        u16::from_le_bytes(bytes) == expected
    })
}
