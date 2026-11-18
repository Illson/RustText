use super::*;
use crate::text_format::TextEncoding;

fn capture(text: &str) -> Capture {
    Capture {
        state: State {
            layout: None,
            tabs: vec![Tab {
                json_validation_disabled: false,
                id: 1,
                file: None,
                format: TextFormat::default(),
                selection: 0..2,
                reversed: true,
                scroll: [0., -154.],
                draft_revision: Some(1),
                log: None,
                line_count: None,
                last_worked_ms: None,
            }],
            closed: Vec::new(),
            active: 0,
            window: WindowSize {
                width: 870.,
                height: 640.,
                maximized: false,
            },
            wrap: true,
            font_size: 16.,
        },
        drafts: vec![Some(Rope::from(text))],
        closed_drafts: Vec::new(),
    }
}

#[test]
fn roundtrips_an_empty_workspace_and_closed_draft() {
    let temporary = tempfile::tempdir().unwrap();
    let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
    let mut empty = capture("closed draft");
    empty.state.closed = std::mem::take(&mut empty.state.tabs);
    empty.closed_drafts = std::mem::take(&mut empty.drafts);
    empty.state.layout = Some(crate::layout::Layout::default());
    journal.commit(1, empty).unwrap();
    let (recovered, warning) = journal.restore();
    assert!(warning.is_none());
    let recovered = recovered.unwrap();
    assert!(recovered.state.tabs.is_empty());
    assert!(recovered.drafts.is_empty());
    assert_eq!(recovered.closed_drafts, [Some("closed draft".into())]);
    assert_eq!(recovered.state.layout.unwrap().active(), None);
}

#[test]
fn legacy_sessions_default_to_one_group_and_enabled_json_validation() {
    let mut legacy = serde_json::to_value(capture("draft").state).unwrap();
    legacy.as_object_mut().unwrap().remove("layout");
    legacy["tabs"][0]
        .as_object_mut()
        .unwrap()
        .remove("json_validation_disabled");
    legacy["tabs"][0]
        .as_object_mut()
        .unwrap()
        .remove("line_count");
    legacy["tabs"][0]
        .as_object_mut()
        .unwrap()
        .remove("last_worked_ms");
    let state: State = serde_json::from_value(legacy).unwrap();
    assert!(state.layout.is_none());
    assert!(!state.tabs[0].json_validation_disabled);
    assert_eq!(state.tabs[0].line_count, None);
    assert_eq!(state.tabs[0].last_worked_ms, None);
}

#[test]
fn roundtrips_disabled_validation_and_an_empty_focused_group() {
    let temporary = tempfile::tempdir().unwrap();
    let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
    let mut snapshot = capture("draft");
    snapshot.state.tabs[0].json_validation_disabled = true;
    let mut layout = crate::layout::Layout::new(vec![1], Some(1));
    let empty_group = layout.split(1, crate::layout::Axis::Horizontal).unwrap();
    layout.focus(empty_group);
    snapshot.state.layout = Some(layout);
    journal.commit(1, snapshot).unwrap();
    let (recovered, warning) = journal.restore();
    assert!(warning.is_none());
    let state = recovered.unwrap().state;
    assert!(state.tabs[0].json_validation_disabled);
    let mut layout = state.layout.unwrap();
    layout.remap(&std::collections::HashMap::from([(1, 42)]));
    layout.normalize(&[42]);
    assert_eq!(layout.active(), None);
    assert_eq!(layout.focused, empty_group);
    assert_eq!(layout.groups().len(), 2);
}

