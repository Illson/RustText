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
fn registration_plan_lists_log_text_json_and_xml_types() {
    assert_eq!(
        ASSOCIATIONS,
        [
            (".log", "RustText.Log"),
            (".txt", "RustText.Text"),
            (".json", "RustText.Json"),
            (".xml", "RustText.Xml")
        ]
    );
    assert_eq!(REGISTERED_APP_NAME, "RustText");
    assert_eq!(CAPABILITIES_PATH, r"Software\RustText\Capabilities");
}

#[test]
fn context_menu_plan_is_scoped_idempotent_and_quotes_both_paths() {
    let executable = r"C:\Program Files\RustText\rusttext.exe";
    let plan = RegistrationPlan::new(executable);
    assert_eq!(
        plan.context_menu,
        RegistrationPlan::new(executable).context_menu
    );
    assert_eq!(plan.context_menu.len(), 8);
    for (index, (extension, _)) in ASSOCIATIONS.iter().enumerate() {
        let verb = format!(r"Software\Classes\SystemFileAssociations\{extension}\shell\RustText");
        assert_eq!(
            plan.context_menu[index * 2],
            (verb.clone(), "Открыть в RustText".to_owned())
        );
        assert_eq!(
            plan.context_menu[index * 2 + 1],
            (
                format!(r"{verb}\command"),
                r#""C:\Program Files\RustText\rusttext.exe" "%1""#.to_owned()
            )
        );
    }
    assert!(
        plan.context_menu
            .iter()
            .all(|(path, _)| !path.contains("UserChoice") && !path.contains("OpenWithProgids"))
    );
}
