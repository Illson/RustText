use std::path::Path;

use super::normalize_shell_path;

#[test]
fn extended_drive_paths_are_normalized_for_shell() {
    assert_eq!(
        normalize_shell_path(Path::new(r"\\?\C:\logs\current.log")).unwrap(),
        r"C:\logs\current.log"
    );
}

#[test]
fn extended_unc_paths_are_normalized_for_shell() {
    assert_eq!(
        normalize_shell_path(Path::new(r"\\?\UNC\server\share\logs\current.log")).unwrap(),
        r"\\server\share\logs\current.log"
    );
}

#[test]
fn ordinary_unc_and_unicode_paths_are_preserved() {
    let path = r"\\server\share\папка\лог.log";
    assert_eq!(normalize_shell_path(Path::new(path)).unwrap(), path);
}

#[test]
fn unsupported_device_paths_are_reported() {
    assert!(normalize_shell_path(Path::new(r"\\.\PhysicalDrive0")).is_err());
    assert!(normalize_shell_path(Path::new(r"\\?\Volume{1234}\file.log")).is_err());
}
