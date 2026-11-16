use super::{
    ASSOCIATIONS, CAPABILITIES_PATH, REGISTERED_APP_NAME, RegistrationPlan, quote_windows_argument,
};

#[test]
fn registration_plan_quotes_executable_and_opened_file() {
    let plan = RegistrationPlan::new(r"C:\Users\Test User\RustText\rusttext.exe");

    assert_eq!(plan.executable_name, "rusttext.exe");
    assert_eq!(
        plan.open_command,
        r#""C:\Users\Test User\RustText\rusttext.exe" "%1""#
    );
}

#[test]
fn windows_argument_quoting_escapes_embedded_quotes_and_trailing_slashes() {
    assert_eq!(
        quote_windows_argument(r#"C:\logs\"archive\""#),
        r#""C:\logs\\\"archive\\\"""#
    );
    assert_eq!(quote_windows_argument(r"C:\folder\"), r#""C:\folder\\""#);
}

#[test]
fn registration_plan_lists_log_text_and_json_types() {
    assert_eq!(
        ASSOCIATIONS,
        [
            (".log", "RustText.Log"),
            (".txt", "RustText.Text"),
            (".json", "RustText.Json")
        ]
    );
    assert_eq!(REGISTERED_APP_NAME, "RustText");
    assert_eq!(CAPABILITIES_PATH, r"Software\RustText\Capabilities");
}
