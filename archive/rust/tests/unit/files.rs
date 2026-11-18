use std::fs;

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
fn interrupted_snapshot_export_keeps_the_destination_and_full_retry_writes_exact_bytes() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("copy.txt");
    fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    assert!(
        save_with(target.clone(), |file| {
            file.write_all(b"partial")?;
            Err(io::Error::other("copy interrupted"))
        })
        .is_err()
    );
    assert_eq!(fs::read(&path).unwrap(), b"original");
    assert_eq!(fs::read_dir(dir.path()).unwrap().count(), 1);

    let source = "Первая строка\n\nПоследняя строка 💡\n";
    save_with(target, |file| file.write_all(source.as_bytes())).unwrap();
    assert_eq!(fs::read(path).unwrap(), source.as_bytes());
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

#[cfg(windows)]
#[test]
fn unguarded_native_replace_can_overwrite_an_edit_after_metadata_check() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let mut pending = windows_save::private_temporary_file(directory.path()).unwrap();
    pending.write_all(b"editor snapshot").unwrap();
    pending.as_file().sync_all().unwrap();
    assert!(FileVersion::at(&path).unwrap() == target.version);
    fs::write(&path, b"external edit after check").unwrap();
    let guard = windows_save::replacement_guard(&path).unwrap();
    windows_save::replace(&path, guard, pending, directory.path()).unwrap();
    assert_eq!(fs::read(&path).unwrap(), b"editor snapshot");
}

#[cfg(windows)]
#[test]
fn shared_publication_blocks_an_in_place_writer_after_version_check() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let mut pending = windows_save::private_temporary_file(directory.path()).unwrap();
    pending.write_all(b"editor snapshot").unwrap();
    pending.as_file().sync_all().unwrap();
    publish_pending(&target, pending, || {
        let error = fs::write(&path, b"external edit after check").unwrap_err();
        assert_eq!(error.raw_os_error(), Some(32));
        assert_eq!(fs::read(&path).unwrap(), b"original");
        Ok(())
    })
    .unwrap();
    assert_eq!(fs::read(&path).unwrap(), b"editor snapshot");
    assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 1);
}

#[cfg(windows)]
#[test]
fn boundary_failure_preserves_original_and_removes_pending_output() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let mut pending = windows_save::private_temporary_file(directory.path()).unwrap();
    pending.write_all(b"editor snapshot").unwrap();
    let error = publish_pending(&target, pending, || {
        fs::write(&path, b"external edit after check")
    })
    .unwrap_err();
    assert_eq!(error.raw_os_error(), Some(32));
    assert_eq!(fs::read(&path).unwrap(), b"original");
    assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 1);
}

#[cfg(windows)]
#[test]
fn save_fails_safely_when_an_external_writer_is_already_open() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let writer = fs::OpenOptions::new().write(true).open(&path).unwrap();
    let error = save(target, TextFormat::default(), "editor snapshot")
        .err()
        .unwrap();
    assert_eq!(error.raw_os_error(), Some(32));
    drop(writer);
    assert_eq!(fs::read(&path).unwrap(), b"original");
    assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 1);
}

#[cfg(windows)]
#[test]
fn replacement_guard_blocks_external_rename_and_replace() {
    use windows::{Win32::Storage::FileSystem::ReplaceFileW, core::PCWSTR};

    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    let renamed = directory.path().join("renamed.txt");
    let replacement = directory.path().join("replacement.txt");
    fs::write(&path, b"original").unwrap();
    fs::write(&replacement, b"replacement").unwrap();

    let guard = windows_save::replacement_guard(&path).unwrap();

    let rename_error = fs::rename(&path, &renamed).unwrap_err();
    assert_eq!(rename_error.raw_os_error(), Some(32));

    let original_wide = windows_save::wide_path(&path);
    let replacement_wide = windows_save::wide_path(&replacement);
    let replace_error = unsafe {
        ReplaceFileW(
            PCWSTR(original_wide.as_ptr()),
            PCWSTR(replacement_wide.as_ptr()),
            PCWSTR::null(),
            Default::default(),
            None,
            None,
        )
    }
    .unwrap_err();
    assert_eq!(replace_error.code().0 as u32, 0x8007_0020);

    drop(guard);
    assert_eq!(fs::read(&path).unwrap(), b"original");
    assert!(!renamed.exists());
}

