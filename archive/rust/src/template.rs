use std::{
    error::Error,
    fmt,
    io::{self, Write},
};

use rand::{Rng, distr::Alphanumeric};
use uuid::Uuid;

use crate::files::FileTarget;

pub(crate) const NEW_TAB_BYTES: u64 = 8 * 1024 * 1024;
const CHUNK_BYTES: usize = 16 * 1024;
const DEFAULT_RANDOM_STRING_LENGTH: usize = 12;

#[derive(Debug, Default)]
#[repr(u8)]
pub(crate) enum TemplateError {
    #[default]
    None = 0,
    NoRows = 1,
    InvalidInteger = 2,
    IntegerOverflow = 3,
    InvalidRandomLength = 4,
    SizeOverflow = 5,
    Allocation = 6,
    Cancelled = 7,
    Io(io::Error) = 8,
}

impl fmt::Display for TemplateError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::None => Ok(()),
            Self::NoRows => formatter.write_str("Укажите количество строк больше нуля."),
            Self::InvalidInteger => formatter.write_str(
                "В плейсхолдере {int_from_N} укажите целое начальное число, например {int_from_5}.",
            ),
            Self::IntegerOverflow => {
                formatter.write_str("Счётчик {int_from_N} вышел за пределы целого числа.")
            }
            Self::InvalidRandomLength => formatter
                .write_str("Длина {rand_string_N} должна быть неотрицательным целым числом."),
            Self::SizeOverflow => {
                formatter.write_str("Размер результата вышел за пределы целого числа.")
            }
            Self::Allocation => {
                formatter.write_str("Недостаточно памяти для результата генерации.")
            }
            Self::Cancelled => formatter.write_str("Генерация отменена."),
            Self::Io(error) => write!(formatter, "Не удалось записать результат. {error}"),
        }
    }
}

impl Error for TemplateError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            _ => None,
        }
    }
}

impl From<io::Error> for TemplateError {
    fn from(error: io::Error) -> Self {
        if error.kind() == io::ErrorKind::OutOfMemory {
            Self::Allocation
        } else {
            Self::Io(error)
        }
    }
}

#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
pub(crate) struct GenerationProgress {
    pub rows: usize,
    pub bytes: u64,
}

/// Repeats a template block `rows` times, expanding its placeholders.
///
/// `{int_from_N}` adds the generated row index to N. `{guid}` creates a UUID
/// v4. `{rand_string}` creates 12 ASCII alphanumeric characters, while
/// `{rand_string_N}` selects a different length. Unrecognized placeholders are
/// copied literally. Use `generate_to_writer` for output that should not be held in memory.
#[cfg(test)]
pub(crate) fn generate(template: &str, rows: usize) -> Result<String, TemplateError> {
    generate_text(template, rows, |_| true)
}

pub(crate) fn generate_text(
    template: &str,
    rows: usize,
    progress: impl FnMut(GenerationProgress) -> bool,
) -> Result<String, TemplateError> {
    let mut output = TextOutput(Vec::new());
    generate_to_writer(template, rows, &mut output, progress)?;
    String::from_utf8(output.0).map_err(|error| TemplateError::Io(io::Error::other(error)))
}

pub(crate) fn completed_text(text: String, cancelled: bool) -> Result<String, TemplateError> {
    if cancelled {
        Err(TemplateError::Cancelled)
    } else {
        Ok(text)
    }
}

pub(crate) fn output_upper_bound(template: &str, rows: usize) -> Result<u64, TemplateError> {
    let segments = parse_template(template, rows)?;
    let mut row_bytes = 0u64;
    for segment in segments {
        let bytes = match segment {
            Segment::None => 0,
            Segment::Literal(text) => text.len(),
            Segment::Counter(_) => 20,
            Segment::Guid => 36,
            Segment::Random(length) => length,
        };
        row_bytes = row_bytes.saturating_add(bytes as u64);
    }
    Ok(row_bytes
        .saturating_mul(rows as u64)
        .saturating_add((rows - 1) as u64))
}

pub(crate) fn generate_to_writer(
    template: &str,
    rows: usize,
    output: &mut impl Write,
    mut progress: impl FnMut(GenerationProgress) -> bool,
) -> Result<GenerationProgress, TemplateError> {
    let segments = parse_template(template, rows)?;
    let mut state = GenerationProgress::default();
    if !progress(state) {
        return Err(TemplateError::Cancelled);
    }
    let mut rng = rand::rng();
    let mut random = [0u8; CHUNK_BYTES];
    for row in 0..rows {
        if row > 0 {
            write_chunk(output, b"\n", &mut state, &mut progress)?;
        }
        for segment in &segments {
            match segment {
                Segment::None => {}
                Segment::Literal(text) => {
                    for chunk in text.as_bytes().chunks(CHUNK_BYTES) {
                        write_chunk(output, chunk, &mut state, &mut progress)?;
                    }
                }
                Segment::Counter(start) => {
                    let row = i64::try_from(row).map_err(|_| TemplateError::IntegerOverflow)?;
                    let value = start
                        .checked_add(row)
                        .ok_or(TemplateError::IntegerOverflow)?;
                    let mut buffer = [0u8; 20];
                    let mut cursor = io::Cursor::new(buffer.as_mut_slice());
                    write!(cursor, "{value}")?;
                    let length = cursor.position() as usize;
                    write_chunk(output, &buffer[..length], &mut state, &mut progress)?;
                }
                Segment::Guid => {
                    let mut buffer = [0u8; 36];
                    let guid = Uuid::new_v4();
                    let text = guid.hyphenated().encode_lower(&mut buffer);
                    write_chunk(output, text.as_bytes(), &mut state, &mut progress)?;
                }
                Segment::Random(length) => {
                    let mut remaining = *length;
                    while remaining > 0 {
                        let length = remaining.min(CHUNK_BYTES);
                        for byte in &mut random[..length] {
                            *byte = rng.sample(Alphanumeric);
                        }
                        write_chunk(output, &random[..length], &mut state, &mut progress)?;
                        remaining -= length;
                    }
                }
            }
        }
        state.rows = row + 1;
        if !progress(state) {
            return Err(TemplateError::Cancelled);
        }
    }
    output.flush()?;
    Ok(state)
}

