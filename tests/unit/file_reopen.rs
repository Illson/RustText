use std::path::PathBuf;

use super::{LoadedDocument, OpenMode, read_document, should_replace_opened_document};
use crate::{files::FileTarget, text_format::TextEncoding};

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

    assert!(should_replace_opened_document(
        false,
        false,
        Some(&windows_1251.target),
        &utf8.target,
        windows_1251.format.encoding,
        utf8.format.encoding,
    ));
}

#[test]
fn keeps_dirty_document_when_reopened_with_another_encoding() {
    let target = FileTarget {
        path: PathBuf::from("same.txt"),
        version: None,
    };

    assert!(!should_replace_opened_document(
        true,
        false,
        Some(&target),
        &target,
        TextEncoding::Windows1251,
        TextEncoding::Utf8,
    ));
}
