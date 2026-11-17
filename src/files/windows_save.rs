use std::{
    ffi::OsString,
    fs::{self, File},
    io,
    mem::offset_of,
    os::windows::{
        ffi::{OsStrExt, OsStringExt},
        io::{AsRawHandle, FromRawHandle},
    },
    path::Path,
};

use tempfile::{Builder, NamedTempFile, PersistError};
use windows::{
    Win32::{
        Foundation::{
            ERROR_HANDLE_EOF, ERROR_INVALID_FUNCTION, ERROR_INVALID_PARAMETER, ERROR_NOT_SUPPORTED,
            GENERIC_READ, GENERIC_WRITE, HANDLE, HLOCAL, LocalFree,
        },
        Security::{
            ACL,
            Authorization::{
                ConvertStringSecurityDescriptorToSecurityDescriptorW, GetSecurityInfo,
                SE_FILE_OBJECT, SetSecurityInfo,
            },
            DACL_SECURITY_INFORMATION, GetSecurityDescriptorControl,
            PROTECTED_DACL_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, SE_DACL_PROTECTED,
            SECURITY_ATTRIBUTES, UNPROTECTED_DACL_SECURITY_INFORMATION,
        },
        Storage::FileSystem::{
            CREATE_ALWAYS, CREATE_NEW, CreateFileW, DELETE, FILE_ATTRIBUTE_NORMAL, FILE_BASIC_INFO,
            FILE_FLAG_BACKUP_SEMANTICS, FILE_ID_INFO, FILE_READ_ATTRIBUTES, FILE_RENAME_INFO,
            FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, FileBasicInfo, FileIdInfo,
            FileRenameInfo, FindClose, FindFirstStreamW, FindNextStreamW, FindStreamInfoStandard,
            GetFileInformationByHandleEx, OPEN_EXISTING, SetFileInformationByHandle,
            WIN32_FIND_STREAM_DATA, WRITE_DAC,
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

struct FindHandle(HANDLE);

impl Drop for FindHandle {
    fn drop(&mut self) {
        let _ = unsafe { FindClose(self.0) };
    }
}

pub(crate) fn private_temporary_file(directory: &Path) -> io::Result<NamedTempFile> {
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
                GENERIC_READ.0 | GENERIC_WRITE.0 | WRITE_DAC.0 | DELETE.0,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
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

pub(super) fn open_metadata_file(path: &Path) -> io::Result<Option<File>> {
    let path = wide_path(path);
    let handle = unsafe {
        CreateFileW(
            PCWSTR(path.as_ptr()),
            FILE_READ_ATTRIBUTES.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            None,
        )
    };
    match handle.map_err(io_error) {
        Ok(handle) => Ok(Some(unsafe { File::from_raw_handle(handle.0) })),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error),
    }
}

pub(super) fn file_id(file: &File) -> io::Result<Option<(u64, u128)>> {
    let mut info = FILE_ID_INFO::default();
    let result = unsafe {
        GetFileInformationByHandleEx(
            HANDLE(file.as_raw_handle()),
            FileIdInfo,
            (&raw mut info).cast(),
            size_of::<FILE_ID_INFO>() as u32,
        )
    };
    match result.map_err(io_error) {
        Ok(()) => Ok(Some((
            info.VolumeSerialNumber,
            u128::from_le_bytes(info.FileId.Identifier),
        ))),
        Err(error)
            if matches!(
                error.raw_os_error(),
                Some(code)
                    if code == ERROR_INVALID_FUNCTION.0 as i32
                        || code == ERROR_NOT_SUPPORTED.0 as i32
                        || code == ERROR_INVALID_PARAMETER.0 as i32
            ) =>
        {
            Ok(None)
        }
        Err(error) => Err(error),
    }
}

pub(super) fn replacement_guard(path: &Path) -> io::Result<File> {
    let path = wide_path(path);
    let handle = unsafe {
        CreateFileW(
            PCWSTR(path.as_ptr()),
            GENERIC_READ.0 | DELETE.0,
            FILE_SHARE_READ,
            None,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            None,
        )
    }
    .map_err(io_error)?;
    Ok(unsafe { File::from_raw_handle(handle.0) })
}

pub(crate) fn replace(
    path: &Path,
    guard: File,
    pending: NamedTempFile,
    directory: &Path,
) -> io::Result<()> {
    replace_with(path, guard, pending, directory, |pending, target_path| {
        pending.persist_noclobber(target_path)
    })
}

pub(super) fn replace_with(
    path: &Path,
    guard: File,
    pending: NamedTempFile,
    directory: &Path,
    persist: impl FnOnce(NamedTempFile, &Path) -> Result<File, PersistError>,
) -> io::Result<()> {
    copy_alternate_data_streams(path, pending.path())?;
    copy_creation_time(&guard, pending.as_file())?;
    copy_dacl(&guard, pending.as_file())?;

    let backup_directory = tempfile::tempdir_in(directory)?;
    let backup = backup_directory.path().join("original");
    rename_by_handle(&guard, &backup)?;

    match persist(pending, path) {
        Ok(_published) => {
            // Release the guard before backup_directory drops so its removal succeeds.
            drop(guard);
            Ok(())
        }
        Err(PersistError {
            error,
            file: pending,
        }) => {
            drop(pending);
            if error.kind() == io::ErrorKind::AlreadyExists {
                drop(guard);
                let preserved = backup_directory.keep().join("original");
                return Err(io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    format!("{error}. Исходный файл сохранён в {}", preserved.display()),
                ));
            }
            if matches!(fs::symlink_metadata(path), Err(e) if e.kind() == io::ErrorKind::NotFound) {
                if let Err(restore_error) = rename_by_handle(&guard, path) {
                    drop(guard);
                    let preserved = backup_directory.keep().join("original");
                    return Err(io::Error::other(format!(
                        "{error} (восстановление не удалось: {restore_error}). Исходный файл сохранён в {}",
                        preserved.display()
                    )));
                }
                drop(guard);
                return Err(error);
            }
            drop(guard);
            let preserved = backup_directory.keep().join("original");
            Err(io::Error::other(format!(
                "{error}. Исходный файл сохранён в {}",
                preserved.display()
            )))
        }
    }
}

fn copy_alternate_data_streams(source: &Path, pending: &Path) -> io::Result<()> {
    let source_wide = wide_path(source);
    let mut data = WIN32_FIND_STREAM_DATA::default();
    let find_result = unsafe {
        FindFirstStreamW(
            PCWSTR(source_wide.as_ptr()),
            FindStreamInfoStandard,
            (&raw mut data).cast(),
            None,
        )
    };
    let find_handle = match find_result.map_err(io_error) {
        Ok(handle) => FindHandle(handle),
        Err(error)
            if matches!(
                error.raw_os_error(),
                Some(code)
                    if code == ERROR_HANDLE_EOF.0 as i32
                        || code == ERROR_INVALID_FUNCTION.0 as i32
                        || code == ERROR_NOT_SUPPORTED.0 as i32
                        || code == ERROR_INVALID_PARAMETER.0 as i32
            ) =>
        {
            return Ok(());
        }
        Err(error) => return Err(error),
    };

    loop {
        let len = data
            .cStreamName
            .iter()
            .position(|&unit| unit == 0)
            .unwrap_or(data.cStreamName.len());
        let stream_name = OsString::from_wide(&data.cStreamName[..len]);
        let is_default_data = stream_name
            .to_str()
            .is_some_and(|name| name.eq_ignore_ascii_case("::$DATA"));
        if !is_default_data {
            copy_named_stream(source, pending, &stream_name)?;
        }

        data = WIN32_FIND_STREAM_DATA::default();
        match unsafe { FindNextStreamW(find_handle.0, (&raw mut data).cast()) }.map_err(io_error) {
            Ok(()) => {}
            Err(error) if error.raw_os_error() == Some(ERROR_HANDLE_EOF.0 as i32) => break,
            Err(error) => return Err(error),
        }
    }
    Ok(())
}

fn copy_named_stream(source: &Path, pending: &Path, stream_name: &OsString) -> io::Result<()> {
    let mut src_stream = source.as_os_str().to_os_string();
    src_stream.push(stream_name);
    let src_wide: Vec<u16> = src_stream.encode_wide().chain(Some(0)).collect();
    let src_handle = unsafe {
        CreateFileW(
            PCWSTR(src_wide.as_ptr()),
            GENERIC_READ.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            None,
        )
    }
    .map_err(io_error)?;
    let mut src_file = unsafe { File::from_raw_handle(src_handle.0) };

    let mut dst_stream = pending.as_os_str().to_os_string();
    dst_stream.push(stream_name);
    let dst_wide: Vec<u16> = dst_stream.encode_wide().chain(Some(0)).collect();
    let dst_handle = unsafe {
        CreateFileW(
            PCWSTR(dst_wide.as_ptr()),
            GENERIC_WRITE.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            None,
        )
    }
    .map_err(io_error)?;
    let mut dst_file = unsafe { File::from_raw_handle(dst_handle.0) };

    io::copy(&mut src_file, &mut dst_file)?;
    Ok(())
}

fn copy_creation_time(guard: &File, pending: &File) -> io::Result<()> {
    let mut src_info = FILE_BASIC_INFO::default();
    unsafe {
        GetFileInformationByHandleEx(
            HANDLE(guard.as_raw_handle()),
            FileBasicInfo,
            (&raw mut src_info).cast(),
            size_of::<FILE_BASIC_INFO>() as u32,
        )
    }
    .map_err(io_error)?;
    let dst_info = FILE_BASIC_INFO {
        CreationTime: src_info.CreationTime,
        ..Default::default()
    };
    unsafe {
        SetFileInformationByHandle(
            HANDLE(pending.as_raw_handle()),
            FileBasicInfo,
            (&raw const dst_info).cast(),
            size_of::<FILE_BASIC_INFO>() as u32,
        )
    }
    .map_err(io_error)
}

fn copy_dacl(guard: &File, pending: &File) -> io::Result<()> {
    let mut descriptor = SecurityDescriptor(PSECURITY_DESCRIPTOR::default());
    let mut acl: *mut ACL = std::ptr::null_mut();
    unsafe {
        GetSecurityInfo(
            HANDLE(guard.as_raw_handle()),
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION,
            None,
            None,
            Some(&mut acl),
            None,
            Some(&mut descriptor.0),
        )
    }
    .ok()
    .map_err(io_error)?;

    let mut control = 0u16;
    let mut revision = 0u32;
    unsafe { GetSecurityDescriptorControl(descriptor.0, &mut control, &mut revision) }
        .map_err(io_error)?;
    let security_info = if control & SE_DACL_PROTECTED.0 != 0 {
        DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION
    } else {
        DACL_SECURITY_INFORMATION | UNPROTECTED_DACL_SECURITY_INFORMATION
    };

    unsafe {
        SetSecurityInfo(
            HANDLE(pending.as_raw_handle()),
            SE_FILE_OBJECT,
            security_info,
            None,
            None,
            (!acl.is_null()).then_some(acl.cast_const()),
            None,
        )
    }
    .ok()
    .map_err(io_error)
}

fn rename_by_handle(file: &File, destination: &Path) -> io::Result<()> {
    let wide = wide_path(destination);
    let name_bytes = (wide.len() - 1) * size_of::<u16>();
    let name_byte_len = u32::try_from(name_bytes)
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "Слишком длинный путь к файлу"))?;
    let header_bytes = offset_of!(FILE_RENAME_INFO, FileName);
    let total_bytes =
        (header_bytes + wide.len() * size_of::<u16>()).max(size_of::<FILE_RENAME_INFO>());
    let mut buffer = vec![0u64; total_bytes.div_ceil(size_of::<u64>())];
    let info_ptr = buffer.as_mut_ptr().cast::<FILE_RENAME_INFO>();
    unsafe {
        (*info_ptr).Anonymous.ReplaceIfExists = false;
        (*info_ptr).RootDirectory = HANDLE::default();
        (*info_ptr).FileNameLength = name_byte_len;
        let dst_name = std::ptr::addr_of_mut!((*info_ptr).FileName).cast::<u16>();
        std::ptr::copy_nonoverlapping(wide.as_ptr(), dst_name, wide.len());
        SetFileInformationByHandle(
            HANDLE(file.as_raw_handle()),
            FileRenameInfo,
            buffer.as_ptr().cast(),
            total_bytes as u32,
        )
    }
    .map_err(io_error)
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
