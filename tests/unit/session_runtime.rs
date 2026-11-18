use super::{Capture, SessionWriter, State, WindowSize};
use crate::session::Journal;
use std::sync::{Arc, Mutex};

fn capture(width: f32) -> Capture {
    Capture {
        state: State {
            layout: None,
            tabs: vec![],
            closed: vec![],
            active: 0,
            window: WindowSize {
                width,
                height: 700.0,
                maximized: false,
            },
            wrap: false,
            font_size: 15.0,
        },
        drafts: vec![],
        closed_drafts: vec![],
    }
}

#[test]
fn queues_the_final_state_even_when_it_matches_the_last_completed_write() {
    let dir = tempfile::tempdir().unwrap();
    let journal = Journal::open(dir.path().to_path_buf()).unwrap();
    let mut writer = SessionWriter {
        journal: Arc::new(Mutex::new(journal)),
        last_saved: Some(capture(1080.0).state),
        revision: 0,
        in_flight: false,
        pending_view: None,
    };
    assert!(writer.queue(capture(1080.0)).is_none());

    let older = writer.queue(capture(960.0)).expect("changed window size");
    assert_eq!(older.capture.state.window.width, 960.0);
    let shutdown = writer
        .queue(capture(1080.0))
        .expect("superseding shutdown capture");
    assert_eq!(shutdown.capture.state.window.width, 1080.0);
    assert!(shutdown.revision > older.revision);
}

#[test]
fn view_only_changes_wait_for_quiet_window_and_flush_on_shutdown() {
    use super::{DraftDigest, VIEW_SAVE_QUIET};
    use crate::session::{Draft, Tab};
    use crate::text_format::TextFormat;
    use std::time::{Duration, Instant};

    let dir = tempfile::tempdir().unwrap();
    let journal = Journal::open(dir.path().to_path_buf()).unwrap();
    let mut base = capture(1080.0);
    base.state.tabs.push(Tab {
        json_validation_disabled: false,
        id: 1,
        file: None,
        format: TextFormat::default(),
        selection: 0..0,
        reversed: false,
        scroll: [0.0, 0.0],
        draft_revision: Some(1),
        log: None,
        line_count: None,
        last_worked_ms: None,
    });
    let text = gpui_kit::component::input::Rope::from("text");
    let digests = vec![Some(DraftDigest::from_rope(&text))];
    base.drafts.push(Some(Draft::Memory(text)));

    let mut writer = SessionWriter {
        journal: Arc::new(Mutex::new(journal)),
        last_saved: Some(base.state.clone()),
        revision: 0,
        in_flight: false,
        pending_view: None,
    };

    let t0 = Instant::now();
    let mut scrolled = capture(1080.0);
    scrolled.state = base.state.clone();
    scrolled.drafts = base.drafts.clone();
    scrolled.state.tabs[0].scroll = [0.0, -500.0];
    scrolled.state.tabs[0].selection = 2..2;

    // First observation of view-only change starts the quiet timer.
    assert!(
        writer
            .queue_prepared(
                Capture {
                    state: scrolled.state.clone(),
                    drafts: scrolled.drafts.clone(),
                    closed_drafts: vec![],
                },
                digests.clone(),
                vec![],
                false,
                t0,
            )
            .is_none()
    );
    // Still inside the quiet window.
    assert!(
        writer
            .queue_prepared(
                Capture {
                    state: scrolled.state.clone(),
                    drafts: scrolled.drafts.clone(),
                    closed_drafts: vec![],
                },
                digests.clone(),
                vec![],
                false,
                t0 + VIEW_SAVE_QUIET - Duration::from_millis(100),
            )
            .is_none()
    );
    // Continuous scrolling resets the timer.
    let mut scrolled_further = scrolled.state.clone();
    scrolled_further.tabs[0].scroll = [0.0, -900.0];
    assert!(
        writer
            .queue_prepared(
                Capture {
                    state: scrolled_further.clone(),
                    drafts: scrolled.drafts.clone(),
                    closed_drafts: vec![],
                },
                digests.clone(),
                vec![],
                false,
                t0 + VIEW_SAVE_QUIET,
            )
            .is_none()
    );
    // Once quiet elapses after the last change, the view write queues.
    let queued = writer
        .queue_prepared(
            Capture {
                state: scrolled_further.clone(),
                drafts: scrolled.drafts.clone(),
                closed_drafts: vec![],
            },
            digests.clone(),
            vec![],
            false,
            t0 + VIEW_SAVE_QUIET + VIEW_SAVE_QUIET,
        )
        .expect("quiet window elapsed");
    assert_eq!(queued.capture.state.tabs[0].scroll, [0.0, -900.0]);
    writer.in_flight = false;
    writer.last_saved = Some(scrolled_further.clone());

    // Content change queues immediately without waiting for the quiet window.
    let mut edited = scrolled_further.clone();
    edited.tabs[0].draft_revision = Some(2);
    assert!(
        writer
            .queue_prepared(
                Capture {
                    state: edited.clone(),
                    drafts: scrolled.drafts.clone(),
                    closed_drafts: vec![],
                },
                digests.clone(),
                vec![],
                false,
                t0 + VIEW_SAVE_QUIET + VIEW_SAVE_QUIET + Duration::from_millis(10),
            )
            .is_some()
    );
    writer.in_flight = false;
    writer.last_saved = Some(edited.clone());

    // Shutdown flushes a fresh view-only change immediately.
    let mut final_scroll = edited;
    final_scroll.tabs[0].scroll = [0.0, -1200.0];
    assert!(
        writer
            .queue_prepared(
                Capture {
                    state: final_scroll,
                    drafts: scrolled.drafts,
                    closed_drafts: vec![],
                },
                digests,
                vec![],
                true,
                t0 + VIEW_SAVE_QUIET + VIEW_SAVE_QUIET + Duration::from_millis(20),
            )
            .is_some()
    );
}
