use std::path::Path;

use crate::workspace::json_validation::{self, Status, Validation};

#[test]
fn accepts_valid_json_numbers_used_by_the_formatter() {
    assert_eq!(
        json_validation::validate(r#"{"value": 1e999}"#),
        Status::Valid
    );
}

#[test]
fn reports_a_multiline_syntax_error_and_editor_range() {
    let Status::Invalid(problem) = json_validation::validate("{\n  \"value\":,\n}") else {
        panic!("invalid JSON should report a problem");
    };

    assert_eq!(problem.line, 2);
    assert!(problem.column > 1);
    assert_eq!(problem.range.start.line, 1);
    assert_eq!(problem.range.end.line, 1);
    assert!(problem.range.end.character >= problem.range.start.character);
}

#[test]
fn converts_utf8_byte_offsets_to_character_and_utf16_columns() {
    let source = r#"{"ключ😀":,}"#;
    let Status::Invalid(problem) = json_validation::validate(source) else {
        panic!("invalid JSON should report a problem");
    };

    let comma = source.find(',').expect("syntax error comma exists");
    let expected_column = source[..comma].chars().count() + 1;
    let expected_utf16_column = source[..comma].encode_utf16().count() as u32;
    assert_eq!(problem.column, expected_column);
    assert_eq!(problem.range.start.character, expected_utf16_column);
}

#[test]
fn recognizes_json_extension_without_case_sensitivity() {
    assert!(json_validation::is_json_file(Path::new("events.JSON")));
    assert!(!json_validation::is_json_file(Path::new("events.jsonl")));
}

#[test]
fn recognizes_json_content_in_text_files_and_scratch_documents() {
    for source in [
        "{}",
        " [1, 2]",
        "\u{feff}  {\"a\": 1}",
        " \n\u{feff} []",
        "{malformed",
    ] {
        assert!(json_validation::should_validate(
            Some(Path::new("data.txt")),
            source.chars()
        ));
        assert!(json_validation::should_validate(None, source.chars()));
    }
}

#[test]
fn json_extension_keeps_primitive_and_malformed_content_eligible() {
    for source in ["true", "123", "\"text\"", "null", "", "broken"] {
        assert!(json_validation::should_validate(
            Some(Path::new("data.JSON")),
            source.chars()
        ));
        assert!(!json_validation::should_validate(
            Some(Path::new("data.txt")),
            source.chars()
        ));
        assert!(!json_validation::should_validate(None, source.chars()));
    }
}

#[test]
fn ignores_plain_text_xml_and_empty_content() {
    for source in [
        "",
        " \r\n",
        "\u{feff}",
        "ordinary log line",
        "<root />",
        "note {value}",
    ] {
        assert!(!json_validation::should_validate(None, source.chars()));
    }
}

#[test]
fn unchanged_revision_keeps_pending_work_and_completed_results() {
    for result in [
        Status::Valid,
        json_validation::validate("{broken"),
        Status::TooLarge,
    ] {
        let mut validation = Validation::default();
        assert!(validation.set_active_revision(Some(1)));
        let generation = validation.generation;
        assert!(!validation.set_active_revision(Some(1)));
        assert!(validation.complete(1, generation, result.clone()));
        assert!(!validation.set_active_revision(Some(1)));
        assert_eq!(validation.status(), Some(&result));
    }
}

#[test]
fn reenabling_validation_restores_the_result_until_text_changes() {
    let mut validation = Validation::default();
    let error = json_validation::validate("{broken");
    assert!(validation.set_active_revision(Some(1)));
    assert!(validation.complete(1, validation.generation, error.clone()));

    assert!(validation.set_active_revision(None));
    assert_eq!(validation.status(), None);
    assert!(validation.set_active_revision(Some(1)));
    assert_eq!(validation.status(), Some(&error));

    assert!(validation.set_active_revision(None));
    assert!(validation.set_active_revision(Some(2)));
    assert_eq!(validation.status(), None);
    assert!(validation.complete(2, validation.generation, Status::Valid));
    assert_eq!(validation.status(), Some(&Status::Valid));
}

#[test]
fn dismissed_and_superseded_validation_tasks_cannot_apply() {
    let mut validation = Validation::default();
    assert!(validation.set_active_revision(Some(1)));
    let original_generation = validation.generation;

    assert!(validation.set_active_revision(None));
    assert!(!validation.complete(1, original_generation, Status::Valid));
    assert!(validation.set_active_revision(Some(1)));
    assert!(!validation.complete(1, original_generation, Status::Valid));

    let generation = validation.generation;
    assert!(validation.set_active_revision(Some(2)));
    assert!(!validation.complete(1, generation, Status::Valid));
    assert_eq!(validation.status(), None);
}

#[test]
fn oversized_json_rope_reports_too_large() {
    let oversized = gpui_kit::component::input::Rope::from(
        format!(
            "[{}]",
            "1,".repeat(crate::json_format::MAX_JSON_BYTES / 2 + 1)
        )
        .as_str(),
    );
    assert_eq!(
        json_validation::validate_rope(&oversized),
        json_validation::Status::TooLarge
    );
}
