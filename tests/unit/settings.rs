use super::*;

#[test]
fn untitled_tab_name_uses_first_line_up_to_char_limit_and_falls_back_when_empty() {
    assert_eq!(
        untitled_tab_name("", DEFAULT_UNTITLED_TAB_LIMIT),
        "Без имени"
    );
    assert_eq!(
        untitled_tab_name("   \r\nвторая строка", DEFAULT_UNTITLED_TAB_LIMIT),
        "Без имени"
    );
    assert_eq!(
        untitled_tab_name("  привет мир  ", DEFAULT_UNTITLED_TAB_LIMIT),
        "привет мир"
    );
    assert_eq!(
        untitled_tab_name("0123456789abcdef", DEFAULT_UNTITLED_TAB_LIMIT),
        "0123456789"
    );
    assert_eq!(untitled_tab_name("абвгдеёжзийкл", 5), "абвгд");
    let rope = Rope::from("Первая длинная строка\nВторая строка");
    assert_eq!(untitled_tab_name_rope(&rope, 10), "Первая дли");
}

#[test]
fn settings_parse_and_roundtrip_clamp_untitled_tab_limit() {
    assert_eq!(parse_untitled_tab_limit(" 10 ").unwrap(), 10);
    assert_eq!(parse_untitled_tab_limit("1").unwrap(), 1);
    assert_eq!(parse_untitled_tab_limit("200").unwrap(), 200);
    assert!(parse_untitled_tab_limit("0").is_err());
    assert!(parse_untitled_tab_limit("201").is_err());
    assert!(parse_untitled_tab_limit("-5").is_err());
    assert!(parse_untitled_tab_limit("abc").is_err());

    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    write_settings(
        &path,
        &Settings {
            untitled_tab_limit: 24,
        },
    )
    .unwrap();
    assert_eq!(
        read_settings(&path).unwrap(),
        Settings {
            untitled_tab_limit: 24
        }
    );

    fs::write(&path, r#"{"untitled_tab_limit": 0}"#).unwrap();
    assert_eq!(
        read_settings(&path).unwrap(),
        Settings {
            untitled_tab_limit: MIN_UNTITLED_TAB_LIMIT
        }
    );
}
