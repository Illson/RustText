use super::*;

#[test]
fn pid_accepts_positive_decimal_u32() {
    assert_eq!(parse_pid("1").unwrap().get(), 1);
    assert_eq!(parse_pid("0042").unwrap().get(), 42);
    assert_eq!(parse_pid("4294967295").unwrap().get(), u32::MAX);
}

#[test]
fn pid_rejects_empty_non_decimal_and_out_of_range_values() {
    for invalid in ["", "0", "+42", "-1", "12a", "0x2a", "4294967296"] {
        assert!(parse_pid(invalid).is_err(), "{invalid:?}");
    }
}

#[test]
fn title_matches_a_case_insensitive_unicode_substring() {
    assert!(title_matches("Отчёт за ноябрь.txt", "за ноябрь"));
    assert!(title_matches("ОТЧЁТ за ноябрь.txt", "отчёт"));
    assert!(!title_matches("Отчёт за ноябрь.txt", "декабрь"));
}

#[test]
fn rejects_empty_query_before_enumerating_windows() {
    for by_pid in [true, false] {
        assert!(find_windows("  \t", by_pid).is_err());
    }
}
