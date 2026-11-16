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

#[cfg(windows)]
#[test]
fn editor_save_preserves_a_protected_acl_and_named_stream() {
    use windows::{
        Win32::Security::{
            ACL,
            Authorization::{GetNamedSecurityInfoW, SE_FILE_OBJECT},
            DACL_SECURITY_INFORMATION, GetSecurityDescriptorControl, SE_DACL_PROTECTED,
        },
        core::{PCWSTR, w},
    };

    fn read_acl(path: &Path) -> (bool, Vec<u8>) {
        let name = windows_save::wide_path(path);
        let mut descriptor = windows_save::SecurityDescriptor(Default::default());
        let mut acl: *mut ACL = std::ptr::null_mut();
        unsafe {
            GetNamedSecurityInfoW(
                PCWSTR(name.as_ptr()),
                SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION,
                None,
                None,
                Some(&mut acl),
                None,
                &mut descriptor.0,
            )
        }
        .ok()
        .unwrap();
        assert!(!acl.is_null());
        let mut control = 0;
        let mut revision = 0;
        unsafe { GetSecurityDescriptorControl(descriptor.0, &mut control, &mut revision) }.unwrap();
        let bytes = unsafe { std::slice::from_raw_parts(acl.cast(), (*acl).AclSize.into()) };
        (control & SE_DACL_PROTECTED.0 != 0, bytes.to_vec())
    }

    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("private.txt");
    let stream = dir.path().join("private.txt:Zone.Identifier");
    let mut source =
        windows_save::temporary_file_with_acl(dir.path(), w!("D:P(A;;GA;;;OW)(A;;GR;;;SY)"))
            .unwrap();
    source.write_all(b"original").unwrap();
    source.into_temp_path().persist_noclobber(&path).unwrap();
    let zone = b"[ZoneTransfer]\r\nZoneId=3\r\n";
    fs::write(&stream, zone).unwrap();
    let acl = read_acl(&path);
    assert!(acl.0);

    let opened = open(&path, false).unwrap();
    let original_created = opened.target.version.unwrap().created;
    let saved = save(opened.target, opened.format, "updated").unwrap();

    assert_eq!(read_acl(&path), acl);
    assert_eq!(fs::read(&stream).unwrap(), zone);
    assert_eq!(fs::read(&path).unwrap(), b"updated");
    assert_eq!(saved.version.unwrap().created, original_created);
    assert!(FileVersion::at(&path).unwrap() == saved.version);
    assert_eq!(fs::read_dir(dir.path()).unwrap().count(), 1);
}

#[cfg(windows)]
#[test]
fn failed_replacement_leaves_the_original_and_removes_staging_files() {
    use std::{fs::OpenOptions, os::windows::fs::OpenOptionsExt};
    use windows::Win32::Storage::FileSystem::FILE_SHARE_READ;

    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("locked.txt");
    fs::write(&path, "original").unwrap();
    let opened = open(&path, false).unwrap();
    let reader = OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ.0)
        .open(&path)
        .unwrap();

    assert!(save(opened.target, opened.format, "updated").is_err());
    drop(reader);
    assert_eq!(fs::read_to_string(&path).unwrap(), "original");
    assert_eq!(fs::read_dir(dir.path()).unwrap().count(), 1);
}
