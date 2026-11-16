use super::*;
use crate::text_format::TextEncoding;

fn capture(text: &str) -> Capture {
    Capture {
        state: State {
            tabs: vec![Tab {
                id: 1,
                file: None,
                format: TextFormat::default(),
                selection: 0..2,
                reversed: true,
                scroll: [0., -154.],
                draft_revision: Some(1),
                log: None,
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
        id: 2,
        file: None,
        format: TextFormat::default(),
        selection: 4..4,
        reversed: false,
        scroll: [3., 9.],
        draft_revision: Some(5),
        log: None,
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
        id: 2,
        file: Some(FileTarget::from_picker(log_path).unwrap()),
        format: TextFormat::for_encoding(TextEncoding::Windows1251),
        selection: 8..12,
        reversed: true,
        scroll: [14., 72.],
        draft_revision: None,
        log: Some(bookmark.clone()),
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
            id: id as u64,
            file: None,
            format: TextFormat::default(),
            selection: 0..0,
            reversed: false,
            scroll: [0., 0.],
            draft_revision: None,
            log: None,
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