#[test]
fn restores_previous_commit_if_the_latest_draft_or_manifest_is_corrupt() {
    for damaged_file in ["draft-0.txt", "manifest.json"] {
        let temporary = tempfile::tempdir().unwrap();
        let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
        journal.commit(1, capture("ёж")).unwrap();
        journal.commit(2, capture("новый")).unwrap();
        fs::write(journal.record_path(2).join(damaged_file), "corrupt").unwrap();
        fs::create_dir(temporary.path().join(".pending-incomplete")).unwrap();
        drop(journal);
        let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
        let (recovered, warning) = journal.restore();
        let recovered = recovered.unwrap();
        assert!(warning.is_some());
        assert_eq!(recovered.drafts, [Some("ёж".into())]);
        assert_eq!(recovered.state.tabs[0].selection, 0..2);
        assert!(recovered.state.tabs[0].reversed);
        assert_eq!(recovered.state.tabs[0].scroll, [0., -154.]);
        assert_eq!(recovered.state.window.width, 870.);
    }
}

#[test]
fn a_delayed_periodic_commit_cannot_overwrite_a_newer_shutdown_commit() {
    let temporary = tempfile::tempdir().unwrap();
    let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
    journal.commit(2, capture("при закрытии")).unwrap();
    journal.commit(1, capture("устарело")).unwrap();
    let (recovered, warning) = journal.restore();
    assert!(warning.is_none());
    assert_eq!(recovered.unwrap().drafts, [Some("при закрытии".into())]);
}

#[test]
fn retains_two_latest_commits_and_refuses_concurrent_writers() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    let mut journal = Journal::open(directory.clone()).unwrap();
    let error = Journal::open(directory.clone())
        .err()
        .expect("another writer must not acquire the journal");
    assert_eq!(error.kind(), io::ErrorKind::WouldBlock);
    for (revision, draft) in [(1, "first"), (2, "second"), (3, "third")] {
        journal.commit(revision, capture(draft)).unwrap();
    }
    assert_eq!(records(&directory).unwrap(), vec![3, 2]);
    drop(journal);

    let mut reopened = Journal::open(directory).unwrap();
    let (state, warning) = reopened.restore();
    assert!(warning.is_none());
    assert_eq!(state.unwrap().drafts, [Some("third".into())]);
}

#[test]
fn restores_previous_checkpoint_after_corrupting_an_unchanged_draft() {
    let capture_with_closed_draft = |cursor| {
        let mut snapshot = capture("open draft");
        let mut closed_tab = snapshot.state.tabs[0].clone();
        closed_tab.id = 2;
        snapshot.state.closed.push(closed_tab);
        snapshot
            .closed_drafts
            .push(Some(Rope::from("closed draft")));
        snapshot.state.tabs[0].selection = cursor..cursor;
        snapshot
    };

    for damaged_file in ["draft-0.txt", "closed-draft-0.txt"] {
        let temporary = tempfile::tempdir().unwrap();
        let directory = temporary.path().to_path_buf();
        let mut journal = Journal::open(directory.clone()).unwrap();
        journal.commit(1, capture_with_closed_draft(1)).unwrap();
        journal.commit(2, capture_with_closed_draft(2)).unwrap();
        let damaged_path = journal.record_path(2).join(damaged_file);
        let mut damaged = fs::read(&damaged_path).unwrap();
        damaged[0] = b'!';
        fs::write(damaged_path, damaged).unwrap();
        drop(journal);

        let mut journal = Journal::open(directory).unwrap();
        let (recovered, warning) = journal.restore();
        let recovered = recovered.expect("the earlier checkpoint must survive in-place corruption");
        assert!(warning.is_some());
        assert_eq!(recovered.state.tabs[0].selection, 1..1);
        assert_eq!(recovered.drafts, [Some("open draft".into())]);
        assert_eq!(recovered.closed_drafts, [Some("closed draft".into())]);
    }
}

#[test]
fn corrupted_previous_draft_is_rewritten_from_the_current_capture() {
    let temporary = tempfile::tempdir().unwrap();
    let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
    journal.commit(1, capture("recoverable text")).unwrap();
    let draft_path = journal.record_path(1).join("draft-0.txt");
    let corrupted = "corrupted draft!";
    assert_eq!(corrupted.len(), "recoverable text".len());
    fs::write(&draft_path, corrupted).unwrap();
    let mut next = capture("recoverable text");
    next.state.tabs[0].selection = 5..5;
    journal.commit(2, next).unwrap();

    let (recovered, warning) = journal.restore();
    assert!(warning.is_none());
    assert_eq!(recovered.unwrap().drafts, [Some("recoverable text".into())]);
}

