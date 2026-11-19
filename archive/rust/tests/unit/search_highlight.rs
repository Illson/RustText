use gpui_kit::base::input::RangeDecorationStyle;

use super::decorations_at;

#[test]
fn find_highlight_pulses_then_keeps_only_the_frame() {
    let initial = decorations_at(4..9, 0.0);
    let pulse = decorations_at(4..9, 0.35);
    let settled = decorations_at(4..9, 1.0);

    assert_eq!(initial[1].style(), RangeDecorationStyle::Fill);
    assert!(pulse[1].color().unwrap().a > initial[1].color().unwrap().a);
    assert_eq!(settled.len(), 1);
    assert_eq!(settled[0].style(), RangeDecorationStyle::Frame);
    assert_eq!(settled[0].range(), &(4..9));
}

#[test]
fn visible_highlighting_has_no_result_list_limit() {
    let text = "match\n".repeat(250);
    let regex = regex::Regex::new("match").unwrap();
    let ranges = super::matching_ranges(&regex, &text, 600..text.len(), false);
    assert_eq!(ranges.len(), 150);
    assert_eq!(ranges.first(), Some(&(600..605)));
}

#[test]
fn editor_anchors_use_document_boundaries_and_logs_use_record_boundaries() {
    let regex = regex::Regex::new("^hit$").unwrap();
    let text = "miss\nhit\nhit";
    assert!(super::matching_ranges(&regex, text, 5..12, false).is_empty());
    assert_eq!(
        super::matching_ranges(&regex, text, 5..12, true),
        vec![5..8, 9..12]
    );
}

#[test]
fn visible_highlighting_reuses_case_word_and_regex_options() {
    let query = crate::search::Query {
        text: "тест[0-9]".into(),
        regex: true,
        whole_word: true,
        ..Default::default()
    };
    let regex = crate::search::compile(&query).unwrap();
    let text = "ТЕСТ1 тест22 тест3";
    let ranges = super::matching_ranges(&regex, text, 0..text.len(), false);
    assert_eq!(ranges, vec![0..9, 21..30]);
}

#[test]
fn cached_ranges_match_regex_oracle_for_unicode_empty_adjacent_and_cross_line_matches() {
    let text = format!("{}αaabb\nλ終 a\n", "a".repeat(63));
    for pattern in [
        "(?s)α.*終",
        "a*",
        "(?:|a)",
        r"\b",
        "(?m)^.*$",
        "aa",
        ".",
        "a|bb",
        "λ終",
        "\na",
    ] {
        let regex = regex::Regex::new(pattern).unwrap();
        let cache = super::MatchCache::build(&regex, &text, false, || false).unwrap();
        for start in 0..=text.len() {
            for span in [0, 1, 4, text.len()] {
                let end = start.saturating_add(span).min(text.len());
                let expected: Vec<_> = regex
                    .find_iter(&text)
                    .filter(|matched| {
                        !matched.is_empty()
                            && matched.end() > start
                            && matched.start() < end
                            && start < end
                    })
                    .map(|matched| matched.range())
                    .collect();
                assert_eq!(
                    cache.visible(start..end),
                    expected,
                    "pattern={pattern:?}, visible={start}..{end}"
                );
            }
        }
    }
}

#[test]
fn cancelled_or_changed_revision_does_not_publish_partial_cache() {
    let examined = std::cell::Cell::new(0);
    let regex = regex::Regex::new("a").unwrap();
    let result = super::MatchCache::build(&regex, "aaaaaa", false, || {
        examined.set(examined.get() + 1);
        examined.get() > 3
    });
    assert!(result.is_none());
}

