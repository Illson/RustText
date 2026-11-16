use super::*;

#[test]
fn detects_external_changes_before_replacing_a_file() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("заметка.txt");
    fs::write(&path, "Черновик").unwrap();
    let old = open(&path, false).unwrap();
    fs::write(&path, "Записано другой программой").unwrap();

    assert!(save(old.target, old.format, "Правки").is_err());
    assert_eq!(
        fs::read_to_string(&path).unwrap(),
        "Записано другой программой"
    );
}

#[test]
fn saves_new_file_and_allows_repeated_saves() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("заметка.txt");
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let target = save(target, TextFormat::default(), "Привет").unwrap();
    save(target, TextFormat::default(), "Привет снова").unwrap();

    assert_eq!(fs::read_to_string(&path).unwrap(), "Привет снова");
}

#[test]
fn rejects_files_above_the_regular_editor_limit() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("oversize.txt");
    let file = File::create(&path).unwrap();
    file.set_len(MAX_EDITOR_BYTES + 1).unwrap();
    drop(file);
    let error = open(&path, false)
        .err()
        .expect("oversize file must be rejected");
    assert_eq!(error.kind(), io::ErrorKind::InvalidData);
    assert!(error.to_string().contains("50 МиБ"));
}

#[test]
fn save_as_cannot_overwrite_a_file_created_after_the_picker_returns() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("new.txt");
    let target = FileTarget::from_picker(path.clone()).unwrap();
    fs::write(&path, "written by another program").unwrap();
    assert!(save(target, TextFormat::default(), "editor draft").is_err());
    assert_eq!(
        fs::read_to_string(path).unwrap(),
        "written by another program"
    );
}
