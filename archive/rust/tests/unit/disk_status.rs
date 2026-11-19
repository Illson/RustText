use super::{DiskStatus, FileTarget};
use std::{fs, io};

#[test]
fn tracks_external_edits_and_removal_without_changing_the_saved_version() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("document.txt");
    fs::write(&path, "saved").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    assert!(matches!(DiskStatus::check(&target), DiskStatus::Current));

    fs::write(&path, "modified externally").unwrap();
    assert!(matches!(DiskStatus::check(&target), DiskStatus::Changed));
    fs::remove_file(&path).unwrap();
    assert!(matches!(DiskStatus::check(&target), DiskStatus::Missing));
}

#[test]
fn distinguishes_unreadable_sources_from_missing_files() {
    let denied = io::Error::from(io::ErrorKind::PermissionDenied);
    let missing = io::Error::from(io::ErrorKind::NotFound);
    assert!(matches!(
        DiskStatus::from_error(&denied),
        DiskStatus::Unavailable
    ));
    assert!(matches!(
        DiskStatus::from_error(&missing),
        DiskStatus::Missing
    ));
}