#[test]
fn restores_checkpoint_with_legacy_draft_timestamp() {
    let temporary = tempfile::tempdir().unwrap();
    let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
    journal.commit(1, capture("previous format")).unwrap();
    let path = journal.record_path(1).join("manifest.json");
    let mut manifest: serde_json::Value =
        serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
    manifest["drafts"][0]["modified_unix_nanos"] = 1_700_000_000_000_000_000_u64.into();
    fs::write(path, serde_json::to_vec(&manifest).unwrap()).unwrap();

    let (recovered, warning) = journal.restore();
    assert!(warning.is_none());
    assert_eq!(recovered.unwrap().drafts, [Some("previous format".into())]);
}

#[test]
fn named_drafts_do_not_change_the_underlying_file() {
    let temporary = tempfile::tempdir().unwrap();
    let document = temporary.path().join("notes.txt");
    fs::write(&document, "saved copy").unwrap();
    let mut draft = capture("unsaved text");
    draft.state.tabs[0].file = Some(FileTarget::from_picker(document.clone()).unwrap());
    let directory = temporary.path().join("journal");
    let mut journal = Journal::open(directory).unwrap();
    journal.commit(1, draft).unwrap();
    let (recovered, warning) = journal.restore();
    assert!(warning.is_none());
    let recovered = recovered.unwrap();
    assert_eq!(recovered.drafts[0].as_deref(), Some("unsaved text"));
    assert_eq!(
        recovered.state.tabs[0].file.as_ref().unwrap().path,
        document.canonicalize().unwrap()
    );
    assert_eq!(fs::read_to_string(document).unwrap(), "saved copy");
}

#[test]
fn a_log_bookmark_round_trips_without_storing_file_contents() {
    use crate::log_file::observe;

    let temporary = tempfile::tempdir().unwrap();
    let log_path = temporary.path().join("view-only.log");
    fs::write(&log_path, "first\nsecond\n").unwrap();
    let (observation, _) = observe(&log_path, None).unwrap();
    let mut bookmark = LogBookmark {
        offset: 6,
        hidden: 6,
        auto_refresh: false,
        follow_end: true,
        ..LogBookmark::default()
    };
    bookmark.record_observation(&observation);
    let mut state = capture("");
    state.state.tabs[0].file = Some(FileTarget::from_picker(log_path).unwrap());
    state.state.tabs[0].draft_revision = None;
    state.state.tabs[0].log = Some(bookmark.clone());
    state.drafts[0] = None;
    let directory = temporary.path().join("journal");
    let mut journal = Journal::open(directory).unwrap();

    journal.commit(1, state).unwrap();

    let record = fs::read_dir(journal.record_path(1))
        .unwrap()
        .map(|entry| entry.unwrap().file_name())
        .collect::<Vec<_>>();
    assert_eq!(record, ["manifest.json"]);
    let manifest = fs::read(journal.record_path(1).join("manifest.json")).unwrap();
    assert!(!String::from_utf8_lossy(&manifest).contains(r"first\nsecond\n"));
    let manifest: serde_json::Value = serde_json::from_slice(&manifest).unwrap();
    let saved_fingerprint = &manifest["state"]["tabs"][0]["log"]["fingerprint"];
    assert_eq!(
        saved_fingerprint,
        &serde_json::to_value(bookmark.fingerprint.as_ref()).unwrap()
    );
    assert_eq!(saved_fingerprint["digest"].as_array().unwrap().len(), 32);
    let (restored, warning) = journal.restore();
    assert!(warning.is_none());
    let restored = restored.unwrap();
    assert_eq!(restored.drafts, [None]);
    assert_eq!(restored.state.tabs[0].log.as_ref(), Some(&bookmark));
}

