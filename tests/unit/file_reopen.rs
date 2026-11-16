use std::path::PathBuf;

use super::{LoadedDocument, OpenMode, read_document, reopen_existing_document};
use crate::{files::FileTarget, session::Tab, text_format::TextEncoding};

fn closed_view(file: FileTarget) -> Tab {
    Tab {
        id: 1,
        file: Some(file),
        format: crate::text_format::TextFormat::for_encoding(TextEncoding::Windows1251),
        selection: 2..6,
        reversed: true,
        scroll: [0., 200.],
        draft_revision: None,
        log: None,
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
        Some(&utf8.target),
        &windows_1251.target,
        utf8.format.encoding,
        windows_1251.format.encoding,
        Some(&closed),
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
        Some(&target),
        &target,
        TextEncoding::Windows1251,
        TextEncoding::Utf8,
        Some(&closed),
    );
    assert!(!reopen.replace_text);
    assert!(reopen.restore.is_none());
}
