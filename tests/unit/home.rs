use super::{format_last_worked, history_meta, text_line_count, utc_datetime_parts};
use gpui_kit::component::input::Rope;

#[test]
fn formats_recent_work_time_as_relative_under_a_day_and_datetime_after() {
    let now = 1_700_000_000_000_u64;
    assert_eq!(format_last_worked(now - 30_000, now), "только что");
    assert_eq!(format_last_worked(now - 5 * 60_000, now), "5 мин назад");
    assert_eq!(format_last_worked(now - 3 * 3_600_000, now), "3 ч назад");

    assert_eq!(format_last_worked(now - 86_399_999, now), "23 ч назад");
    let older = now - 86_400_000;
    let formatted = format_last_worked(older, now);
    assert!(formatted.contains('.'));
    assert!(formatted.contains(':'));
    assert!(!formatted.contains("назад"));
    assert_eq!(
        utc_datetime_parts(1_700_000_000_000),
        (2023, 11, 14, 22, 13)
    );
}

#[test]
fn formats_history_metadata_with_line_count_and_last_worked() {
    let now = 1_700_000_000_000_u64;
    let last_worked = Some(now - 12 * 60_000);
    assert_eq!(
        history_meta(Some(420), last_worked, now),
        "420 стр. · 12 мин назад"
    );
    assert_eq!(history_meta(Some(0), None, now), "0 стр.");
    assert_eq!(history_meta(None, last_worked, now), "12 мин назад");
    assert_eq!(history_meta(None, None, now), "");
}

#[test]
fn counts_empty_rope_as_zero_lines_matching_log_mode() {
    assert_eq!(text_line_count(&Rope::from("")), 0);
    assert_eq!(text_line_count(&Rope::from("one")), 1);
    assert_eq!(text_line_count(&Rope::from("one\ntwo")), 2);
}
