use crate::log_io::SearchPage;

use super::can_continue;

#[test]
fn incomplete_tail_waits_for_file_growth_before_enabling_more() {
    let page = SearchPage {
        hits: Vec::new(),
        next_offset: 0,
        next_line_index: 0,
        scanned_bytes: 4,
        complete: false,
        pending_line_start: Some(0),
        file_size: 7,
        work_limit_reached: false,
    };

    assert!(!can_continue(&page, 7));
    assert!(can_continue(&page, 8));
}