#[test]
fn unchanged_scroll_reuses_bounded_cache() {
    use std::{hint::black_box, time::Instant};
    let benchmark = std::env::var_os("RUSTTEXT_HIGHLIGHT_BENCH").is_some();
    let bytes = if benchmark {
        50 * 1024 * 1024
    } else {
        64 * 1024
    };
    let line = format!("needle{}\n", "x".repeat(1017));
    let text = line.repeat(bytes / line.len());
    let regex = regex::Regex::new("needle").unwrap();
    let pages: Vec<_> = [0, 1, 2, 3]
        .map(|index| {
            let start = index * (text.len() - 16 * 1024) / 3;
            start..start + 16 * 1024
        })
        .into_iter()
        .collect();
    let before = Instant::now();
    let mut expected = Vec::new();
    for page in &pages {
        let full_copy = black_box(text.clone());
        expected.push(
            regex
                .find_iter(&full_copy)
                .filter(|matched| {
                    !matched.is_empty() && matched.end() > page.start && matched.start() < page.end
                })
                .map(|matched| matched.range())
                .collect::<Vec<_>>(),
        );
    }
    let baseline = before.elapsed();
    let before = Instant::now();
    let cache = super::MatchCache::build(&regex, &text, false, || false).unwrap();
    let build = before.elapsed();
    let before = Instant::now();
    for (page, expected) in pages.iter().zip(&expected) {
        assert_eq!(black_box(cache.visible(page.clone())), *expected);
    }
    let scrolling = before.elapsed();
    let storage = (cache.starts.len() + cache.ends.len()) * std::mem::size_of::<u64>();
    assert!(storage <= text.len() / 4 + 16);
    if benchmark {
        println!(
            "HIGHLIGHT_BENCH bytes={} pages={} baseline_copied_bytes={} baseline_regex_scans={} baseline_us={} cache_build_regex_scans=1 cache_build_us={} cache_bytes={} cached_scroll_copied_bytes=0 cached_scroll_regex_scans=0 cached_scroll_us={}",
            text.len(),
            pages.len(),
            text.len() * pages.len(),
            pages.len(),
            baseline.as_micros(),
            build.as_micros(),
            storage,
            scrolling.as_micros()
        );
    }
}

#[test]
fn dense_long_line_materialization_follows_supplied_byte_window() {
    let text = "я ".repeat(4096);
    let regex = regex::Regex::new("я").unwrap();
    let cache = super::MatchCache::build(&regex, &text, false, || false).unwrap();
    let physical_row = cache.visible(0..text.len());
    let clipped_bytes = 1200..1320;
    let clipped = cache.visible(clipped_bytes.clone());
    let expected: Vec<_> = regex
        .find_iter(&text)
        .filter(|matched| {
            matched.end() > clipped_bytes.start && matched.start() < clipped_bytes.end
        })
        .map(|matched| matched.range())
        .collect();
    assert_eq!(physical_row.len(), 4096);
    assert_eq!(clipped.len(), 40);
    assert_eq!(clipped, expected);
}

#[test]
fn clipped_windows_preserve_spanning_matches_without_duplicate_decorations() {
    let text = "α start\ninside\nfinish 終";
    let regex = regex::Regex::new("(?s)start.*finish").unwrap();
    let cache = super::MatchCache::build(&regex, text, false, || false).unwrap();
    assert_eq!(cache.visible_windows(&[4..8, 12..15]), vec![3..22]);
}

#[test]
fn disjoint_horizontal_windows_do_not_materialize_hidden_matches() {
    let text = "я ".repeat(4096);
    let regex = regex::Regex::new("я").unwrap();
    let cache = super::MatchCache::build(&regex, &text, false, || false).unwrap();
    let windows = [1200..1320, 9000..9060];
    let ranges = cache.visible_windows(&windows);
    let expected: Vec<_> = regex
        .find_iter(&text)
        .filter(|matched| {
            windows
                .iter()
                .any(|window| matched.end() > window.start && matched.start() < window.end)
        })
        .map(|matched| matched.range())
        .collect();
    assert_eq!(ranges.len(), 60);
    assert_eq!(ranges, expected);
}

#[test]
fn caret_projection_clips_deep_scroll_without_pixel_hit_testing() {
    let calls = std::cell::Cell::new(0);
    let visible = super::projected_window(0..4096, -12.0..108.0, |index| {
        calls.set(calls.get() + 1);
        Some(index as f32 * 8.0 - 30_000.0)
    });
    assert_eq!(visible, Some(3748..3764));
    assert!(calls.get() < 50);
}