#[test]
fn closed_scratch_tabs_keep_unicode_text_across_restart() {
    let temporary = tempfile::tempdir().unwrap();
    let mut state = capture("open");
    state.state.closed.push(Tab {
        json_validation_disabled: false,
        id: 2,
        file: None,
        format: TextFormat::default(),
        selection: 4..4,
        reversed: false,
        scroll: [3., 9.],
        draft_revision: Some(5),
        log: None,
        line_count: Some(1),
        last_worked_ms: Some(1_700_000_000_000),
    });
    state
        .closed_drafts
        .push(Some(Rope::from("Заметки 🦀 café")));
    let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();

    journal.commit(1, state).unwrap();
    drop(journal);

    let mut reopened = Journal::open(temporary.path().to_path_buf()).unwrap();
    let (recovered, warning) = reopened.restore();
    assert!(warning.is_none());
    let recovered = recovered.unwrap();
    assert_eq!(
        recovered.closed_drafts,
        [Some("Заметки 🦀 café".to_owned())]
    );
    assert_eq!(recovered.state.closed[0].id, 2);
    assert_eq!(recovered.state.closed[0].selection, 4..4);
    assert_eq!(recovered.state.closed[0].line_count, Some(1));
    assert_eq!(
        recovered.state.closed[0].last_worked_ms,
        Some(1_700_000_000_000)
    );
}

#[test]
fn closed_log_tabs_keep_view_state_and_bookmark_across_restart() {
    use crate::log_file::observe;

    let temporary = tempfile::tempdir().unwrap();
    let log_path = temporary.path().join("closed.log");
    fs::write(&log_path, "first\nsecond\nthird\n").unwrap();
    let (observation, _) = observe(&log_path, None).unwrap();
    let mut bookmark = LogBookmark {
        offset: 12,
        hidden: 6,
        auto_refresh: false,
        follow_end: true,
        ..LogBookmark::default()
    };
    bookmark.record_observation(&observation);
    let mut state = capture("");
    state.state.closed.push(Tab {
        json_validation_disabled: false,
        id: 2,
        file: Some(FileTarget::from_picker(log_path).unwrap()),
        format: TextFormat::for_encoding(TextEncoding::Windows1251),
        selection: 8..12,
        reversed: true,
        scroll: [14., 72.],
        draft_revision: None,
        log: Some(bookmark.clone()),
        line_count: None,
        last_worked_ms: None,
    });
    state.closed_drafts.push(None);
    let mut journal = Journal::open(temporary.path().join("journal")).unwrap();

    journal.commit(1, state).unwrap();
    let (restored, warning) = journal.restore();
    assert!(warning.is_none());
    let restored = restored.unwrap();
    let closed = &restored.state.closed[0];
    assert_eq!(closed.id, 2);
    assert!(closed.format.encoding == TextEncoding::Windows1251);
    assert_eq!(closed.selection, 8..12);
    assert!(closed.reversed);
    assert_eq!(closed.scroll, [14., 72.]);
    assert_eq!(closed.log.as_ref(), Some(&bookmark));
}

#[test]
fn restores_records_written_before_closed_tab_history_was_added() {
    let temporary = tempfile::tempdir().unwrap();
    let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();
    journal.commit(1, capture("старый формат")).unwrap();
    let manifest_path = journal.record_path(1).join("manifest.json");
    let mut manifest: serde_json::Value =
        serde_json::from_slice(&fs::read(&manifest_path).unwrap()).unwrap();
    manifest["state"].as_object_mut().unwrap().remove("closed");
    manifest.as_object_mut().unwrap().remove("closed_drafts");
    fs::write(&manifest_path, serde_json::to_vec(&manifest).unwrap()).unwrap();

    let (recovered, warning) = journal.restore();
    assert!(warning.is_none());
    let recovered = recovered.unwrap();
    assert!(recovered.state.closed.is_empty());
    assert!(recovered.closed_drafts.is_empty());
    assert_eq!(recovered.drafts, [Some("старый формат".into())]);
}

