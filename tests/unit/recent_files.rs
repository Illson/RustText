use super::*;

#[test]
fn merges_loaded_history_without_replacing_newer_in_memory_positions() {
    let path = PathBuf::from("current.txt");
    let missing = RecentFile::new(PathBuf::from("other.txt"), TextEncoding::Utf8);
    let current = RecentFile {
        position: Some(RecentPosition {
            selection: 30..30,
            ..RecentPosition::default()
        }),
        ..RecentFile::new(path.clone(), TextEncoding::Utf8)
    };
    let mut recent = RecentFiles {
        entries: vec![current.clone()],
        positions: vec![(path.clone(), current.position.clone().unwrap())],
        ..RecentFiles::default()
    };
    recent.merge_loaded(RecentFiles {
        entries: vec![RecentFile::new(path, TextEncoding::Utf8), missing.clone()],
        ..RecentFiles::default()
    });
    assert_eq!(recent.entries().len(), 2);
    assert_eq!(recent.entries()[0].path, current.path);
    assert_eq!(
        recent.entries()[0].position.as_ref().unwrap().selection,
        30..30
    );
    assert_eq!(recent.entries()[1].path, missing.path);
}

#[test]
fn merges_incoming_positions_and_flags_for_clean_existing_entries() {
    let path = PathBuf::from("existing.txt");
    let mut recent = RecentFiles {
        entries: vec![RecentFile::new(path.clone(), TextEncoding::Utf8)],
        ..RecentFiles::default()
    };
    recent.merge_loaded(RecentFiles {
        entries: vec![RecentFile {
            position: Some(RecentPosition {
                json_validation_disabled: true,
                selection: 42..45,
                ..RecentPosition::default()
            }),
            ..RecentFile::new(path, TextEncoding::Windows1251)
        }],
        ..RecentFiles::default()
    });
    assert_eq!(recent.entries().len(), 1);
    assert!(recent.entries()[0].windows_1251);
    let position = recent.entries()[0].position.as_ref().unwrap();
    assert_eq!(position.selection, 42..45);
    assert!(position.json_validation_disabled);
}

#[test]
fn batches_positions_without_reordering_or_losing_new_paths() {
    let dir = tempfile::tempdir().unwrap();
    let mut recent = RecentFiles {
        path: Some(dir.path().join("recent-files.json")),
        ..RecentFiles::default()
    };
    let first = dir.path().join("first.txt");
    let second = dir.path().join("second.txt");
    recent
        .record([
            RecentFile::new(second.clone(), TextEncoding::Utf8),
            RecentFile::new(first.clone(), TextEncoding::Utf8),
        ])
        .unwrap();
    let unknown = dir.path().join("unknown.txt");
    recent
        .record_positions(vec![
            RecentFile {
                position: Some(RecentPosition {
                    selection: 10..20,
                    ..RecentPosition::default()
                }),
                ..RecentFile::new(second.clone(), TextEncoding::Utf8)
            },
            RecentFile {
                position: Some(RecentPosition {
                    selection: 25..25,
                    ..RecentPosition::default()
                }),
                ..RecentFile::new(unknown.clone(), TextEncoding::Utf8)
            },
        ])
        .unwrap();
    recent.reload().unwrap();
    assert_eq!(
        recent
            .entries()
            .iter()
            .map(|file| &file.path)
            .collect::<Vec<_>>(),
        [&unknown, &first, &second]
    );
    assert_eq!(
        recent.entries()[0].position.as_ref().unwrap().selection,
        25..25
    );
    assert_eq!(
        recent.entries()[2].position.as_ref().unwrap().selection,
        10..20
    );
}

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
fn merges_updates_from_two_windows_without_truncating_history() {
    let dir = tempfile::tempdir().unwrap();
    let mut first_window = RecentFiles {
        path: Some(dir.path().join("recent-files.json")),
        ..RecentFiles::default()
    };
    let mut second_window = first_window.clone();
    let files = (0..40)
        .map(|index| RecentFile::new(dir.path().join(format!("{index}.txt")), TextEncoding::Utf8));
    first_window.record(files).unwrap();
    let new_file = dir.path().join("new.txt");
    second_window
        .record([RecentFile::new(new_file.clone(), TextEncoding::Utf8)])
        .unwrap();

    first_window.reload().unwrap();
    assert_eq!(first_window.entries().len(), 41);
    assert_eq!(first_window.entries()[0].path, new_file);
    assert_eq!(first_window.entries()[40].path, dir.path().join("0.txt"));
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

#[test]
fn reads_legacy_array_and_roundtrips_positions_without_reordering() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("recent-files.json");
    let first = dir.path().join("first.txt");
    let second = dir.path().join("second.txt");
    fs::write(
        &path,
        serde_json::to_vec(&serde_json::json!([
            {"path": first, "windows_1251": false},
            {"path": second, "windows_1251": true, "view_as_log": true}
        ]))
        .unwrap(),
    )
    .unwrap();
    let mut recent = RecentFiles {
        path: Some(path),
        ..RecentFiles::default()
    };
    recent.reload().unwrap();
    assert!(recent.entries()[0].position.is_none());
    recent
        .record_positions(vec![RecentFile {
            position: Some(RecentPosition {
                json_validation_disabled: true,
                selection: 12..25,
                reversed: true,
                scroll: [-10.0, -125.5],
                log: Some(LogBookmark {
                    offset: 12345,
                    hidden: 100,
                    ..LogBookmark::default()
                }),
            }),
            ..RecentFile::new(second.clone(), TextEncoding::Utf8)
        }])
        .unwrap();
    recent.reload().unwrap();
    assert_eq!(recent.entries()[0].path, first);
    let position = recent.entries()[1].position.as_ref().unwrap();
    assert!(position.json_validation_disabled);
    assert_eq!(position.selection, 12..25);
    assert!(position.reversed);
    assert_eq!(position.scroll, [-10.0, -125.5]);
    assert_eq!(position.log.as_ref().unwrap().offset, 12345);
    recent
        .record([RecentFile::new(second.clone(), TextEncoding::Utf8)])
        .unwrap();
    assert_eq!(
        recent.entries()[0].position.as_ref().unwrap().selection,
        12..25
    );
}

