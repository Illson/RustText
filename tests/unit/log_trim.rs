use std::{fs, io, sync::atomic::AtomicBool};

use super::{
    BUFFER_SIZE, TrimIoHooks, hidden_start, prepare, prepare_with_progress, trim, trim_with_hooks,
};
use crate::text_format::TextEncoding;

fn temp_log(bytes: &[u8]) -> (tempfile::TempDir, std::path::PathBuf) {
    let dir = tempfile::tempdir().expect("temporary directory");
    let path = dir.path().join("service.log");
    fs::write(&path, bytes).expect("write fixture");
    (dir, path)
}

#[test]
fn prepare_advances_to_the_next_complete_lf_line() {
    let (_dir, path) = temp_log(b"line one\nline two\nline three\n");
    let plan = prepare(&path, 40, TextEncoding::Utf8).unwrap();
    assert_eq!(plan.boundary_offset, 18);
    assert_eq!(plan.removable_bytes, 18);
    assert_eq!(plan.removed_lines, 2);
}

#[test]
fn boundary_treats_crlf_as_one_ending() {
    let (_dir, path) = temp_log(b"aaaa\r\nbbbb\r\ncccc");
    assert_eq!(hidden_start(&path, 40, TextEncoding::Utf8).unwrap(), 6);
    assert_eq!(
        prepare(&path, 40, TextEncoding::Utf8)
            .unwrap()
            .removed_lines,
        1
    );
}

#[test]
fn boundary_supports_cr_only_files() {
    let (_dir, path) = temp_log(b"aaaa\rbbbb\rcccc");
    let plan = prepare(&path, 40, TextEncoding::Utf8).unwrap();
    assert_eq!(plan.boundary_offset, 5);
}

#[test]
fn utf8_bom_is_retained_after_trim() {
    let (_dir, path) = temp_log(b"\xEF\xBB\xBFfirst\nsecond\nthird\n");
    let plan = prepare(&path, 70, TextEncoding::Utf8Bom).unwrap();
    let outcome = trim(&path, &plan, &AtomicBool::new(false), |_, _| {}).unwrap();
    assert_eq!(fs::read(&path).unwrap(), b"\xEF\xBB\xBFthird\n");
    assert_eq!(plan.removable_bytes, 13);
    assert_eq!(outcome.removed_lines, 2);
    assert_eq!(
        fs::read(outcome.backup_path).unwrap(),
        b"\xEF\xBB\xBFfirst\nsecond\nthird\n"
    );
}

#[test]
fn utf16_le_bom_is_retained_and_lines_are_aligned() {
    let mut bytes = vec![0xFF, 0xFE];
    for unit in "one\r\ntwo\r\nthree\r\n".encode_utf16() {
        bytes.extend(unit.to_le_bytes());
    }
    let (_dir, path) = temp_log(&bytes);
    let plan = prepare(&path, 40, TextEncoding::Utf16Le).unwrap();
    assert_eq!(plan.boundary_offset % 2, 0);
    let outcome = trim(&path, &plan, &AtomicBool::new(false), |_, _| {}).unwrap();
    let mut expected = vec![0xFF, 0xFE];
    for unit in "three\r\n".encode_utf16() {
        expected.extend(unit.to_le_bytes());
    }
    assert_eq!(fs::read(&path).unwrap(), expected);
    assert_eq!(outcome.removed_lines, 2);
}

#[test]
fn utf16_be_bom_is_retained() {
    let mut bytes = vec![0xFE, 0xFF];
    for unit in "first\nsecond\n".encode_utf16() {
        bytes.extend(unit.to_be_bytes());
    }
    let (_dir, path) = temp_log(&bytes);
    let boundary = hidden_start(&path, 40, TextEncoding::Utf16Be).unwrap();
    assert!(boundary >= 2);
    assert_eq!(boundary % 2, 0);
}

#[test]
fn changed_file_is_rejected_without_mutation() {
    let (_dir, path) = temp_log(b"first\nsecond\nthird\n");
    let plan = prepare(&path, 40, TextEncoding::Utf8).unwrap();
    fs::write(&path, b"replacement content\n").unwrap();
    let before = fs::read(&path).unwrap();
    let result = trim(&path, &plan, &AtomicBool::new(false), |_, _| {});
    assert!(result.is_err());
    assert_eq!(fs::read(&path).unwrap(), before);
}

