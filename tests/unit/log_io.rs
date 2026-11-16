use super::*;
use std::io::Write;

fn reader_with(
    contents: &[u8],
    encoding: Option<TextEncoding>,
) -> (tempfile::NamedTempFile, LogReader) {
    let mut file = tempfile::NamedTempFile::new().unwrap();
    file.write_all(contents).unwrap();
    file.flush().unwrap();
    let reader = LogReader::open(file.path(), encoding).unwrap();
    (file, reader)
}

#[test]
fn detects_utf_boms_and_decodes_rows() {
    let (file, mut reader) = reader_with(b"\xef\xbb\xbfhello\r\nworld", None);
    assert!(reader.encoding() == TextEncoding::Utf8Bom);
    let page = reader.render_page(0, PageOptions::default()).unwrap();
    assert_eq!(page.rows[0].text, "hello");
    assert!(page.rows[0].terminated);
    assert_eq!(page.rows[1].text, "world");
    assert!(page.eof);
    drop(file);

    let encoded = [0xff, 0xfe, b'h', 0, b'i', 0, 10, 0];
    let (_file, mut reader) = reader_with(&encoded, None);
    assert!(reader.encoding() == TextEncoding::Utf16Le);
    assert_eq!(
        reader.render_page(0, PageOptions::default()).unwrap().rows[0].text,
        "hi"
    );
}

#[test]
fn renders_long_lines_as_bounded_fragments_with_offsets() {
    let contents = format!("{}\nlast", "я".repeat(80));
    let (_file, mut reader) = reader_with(contents.as_bytes(), None);
    let opts = PageOptions {
        max_rows: 10,
        max_row_bytes: 16,
    };
    let first = reader.render_page(0, opts).unwrap();
    assert_eq!(first.rows.len(), 10);
    assert!(first.rows[0].continues);
    assert!(first.rows.iter().all(|row| row.text.len() <= 16));
    assert_eq!(first.rows[1].start, first.rows[0].end);
    assert!(first.next_offset < reader.size());
}

#[test]
fn utf8_fragment_and_crlf_boundaries_preserve_characters_and_rows() {
    let (_file, mut reader) = reader_with("ab🙂z\r\nnext".as_bytes(), None);
    let page = reader
        .render_page(
            0,
            PageOptions {
                max_rows: 4,
                max_row_bytes: 5,
            },
        )
        .unwrap();
    assert_eq!(page.rows[0].text, "ab");
    assert!(page.rows[0].continues);
    assert_eq!(page.rows[1].text, "🙂z");
    assert!(page.rows[1].terminated);
    assert_eq!(page.rows[2].text, "next");
}

#[test]
fn line_navigation_and_incremental_sparse_index_work() {
    let (_file, mut reader) = reader_with(b"a\nb\nc\nd\ne", None);
    assert_eq!(reader.line_start_at(4).unwrap(), 4);
    assert_eq!(reader.line_start_at(3).unwrap(), 2);

    let cancel = AtomicBool::new(false);
    let consumed = reader.advance_index(3, &cancel).unwrap();
    assert_eq!(consumed, 3);
    assert_eq!(reader.index.scanned_to(), 3);
    reader.advance_index(100, &cancel).unwrap();
    assert_eq!(reader.index.line_count(), 5);
}

#[test]
fn sparse_index_counts_crlf_once_across_a_64k_boundary() {
    let mut contents = vec![b'x'; BLOCK_SIZE - 1];
    contents.extend_from_slice(b"\r\na\nb\n");
    let (_file, mut reader) = reader_with(&contents, None);
    let cancel = AtomicBool::new(false);

    assert_eq!(
        reader.advance_index(BLOCK_SIZE as u64, &cancel).unwrap(),
        BLOCK_SIZE as u64
    );
    assert_eq!(reader.line_index().line_count(), 1);
    reader.advance_index(16, &cancel).unwrap();

    assert_eq!(reader.line_index().line_count(), 4);
    assert_eq!(reader.line_index().entries()[1].line, 2);
    assert_eq!(
        reader.line_index().entries()[1].offset,
        (BLOCK_SIZE + 1) as u64
    );
}

#[test]
fn search_streams_lines_and_reports_work_limits_and_cancellation() {
    let (file, _reader) = reader_with("one\nКОД=17\nthree\nКОД=24".as_bytes(), None);
    let query = LogSearchQuery::regex(r"код=(\d+)", false);
    let page = search_page(
        file.path(),
        None,
        &query,
        0,
        1,
        SearchLimits::default(),
        &AtomicBool::new(false),
    )
    .unwrap();
    assert_eq!(page.hits.len(), 1);
    assert_eq!(page.hits[0].preview, "КОД=17");
    assert!(!page.complete);
    assert_eq!(page.next_offset, page.hits[0].line_end);

    let cancelled = AtomicBool::new(true);
    assert!(matches!(
        search_page(
            file.path(),
            None,
            &LogSearchQuery::literal("absent", true),
            0,
            10,
            SearchLimits::default(),
            &cancelled,
        ),
        Err(LogError::Cancelled)
    ));
}

