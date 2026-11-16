use super::*;

#[test]
fn persists_order_and_encoding_and_moves_reopened_files_to_front() {
    let dir = tempfile::tempdir().unwrap();
    let mut recent = RecentFiles {
        path: Some(dir.path().join("recent-files.json")),
        ..RecentFiles::default()
    };
    let first = dir.path().join("Журнал.txt");
    let second = dir.path().join("Черновик.txt");
    recent
        .record([
            RecentFile::new(first.clone(), TextEncoding::Windows1251),
            RecentFile::new(second.clone(), TextEncoding::Utf8),
            RecentFile::new(first.clone(), TextEncoding::Windows1251),
        ])
        .unwrap();

    recent = RecentFiles {
        path: recent.path,
        ..RecentFiles::default()
    };
    recent.reload().unwrap();
    let entries = recent.entries();
    assert_eq!(entries.len(), 2);
    assert_eq!(entries[0].path, first);
    assert!(entries[0].windows_1251);
    assert_eq!(entries[1].path, second);
    assert!(!entries[1].windows_1251);
}

#[test]
fn repairs_truncated_history_when_a_file_is_opened() {
    let dir = tempfile::tempdir().unwrap();
    let history_path = dir.path().join("recent-files.json");
    fs::write(&history_path, "[").unwrap();
    let mut recent = RecentFiles {
        path: Some(history_path),
        ..RecentFiles::default()
    };
    assert_eq!(
        recent.reload().unwrap_err().kind(),
        io::ErrorKind::InvalidData
    );

    let file = dir.path().join("example.txt");
    recent
        .record([RecentFile::new(file.clone(), TextEncoding::Utf8)])
        .unwrap();
    recent.reload().unwrap();
    assert_eq!(recent.entries()[0].path, file);
}

#[test]
fn merges_updates_from_two_windows_and_keeps_only_the_most_recent_files() {
    let dir = tempfile::tempdir().unwrap();
    let mut first_window = RecentFiles {
        path: Some(dir.path().join("recent-files.json")),
        ..RecentFiles::default()
    };
    let mut second_window = first_window.clone();
    let files = (0..MAX_FILES)
        .map(|index| RecentFile::new(dir.path().join(format!("{index}.txt")), TextEncoding::Utf8));
    first_window.record(files).unwrap();
    let new_file = dir.path().join("new.txt");
    second_window
        .record([RecentFile::new(new_file.clone(), TextEncoding::Utf8)])
        .unwrap();

    first_window.reload().unwrap();
    assert_eq!(first_window.entries().len(), MAX_FILES);
    assert_eq!(first_window.entries()[0].path, new_file);
    assert_eq!(
        first_window.entries()[MAX_FILES - 1].path,
        dir.path().join("1.txt")
    );
}

#[test]
fn starts_empty_when_history_is_absent_and_stores_only_successfully_added_paths() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("nested").join("recent-files.json");
    let mut recent = RecentFiles {
        path: Some(path.clone()),
        ..RecentFiles::default()
    };
    recent.reload().unwrap();
    recent.record([]).unwrap();
    assert!(recent.entries().is_empty());
    assert!(!path.exists());

    let document = dir.path().join("example.txt");
    recent
        .record([RecentFile::new(document.clone(), TextEncoding::Utf8)])
        .unwrap();
    recent.reload().unwrap();
    assert_eq!(recent.entries().len(), 1);
    assert_eq!(recent.entries()[0].path, document);
}