#[test]
fn pre_cancelled_operation_leaves_source_untouched() {
    let (_dir, path) = temp_log(b"first\nsecond\nthird\n");
    let before = fs::read(&path).unwrap();
    let plan = prepare(&path, 40, TextEncoding::Utf8).unwrap();
    let cancelled = AtomicBool::new(true);
    let result = trim(&path, &plan, &cancelled, |_, _| {});
    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::Interrupted);
    assert_eq!(fs::read(&path).unwrap(), before);
}

#[test]
fn cancellation_during_boundary_scan_returns_interrupted_without_mutation() {
    let bytes = vec![b'x'; 3 * super::BUFFER_SIZE + 7];
    let (_dir, path) = temp_log(&bytes);
    let cancelled = AtomicBool::new(false);
    let mut progress_calls = 0;
    let result = prepare_with_progress(
        &path,
        50,
        TextEncoding::Utf8,
        &cancelled,
        |scanned, total| {
            assert!(total > 0);
            assert!(scanned > 0);
            progress_calls += 1;
            cancelled.store(true, std::sync::atomic::Ordering::Relaxed);
        },
    );

    assert_eq!(result.err().unwrap().kind(), io::ErrorKind::Interrupted);
    assert_eq!(progress_calls, 1);
    assert_eq!(fs::metadata(&path).unwrap().len(), bytes.len() as u64);
    assert_eq!(fs::read(&path).unwrap(), bytes);
}

#[test]
fn invalid_percent_is_rejected() {
    let (_dir, path) = temp_log(b"one\ntwo\n");
    assert_eq!(
        prepare(&path, 0, TextEncoding::Utf8).err().unwrap().kind(),
        io::ErrorKind::InvalidInput
    );
    assert_eq!(
        hidden_start(&path, 101, TextEncoding::Utf8)
            .err()
            .unwrap()
            .kind(),
        io::ErrorKind::InvalidInput
    );
}

#[test]
fn backup_is_synced_before_progress_can_cancel_trim() {
    let (dir, path) = temp_log(b"first\nsecond\nthird\n");
    let plan = prepare(&path, 40, TextEncoding::Utf8).unwrap();
    let cancelled = AtomicBool::new(false);
    let result = trim(&path, &plan, &cancelled, |copied, _| {
        if copied > 0 {
            cancelled.store(true, std::sync::atomic::Ordering::Relaxed);
        }
    });
    let err = result.unwrap_err();
    assert_eq!(err.kind(), io::ErrorKind::Interrupted);
    // The full backup is kept when cancellation arrives after backup flush.
    // The error contains its path so the user can recover it.
    assert_eq!(fs::read(&path).unwrap(), b"first\nsecond\nthird\n");
    let backup = fs::read_dir(dir.path())
        .unwrap()
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .find(|candidate| {
            candidate
                .file_name()
                .is_some_and(|name| name.to_string_lossy().contains(".rusttext-"))
        })
        .expect("complete backup should remain available");
    assert!(err.to_string().contains(&backup.display().to_string()));
    assert_eq!(fs::read(backup).unwrap(), b"first\nsecond\nthird\n");
}

#[test]
fn disk_full_during_backup_keeps_source_unchanged_and_removes_partial_copy() {
    struct DiskFullAfterOneChunk;

    impl TrimIoHooks for DiskFullAfterOneChunk {
        fn before_backup_chunk(&mut self, copied: u64) -> io::Result<()> {
            if copied >= BUFFER_SIZE as u64 {
                // Windows ERROR_DISK_FULL.
                Err(io::Error::from_raw_os_error(112))
            } else {
                Ok(())
            }
        }
    }

    let bytes = vec![b'x'; BUFFER_SIZE + 32];
    let (dir, path) = temp_log(&bytes);
    let plan = prepare(&path, 50, TextEncoding::Utf8).unwrap();
    let result = trim_with_hooks(
        &path,
        &plan,
        &AtomicBool::new(false),
        |_, _| {},
        &mut DiskFullAfterOneChunk,
    );

    let error = result.unwrap_err();
    assert_eq!(error.raw_os_error(), Some(112));
    assert_eq!(fs::read(&path).unwrap(), bytes);
    let entries = fs::read_dir(dir.path()).unwrap().count();
    assert_eq!(
        entries, 1,
        "an incomplete backup must not be presented as recoverable"
    );
}