#[test]
fn position_updates_merge_with_another_windows_new_entries() {
    let dir = tempfile::tempdir().unwrap();
    let mut first = RecentFiles {
        path: Some(dir.path().join("recent-files.json")),
        ..RecentFiles::default()
    };
    let old = dir.path().join("old.txt");
    first
        .record([RecentFile::new(old.clone(), TextEncoding::Utf8)])
        .unwrap();
    let mut second = first.clone();
    let new = dir.path().join("new.txt");
    second
        .record([RecentFile::new(new.clone(), TextEncoding::Utf8)])
        .unwrap();
    first
        .record_positions(vec![RecentFile {
            position: Some(RecentPosition {
                selection: 8..8,
                ..RecentPosition::default()
            }),
            ..RecentFile::new(old.clone(), TextEncoding::Utf8)
        }])
        .unwrap();
    assert_eq!(first.entries()[0].path, new);
    assert_eq!(
        first.entries()[1].position.as_ref().unwrap().selection,
        8..8
    );
}

#[test]
fn merge_loaded_updates_flags_when_path_only_has_pending_position() {
    let path = PathBuf::from("log.txt");
    let current = RecentFile {
        position: Some(RecentPosition {
            selection: 40..40,
            ..RecentPosition::default()
        }),
        ..RecentFile::new(path.clone(), TextEncoding::Utf8)
    };
    let mut recent = RecentFiles {
        entries: vec![current],
        positions: vec![(
            path.clone(),
            RecentPosition {
                selection: 40..40,
                ..RecentPosition::default()
            },
        )],
        ..RecentFiles::default()
    };
    let mut loaded = RecentFile::new(path, TextEncoding::Windows1251);
    loaded.view_as_log = true;
    loaded.position = Some(RecentPosition {
        selection: 5..5,
        ..RecentPosition::default()
    });
    recent.merge_loaded(RecentFiles {
        entries: vec![loaded],
        ..RecentFiles::default()
    });
    assert!(recent.entries()[0].windows_1251);
    assert!(recent.entries()[0].view_as_log);
    assert_eq!(
        recent.entries()[0].position.as_ref().unwrap().selection,
        40..40
    );
}

#[test]
fn concurrent_detached_record_and_position_flush_do_not_clobber_each_other() {
    let dir = tempfile::tempdir().unwrap();
    let mut workspace_recent = RecentFiles {
        path: Some(dir.path().join("recent-files.json")),
        ..RecentFiles::default()
    };
    let first = dir.path().join("first.txt");
    let second = dir.path().join("second.txt");
    workspace_recent
        .record([
            RecentFile::new(second.clone(), TextEncoding::Utf8),
            RecentFile::new(first.clone(), TextEncoding::Utf8),
        ])
        .unwrap();

    let mut bg_record = workspace_recent.detached();
    let recorded = RecentFile::new(second.clone(), TextEncoding::Windows1251);
    bg_record.record([recorded.clone()]).unwrap();

    let mut bg_positions = workspace_recent.detached();
    bg_positions
        .record_positions(vec![RecentFile {
            position: Some(RecentPosition {
                selection: 12..18,
                ..RecentPosition::default()
            }),
            ..RecentFile::new(first.clone(), TextEncoding::Utf8)
        }])
        .unwrap();

    // Simulate position flush finishing on UI thread before stale record completion arrives.
    workspace_recent.merge_loaded(bg_positions);
    workspace_recent.record_in_memory(&[recorded]);
    workspace_recent.merge_loaded(bg_record);

    assert_eq!(workspace_recent.entries()[0].path, second);
    assert!(workspace_recent.entries()[0].windows_1251);
    assert_eq!(workspace_recent.entries()[1].path, first);
    assert_eq!(
        workspace_recent.entries()[1]
            .position
            .as_ref()
            .unwrap()
            .selection,
        12..18
    );
}
