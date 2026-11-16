use super::*;
use std::sync::atomic::AtomicBool;

fn run_search(
    query: &Query,
    command: Command,
    sources: Vec<Source>,
    origin: &Hit,
    skip_current: bool,
    replacement: &str,
) -> Result<Outcome, String> {
    let cancelled = AtomicBool::new(false);
    let work = WorkBudget::new(&cancelled, Instant::now() + MAX_SEARCH_TIME);
    run(
        query,
        command,
        sources,
        origin,
        replacement,
        &work,
        NavigationStart {
            skip_current,
            progress: None,
        },
    )
}

#[test]
fn navigates_tabs_with_unicode_words_and_wraps_empty_matches() {
    let documents = || {
        vec![
            Source {
                tab: 1,
                text: Rope::from("котик КОТ"),
            },
            Source {
                tab: 2,
                text: Rope::from("кот"),
            },
        ]
    };
    let query = Query {
        text: "кот".into(),
        whole_word: true,
        ..Query::default()
    };
    let origin = Hit {
        tab: 1,
        range: 11..17,
    };
    let Outcome::Found { hit: Some(hit) } =
        run_search(&query, Command::Next, documents(), &origin, true, "").unwrap()
    else {
        panic!("expected two whole words");
    };
    assert_eq!(
        hit,
        Hit {
            tab: 2,
            range: 0..6
        }
    );

    let query = Query {
        text: "^".into(),
        regex: true,
        ..Query::default()
    };
    let origin = Hit {
        tab: 2,
        range: 0..0,
    };
    let Outcome::Found { hit: Some(hit) } =
        run_search(&query, Command::Next, documents(), &origin, true, "").unwrap()
    else {
        panic!("expected two empty matches");
    };
    assert_eq!(
        hit,
        Hit {
            tab: 1,
            range: 0..0
        }
    );
}

#[test]
fn replaces_captures_and_treats_literal_replacements_literally() {
    let query = Query {
        text: r"код=(\d+)".into(),
        regex: true,
        ..Query::default()
    };
    let regex = compile(&query).unwrap();
    let Outcome::Replaced { text, count: 2, .. } = replace(
        &regex,
        true,
        "код=17, код=24",
        0..0,
        "[$1]",
        true,
        &WorkBudget::new(
            &AtomicBool::new(false),
            Instant::now() + std::time::Duration::from_secs(5),
        ),
    )
    .unwrap() else {
        panic!("expected two replacements");
    };
    assert_eq!(text, "[17], [24]");

    let Outcome::Replaced {
        range,
        text,
        count: 1,
    } = replace(
        &Regex::new("кот").unwrap(),
        false,
        "кот кот",
        7..13,
        "$1",
        false,
        &WorkBudget::new(
            &AtomicBool::new(false),
            Instant::now() + std::time::Duration::from_secs(5),
        ),
    )
    .unwrap()
    else {
        panic!("expected one replacement");
    };
    assert_eq!(range, 7..13);
    assert_eq!(text, "$1");
}

#[test]
fn resumes_backward_across_tabs_and_wraps_from_the_first_hit() {
    let documents = vec![
        Source {
            tab: 9,
            text: Rope::from("x"),
        },
        Source {
            tab: 3,
            text: Rope::from("xx"),
        },
    ];
    let query = Query {
        text: "x".into(),
        ..Query::default()
    };
    let origin = Hit {
        tab: 9,
        range: 0..1,
    };
    let Outcome::Found { hit } =
        run_search(&query, Command::Previous, documents, &origin, true, "").unwrap()
    else {
        panic!("expected a navigation result");
    };
    assert_eq!(
        hit,
        Some(Hit {
            tab: 3,
            range: 1..2
        })
    );
}

#[test]
fn verifies_literal_punctuation_case_sensitivity_and_no_match() {
    let query = Query {
        text: "а.б".into(),
        case_sensitive: true,
        ..Query::default()
    };
    let document = |text| {
        vec![Source {
            tab: 1,
            text: Rope::from(text),
        }]
    };
    let origin = Hit {
        tab: 1,
        range: 0..0,
    };
    let Outcome::Found { hit } = run_search(
        &query,
        Command::Next,
        document("А.б ахб а.б"),
        &origin,
        false,
        "",
    )
    .unwrap() else {
        panic!("expected a navigation result");
    };
    assert_eq!(
        hit,
        Some(Hit {
            tab: 1,
            range: 13..18
        })
    );
    assert!(matches!(
        run_search(
            &query,
            Command::Next,
            document("А.б ахб"),
            &origin,
            false,
            ""
        )
        .unwrap(),
        Outcome::Found { hit: None }
    ));
}

