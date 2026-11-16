use std::{
    fs::{self, File},
    io,
    os::windows::{ffi::OsStrExt, io::FromRawHandle},
    path::Path,
};

use tempfile::{Builder, NamedTempFile};
use windows::{
    Win32::{
        Foundation::{GENERIC_READ, GENERIC_WRITE, HLOCAL, LocalFree},
        Security::{
            Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW,
            PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES,
        },
        Storage::FileSystem::{
            CREATE_NEW, CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_DELETE, MoveFileExW,
            ReplaceFileW,
        },
    },
    core::{Error, PCWSTR, w},
};

pub(super) struct SecurityDescriptor(pub(super) PSECURITY_DESCRIPTOR);

impl Drop for SecurityDescriptor {
    fn drop(&mut self) {
        unsafe { LocalFree(Some(HLOCAL(self.0.0))) };
    }
}

pub(super) fn private_temporary_file(directory: &Path) -> io::Result<NamedTempFile> {
    // The pending contents must stay private even in a directory with inherited read access.
    temporary_file_with_acl(directory, w!("D:P(A;;GA;;;OW)"))
}

pub(super) fn temporary_file_with_acl(directory: &Path, acl: PCWSTR) -> io::Result<NamedTempFile> {
    let mut descriptor = SecurityDescriptor(PSECURITY_DESCRIPTOR::default());
    unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(acl, 1, &mut descriptor.0, None)
    }
    .map_err(io_error)?;
    let attributes = SECURITY_ATTRIBUTES {
        nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: descriptor.0.0,
        ..Default::default()
    };
    Builder::new().make_in(directory, |path| {
        let path = wide_path(path);
        let handle = unsafe {
            CreateFileW(
                PCWSTR(path.as_ptr()),
                (GENERIC_READ | GENERIC_WRITE).0,
                FILE_SHARE_DELETE,
                Some(&attributes),
                CREATE_NEW,
                FILE_ATTRIBUTE_NORMAL,
                None,
            )
        }
        .map_err(io_error)?;
        Ok(unsafe { File::from_raw_handle(handle.0) })
    })
}

pub(super) fn replace(path: &Path, pending: NamedTempFile, directory: &Path) -> io::Result<()> {
    let pending = pending.into_temp_path();
    let backup_directory = tempfile::tempdir_in(directory)?;
    let backup = backup_directory.path().join("original");
    let original_wide = wide_path(path);
    let pending_wide = wide_path(&pending);
    let backup_wide = wide_path(&backup);
    let original_name = PCWSTR(original_wide.as_ptr());
    let backup_name = PCWSTR(backup_wide.as_ptr());
    let result = unsafe {
        ReplaceFileW(
            original_name,
            PCWSTR(pending_wide.as_ptr()),
            backup_name,
            Default::default(),
            None,
            None,
        )
    };
    if let Err(error) = result.map_err(io_error) {
        // A failed replacement can leave the original at the backup path.
        if !matches!(fs::symlink_metadata(&backup), Err(e) if e.kind() == io::ErrorKind::NotFound)
            && unsafe { MoveFileExW(backup_name, original_name, Default::default()) }.is_err()
        {
            let backup = backup_directory.keep().join("original");
            return Err(io::Error::other(format!(
                "{error}. Исходный файл сохранён в {}",
                backup.display()
            )));
        }
        return Err(error);
    }
    Ok(())
}

pub(super) fn wide_path(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain(Some(0)).collect()
}

fn io_error(error: Error) -> io::Error {
    let code = error.code().0 as u32;
    if code >> 16 == 0x8007 {
        io::Error::from_raw_os_error((code & 0xffff) as i32)
    } else {
        io::Error::other(error)
    }
}