#[test]
fn commits_keep_only_the_newest_hundred_closed_tabs() {
    let temporary = tempfile::tempdir().unwrap();
    let mut state = capture("open");
    state.state.closed = (0..MAX_CLOSED_TABS + 1)
        .map(|id| Tab {
            json_validation_disabled: false,
            id: id as u64,
            file: None,
            format: TextFormat::default(),
            selection: 0..0,
            reversed: false,
            scroll: [0., 0.],
            draft_revision: None,
            log: None,
            line_count: None,
            last_worked_ms: None,
        })
        .collect();
    state.closed_drafts = vec![None; MAX_CLOSED_TABS + 1];
    let mut journal = Journal::open(temporary.path().to_path_buf()).unwrap();

    journal.commit(1, state).unwrap();
    let (recovered, warning) = journal.restore();
    assert!(warning.is_none());
    let recovered = recovered.unwrap();
    assert_eq!(recovered.state.closed.len(), MAX_CLOSED_TABS);
    assert_eq!(recovered.state.closed.first().unwrap().id, 1);
    assert_eq!(recovered.state.closed.last().unwrap().id, 100);
    assert_eq!(recovered.closed_drafts.len(), MAX_CLOSED_TABS);
}

fn file_identity(path: &Path) -> crate::log_file::FileIdentity {
    crate::log_file::FileIdentity::read(&File::open(path).unwrap()).unwrap()
}

#[test]
fn reuses_unchanged_draft_from_non_adjacent_checkpoint_via_hard_link() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    let mut journal = Journal::open(directory).unwrap();

    let snapshot = |cursor: usize| {
        let mut s = capture("unchanged open draft");
        let mut closed_tab = s.state.tabs[0].clone();
        closed_tab.id = 2;
        s.state.closed.push(closed_tab);
        s.closed_drafts
            .push(Some(Rope::from("unchanged closed draft")));
        s.state.tabs[0].selection = cursor..cursor;
        s
    };

    journal.commit(1, snapshot(1)).unwrap();
    journal.commit(2, snapshot(2)).unwrap();
    let open_id_1 = file_identity(&journal.record_path(1).join("draft-0.txt"));
    let open_id_2 = file_identity(&journal.record_path(2).join("draft-0.txt"));
    let closed_id_1 = file_identity(&journal.record_path(1).join("closed-draft-0.txt"));
    let closed_id_2 = file_identity(&journal.record_path(2).join("closed-draft-0.txt"));
    assert_ne!(open_id_1, open_id_2);
    assert_ne!(closed_id_1, closed_id_2);

    journal.commit(3, snapshot(3)).unwrap();
    let open_id_3 = file_identity(&journal.record_path(3).join("draft-0.txt"));
    let closed_id_3 = file_identity(&journal.record_path(3).join("closed-draft-0.txt"));
    assert_eq!(open_id_3, open_id_1);
    assert_ne!(open_id_3, open_id_2);
    assert_eq!(closed_id_3, closed_id_1);
    assert_ne!(closed_id_3, closed_id_2);
}

#[test]
fn restores_previous_checkpoint_after_corrupting_hardlinked_draft() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    let mut journal = Journal::open(directory.clone()).unwrap();

    let snapshot = |cursor: usize| {
        let mut s = capture("open draft text");
        s.state.tabs[0].selection = cursor..cursor;
        s
    };
    journal.commit(1, snapshot(1)).unwrap();
    journal.commit(2, snapshot(2)).unwrap();
    journal.commit(3, snapshot(3)).unwrap();

    let path_3 = journal.record_path(3).join("draft-0.txt");
    let mut bytes = fs::read(&path_3).unwrap();
    bytes[0] = b'!';
    fs::write(&path_3, bytes).unwrap();
    drop(journal);

    let mut reopened = Journal::open(directory).unwrap();
    let (recovered, warning) = reopened.restore();
    assert!(warning.is_some());
    let recovered = recovered.unwrap();
    assert_eq!(recovered.state.tabs[0].selection, 2..2);
    assert_eq!(recovered.drafts, [Some("open draft text".into())]);
}

