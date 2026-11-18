use std::path::PathBuf;

use super::{
    LoadedDocument, OpenMode, can_reuse_log_document, read_document, reopen_existing_document,
};
use crate::{files::FileTarget, session::Tab, text_format::TextEncoding};

fn closed_view(file: FileTarget) -> Tab {
    Tab {
        json_validation_disabled: false,
        id: 1,
        file: Some(file),
        format: crate::text_format::TextFormat::for_encoding(TextEncoding::Windows1251),
        selection: 2..6,
        reversed: true,
        scroll: [0., 200.],
        draft_revision: None,
        log: None,
        line_count: None,
        last_worked_ms: None,
    }
}

#[test]
fn replaces_clean_document_when_reopened_with_another_encoding() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("same.txt");
    std::fs::write(&path, "Привет").unwrap();

    let LoadedDocument::Text(utf8) = read_document(&path, OpenMode::NORMAL).unwrap() else {
        panic!("small text file must open in the editor");
    };
    let LoadedDocument::Text(windows_1251) = read_document(&path, OpenMode::WINDOWS_1251).unwrap()
    else {
        panic!("small text file must open in the editor");
    };

    assert!(utf8.target == windows_1251.target);
    assert_eq!(utf8.text, "Привет");
    assert_ne!(utf8.text, windows_1251.text);
    assert!(windows_1251.format.encoding == TextEncoding::Windows1251);
    assert!(utf8.format.encoding == TextEncoding::Utf8);

    let closed = closed_view(windows_1251.target.clone());
    let reopen = reopen_existing_document(
        false,
        false,
        Some((&utf8.target, utf8.format.encoding)),
        (&windows_1251.target, windows_1251.format.encoding),
        Some(&closed),
        None,
    );
    assert!(reopen.replace_text);
    let restore = reopen.restore.expect("closed tab view must be restored");
    assert_eq!(restore.selection, 2..6);
    assert!(restore.reversed);
    assert_eq!(restore.scroll, [0., 200.]);
}

#[test]
fn keeps_dirty_document_when_reopened_with_another_encoding() {
    let target = FileTarget {
        path: PathBuf::from("same.txt"),
        version: None,
    };

    let closed = closed_view(target.clone());
    let reopen = reopen_existing_document(
        true,
        false,
        Some((&target, TextEncoding::Windows1251)),
        (&target, TextEncoding::Utf8),
        Some(&closed),
        None,
    );
    assert!(!reopen.replace_text);
    assert!(reopen.restore.is_none());
}

#[test]
fn restores_pending_clean_document_view_when_opened_from_picker() {
    let target = FileTarget {
        path: PathBuf::from("same.json"),
        version: None,
    };
    let mut pending = closed_view(target.clone());
    pending.json_validation_disabled = true;

    let reopen = reopen_existing_document(
        false,
        true,
        Some((&target, TextEncoding::Utf8)),
        (&target, TextEncoding::Utf8),
        None,
        Some(&pending),
    );

    assert!(reopen.replace_text);
    let restore = reopen
        .restore
        .expect("pending clean document view must be preserved");
    assert_eq!(restore.selection, 2..6);
    assert!(restore.reversed);
    assert_eq!(restore.scroll, [0., 200.]);
    assert!(restore.json_validation_disabled);
}

#[test]
fn reuses_same_disk_log_but_never_a_draft_preview() {
    let path = PathBuf::from("large.log");

    assert!(can_reuse_log_document(
        Some(&path),
        &path,
        TextEncoding::Utf8Bom,
        false,
        false,
    ));
    assert!(can_reuse_log_document(
        Some(&path),
        &path,
        TextEncoding::Utf16Le,
        false,
        false,
    ));
    assert!(!can_reuse_log_document(
        Some(&path),
        &path,
        TextEncoding::Utf8,
        false,
        true,
    ));
    assert!(!can_reuse_log_document(
        Some(&path),
        &path,
        TextEncoding::Windows1251,
        false,
        false,
    ));
}
