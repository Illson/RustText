use super::*;

#[test]
fn increments_integer_placeholders_for_each_generated_row() {
    assert_eq!(
        generate("strokna {int_from_5}", 4).unwrap(),
        "strokna 5\nstrokna 6\nstrokna 7\nstrokna 8"
    );
}

#[test]
fn expands_multiple_placeholders_per_row() {
    assert_eq!(
        generate("{int_from_-2}:{int_from_5}:{int_from_-2}", 3).unwrap(),
        "-2:5:-2\n-1:6:-1\n0:7:0"
    );
}

#[test]
fn expands_placeholders_inside_json_braces() {
    let generated = generate(r#"{"id":"{guid}","n":{int_from_5}}"#, 2).unwrap();
    let values = generated
        .lines()
        .map(|line| {
            let value: serde_json::Value = serde_json::from_str(line).unwrap();
            assert!(
                Uuid::parse_str(value["id"].as_str().unwrap())
                    .unwrap()
                    .get_version_num()
                    == 4
            );
            value["n"].as_i64().unwrap()
        })
        .collect::<Vec<_>>();
    assert_eq!(values, [5, 6]);
}

#[test]
fn expands_guid_and_random_strings_with_requested_lengths() {
    let output = generate("{guid} {rand_string} {rand_string_4}", 2).unwrap();
    for row in output.lines() {
        let mut fields = row.split(' ');
        let guid = fields.next().unwrap();
        let default_random = fields.next().unwrap();
        let custom_random = fields.next().unwrap();
        assert!(Uuid::parse_str(guid).unwrap().get_version_num() == 4);
        assert_eq!(default_random.len(), DEFAULT_RANDOM_STRING_LENGTH);
        assert_eq!(custom_random.len(), 4);
        assert!(fields.next().is_none());
        assert!(
            default_random
                .bytes()
                .chain(custom_random.bytes())
                .all(|byte| byte.is_ascii_alphanumeric())
        );
    }
}

#[test]
fn repeats_multiline_templates_and_preserves_unknown_placeholders() {
    assert_eq!(
        generate("a {int_from_1}\nb {other}", 2).unwrap(),
        "a 1\nb {other}\na 2\nb {other}"
    );
}

#[test]
fn rejects_zero_rows_invalid_placeholders_and_counter_overflow() {
    assert!(matches!(generate("x", 0), Err(TemplateError::NoRows)));
    assert!(matches!(
        generate("{int_from_x}", 1),
        Err(TemplateError::InvalidInteger)
    ));
    assert!(matches!(
        generate("{rand_string_-1}", 1),
        Err(TemplateError::InvalidRandomLength)
    ));
    assert!(matches!(
        generate("{int_from_9223372036854775807}", 2),
        Err(TemplateError::IntegerOverflow)
    ));
}

#[derive(Default)]
struct CountingWriter {
    bytes: u64,
    newlines: usize,
    largest_chunk: usize,
    alphanumeric: bool,
}

impl io::Write for CountingWriter {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.bytes += bytes.len() as u64;
        self.newlines += bytes.iter().filter(|byte| **byte == b'\n').count();
        self.largest_chunk = self.largest_chunk.max(bytes.len());
        self.alphanumeric &= bytes.iter().all(u8::is_ascii_alphanumeric);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

#[test]
fn streams_more_than_one_million_rows() {
    let mut output = CountingWriter::default();
    let rows = 1_000_001;
    let state = generate_to_writer("x", rows, &mut output, |_| true).unwrap();
    assert_eq!(state.rows, rows);
    assert_eq!(state.bytes, 2 * rows as u64 - 1);
    assert_eq!(output.bytes, state.bytes);
    assert_eq!(output.newlines, rows - 1);
}

#[test]
fn streams_more_than_eight_mebibytes_in_bounded_random_chunks() {
    let mut output = CountingWriter {
        alphanumeric: true,
        ..Default::default()
    };
    let state = generate_to_writer("{rand_string_8388609}", 1, &mut output, |_| true).unwrap();
    assert_eq!(state.bytes, 8 * 1024 * 1024 + 1);
    assert_eq!(output.bytes, state.bytes);
    assert_eq!(state.rows, 1);
    assert_eq!(output.newlines, 0);
    assert!(output.alphanumeric);
    assert_eq!(output.largest_chunk, CHUNK_BYTES);
}

#[test]
fn cancels_a_machine_sized_random_placeholder_after_one_chunk() {
    let mut output = CountingWriter::default();
    let template = format!("{{rand_string_{}}}", usize::MAX);
    let result = generate_to_writer(&template, 1, &mut output, |state| state.bytes == 0);
    assert!(matches!(result, Err(TemplateError::Cancelled)));
    assert_eq!(output.bytes, CHUNK_BYTES as u64);
    assert_eq!(output.largest_chunk, CHUNK_BYTES);
}

#[test]
fn cancellation_before_start_writes_nothing() {
    let mut output = CountingWriter::default();
    let result = generate_to_writer("x", usize::MAX, &mut output, |_| false);
    assert!(matches!(result, Err(TemplateError::Cancelled)));
    assert_eq!(output.bytes, 0);
}

#[test]
fn checks_counter_overflow_before_writing_any_output() {
    let mut output = CountingWriter::default();
    let result = generate_to_writer(
        "prefix {int_from_9223372036854775807}",
        2,
        &mut output,
        |_| true,
    );
    assert!(matches!(result, Err(TemplateError::IntegerOverflow)));
    assert_eq!(output.bytes, 0);
}

#[test]
fn reports_writer_and_flush_failures() {
    struct FailingWriter {
        fail_flush: bool,
    }
    impl io::Write for FailingWriter {
        fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
            if self.fail_flush {
                Ok(bytes.len())
            } else {
                Err(io::Error::new(io::ErrorKind::StorageFull, "disk full"))
            }
        }
        fn flush(&mut self) -> io::Result<()> {
            Err(io::Error::new(io::ErrorKind::BrokenPipe, "flush failed"))
        }
    }
    for (fail_flush, expected) in [
        (false, io::ErrorKind::StorageFull),
        (true, io::ErrorKind::BrokenPipe),
    ] {
        let result = generate_to_writer("x", 1, &mut FailingWriter { fail_flush }, |_| true);
        assert!(matches!(result, Err(TemplateError::Io(error)) if error.kind() == expected));
    }
}

#[test]
fn streams_literals_and_counters_with_existing_format_semantics() {
    let mut output = Vec::new();
    let state = generate_to_writer(
        "{int_from_-2}:{unknown}\n{rand_string_0}",
        3,
        &mut output,
        |_| true,
    )
    .unwrap();
    assert_eq!(output, b"-2:{unknown}\n\n-1:{unknown}\n\n0:{unknown}\n");
    assert_eq!(state.rows, 3);
    assert_eq!(state.bytes, output.len() as u64);
}

#[test]
fn large_literal_is_split_into_bounded_writes() {
    let literal = "я".repeat(CHUNK_BYTES + 1);
    let mut output = CountingWriter::default();
    generate_to_writer(&literal, 1, &mut output, |_| true).unwrap();
    assert_eq!(output.bytes, literal.len() as u64);
    assert_eq!(output.largest_chunk, CHUNK_BYTES);
}

#[test]
fn chooses_file_output_without_rejecting_large_jobs() {
    assert!(output_upper_bound("x", 3).unwrap() < NEW_TAB_BYTES);
    assert!(output_upper_bound("{rand_string_8388609}", 1).unwrap() > NEW_TAB_BYTES);
    assert!(output_upper_bound("x", usize::MAX).unwrap() > NEW_TAB_BYTES);
}

#[test]
fn cancellation_preserves_existing_destination_and_removes_temporary_file() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("existing.txt");
    std::fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let result = generate_file("{rand_string_100000}", 10, target, |state| {
        state.bytes < CHUNK_BYTES as u64
    });
    assert!(matches!(result, Err(TemplateError::Cancelled)));
    assert_eq!(std::fs::read(&path).unwrap(), b"original");
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 1);
}