#[test]
fn corrupted_donor_before_commit_falls_back_to_fresh_write() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    let mut journal = Journal::open(directory.clone()).unwrap();

    let snapshot = |cursor: usize| {
        let mut s = capture("fresh draft text");
        s.state.tabs[0].selection = cursor..cursor;
        s
    };
    journal.commit(1, snapshot(1)).unwrap();
    journal.commit(2, snapshot(2)).unwrap();

    let donor_path = journal.record_path(1).join("draft-0.txt");
    fs::write(&donor_path, "corrupted donor!").unwrap();

    journal.commit(3, snapshot(3)).unwrap();
    drop(journal);

    let mut reopened = Journal::open(directory).unwrap();
    let (recovered, warning) = reopened.restore();
    assert!(warning.is_none());
    assert_eq!(recovered.unwrap().drafts, [Some("fresh draft text".into())]);
}

#[test]
fn reuses_draft_when_moving_between_open_and_closed_tabs_and_cleans_pending_dirs() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    fs::create_dir(directory.join(".pending-orphan")).unwrap();

    let mut journal = Journal::open(directory.clone()).unwrap();
    assert!(!directory.join(".pending-orphan").exists());

    journal.commit(1, capture("moved text")).unwrap();
    let open_id_1 = file_identity(&journal.record_path(1).join("draft-0.txt"));
    journal.commit(2, capture("moved text")).unwrap();

    let mut closed = capture("other");
    closed.state.closed.push(Tab {
        json_validation_disabled: false,
        id: 2,
        file: None,
        format: TextFormat::default(),
        selection: 0..0,
        reversed: false,
        scroll: [0., 0.],
        draft_revision: Some(1),
        log: None,
        line_count: None,
        last_worked_ms: None,
    });
    closed.closed_drafts.push(Some(Rope::from("moved text")));
    journal.commit(3, closed).unwrap();

    let closed_id_3 = file_identity(&journal.record_path(3).join("closed-draft-0.txt"));
    assert_eq!(closed_id_3, open_id_1);
}

#[test]
fn reopened_journal_verifies_older_untrusted_donor_before_linking() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    {
        let mut journal = Journal::open(directory.clone()).unwrap();
        journal.commit(1, capture("trusted text")).unwrap();
        journal.commit(2, capture("trusted text")).unwrap();
    }

    fs::write(
        directory
            .join(format!("{RECORD_PREFIX}{:020}", 1))
            .join("draft-0.txt"),
        "tampered!!!!",
    )
    .unwrap();

    let mut reopened = Journal::open(directory).unwrap();
    let (_, warning) = reopened.restore();
    assert!(warning.is_none());

    let mut next = capture("trusted text");
    next.state.tabs[0].selection = 1..1;
    reopened.commit(3, next).unwrap();

    let (recovered, warning) = reopened.restore();
    assert!(warning.is_none());
    assert_eq!(recovered.unwrap().drafts, [Some("trusted text".into())]);
}