#[test]
fn rejects_invalid_queries_with_an_explicit_error() {
    assert!(compile(&Query::default()).unwrap_err().contains("Введите"));
    let invalid = Query {
        text: "(".into(),
        regex: true,
        ..Query::default()
    };
    assert!(
        compile(&invalid)
            .unwrap_err()
            .contains("Ошибка регулярного выражения")
    );
}

#[test]
fn next_search_stops_after_the_nearest_hit_and_previous_is_bounded() {
    let query = Query {
        text: r".*[^A-Z]|[A-Z]".into(),
        regex: true,
        case_sensitive: true,
        ..Query::default()
    };
    let source = || {
        vec![Source {
            tab: 1,
            text: Rope::from("A".repeat(12_000)),
        }]
    };
    let origin = Hit {
        tab: 1,
        range: 0..0,
    };

    let Outcome::Found { hit: Some(hit) } =
        run_search(&query, Command::Next, source(), &origin, false, "").unwrap()
    else {
        panic!("next search should return its first hit without counting the rest");
    };
    assert_eq!(hit.range, 0..1);

    let end = Hit {
        tab: 1,
        range: 12_000..12_000,
    };
    let error = run_search(&query, Command::Previous, source(), &end, false, "").unwrap_err();
    assert!(error.contains("лимит времени") || error.contains("10 000"));
}

#[test]
fn cursor_inside_a_match_advances_to_the_next_non_overlapping_match() {
    let query = Query {
        text: "a.c".into(),
        regex: true,
        ..Query::default()
    };
    let source = vec![Source {
        tab: 1,
        text: Rope::from("abc abc"),
    }];
    let origin = Hit {
        tab: 1,
        range: 1..1,
    };
    let Outcome::Found { hit: Some(hit) } =
        run_search(&query, Command::Next, source, &origin, false, "").unwrap()
    else {
        panic!("the next non-overlapping match should be selected");
    };
    assert_eq!(hit.range, 4..7);
}

#[test]
fn navigation_and_replacement_keep_non_overlapping_match_order() {
    let query = Query {
        text: "AA".into(),
        ..Query::default()
    };
    let origin = Hit {
        tab: 1,
        range: 1..1,
    };
    let source = || {
        vec![Source {
            tab: 1,
            text: Rope::from("AAAA"),
        }]
    };

    let Outcome::Found { hit: Some(hit) } =
        run_search(&query, Command::Next, source(), &origin, false, "").unwrap()
    else {
        panic!("expected the next non-overlapping match");
    };
    assert_eq!(hit.range, 2..4);

    let start = Hit {
        tab: 1,
        range: 0..0,
    };
    let Outcome::Found { hit: Some(hit) } =
        run_search(&query, Command::Previous, source(), &start, false, "").unwrap()
    else {
        panic!("expected the previous match after wrapping");
    };
    assert_eq!(hit.range, 2..4);

    let Outcome::Replaced { range, .. } =
        run_search(&query, Command::Replace, source(), &origin, false, "B").unwrap()
    else {
        panic!("expected the next non-overlapping match to be replaced");
    };
    assert_eq!(range, 2..4);
}

#[test]
fn resumed_navigation_advances_after_a_bounded_batch_and_reaches_the_cursor() {
    let query = Query {
        text: r".*[^A-Z]|[A-Z]".into(),
        regex: true,
        case_sensitive: true,
        ..Query::default()
    };
    let source = vec![Source {
        tab: 1,
        text: Rope::from("A".repeat(256)),
    }];
    let cancelled = AtomicBool::new(false);

    for cursor in [64, 192] {
        let origin = Hit {
            tab: 1,
            range: cursor..cursor,
        };
        let mut progress = None;
        let mut previous_boundary = 0;
        let mut found = None;

        for _ in 0..8 {
            let work = WorkBudget::with_check_limit(&cancelled, 40);
            let outcome = run(
                &query,
                Command::Next,
                source.clone(),
                &origin,
                "",
                &work,
                NavigationStart {
                    skip_current: false,
                    progress: progress.take(),
                },
            )
            .unwrap();
            match outcome {
                Outcome::Incomplete {
                    progress: next_progress,
                } => {
                    assert!(next_progress.boundary > previous_boundary);
                    previous_boundary = next_progress.boundary;
                    progress = Some(next_progress);
                }
                Outcome::Found { hit: Some(hit) } => {
                    found = Some(hit);
                    break;
                }
                other => panic!("unexpected navigation outcome {other:?}"),
            }
        }

        let found = found.expect("retry must reach the requested position");
        assert_eq!(found.range, cursor..cursor + 1);
        let next = run(
            &query,
            Command::Next,
            source.clone(),
            &found,
            "",
            &WorkBudget::with_check_limit(&cancelled, 4),
            NavigationStart {
                skip_current: true,
                progress: Some(NavigationProgress::after_hit(
                    found.tab,
                    found.range.clone(),
                )),
            },
        )
        .unwrap();
        let Outcome::Found { hit: Some(next) } = next else {
            panic!("next search must use the verified match boundary");
        };
        assert_eq!(next.range, cursor + 1..cursor + 2);
    }
}

