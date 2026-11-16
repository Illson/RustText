use super::*;
use crate::text_format::TextEncoding;
use std::{io::Write, sync::atomic::AtomicBool};

fn reader_with(
    contents: &[u8],
    encoding: Option<TextEncoding>,
) -> (tempfile::NamedTempFile, LogReader) {
    let mut file = tempfile::NamedTempFile::new().unwrap();
    file.write_all(contents).unwrap();
    file.flush().unwrap();
    let reader = LogReader::open(file.path(), encoding).unwrap();
    (file, reader)
}

fn timestamp(second: usize) -> String {
    format!(
        "2026-11-17 01:{:02}:{:02}.{:04}",
        second / 60,
        second % 60,
        second % 10_000
    )
}

#[test]
fn binary_search_finds_a_timestamp_among_variable_length_utf8_lines() {
    let mut contents = Vec::new();
    let mut expected = 0;
    let target_index = 713;
    let target = timestamp(target_index);
    for index in 0..2_000 {
        if index == target_index {
            expected = contents.len() as u64;
        }
        let line = format!("INFO {} {}", timestamp(index), "x".repeat(index % 71));
        contents.extend_from_slice(line.as_bytes());
        contents.extend_from_slice(if index % 2 == 0 { b"\r\n" } else { b"\n" });
    }
    let (_file, mut reader) = reader_with(&contents, Some(TextEncoding::Utf8));

    let result = search(&mut reader, 0, &target, &AtomicBool::new(false)).unwrap();
    assert_eq!(result.offset, Some(expected));
    assert!(!result.used_fallback);
    assert!(result.probes < 20);
}

#[test]
fn binary_search_handles_utf16_and_fractional_precision() {
    let records = [
        "2026-11-17 01:30:20.08 first\r\n",
        "2026-11-17 01:30:20.0850 second\r\n",
        "2026-11-17 01:30:20.090 third",
    ];
    let mut contents = Vec::new();
    let mut expected = 0;
    for (index, record) in records.iter().enumerate() {
        if index == 1 {
            expected = contents.len() as u64;
        }
        for unit in record.encode_utf16() {
            contents.extend_from_slice(&unit.to_le_bytes());
        }
    }
    let (_file, mut reader) = reader_with(&contents, Some(TextEncoding::Utf16Le));

    let result = search(
        &mut reader,
        0,
        "2026-11-17 01:30:20.0850",
        &AtomicBool::new(false),
    )
    .unwrap();
    assert_eq!(result.offset, Some(expected));
}

#[test]
fn inconsistent_timestamp_probes_fall_back_to_the_first_qualifying_line() {
    let seconds = [1, 2, 3, 12, 9, 5, 6, 10];
    let mut contents = Vec::new();
    let mut expected = 0;
    for (index, second) in seconds.into_iter().enumerate() {
        if index == 3 {
            expected = contents.len() as u64;
        }
        contents.extend_from_slice(format!("{} record\n", timestamp(second)).as_bytes());
    }
    let (_file, mut reader) = reader_with(&contents, Some(TextEncoding::Utf8));

    let result = search(&mut reader, 0, &timestamp(8), &AtomicBool::new(false)).unwrap();
    assert_eq!(result.offset, Some(expected));
    assert!(result.used_fallback);
}

#[test]
fn invalid_target_timestamp_has_a_specific_error() {
    let (_file, mut reader) = reader_with(b"2026-11-17 01:30:20 event\n", None);

    assert!(matches!(
        reader.seek_timestamp(0, "not a timestamp", &AtomicBool::new(false)),
        Err(LogError::InvalidTimestamp)
    ));
}