pub(crate) fn generate_file(
    template: &str,
    rows: usize,
    target: FileTarget,
    mut progress: impl FnMut(GenerationProgress) -> bool,
) -> Result<GenerationProgress, TemplateError> {
    parse_template(template, rows)?;
    let directory = target
        .path
        .parent()
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "Укажите папку файла"))?;
    #[cfg(windows)]
    let mut pending = if target.version.is_some() {
        crate::files::windows_save::private_temporary_file(directory)?
    } else {
        tempfile::NamedTempFile::new_in(directory)?
    };
    #[cfg(not(windows))]
    let mut pending = tempfile::NamedTempFile::new_in(directory)?;
    let state = {
        let mut writer = io::BufWriter::with_capacity(CHUNK_BYTES, &mut pending);
        generate_to_writer(template, rows, &mut writer, &mut progress)?
    };
    pending.as_file().sync_all()?;
    if !progress(state) {
        return Err(TemplateError::Cancelled);
    }
    crate::files::publish(&target, pending)?;
    Ok(state)
}

fn write_chunk(
    output: &mut impl Write,
    chunk: &[u8],
    state: &mut GenerationProgress,
    progress: &mut impl FnMut(GenerationProgress) -> bool,
) -> Result<(), TemplateError> {
    if !progress(*state) {
        return Err(TemplateError::Cancelled);
    }
    let bytes = state
        .bytes
        .checked_add(chunk.len() as u64)
        .ok_or(TemplateError::SizeOverflow)?;
    output.write_all(chunk)?;
    state.bytes = bytes;
    Ok(())
}

struct TextOutput(Vec<u8>);

impl Write for TextOutput {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.0
            .try_reserve(bytes.len())
            .map_err(|_| io::Error::new(io::ErrorKind::OutOfMemory, "Недостаточно памяти"))?;
        self.0.extend_from_slice(bytes);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

#[repr(u8)]
enum Segment<'a> {
    None = 0,
    Literal(&'a str) = 1,
    Counter(i64) = 2,
    Guid = 3,
    Random(usize) = 4,
}

fn parse_template(template: &str, rows: usize) -> Result<Vec<Segment<'_>>, TemplateError> {
    if rows == 0 {
        return Err(TemplateError::NoRows);
    }
    let mut segments = Vec::new();
    let mut open = None;
    let mut literal_start = 0;
    for (offset, brace) in template.match_indices(['{', '}']) {
        match brace {
            "{" => open = Some(offset),
            "}" => {
                let Some(open_offset) = open.take() else {
                    continue;
                };
                let placeholder = &template[open_offset + 1..offset];
                let segment = if let Some(start) = placeholder.strip_prefix("int_from_") {
                    let start = start
                        .parse::<i64>()
                        .map_err(|_| TemplateError::InvalidInteger)?;
                    let last_row =
                        i64::try_from(rows - 1).map_err(|_| TemplateError::IntegerOverflow)?;
                    start
                        .checked_add(last_row)
                        .ok_or(TemplateError::IntegerOverflow)?;
                    Segment::Counter(start)
                } else if placeholder == "guid" {
                    Segment::Guid
                } else if placeholder == "rand_string" {
                    Segment::Random(DEFAULT_RANDOM_STRING_LENGTH)
                } else if let Some(length) = placeholder.strip_prefix("rand_string_") {
                    let length = length
                        .parse::<usize>()
                        .map_err(|_| TemplateError::InvalidRandomLength)?;
                    Segment::Random(length)
                } else {
                    Segment::None
                };
                if !matches!(segment, Segment::None) {
                    segments
                        .try_reserve(2)
                        .map_err(|_| TemplateError::Allocation)?;
                    segments.push(Segment::Literal(&template[literal_start..open_offset]));
                    segments.push(segment);
                    literal_start = offset + 1;
                }
            }
            _ => unreachable!("brace pattern returns only opening and closing braces"),
        }
    }
    segments
        .try_reserve(1)
        .map_err(|_| TemplateError::Allocation)?;
    segments.push(Segment::Literal(&template[literal_start..]));
    Ok(segments)
}

#[cfg(test)]
#[path = "../tests/unit/template.rs"]
mod tests;
