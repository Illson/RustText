use std::{error::Error, fmt};

use rand::distr::{Alphanumeric, SampleString};
use uuid::Uuid;

const MAX_GENERATED_BYTES: usize = 8 * 1024 * 1024;
const MAX_GENERATED_ROWS: usize = 1_000_000;
const DEFAULT_RANDOM_STRING_LENGTH: usize = 12;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum TemplateError {
    NoRows,
    TooManyRows,
    InvalidInteger,
    IntegerOverflow,
    InvalidRandomLength,
    OutputTooLarge,
}

impl fmt::Display for TemplateError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NoRows => formatter.write_str("Укажите количество строк больше нуля."),
            Self::TooManyRows => {
                formatter.write_str("За одну генерацию можно создать не больше 1 000 000 строк.")
            }
            Self::InvalidInteger => formatter.write_str(
                "В плейсхолдере {int_from_N} укажите целое начальное число, например {int_from_5}.",
            ),
            Self::IntegerOverflow => {
                formatter.write_str("Счётчик {int_from_N} вышел за пределы целого числа.")
            }
            Self::InvalidRandomLength => formatter
                .write_str("Длина {rand_string_N} должна быть неотрицательным целым числом."),
            Self::OutputTooLarge => formatter
                .write_str("Результат слишком велик: максимальный размер генерации — 8 МиБ."),
        }
    }
}

impl Error for TemplateError {}

/// Repeats a template block `rows` times, expanding its placeholders.
///
/// `{int_from_N}` adds the generated row index to N. `{guid}` creates a UUID
/// v4. `{rand_string}` creates 12 ASCII alphanumeric characters, while
/// `{rand_string_N}` selects a different length. Unrecognized placeholders are
/// copied literally.
pub(crate) fn generate(template: &str, rows: usize) -> Result<String, TemplateError> {
    if rows == 0 {
        return Err(TemplateError::NoRows);
    }
    if rows > MAX_GENERATED_ROWS {
        return Err(TemplateError::TooManyRows);
    }
    let segments = parse_template(template, rows)?;

    let mut output = String::new();
    for row in 0..rows {
        if row > 0 {
            push_bounded(&mut output, "\n")?;
        }
        let row = i64::try_from(row).map_err(|_| TemplateError::IntegerOverflow)?;
        for segment in &segments {
            match segment {
                Segment::Literal(text) => push_bounded(&mut output, text)?,
                Segment::Counter(start) => {
                    let value = start
                        .checked_add(row)
                        .ok_or(TemplateError::IntegerOverflow)?;
                    push_bounded(&mut output, &value.to_string())?;
                }
                Segment::Guid => push_bounded(&mut output, &Uuid::new_v4().to_string())?,
                Segment::Random(length) => {
                    let remaining = MAX_GENERATED_BYTES.saturating_sub(output.len());
                    if *length > remaining {
                        return Err(TemplateError::OutputTooLarge);
                    }
                    let random = Alphanumeric.sample_string(&mut rand::rng(), *length);
                    push_bounded(&mut output, &random)?;
                }
            }
        }
    }
    Ok(output)
}

enum Segment<'a> {
    Literal(&'a str),
    Counter(i64),
    Guid,
    Random(usize),
}

fn parse_template(template: &str, rows: usize) -> Result<Vec<Segment<'_>>, TemplateError> {
    let last_row = i64::try_from(rows - 1).map_err(|_| TemplateError::IntegerOverflow)?;
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
                    start
                        .checked_add(last_row)
                        .ok_or(TemplateError::IntegerOverflow)?;
                    Some(Segment::Counter(start))
                } else if placeholder == "guid" {
                    Some(Segment::Guid)
                } else if placeholder == "rand_string" {
                    Some(Segment::Random(DEFAULT_RANDOM_STRING_LENGTH))
                } else if let Some(length) = placeholder.strip_prefix("rand_string_") {
                    let length = length
                        .parse::<usize>()
                        .map_err(|_| TemplateError::InvalidRandomLength)?;
                    if length > MAX_GENERATED_BYTES {
                        return Err(TemplateError::OutputTooLarge);
                    }
                    Some(Segment::Random(length))
                } else {
                    None
                };
                if let Some(segment) = segment {
                    segments.push(Segment::Literal(&template[literal_start..open_offset]));
                    segments.push(segment);
                    literal_start = offset + 1;
                }
            }
            _ => unreachable!("brace pattern returns only opening and closing braces"),
        }
    }
    segments.push(Segment::Literal(&template[literal_start..]));
    Ok(segments)
}

fn push_bounded(output: &mut String, value: &str) -> Result<(), TemplateError> {
    if output
        .len()
        .checked_add(value.len())
        .is_none_or(|length| length > MAX_GENERATED_BYTES)
    {
        return Err(TemplateError::OutputTooLarge);
    }
    output.push_str(value);
    Ok(())
}

#[cfg(test)]
#[path = "../tests/unit/template.rs"]
mod tests;
