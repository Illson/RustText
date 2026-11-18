use gpui_kit::component::input::Rope;

use super::{
    MAX_EDITOR_LINE_BYTES, MAX_EDITOR_LINES, PreparedDraft, Recovery, RestoreProgress, Stage,
    StagedDraft, Tab, prepare_tab, stage_draft,
};
use crate::{
    files::FileTarget,
    session::{Draft, DraftDigest},
};
use std::sync::{Arc, atomic::Ordering};

#[test]
fn cancel_error_and_preview_preserve_the_original_checkpoint_payload() {
    let source = "Первая строка\n\nПоследняя строка 💡\n";
    let progress = Arc::new(RestoreProgress::new(source.len() as u64));
    let text = Rope::from(source);
    let digest = DraftDigest::from_rope(&text);
    let mut recovery = Recovery {
        tab: Tab {
            id: 3,
            file: None,
            format: Default::default(),
            selection: 3..7,
            reversed: true,
            scroll: [0., -100.],
            draft_revision: Some(51),
            json_validation_disabled: true,
            log: None,
            line_count: None,
            last_worked_ms: None,
        },
        draft: Some(Draft::Memory(text)),
        digest: Some(digest),
        stage: Stage::Loading(progress.clone()),
    };
    recovery.cancel();
    assert!(progress.cancelled.load(Ordering::Relaxed));

    for stage in [
        Stage::Queued,
        recovery.stage,
        Stage::Failed("Ошибка чтения".into()),
        Stage::Preview,
    ] {
        recovery.stage = stage;
        let (tab, draft, retained_digest) = recovery.snapshot(true);
        assert_eq!(tab.draft_revision, Some(51));
        assert_eq!(tab.selection, 3..7);
        let mut bytes = Vec::new();
        draft.unwrap().write_to(&mut bytes).unwrap();
        assert_eq!(bytes, source.as_bytes());
        assert_eq!(retained_digest, Some(digest));
        let (metadata, draft, digest) = recovery.snapshot(false);
        assert!(metadata.draft_revision.is_none());
        assert!(draft.is_none());
        assert!(digest.is_none());
    }
}

#[test]
fn staging_keeps_small_drafts_editable_and_copies_every_byte_of_oversized_drafts() {
    let dense = "я\n".repeat(MAX_EDITOR_LINES);
    let wide = "a".repeat(MAX_EDITOR_LINE_BYTES + 1);
    for (source, preview) in [
        ("строка\nтекст 💡\n", false),
        (dense.as_str(), true),
        (wide.as_str(), true),
    ] {
        let draft = Draft::Memory(Rope::from(source));
        let progress = RestoreProgress::new(draft.byte_len());
        match (stage_draft(&draft, &progress).unwrap(), preview) {
            (StagedDraft::Editable(text), false) => assert_eq!(text, source),
            (StagedDraft::Preview(file), true) => {
                assert_eq!(std::fs::read(file.path()).unwrap(), source.as_bytes())
            }
            _ => panic!("unexpected restore mode"),
        }
        assert_eq!(progress.percent(), Some(100));
    }
}

#[test]
fn cancelled_staging_does_not_consume_draft() {
    let draft = Draft::Memory(Rope::from("текст"));
    let progress = RestoreProgress::new(draft.byte_len());
    progress.cancelled.store(true, Ordering::Relaxed);
    assert!(stage_draft(&draft, &progress).is_err());
    assert_eq!(progress.percent(), Some(0));
    let retry = RestoreProgress::new(draft.byte_len());
    assert!(matches!(
        stage_draft(&draft, &retry).unwrap(),
        StagedDraft::Editable(text) if text == "текст"
    ));
}

#[test]
fn restoring_a_clean_nine_mib_file_keeps_it_editable() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("document.txt");
    let text = "text on disk\n".repeat(9 * 1024 * 1024 / "text on disk\n".len() + 1);
    std::fs::write(&path, &text).unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let tab = Tab {
        id: 1,
        file: Some(target),
        format: Default::default(),
        selection: 4..7,
        reversed: false,
        scroll: [0., -80.],
        draft_revision: None,
        json_validation_disabled: false,
        log: None,
        line_count: None,
        last_worked_ms: None,
    };
    let prepared = prepare_tab(&tab, None, &RestoreProgress::new(0)).unwrap();
    assert!(matches!(prepared, PreparedDraft::File(opened)
        if tab.file.as_ref() == Some(&opened.target) && opened.text == text));
}
