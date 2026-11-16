use std::{io, path::Path};

pub(crate) fn select_in_explorer(path: &Path) -> io::Result<()> {
    #[cfg(windows)]
    {
        let path = std::fs::canonicalize(path)?;
        let shell_path = normalize_shell_path(&path)?;
        std::thread::Builder::new()
            .name("rusttext-explorer-select".to_owned())
            .spawn(move || select_in_explorer_sta(&shell_path))
            .map_err(|error| {
                io::Error::other(format!("Не удалось запустить поток Проводника: {error}"))
            })?
            .join()
            .map_err(|_| io::Error::other("Поток выбора файла в Проводнике завершился с ошибкой"))?
    }

    #[cfg(not(windows))]
    {
        let _ = path;
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Показать файл в Проводнике можно только в Windows.",
        ))
    }
}

#[cfg(any(windows, test))]
fn normalize_shell_path(path: &Path) -> io::Result<String> {
    let path = path.to_str().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            "Путь содержит символы, которые не поддерживает Windows Shell.",
        )
    })?;

    if let Some(unc_path) = strip_prefix_ascii_case(path, r"\\?\UNC\") {
        if unc_path.is_empty() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "UNC-путь не содержит имени сервера.",
            ));
        }
        return Ok(format!(r"\\{unc_path}"));
    }

    if let Some(local_path) = strip_prefix_ascii_case(path, r"\\?\") {
        let bytes = local_path.as_bytes();
        let is_drive_path = bytes.len() >= 3
            && bytes[0].is_ascii_alphabetic()
            && bytes[1] == b':'
            && bytes[2] == b'\\';
        if is_drive_path {
            return Ok(local_path.to_owned());
        }
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Windows Shell не поддерживает этот тип расширенного пути.",
        ));
    }

    if path.starts_with(r"\\.\") {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Windows Shell не поддерживает пути к устройствам.",
        ));
    }

    Ok(path.to_owned())
}

#[cfg(any(windows, test))]
fn strip_prefix_ascii_case<'a>(value: &'a str, prefix: &str) -> Option<&'a str> {
    value
        .get(..prefix.len())
        .filter(|head| head.eq_ignore_ascii_case(prefix))
        .map(|_| &value[prefix.len()..])
}

#[cfg(windows)]
fn select_in_explorer_sta(path: &str) -> io::Result<()> {
    use std::ptr;

    use windows::{
        Win32::{
            System::Com::{
                COINIT_APARTMENTTHREADED, CoInitializeEx, CoTaskMemFree, CoUninitialize, IBindCtx,
            },
            UI::Shell::{Common::ITEMIDLIST, SHOpenFolderAndSelectItems, SHParseDisplayName},
        },
        core::PCWSTR,
    };

    struct Apartment;
    impl Drop for Apartment {
        fn drop(&mut self) {
            unsafe { CoUninitialize() };
        }
    }

    struct Pidl(*mut ITEMIDLIST);
    impl Drop for Pidl {
        fn drop(&mut self) {
            unsafe { CoTaskMemFree(Some(self.0.cast_const().cast())) };
        }
    }

    unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
        .ok()
        .map_err(|error| io::Error::other(format!("Не удалось инициализировать COM: {error}")))?;
    let _apartment = Apartment;

    let path_wide: Vec<u16> = path.encode_utf16().chain(std::iter::once(0)).collect();
    let mut item = ptr::null_mut();
    unsafe {
        SHParseDisplayName(
            PCWSTR(path_wide.as_ptr()),
            None::<&IBindCtx>,
            &mut item,
            0,
            None,
        )
    }
    .map_err(|error| io::Error::other(format!("Windows Shell не смог разобрать путь: {error}")))?;
    if item.is_null() {
        return Err(io::Error::other(
            "Windows Shell вернул пустой идентификатор файла.",
        ));
    }
    // The full PIDL identifies the file; no separate child PIDL is required.
    let item = Pidl(item);

    unsafe { SHOpenFolderAndSelectItems(item.0.cast_const(), None, 0) }
        .map_err(|error| io::Error::other(format!("Windows Shell не смог выделить файл: {error}")))
}

#[cfg(test)]
#[path = "../../tests/unit/workspace_reveal.rs"]
mod tests;