#[test]
fn explicit_windows_1251_decodes_cyrillic() {
    let (_file, mut reader) = reader_with(
        &[0xcf, 0xf0, 0xe8, 0xe2, 0xe5, 0xf2, 10],
        Some(TextEncoding::Windows1251),
    );
    let page = reader.render_page(0, PageOptions::default()).unwrap();
    assert_eq!(page.rows[0].text, "Привет");
}

#[test]
fn search_batches_resume_on_the_next_physical_line() {
    let (file, _reader) = reader_with(b"111\n222\nhit\n", None);
    let query = LogSearchQuery::literal("hit", true);
    let limits = SearchLimits {
        max_scanned_bytes: 5,
        ..SearchLimits::default()
    };
    let first = search_page(
        file.path(),
        None,
        &query,
        0,
        10,
        limits,
        &AtomicBool::new(false),
    )
    .unwrap();
    assert!(first.work_limit_reached);
    assert_eq!(first.next_offset, 8);
    assert!(first.hits.is_empty());

    let second = search_page(
        file.path(),
        None,
        &query,
        first.next_offset,
        10,
        limits,
        &AtomicBool::new(false),
    )
    .unwrap();
    assert_eq!(second.hits.len(), 1);
    assert_eq!(second.hits[0].line_start, 8);
}

#[test]
fn page_before_returns_a_viewport_near_the_requested_offset() {
    let (_file, mut reader) = reader_with(b"one\ntwo\nthree\nfour\nfive\n", None);
    let page = reader
        .page_before(
            18,
            PageOptions {
                max_rows: 2,
                max_row_bytes: 64,
            },
            1024,
        )
        .unwrap();
    assert_eq!(page.rows.len(), 2);
    assert_eq!(page.rows[0].text, "two");
    assert_eq!(page.rows[1].text, "three");
}

#[test]
fn page_before_end_includes_an_unterminated_final_line() {
    let (_file, mut reader) = reader_with(b"one\ntwo\nthree\nfour\nfive", None);
    let size = reader.size();
    let page = reader
        .page_before(
            size,
            PageOptions {
                max_rows: 2,
                max_row_bytes: 64,
            },
            1024,
        )
        .unwrap();
    assert_eq!(page.rows.len(), 2);
    assert_eq!(page.rows[0].text, "four");
    assert_eq!(page.rows[1].text, "five");
    assert_eq!(page.next_offset, size);
}

#[test]
fn page_before_large_unterminated_line_returns_fragments_near_eof() {
    let contents = vec![b'x'; 3 * 1024 * 1024 + 37];
    let (_file, mut reader) = reader_with(&contents, None);
    let page = reader
        .page_before(
            reader.size(),
            PageOptions {
                max_rows: 2,
                max_row_bytes: 64,
            },
            2 * 1024 * 1024,
        )
        .unwrap();
    assert_eq!(page.rows.len(), 2);
    assert_eq!(page.next_offset, reader.size());
    assert!(page.rows[0].start >= reader.size() - 128);
    assert!(page.eof);
}

#[test]
fn page_before_many_long_lines_uses_byte_fallback_to_reach_end() {
    let mut contents = Vec::new();
    for _ in 0..10 {
        contents.extend(std::iter::repeat_n(b'x', 256 * 1024));
        contents.push(b'\n');
    }
    let (_file, mut reader) = reader_with(&contents, None);
    let size = reader.size();
    let page = reader
        .page_before(
            size,
            PageOptions {
                max_rows: 2,
                max_row_bytes: 64,
            },
            2 * 1024 * 1024,
        )
        .unwrap();
    assert_eq!(page.rows.len(), 2);
    assert_eq!(page.next_offset, size);
    assert!(page.rows[0].continues);
    assert!(page.rows[1].terminated);
}

#[test]
fn search_reports_unprocessable_long_lines_and_bad_patterns() {
    let (file, _reader) = reader_with(b"0123456789\n", None);
    let too_short = SearchLimits {
        max_line_bytes: 4,
        ..SearchLimits::default()
    };
    assert!(matches!(
        search_page(
            file.path(),
            None,
            &LogSearchQuery::literal("missing", true),
            0,
            5,
            too_short,
            &AtomicBool::new(false),
        ),
        Err(LogError::LineLimit { .. })
    ));
    assert!(matches!(
        search_page(
            file.path(),
            None,
            &LogSearchQuery::regex("[", true),
            0,
            5,
            SearchLimits::default(),
            &AtomicBool::new(false),
        ),
        Err(LogError::InvalidRegex(_))
    ));
}

#[test]
fn search_keeps_crlf_together_across_a_buffer_boundary() {
    let mut contents = vec![b'x'; BLOCK_SIZE * 2 - 1];
    contents.extend_from_slice(b"\r\nneedle\r\n");
    let (file, _reader) = reader_with(&contents, None);
    let result = search_page(
        file.path(),
        None,
        &LogSearchQuery::literal("needle", true),
        0,
        1,
        SearchLimits {
            max_scanned_bytes: 1024 * 1024,
            max_line_bytes: 1024 * 1024,
            ..SearchLimits::default()
        },
        &AtomicBool::new(false),
    )
    .unwrap();
    assert_eq!(result.hits.len(), 1);
    assert_eq!(result.hits[0].line_start, (BLOCK_SIZE * 2 + 1) as u64);
}
