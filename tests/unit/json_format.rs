use super::format;

#[test]
fn formats_without_rewriting_lexemes_or_reordering_keys() {
    let source =
        r#"{"z":123456789012345678901234567890,"a":1e9000,"z":"\"/ёж\u2603","arr":[{},[],-0.00]}"#;
    assert_eq!(
        format(source, "\n").unwrap(),
        concat!(
            "{\n",
            "  \"z\": 123456789012345678901234567890,\n",
            "  \"a\": 1e9000,\n",
            "  \"z\": \"\\\"/ёж\\u2603\",\n",
            "  \"arr\": [\n    {},\n    [],\n    -0.00\n  ]\n}"
        )
    );
}

#[test]
fn reports_invalid_json_with_a_location() {
    let error = format("{\n \"a\": [1,]\n}", "\n").unwrap_err();
    assert!(error.contains("строка 2, столбец 10"), "{error}");
    assert!(format(r#"{"a":"\x00"}"#, "\n").is_err());
    assert!(format("01", "\n").is_err());
    assert!(format("{} false", "\n").is_err());
}

#[test]
fn retains_crlf_and_formats_empty_containers_and_root_values() {
    assert_eq!(
        format(" { \"empty\": [ ], \"object\": { } }\r\n", "\r\n").unwrap(),
        "{\r\n  \"empty\": [],\r\n  \"object\": {}\r\n}\r\n"
    );
    assert_eq!(format("  -0.00e+03 ", "\n").unwrap(), "-0.00e+03");
    assert_eq!(format(r#" "{[ , : ]}" "#, "\n").unwrap(), r#""{[ , : ]}""#);
    assert_eq!(format("true", "\n").unwrap(), "true");
}

#[test]
fn rejects_empty_input_and_reports_unterminated_strings() {
    assert!(
        format("", "\n")
            .unwrap_err()
            .contains("строка 1, столбец 1")
    );
    assert!(
        format("{\n\"z\": \"незакрытая", "\n")
            .unwrap_err()
            .contains("строка 2")
    );
}
