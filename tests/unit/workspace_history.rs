use std::path::PathBuf;

use gpui_kit::component::input::Rope;

use super::{ClosedTab, available_history_line_count, closed_tab_entries, home::text_line_count};
use crate::{files::FileTarget, recent_files::RecentFile, session::Tab, text_format::TextEncoding};

#[test]
fn placeholder_keeps_missing_line_count_while_empty_file_has_zero_lines() {
    assert_eq!(
        available_history_line_count(true, || panic!("placeholder has no content to count")),
        None
    );
    assert_eq!(
        available_history_line_count(false, || Some(text_line_count(&Rope::new()))),
        Some(0)
    );
}

#[test]
fn closed_tabs_fill_only_missing_metadata_from_first_recent_match() {
    let path = PathBuf::from("unavailable.txt");
    let closed_tab = |line_count, last_worked_ms| ClosedTab {
        tab: Tab {
            json_validation_disabled: false,
            id: 1,
            file: Some(FileTarget {
                path: path.clone(),
                version: None,
            }),
            format: Default::default(),
            selection: 0..0,
            reversed: false,
            scroll: [0., 0.],
            draft_revision: None,
            log: None,
            line_count,
            last_worked_ms,
        },
        draft: None,
    };
    let closed = [
        closed_tab(Some(0), Some(800)),
        closed_tab(None, Some(900)),
        closed_tab(Some(2), None),
        closed_tab(None, None),
    ];
    let mut first = RecentFile::new(path.clone(), TextEncoding::Utf8);
    first.line_count = Some(27);
    first.last_worked_ms = Some(700);
    let mut older = first.clone();
    older.line_count = Some(12);
    older.last_worked_ms = Some(600);
    let metadata: Vec<_> = closed_tab_entries(closed.iter(), &[first, older], 10)
        .map(|entry| (entry.line_count, entry.last_worked_ms))
        .collect();

    assert_eq!(
        metadata,
        [
            (Some(0), Some(800)),
            (Some(27), Some(900)),
            (Some(2), Some(700)),
            (Some(27), Some(700)),
        ]
    );
}