#[test]
fn backup_name_collision_is_never_removed_as_a_partial_copy() {
    struct BackupPathCollision {
        collision_path: Option<std::path::PathBuf>,
    }

    impl TrimIoHooks for BackupPathCollision {
        fn before_backup_create(&mut self, path: &std::path::Path) -> io::Result<()> {
            fs::write(path, b"file created by another process")?;
            self.collision_path = Some(path.to_path_buf());
            Ok(())
        }
    }

    let original = b"first line\nsecond line\nthird line\n";
    let (_dir, path) = temp_log(original);
    let plan = prepare(&path, 40, TextEncoding::Utf8).unwrap();
    let mut hooks = BackupPathCollision {
        collision_path: None,
    };
    let result = trim_with_hooks(&path, &plan, &AtomicBool::new(false), |_, _| {}, &mut hooks);

    assert_eq!(result.unwrap_err().kind(), io::ErrorKind::AlreadyExists);
    assert_eq!(fs::read(&path).unwrap(), original);
    let collision_path = hooks.collision_path.expect("collision path was recorded");
    assert_eq!(
        fs::read(collision_path).unwrap(),
        b"file created by another process"
    );
}

#[test]
fn interruption_after_partial_shift_restores_source_and_keeps_full_backup() {
    struct InterruptAfterShift;

    impl TrimIoHooks for InterruptAfterShift {
        fn after_shift_chunk(&mut self, shifted: u64) -> io::Result<()> {
            if shifted > 0 {
                Err(io::Error::new(
                    io::ErrorKind::Interrupted,
                    "injected interruption after a source chunk was rewritten",
                ))
            } else {
                Ok(())
            }
        }
    }

    let mut bytes = vec![b'x'; BUFFER_SIZE * 2 + 500_000];
    bytes[600_000] = b'\n';
    let (dir, path) = temp_log(&bytes);
    let plan = prepare(&path, 10, TextEncoding::Utf8).unwrap();
    assert!(plan.original_size - plan.boundary_offset > BUFFER_SIZE as u64);
    let result = trim_with_hooks(
        &path,
        &plan,
        &AtomicBool::new(false),
        |_, _| {},
        &mut InterruptAfterShift,
    );

    let error = result.unwrap_err();
    assert_eq!(error.kind(), io::ErrorKind::Interrupted);
    assert!(
        error
            .to_string()
            .contains("исходное содержимое восстановлено")
    );
    assert_eq!(fs::read(&path).unwrap(), bytes);

    let backup = fs::read_dir(dir.path())
        .unwrap()
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .find(|candidate| candidate != &path)
        .expect("complete recovery backup should remain");
    assert!(error.to_string().contains(&backup.display().to_string()));
    assert_eq!(fs::read(backup).unwrap(), bytes);
}

#[test]
fn trims_a_file_without_a_line_ending_to_eof() {
    let (_dir, path) = temp_log(b"0123456789");
    let plan = prepare(&path, 50, TextEncoding::Utf8).unwrap();
    assert_eq!(plan.boundary_offset, 10);
    let outcome = trim(&path, &plan, &AtomicBool::new(false), |_, _| {}).unwrap();
    assert!(fs::read(&path).unwrap().is_empty());
    assert_eq!(outcome.removed_lines, 1);
}

#[test]
fn trim_counts_complete_utf16_characters_with_an_incomplete_tail() {
    let (_dir, path) = temp_log(b"\xff\xfea\x00x");
    let plan = prepare(&path, 100, TextEncoding::Utf16Le).unwrap();
    assert_eq!(plan.boundary_offset, 5);
    assert_eq!(plan.removed_lines, 1);

    trim(&path, &plan, &AtomicBool::new(false), |_, _| {}).unwrap();
    assert_eq!(fs::read(path).unwrap(), b"\xff\xfe");
}
