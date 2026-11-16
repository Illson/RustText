use super::{Capture, SessionWriter, State, WindowSize};
use crate::session::Journal;
use std::sync::{Arc, Mutex};

fn capture(width: f32) -> Capture {
    Capture {
        state: State {
            tabs: vec![],
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
