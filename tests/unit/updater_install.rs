use super::*;

fn write_pe(path: &Path, machine: u16) {
    let mut bytes = vec![0_u8; 70];
    bytes[..2].copy_from_slice(b"MZ");
    bytes[60..64].copy_from_slice(&64_u32.to_le_bytes());
    bytes[64..68].copy_from_slice(b"PE\0\0");
    bytes[68..70].copy_from_slice(&machine.to_le_bytes());
    fs::write(path, bytes).unwrap();
}

#[test]
fn accepts_only_a_windows_x64_executable() {
    let directory = tempfile::tempdir().unwrap();
    let x64 = directory.path().join("x64.exe");
    write_pe(&x64, 0x8664);
    validate_executable(&x64).unwrap();

    let text = directory.path().join("text.exe");
    fs::write(&text, b"ordinary text").unwrap();
    assert_eq!(
        validate_executable(&text).unwrap_err().kind(),
        io::ErrorKind::InvalidData
    );

    let x86 = directory.path().join("x86.exe");
    write_pe(&x86, 0x014c);
    assert_eq!(
        validate_executable(&x86).unwrap_err().kind(),
        io::ErrorKind::InvalidData
    );
}

#[test]
fn rejects_an_out_of_file_pe_header() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("short.exe");
    let mut bytes = vec![0_u8; 64];
    bytes[..2].copy_from_slice(b"MZ");
    bytes[60..64].copy_from_slice(&100_u32.to_le_bytes());
    fs::write(&path, bytes).unwrap();
    assert_eq!(
        validate_executable(&path).unwrap_err().kind(),
        io::ErrorKind::InvalidData
    );
}

#[test]
fn creates_unique_adjacent_staging_paths_without_touching_the_application() {
    let source = env::current_exe().unwrap();
    let original = fs::read(&source).unwrap();
    let first = StagedUpdate::new_for_executable(&source).unwrap();
    let second = StagedUpdate::new_for_executable(&source).unwrap();

    assert!(first.replacement_path().starts_with(first.directory.path()));
    assert!(!first.replacement_path().exists());
    assert_ne!(first.directory.path(), second.directory.path());
    assert_eq!(fs::read(source).unwrap(), original);
}

#[test]
fn validates_fixed_payload_name_and_renamed_install_executable() {
    let directory = tempfile::tempdir().unwrap();
    let install = directory.path().join("portable-copy.exe");
    fs::write(&install, b"old app").unwrap();
    let staging = tempfile::Builder::new()
        .prefix(".rusttext-update-")
        .tempdir_in(directory.path())
        .unwrap();
    let helper = staging.path().join("rusttext-updater.exe");
    let replacement = staging.path().join("replacement.exe");
    write_pe(&helper, 0x8664);
    write_pe(&replacement, 0x8664);

    assert_eq!(
        validate_update_paths(&helper, &install, &replacement).unwrap(),
        fs::canonicalize(&install).unwrap()
    );

    let collision_name = staging.path().join("portable-copy.exe");
    write_pe(&collision_name, 0x8664);
    assert_eq!(
        validate_update_paths(&helper, &install, &collision_name)
            .unwrap_err()
            .kind(),
        io::ErrorKind::InvalidInput
    );
}

#[test]
fn cleanup_removes_only_known_updater_files() {
    let directory = tempfile::tempdir().unwrap();
    let staging = tempfile::Builder::new()
        .prefix(".rusttext-update-")
        .tempdir_in(directory.path())
        .unwrap();
    for name in [
        "replacement.exe",
        ".rusttext-previous.exe",
        ".helper-ready",
        "rusttext-updater.exe",
        "keep-me.txt",
    ] {
        fs::write(staging.path().join(name), b"test").unwrap();
    }
    let staging_path = staging.keep();

    cleanup_staging_directory(&staging_path);

    assert_eq!(fs::read(staging_path.join("keep-me.txt")).unwrap(), b"test");
    assert!(staging_path.exists());
}

#[cfg(windows)]
#[test]
fn replace_file_keeps_backup_and_restores_old_contents() {
    let directory = tempfile::tempdir().unwrap();
    let target = directory.path().join("portable.exe");
    let replacement = directory.path().join("replacement.exe");
    let backup = directory.path().join("previous.exe");
    fs::write(&target, b"old version").unwrap();
    fs::write(&replacement, b"new version").unwrap();

    replace_file(&target, &replacement, Some(&backup)).unwrap();

    assert_eq!(fs::read(&target).unwrap(), b"new version");
    assert_eq!(fs::read(&backup).unwrap(), b"old version");
    assert!(!replacement.exists());

    restore_backup(&target, &backup).unwrap();

    assert_eq!(fs::read(&target).unwrap(), b"old version");
    assert!(!backup.exists());
}

#[cfg(windows)]
#[test]
fn restore_backup_recreates_target_if_replace_left_it_missing() {
    let directory = tempfile::tempdir().unwrap();
    let target = directory.path().join("portable.exe");
    let backup = directory.path().join("previous.exe");
    fs::write(&backup, b"old version").unwrap();

    restore_backup(&target, &backup).unwrap();

    assert_eq!(fs::read(&target).unwrap(), b"old version");
    assert!(!backup.exists());
}

#[cfg(windows)]
#[test]
fn detects_a_child_that_exits_with_failure_during_startup_check() {
    let mut child = Command::new("cmd.exe")
        .args(["/C", "exit 17"])
        .spawn()
        .unwrap();
    let error = wait_for_child_startup(&mut child).unwrap_err();
    assert!(error.to_string().contains("завершился с ошибкой"));
}

#[test]
fn validates_the_helper_argument_count_before_parsing_paths() {
    let args = vec![OsString::from("rusttext"), OsString::from(APPLY_SWITCH)];
    assert_eq!(
        apply_from_arguments(&args).unwrap_err().kind(),
        io::ErrorKind::InvalidInput
    );
}
