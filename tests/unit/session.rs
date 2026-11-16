use super::*;

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
            }],
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