#[test]
fn resumable_empty_and_nonempty_matches_follow_the_iterator_sequence() {
    let regex = Regex::new("a|").unwrap();
    let text = "a💙aa";
    let expected: Vec<_> = regex
        .find_iter(text)
        .map(|matched| matched.range())
        .collect();
    let mut progress = NavigationProgress {
        tab: 1,
        boundary: 0,
        last_match: None,
        wrapped: false,
    };
    let cancelled = AtomicBool::new(false);
    let work = WorkBudget::new(
        &cancelled,
        Instant::now() + std::time::Duration::from_secs(5),
    );
    let mut actual = Vec::new();

    loop {
        match scan_forward(&regex, text, &mut progress, 0, None, &work) {
            ForwardScan::Found(hit) => actual.push(hit.range),
            ForwardScan::Complete => break,
            ForwardScan::Incomplete | ForwardScan::Cancelled => {
                panic!("the sequence should finish within the test budget")
            }
        }
    }

    assert_eq!(actual, expected);
}

#[test]
fn anchored_regex_search_keeps_the_original_document_boundaries() {
    let query = Query {
        text: "^".into(),
        regex: true,
        ..Query::default()
    };
    let source = vec![Source {
        tab: 1,
        text: Rope::from("first\nsecond"),
    }];
    let origin = Hit {
        tab: 1,
        range: 3..3,
    };
    let Outcome::Found { hit: Some(hit) } =
        run_search(&query, Command::Next, source, &origin, false, "").unwrap()
    else {
        panic!("document start should be the only match");
    };
    assert_eq!(hit.range, 0..0);
}

#[test]
fn cancelled_navigation_returns_without_a_match() {
    let query = Query {
        text: "x".into(),
        ..Query::default()
    };
    let source = vec![Source {
        tab: 1,
        text: Rope::from("x"),
    }];
    let origin = Hit {
        tab: 1,
        range: 0..0,
    };
    let cancelled = AtomicBool::new(true);

    assert!(matches!(
        run(
            &query,
            Command::Next,
            source,
            &origin,
            "",
            &WorkBudget::new(
                &cancelled,
                Instant::now() + std::time::Duration::from_secs(5),
            ),
            NavigationStart {
                skip_current: false,
                progress: None,
            },
        )
        .unwrap(),
        Outcome::Cancelled
    ));
}

#[test]
fn navigation_stops_when_its_work_deadline_has_expired() {
    let query = Query {
        text: "x".into(),
        ..Query::default()
    };
    let source = vec![Source {
        tab: 1,
        text: Rope::from("x"),
    }];
    let origin = Hit {
        tab: 1,
        range: 0..0,
    };
    let error = run(
        &query,
        Command::Next,
        source,
        &origin,
        "",
        &WorkBudget::new(
            &AtomicBool::new(false),
            Instant::now() - std::time::Duration::from_millis(1),
        ),
        NavigationStart {
            skip_current: false,
            progress: None,
        },
    )
    .unwrap_err();

    assert!(error.contains("400 мс"));
}

#[test]
fn replaces_only_the_active_document_even_if_other_sources_are_present() {
    let query = Query {
        text: "кот".into(),
        all_tabs: true,
        ..Query::default()
    };
    let matching = vec![
        Source {
            tab: 3,
            text: Rope::from("кот кот"),
        },
        Source {
            tab: 1,
            text: Rope::from("мой кот"),
        },
    ];
    let origin = Hit {
        tab: 1,
        range: 0..0,
    };
    let Outcome::Replaced { range, text, count } =
        run_search(&query, Command::ReplaceAll, matching, &origin, false, "пёс").unwrap()
    else {
        panic!("expected a replacement");
    };
    assert_eq!(range, 0.."мой кот".len());
    assert_eq!(text, "мой пёс");
    assert_eq!(count, 1);
}

#[test]
fn zero_width_replacements_preserve_unicode_and_insert_once_per_line() {
    let regex = Regex::new("(?m)^").unwrap();
    let Outcome::Replaced { text, count, .. } = replace(
        &regex,
        true,
        "ёж\nкот",
        0..0,
        "> ",
        true,
        &WorkBudget::new(
            &AtomicBool::new(false),
            Instant::now() + std::time::Duration::from_secs(5),
        ),
    )
    .unwrap() else {
        panic!("expected a replacement");
    };
    assert_eq!(text, "> ёж\n> кот");
    assert_eq!(count, 2);
}