#[cfg(windows)]
#[test]
fn save_catches_same_size_and_timestamp_inode_swap_via_file_id() {
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::{
        Foundation::HANDLE,
        Storage::FileSystem::{
            FILE_BASIC_INFO, FileBasicInfo, GetFileInformationByHandleEx,
            SetFileInformationByHandle,
        },
    };

    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    let moved = directory.path().join("moved.txt");
    fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();

    let mut original_basic = FILE_BASIC_INFO::default();
    let original_file = File::open(&path).unwrap();
    unsafe {
        GetFileInformationByHandleEx(
            HANDLE(original_file.as_raw_handle()),
            FileBasicInfo,
            (&raw mut original_basic).cast(),
            size_of::<FILE_BASIC_INFO>() as u32,
        )
    }
    .unwrap();
    drop(original_file);

    fs::rename(&path, &moved).unwrap();
    let swapped = fs::OpenOptions::new()
        .create_new(true)
        .read(true)
        .write(true)
        .open(&path)
        .unwrap();
    (&swapped).write_all(b"swapped!").unwrap();
    swapped.sync_all().unwrap();
    unsafe {
        SetFileInformationByHandle(
            HANDLE(swapped.as_raw_handle()),
            FileBasicInfo,
            (&raw const original_basic).cast(),
            size_of::<FILE_BASIC_INFO>() as u32,
        )
    }
    .unwrap();
    drop(swapped);

    assert!(save(target, TextFormat::default(), "editor").is_err());
    assert_eq!(fs::read(&path).unwrap(), b"swapped!");
    assert_eq!(fs::read(&moved).unwrap(), b"original");
}

#[cfg(windows)]
#[test]
fn deserialized_file_version_without_file_id_stays_compatible() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("session_target.txt");
    fs::write(&path, "original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let json = serde_json::to_string(&target).unwrap();
    let restored: FileTarget = serde_json::from_str(&json).unwrap();

    assert!(FileVersion::at(&path).unwrap() == restored.version);
    let saved = save(restored, TextFormat::default(), "updated").unwrap();
    assert_eq!(fs::read_to_string(&path).unwrap(), "updated");
    assert!(FileVersion::at(&path).unwrap() == saved.version);
}

#[cfg(windows)]
#[test]
fn before_replace_external_rename_is_blocked_and_preserves_original() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    let renamed = directory.path().join("renamed.txt");
    fs::write(&path, b"original").unwrap();
    let target = FileTarget::from_picker(path.clone()).unwrap();
    let mut pending = windows_save::private_temporary_file(directory.path()).unwrap();
    pending.write_all(b"editor snapshot").unwrap();

    let error = publish_pending(&target, pending, || fs::rename(&path, &renamed)).unwrap_err();
    assert_eq!(error.raw_os_error(), Some(32));
    assert_eq!(fs::read(&path).unwrap(), b"original");
    assert!(!renamed.exists());
    assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 1);
}

#[cfg(windows)]
#[test]
fn replace_preserves_external_file_and_backup_on_already_exists() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    fs::write(&path, b"original").unwrap();
    let guard = windows_save::replacement_guard(&path).unwrap();
    let mut pending = windows_save::private_temporary_file(directory.path()).unwrap();
    pending.write_all(b"editor snapshot").unwrap();
    pending.as_file().sync_all().unwrap();

    let error = windows_save::replace_with(
        &path,
        guard,
        pending,
        directory.path(),
        |pending, target_path| {
            fs::write(target_path, b"external winner").unwrap();
            pending.persist_noclobber(target_path)
        },
    )
    .unwrap_err();

    assert_eq!(error.kind(), io::ErrorKind::AlreadyExists);
    let message = error.to_string();
    let marker = "Исходный файл сохранён в ";
    let backup_path = Path::new(message.split(marker).nth(1).unwrap());
    assert!(backup_path.exists());
    assert_eq!(fs::read(backup_path).unwrap(), b"original");
    assert_eq!(fs::read(&path).unwrap(), b"external winner");
}

#[cfg(windows)]
#[test]
fn replace_restores_original_when_persist_fails_without_target() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("result.txt");
    fs::write(&path, b"original").unwrap();
    let guard = windows_save::replacement_guard(&path).unwrap();
    let mut pending = windows_save::private_temporary_file(directory.path()).unwrap();
    pending.write_all(b"editor snapshot").unwrap();
    pending.as_file().sync_all().unwrap();

    let error =
        windows_save::replace_with(&path, guard, pending, directory.path(), |pending, _| {
            Err(tempfile::PersistError {
                error: io::Error::other("simulated persist failure"),
                file: pending,
            })
        })
        .unwrap_err();

    assert!(error.to_string().contains("simulated persist failure"));
    assert_eq!(fs::read(&path).unwrap(), b"original");
    assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 1);
}