#[test]
fn caret_projection_keeps_ligature_and_combining_cluster_boundaries() {
    let carets = [0.0, 12.0, 12.0, 12.0, 20.0, 20.0, 28.0];
    assert_eq!(
        super::projected_window(0..6, 2.0..8.0, |index| Some(carets[index])),
        Some(0..3)
    );
    assert_eq!(
        super::projected_window(0..6, 12.0..19.0, |index| Some(carets[index])),
        Some(3..5)
    );
    assert_eq!(
        super::projected_window(0..6, 28.0..50.0, |index| Some(carets[index])),
        None
    );
}

#[test]
fn caret_projection_handles_wrap_boundary_and_unicode_byte_mapping() {
    use gpui_kit::component::input::Rope;
    let text = Rope::from("я🙂e\u{301}λ");
    let carets = [0.0, 8.0, 24.0, 32.0, 32.0, f32::INFINITY];
    let characters = super::projected_window(0..5, 6.0..30.0, |index| Some(carets[index])).unwrap();
    assert_eq!(characters, 0..4);
    let bytes = text.char_to_byte_idx(characters.start)..text.char_to_byte_idx(characters.end);
    assert_eq!(text.slice(bytes).to_string(), "я🙂e\u{301}");
}

#[test]
fn caret_projection_at_scroll_zero_on_megaline_stays_near_origin() {
    let calls = std::cell::Cell::new(0);
    let max_index = std::cell::Cell::new(0);
    let visible = super::projected_window(0..1_000_000, 0.0..1200.0, |index| {
        calls.set(calls.get() + 1);
        max_index.set(max_index.get().max(index));
        Some(index as f32 * 8.0)
    });
    assert_eq!(visible, Some(0..150));
    assert!(calls.get() <= 32, "calls={}", calls.get());
    assert!(max_index.get() <= 512, "max_index={}", max_index.get());
}

#[test]
fn unchanged_projection_does_no_caret_probes_and_geometry_or_edit_changes_invalidate() {
    let probes = std::cell::Cell::new(0);
    let mut cache = super::ProjectionCache::default();
    let mut stamp = [0u64; 10];
    let project = || {
        probes.set(probes.get() + 1);
        Some(vec![1200..1260, 1300..1320])
    };
    assert_eq!(
        cache.get(stamp, project),
        Some(vec![1200..1260, 1300..1320])
    );
    for _ in 0..100 {
        assert_eq!(
            cache.get(stamp, project),
            Some(vec![1200..1260, 1300..1320])
        );
    }
    assert_eq!(probes.get(), 1);
    for field in 0..stamp.len() {
        stamp[field] += 1;
        assert_eq!(
            cache.get(stamp, project),
            Some(vec![1200..1260, 1300..1320])
        );
        assert_eq!(probes.get(), field + 2);
    }
    let mut next_generation = super::ProjectionCache::default();
    next_generation.get(stamp, project);
    assert_eq!(probes.get(), stamp.len() + 2);
}

#[test]
fn visible_display_row_range_keeps_row_precision_past_f32_cliff() {
    let line_height = gpui_kit::px(22.5);
    let viewport_height = gpui_kit::px(700.0);
    let deep_scroll = -210_000_000.0;
    let start_row = (210_000_000.0_f64 / 22.5).floor() as usize;

    let base = super::visible_display_row_range(
        deep_scroll,
        gpui_kit::px(0.0),
        viewport_height,
        line_height,
    );
    assert_eq!(base.start, start_row);
    assert!(base.end > base.start + 30);

    // Small 4 px step at -210M px is lost in f32, but crosses the exact row boundary in f64.
    let next_row_edge = -((start_row + 1) as f64) * 22.5;
    let before = super::visible_display_row_range(
        next_row_edge + 2.0,
        gpui_kit::px(0.0),
        viewport_height,
        line_height,
    );
    let after = super::visible_display_row_range(
        next_row_edge - 2.0,
        gpui_kit::px(0.0),
        viewport_height,
        line_height,
    );
    assert_eq!(before.start, start_row);
    assert_eq!(after.start, start_row + 1);
}
