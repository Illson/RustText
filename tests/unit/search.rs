use super::*;

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
    let Outcome::Found {
        hit: Some(hit),
        total: 2,
    } = run(&query, Command::Next, documents(), &origin, true, "").unwrap()
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
    let Outcome::Found {
        hit: Some(hit),
        total: 2,
    } = run(&query, Command::Next, documents(), &origin, true, "").unwrap()
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
    let Outcome::Replaced { text, count: 2, .. } =
        replace(&regex, true, "код=17, код=24", 0..0, "[$1]", true)
    else {
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
    )
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
    let Outcome::Found { hit, total } =
        run(&query, Command::Previous, documents, &origin, true, "").unwrap()
    else {
        panic!("expected a navigation result");
    };
    assert_eq!(total, 3);
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
    let Outcome::Found { hit, total } = run(
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
    assert_eq!(total, 1);
    assert_eq!(
        hit,
        Some(Hit {
            tab: 1,
            range: 13..18
        })
    );
    assert!(matches!(
        run(
            &query,
            Command::Next,
            document("А.б ахб"),
            &origin,
            false,
            ""
        )
        .unwrap(),
        Outcome::Found {
            hit: None,
            total: 0
        }
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
        run(&query, Command::ReplaceAll, matching, &origin, false, "пёс").unwrap()
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
    let Outcome::Replaced { text, count, .. } = replace(&regex, true, "ёж\nкот", 0..0, "> ", true)
    else {
        panic!("expected a replacement");
    };
    assert_eq!(text, "> ёж\n> кот");
    assert_eq!(count, 2);
}
