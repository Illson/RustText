use super::*;

#[test]
fn compares_unique_lines_and_preserves_first_appearance() {
    let result = compare("third\ncommon\nfirst\ncommon", "other\ncommon\nother\nlast");
    assert_eq!(result.common, ["common"]);
    assert_eq!(result.left_only, ["third", "first"]);
    assert_eq!(result.right_only, ["other", "last"]);
}

#[test]
fn compares_exact_text_without_trimming_or_folding_case() {
    let result = compare("A\n a\nа\n", "a\na \nа");
    assert_eq!(result.common, ["а"]);
    assert_eq!(result.left_only, ["A", " a"]);
    assert_eq!(result.right_only, ["a", "a "]);
}

#[test]
fn normalizes_line_endings_and_keeps_explicit_empty_lines() {
    let result = compare("one\r\n\r\ntwo\r\n", "two\n\none\n");
    assert_eq!(result.common, ["one", "", "two"]);
    assert!(result.left_only.is_empty());
    assert!(result.right_only.is_empty());
}

#[test]
fn compares_cr_only_and_mixed_endings_without_extra_crlf_rows() {
    let result = compare("one\r\rtwo\r", "two\r\none\n\n");
    assert_eq!(result.common, ["one", "", "two"]);
    assert!(result.left_only.is_empty());
    assert!(result.right_only.is_empty());
}

#[test]
fn empty_inputs_have_no_phantom_row() {
    assert_eq!(compare("", ""), ComparedLists::default());
    assert_eq!(compare("", "one\n").right_only, ["one"]);
}