#[test]
fn cancellation_does_not_publish_new_destination() {
    let directory = tempfile::tempdir().unwrap();
    let target = FileTarget::from_picker(directory.path().join("new.txt")).unwrap();
    let result = generate_file("x", 100, target, |state| state.rows < 2);
    assert!(matches!(result, Err(TemplateError::Cancelled)));
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 0);
}

#[test]
fn publishes_complete_file_for_new_and_existing_destination() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    for _ in 0..2 {
        let target = FileTarget::from_picker(path.clone()).unwrap();
        let state = generate_file("{int_from_5}", 3, target, |_| true).unwrap();
        assert_eq!(std::fs::read(&path).unwrap(), b"5\n6\n7");
        assert_eq!(state.bytes, 5);
        assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 1);
    }
}

#[test]
fn refuses_to_replace_destination_changed_during_generation() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    std::fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let mut changed = false;
    let result = generate_file("x", 3, target, |state| {
        if state.rows == 1 && !changed {
            std::fs::write(&path, b"changed externally").unwrap();
            changed = true;
        }
        true
    });
    assert!(matches!(result, Err(TemplateError::Io(_))));
    assert_eq!(std::fs::read(&path).unwrap(), b"changed externally");
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 1);
}

#[test]
fn stale_target_error_preserves_destination_and_removes_temporary_file() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    std::fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    std::fs::write(&path, b"changed before start").unwrap();
    let result = generate_file("x", 3, target, |_| true);
    assert!(matches!(result, Err(TemplateError::Io(_))));
    assert_eq!(std::fs::read(&path).unwrap(), b"changed before start");
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 1);
}

