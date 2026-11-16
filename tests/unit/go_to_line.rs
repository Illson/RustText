use super::line_index;

#[test]
fn go_to_line_accepts_only_existing_one_based_lines() {
    assert_eq!(line_index("1", 80), Some(0));
    assert_eq!(line_index(" 80 ", 80), Some(79));

    for invalid in ["", "0", "-1", "81", "1.5", "4294967296"] {
        assert_eq!(line_index(invalid, 80), None, "{invalid:?}");
    }
}

#[test]
fn accepts_only_the_first_line_in_an_empty_editors_single_row() {
    assert_eq!(line_index(" 1 ", 1), Some(0));
    assert_eq!(line_index("2", 1), None);
    assert_eq!(line_index(" ", 1), None);
}