#[test]
fn rollback_excludes_donors_sharing_file_id_with_restored_previous() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    let mut journal = Journal::open(directory.clone()).unwrap();

    let snapshot = |cursor: usize| {
        let mut s = capture("shared fallback draft");
        s.state.tabs[0].selection = cursor..cursor;
        s
    };

    journal.commit(1, snapshot(1)).unwrap();
    journal.commit(2, snapshot(2)).unwrap();
    journal.commit(3, snapshot(3)).unwrap();
    journal.commit(4, snapshot(4)).unwrap();

    // Simulate an unpruned checkpoint-1 whose draft shares the physical file
    // with checkpoint-3 while checkpoint-4 is corrupt.
    let dir_1 = journal.record_path(1);
    let dir_3 = journal.record_path(3);
    fs::create_dir_all(&dir_1).unwrap();
    fs::copy(dir_3.join("manifest.json"), dir_1.join("manifest.json")).unwrap();
    fs::hard_link(dir_3.join("draft-0.txt"), dir_1.join("draft-0.txt")).unwrap();
    let id_3 = file_identity(&dir_3.join("draft-0.txt"));

    let path_4 = journal.record_path(4).join("draft-0.txt");
    let mut bytes_4 = fs::read(&path_4).unwrap();
    bytes_4[0] = b'!';
    fs::write(&path_4, bytes_4).unwrap();
    drop(journal);

    let mut reopened = Journal::open(directory.clone()).unwrap();
    let (recovered, warning) = reopened.restore();
    assert!(warning.is_some());
    assert_eq!(recovered.unwrap().state.tabs[0].selection, 3..3);

    reopened.commit(5, snapshot(5)).unwrap();
    let id_5 = file_identity(&reopened.record_path(5).join("draft-0.txt"));
    assert_ne!(id_5, id_3);

    let path_5 = reopened.record_path(5).join("draft-0.txt");
    let mut bytes_5 = fs::read(&path_5).unwrap();
    bytes_5[0] = b'?';
    fs::write(&path_5, bytes_5).unwrap();
    drop(reopened);

    let mut verify = Journal::open(directory).unwrap();
    let (fallback, warning) = verify.restore();
    assert!(warning.is_some());
    let fallback = fallback.unwrap();
    assert_eq!(fallback.state.tabs[0].selection, 3..3);
    assert_eq!(fallback.drafts, [Some("shared fallback draft".into())]);
}

#[test]
fn large_donor_tampered_with_same_size_and_preserved_mtime_is_rejected() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    let mut journal = Journal::open(directory).unwrap();

    let large_text = "A".repeat(1_500_000);
    let snapshot = |cursor: usize| {
        let mut s = capture(&large_text);
        s.state.tabs[0].selection = cursor..cursor;
        s
    };

    journal.commit(1, snapshot(1)).unwrap();
    journal.commit(2, snapshot(2)).unwrap();

    let donor_path = journal.record_path(1).join("draft-0.txt");
    let donor_id = file_identity(&donor_path);
    let original_modified = fs::metadata(&donor_path).unwrap().modified().unwrap();
    let mut tampered = large_text.as_bytes().to_vec();
    tampered[0] = b'Z';
    {
        let mut file = OpenOptions::new()
            .write(true)
            .truncate(true)
            .open(&donor_path)
            .unwrap();
        file.write_all(&tampered).unwrap();
        file.set_modified(original_modified).unwrap();
    }

    journal.commit(3, snapshot(3)).unwrap();
    let path_3 = journal.record_path(3).join("draft-0.txt");
    assert_ne!(file_identity(&path_3), donor_id);
    assert_eq!(fs::read_to_string(&path_3).unwrap(), large_text);
}

#[test]
fn open_writer_on_donor_falls_back_to_fresh_write() {
    let temporary = tempfile::tempdir().unwrap();
    let directory = temporary.path().to_path_buf();
    let mut journal = Journal::open(directory).unwrap();

    let snapshot = |cursor: usize| {
        let mut s = capture("writer locked draft");
        s.state.tabs[0].selection = cursor..cursor;
        s
    };

    journal.commit(1, snapshot(1)).unwrap();
    journal.commit(2, snapshot(2)).unwrap();

    let donor_path = journal.record_path(1).join("draft-0.txt");
    let donor_id = file_identity(&donor_path);
    let writer = OpenOptions::new().write(true).open(&donor_path).unwrap();

    journal.commit(3, snapshot(3)).unwrap();
    drop(writer);
    let path_3 = journal.record_path(3).join("draft-0.txt");
    assert_ne!(file_identity(&path_3), donor_id);
    assert_eq!(fs::read_to_string(&path_3).unwrap(), "writer locked draft");
}
