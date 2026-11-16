use super::*;

#[test]
fn increments_integer_placeholders_for_each_generated_row() {
    assert_eq!(
        generate("strokna {int_from_5}", 4).unwrap(),
        "strokna 5\nstrokna 6\nstrokna 7\nstrokna 8"
    );
}

#[test]
fn expands_multiple_placeholders_per_row() {
    assert_eq!(
        generate("{int_from_-2}:{int_from_5}:{int_from_-2}", 3).unwrap(),
        "-2:5:-2\n-1:6:-1\n0:7:0"
    );
}

#[test]
fn expands_placeholders_inside_json_braces() {
    let generated = generate(r#"{"id":"{guid}","n":{int_from_5}}"#, 2).unwrap();
    let values = generated
        .lines()
        .map(|line| {
            let value: serde_json::Value = serde_json::from_str(line).unwrap();
            assert!(
                Uuid::parse_str(value["id"].as_str().unwrap())
                    .unwrap()
                    .get_version_num()
                    == 4
            );
            value["n"].as_i64().unwrap()
        })
        .collect::<Vec<_>>();
    assert_eq!(values, [5, 6]);
}

#[test]
fn expands_guid_and_random_strings_with_requested_lengths() {
    let output = generate("{guid} {rand_string} {rand_string_4}", 2).unwrap();
    for row in output.lines() {
        let mut fields = row.split(' ');
        let guid = fields.next().unwrap();
        let default_random = fields.next().unwrap();
        let custom_random = fields.next().unwrap();
        assert!(Uuid::parse_str(guid).unwrap().get_version_num() == 4);
        assert_eq!(default_random.len(), DEFAULT_RANDOM_STRING_LENGTH);
        assert_eq!(custom_random.len(), 4);
        assert!(fields.next().is_none());
        assert!(
            default_random
                .bytes()
                .chain(custom_random.bytes())
                .all(|byte| byte.is_ascii_alphanumeric())
        );
    }
}

#[test]
fn repeats_multiline_templates_and_preserves_unknown_placeholders() {
    assert_eq!(
        generate("a {int_from_1}\nb {other}", 2).unwrap(),
        "a 1\nb {other}\na 2\nb {other}"
    );
}

#[test]
fn rejects_zero_rows_overflow_and_oversized_output() {
    assert_eq!(generate("x", 0), Err(TemplateError::NoRows));
    assert_eq!(
        generate("x", MAX_GENERATED_ROWS + 1),
        Err(TemplateError::TooManyRows)
    );
    assert_eq!(
        generate("{int_from_9223372036854775807}", 2),
        Err(TemplateError::IntegerOverflow)
    );
    assert_eq!(
        generate("{rand_string_8388609}", 1),
        Err(TemplateError::OutputTooLarge)
    );
}
