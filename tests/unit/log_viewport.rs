use std::{fs, io::Write as _};

use super::{
    Destination, LineIndex, LogBookmark, LogReader, PageOptions, PreviousPage, append_page,
    assemble_page_text, read_page,
};
use crate::log_file::observe;

#[test]
fn copied_long_json_line_omits_editor_fragment_breaks_but_keeps_log_newlines() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("long-json.log");
    let json = format!(r#"{{"value":"{}"}}"#, "x".repeat(5_000));
    let contents = format!("{json}\nnext");
    fs::write(&path, &contents).unwrap();

    let mut reader = LogReader::open(&path, None).unwrap();
    let page = reader
        .render_page(
            0,
            PageOptions {
                max_rows: 8,
                max_row_bytes: 2048,
            },
        )
        .unwrap();
    let editor_text = assemble_page_text(&page);

    assert!(page.rows.len() > 1);
    assert_eq!(editor_text, contents);
    let (copied_json, following_line) = editor_text.split_once('\n').unwrap();
    assert_eq!(
        serde_json::from_str::<serde_json::Value>(copied_json).unwrap()["value"],
        "x".repeat(5_000)
    );
    assert_eq!(following_line, "next");
}

#[test]
fn copied_utf8_fragments_preserve_characters_and_real_line_breaks() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("utf8-fragments.log");
    let contents = format!("{}\nnext", "🙂".repeat(1_200));
    fs::write(&path, &contents).unwrap();

    let mut reader = LogReader::open(&path, None).unwrap();
    let page = reader
        .render_page(
            0,
            PageOptions {
                max_rows: 8,
                max_row_bytes: 2048,
            },
        )
        .unwrap();
    let editor_text = assemble_page_text(&page);

    assert_eq!(editor_text, contents);
}

#[test]
fn append_reuses_complete_rows_and_extends_one_partial_row() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("append.log");
    fs::write(&path, b"old\npartial").unwrap();
    let options = PageOptions {
        max_rows: 4,
        max_row_bytes: 128,
    };
    let mut reader = LogReader::open(&path, None).unwrap();
    let before = reader.render_page(0, options).unwrap();
    assert!(before.eof);
    assert!(!before.rows[1].terminated);

    let mut writer = fs::OpenOptions::new().append(true).open(&path).unwrap();
    writer.write_all(b"-rest\nnew\n").unwrap();
    drop(writer);

    let mut reader = LogReader::open(&path, None).unwrap();
    let after = append_page(&mut reader, before.clone(), options, false).unwrap();
    assert_eq!(
        after
            .rows
            .iter()
            .map(|row| row.text.as_str())
            .collect::<Vec<_>>(),
        ["old", "partial-rest", "new"]
    );
    assert_eq!(after.rows[0].start, before.rows[0].start);
    assert!(after.eof);
}

#[test]
fn following_keeps_the_final_rows_and_fixed_mode_keeps_the_start() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("append.log");
    fs::write(&path, b"one\ntwo\n").unwrap();
    let options = PageOptions {
        max_rows: 2,
        max_row_bytes: 128,
    };
    let mut reader = LogReader::open(&path, None).unwrap();
    let before = reader.render_page(0, options).unwrap();
    assert!(before.eof);

    fs::write(&path, b"one\ntwo\nthree\nfour\nfive\n").unwrap();
    let mut reader = LogReader::open(&path, None).unwrap();
    let fixed = append_page(&mut reader, before.clone(), options, false).unwrap();
    let followed = append_page(&mut reader, before, options, true).unwrap();
    assert_eq!(fixed.rows[0].text, "one");
    assert!(!fixed.eof);
    assert_eq!(followed.rows[0].text, "four");
    assert_eq!(followed.rows[1].text, "five");
    assert!(followed.eof);
}

#[test]
fn previous_page_at_hidden_boundary_ends_where_the_current_page_starts() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("hidden.log");
    fs::write(&path, b"zero\none\ntwo\nthree\n").unwrap();
    let bookmark = LogBookmark {
        hidden: 9,
        offset: 13,
        ..LogBookmark::default()
    };
    let previous = read_page(
        &path,
        None,
        &bookmark,
        Destination::Before(bookmark.offset),
        PageOptions {
            max_rows: 4,
            max_row_bytes: 128,
        },
        PreviousPage {
            observation: None,
            page: None,
        },
        LineIndex::default(),
    )
    .unwrap();

    assert_eq!(previous.start, bookmark.hidden);
    assert_eq!(previous.page.next_offset, bookmark.offset);
    assert_eq!(previous.page.rows.len(), 1);
    assert_eq!(previous.page.rows[0].text, "two");
}

#[test]
fn refreshing_an_in_place_rewrite_clears_the_hidden_boundary() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("rewrite.log");
    fs::write(&path, b"old\nrow\n").unwrap();
    let (observation, _) = observe(&path, None).unwrap();
    let bookmark = LogBookmark {
        identity: Some(observation.stamp.identity),
        size: observation.stamp.len,
        hidden: 4,
        offset: 4,
        ..LogBookmark::default()
    };

    fs::write(&path, b"new\nrow\n").unwrap();
    let refreshed = read_page(
        &path,
        None,
        &bookmark,
        Destination::Restore,
        PageOptions::default(),
        PreviousPage {
            observation: Some(observation),
            page: None,
        },
        LineIndex::default(),
    )
    .unwrap();
    assert!(refreshed.reset);
    assert_eq!((refreshed.start, refreshed.hidden), (0, 0));
    assert_eq!(refreshed.page.rows[0].text, "new");
}