#[test]
fn cancellation_after_last_row_still_preserves_destination() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    std::fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let mut completed_reports = 0;
    let result = generate_file("x", 1, target, |state| {
        if state.rows == 1 {
            completed_reports += 1;
        }
        completed_reports < 2
    });
    assert!(matches!(result, Err(TemplateError::Cancelled)));
    assert_eq!(std::fs::read(&path).unwrap(), b"original");
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 1);
}

#[test]
fn validates_file_template_before_creating_temporary_output() {
    let directory = tempfile::tempdir().unwrap();
    let target = FileTarget::from_picker(directory.path().join("result.txt")).unwrap();
    let result = generate_file("{int_from_x}", 1, target, |_| true);
    assert!(matches!(result, Err(TemplateError::InvalidInteger)));
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 0);
}

#[test]
fn cancellation_after_background_text_completion_discards_the_result() {
    use std::sync::atomic::{AtomicBool, Ordering};

    let cancelled = AtomicBool::new(false);
    let output = generate_text("{int_from_5}", 3, |_| !cancelled.load(Ordering::Relaxed)).unwrap();
    assert_eq!(output, "5\n6\n7");
    cancelled.store(true, Ordering::Relaxed);
    let result = completed_text(output, cancelled.load(Ordering::Relaxed));
    assert!(matches!(result, Err(TemplateError::Cancelled)));
}

#[test]
fn completed_text_without_cancellation_is_available_to_open() {
    let output = generate_text("{int_from_5}", 3, |_| true).unwrap();
    assert_eq!(completed_text(output, false).unwrap(), "5\n6\n7");
}

#[cfg(windows)]
#[test]
fn generator_fails_safely_when_a_writer_opens_before_publication() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    std::fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let mut writer = None;
    let result = generate_file("generated output", 1, target, |state| {
        if state.rows == 1 && writer.is_none() {
            writer = Some(std::fs::OpenOptions::new().write(true).open(&path).unwrap());
        }
        true
    });
    assert!(matches!(result, Err(TemplateError::Io(error)) if error.raw_os_error() == Some(32)));
    drop(writer);
    assert_eq!(std::fs::read(&path).unwrap(), b"original");
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 1);
}
