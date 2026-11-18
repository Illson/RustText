use super::{
    DAY_MS, HOUR_MS, HistoryRefresh, MINUTE_MS, format_last_worked, format_local_datetime,
    history_meta, next_history_refresh, text_line_count, utc_datetime_parts,
};
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
fn batches_recent_history_changes_with_a_ten_second_minimum_delay() {
    let now = 1_700_000_000_000;
    let recent = now - MINUTE_MS + 10;
    let nearly_an_hour = now - HOUR_MS + 20;
    let nearly_a_day = now - DAY_MS + 30;
    let refresh =
        next_history_refresh([nearly_a_day, recent, nearly_an_hour].into_iter(), now).unwrap();
    assert_eq!(
        refresh,
        HistoryRefresh {
            deadline_ms: now + 10_000,
            min_interval_ms: 10_000,
        }
    );
    assert_eq!(
        format_last_worked(recent, refresh.deadline_ms),
        "1 мин назад"
    );
    assert_eq!(
        format_last_worked(nearly_an_hour, refresh.deadline_ms),
        "1 ч назад"
    );
    assert_eq!(
        next_history_refresh([now].into_iter(), now)
            .unwrap()
            .deadline_ms,
        now + MINUTE_MS
    );
}

#[test]
fn batches_hourly_history_changes_with_a_one_minute_minimum_delay() {
    let now = 1_700_000_000_000;
    let nearly_two_hours = now - 2 * HOUR_MS + 20;
    let nearly_a_day = now - DAY_MS + 30;
    let old = now - DAY_MS;
    let refresh =
        next_history_refresh([old, nearly_a_day, nearly_two_hours].into_iter(), now).unwrap();
    assert_eq!(
        refresh,
        HistoryRefresh {
            deadline_ms: now + MINUTE_MS,
            min_interval_ms: MINUTE_MS,
        }
    );
    assert_eq!(
        format_last_worked(nearly_two_hours, refresh.deadline_ms),
        "2 ч назад"
    );
    assert_eq!(
        format_last_worked(nearly_a_day, refresh.deadline_ms),
        format_local_datetime(nearly_a_day)
    );
    assert_eq!(
        next_history_refresh([now - HOUR_MS].into_iter(), now)
            .unwrap()
            .deadline_ms,
        now + HOUR_MS
    );
    assert_eq!(
        next_history_refresh([old, nearly_a_day].into_iter(), now + 30),
        None
    );
}

#[test]
fn forty_staggered_recent_files_need_six_refreshes_in_a_minute() {
    let start = 1_700_000_000_000;
    let timestamps = (1..=40).map(|row| start - MINUTE_MS + row * 1_500);
    let mut now = start;
    for _ in 0..6 {
        let refresh = next_history_refresh(timestamps.clone(), now).unwrap();
        assert_eq!(refresh.deadline_ms, now + 10_000);
        now = refresh.deadline_ms;
    }
    assert_eq!(now, start + MINUTE_MS);
}

#[test]
fn counts_empty_rope_as_zero_lines_matching_log_mode() {
    assert_eq!(text_line_count(&Rope::from("")), 0);
    assert_eq!(text_line_count(&Rope::from("one")), 1);
    assert_eq!(text_line_count(&Rope::from("one\ntwo")), 2);
}
