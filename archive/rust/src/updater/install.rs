use std::{
    env,
    ffi::{OsStr, OsString},
    fs::{self, File, OpenOptions},
    io::{self, Read, Seek, SeekFrom},
    path::{Path, PathBuf},
    process::Command,
    thread,
};

use tempfile::TempDir;

const APPLY_SWITCH: &str = "--rusttext-apply-update";
const CLEANUP_PID_ENV: &str = "RUSTTEXT_UPDATE_HELPER_PID";
const CLEANUP_DIRECTORY_ENV: &str = "RUSTTEXT_UPDATE_STAGING_DIRECTORY";
const PARENT_WAIT_MS: u32 = 180_000;
const HELPER_READY_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(10);
const CHILD_STARTUP_CHECK: std::time::Duration = std::time::Duration::from_millis(1500);

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

pub(crate) struct StagedUpdate {
    directory: TempDir,
    executable: PathBuf,
    helper_executable: PathBuf,
    current_executable: PathBuf,
}

impl StagedUpdate {
    pub(crate) fn new_for_current_exe() -> io::Result<Self> {
        let current_executable = env::current_exe()?;
        Self::new_for_executable(&current_executable)
    }

    fn new_for_executable(current_executable: &Path) -> io::Result<Self> {
        let current_executable = fs::canonicalize(current_executable)?;
        let parent = current_executable.parent().ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "Приложение не имеет папки установки.",
            )
        })?;
        if current_executable.file_name().is_none() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Не удалось определить имя приложения.",
            ));
        }
        let directory = tempfile::Builder::new()
            .prefix(".rusttext-update-")
            .tempdir_in(parent)?;
        let executable = directory.path().join("replacement.exe");
        let helper_executable = directory.path().join("rusttext-updater.exe");
        fs::copy(&current_executable, &helper_executable)?;
        OpenOptions::new()
            .write(true)
            .open(&helper_executable)?
            .sync_all()?;

        Ok(Self {
            directory,
            executable,
            helper_executable,
            current_executable,
        })
    }

    pub(crate) fn replacement_path(&self) -> &Path {
        &self.executable
    }

    pub(crate) fn launch(self) -> io::Result<()> {
        validate_executable(&self.executable)?;
        let ready_marker = self.directory.path().join(".helper-ready");
        let mut helper = Command::new(&self.helper_executable);
        helper
            .arg(APPLY_SWITCH)
            .arg(std::process::id().to_string())
            .arg(&self.current_executable)
            .arg(&self.executable)
            .arg(&ready_marker);
        set_hidden(&mut helper);
        let mut child = helper.spawn()?;
        let started = std::time::Instant::now();
        while !ready_marker.exists() {
            if let Some(status) = child.try_wait()? {
                return Err(io::Error::other(format!(
                    "Установщик обновления завершился до готовности. Код завершения: {status}"
                )));
            }
            if started.elapsed() >= HELPER_READY_TIMEOUT {
                child.kill()?;
                let _ = child.wait();
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "Установщик обновления не подтвердил готовность. Окно RustText оставлено открытым.",
                ));
            }
            thread::sleep(std::time::Duration::from_millis(20));
        }
        let _ = self.directory.keep();
        Ok(())
    }
}

/// Returns `Some` when the process is the updater helper and should exit after this call.
/// A cleanup-only invocation returns `None` after scheduling cleanup so normal startup continues.
pub(crate) fn run_helper_if_requested() -> Option<io::Result<()>> {
    let arguments = env::args_os().collect::<Vec<_>>();
    if arguments.get(1).is_some_and(|arg| arg == APPLY_SWITCH) {
        Some(apply_from_arguments(&arguments))
    } else {
        let cleanup = cleanup_request_from_environment();
        clear_cleanup_environment();
        if let Some((process_id, directory)) = cleanup {
            start_cleanup_thread(process_id, directory);
        }
        None
    }
}

fn apply_from_arguments(arguments: &[OsString]) -> io::Result<()> {
    if arguments.len() != 6 {
        return Err(invalid_helper_arguments());
    }
    let parent_id = arguments[2]
        .to_str()
        .and_then(|value| value.parse::<u32>().ok())
        .filter(|value| *value != 0)
        .ok_or_else(invalid_helper_arguments)?;
    let target = PathBuf::from(&arguments[3]);
    let replacement = PathBuf::from(&arguments[4]);
    let ready_marker = PathBuf::from(&arguments[5]);
    let helper = env::current_exe()?;
    let target = validate_update_paths(&helper, &target, &replacement)?;
    let expected_marker = replacement
        .parent()
        .expect("validated replacement has a parent")
        .join(".helper-ready");
    if ready_marker != expected_marker || ready_marker.exists() {
        return Err(invalid_helper_arguments());
    }
    validate_executable(&replacement)?;
    let parent = open_parent(parent_id)?;
    File::options()
        .write(true)
        .create_new(true)
        .open(&ready_marker)?
        .sync_all()?;
    wait_for_open_parent(&parent)?;

    let _update_lock = acquire_update_lock(&target)?;
    let backup = replacement
        .parent()
        .expect("validated replacement has a parent")
        .join(".rusttext-previous.exe");
    if let Err(error) = replace_file(&target, &replacement, Some(&backup)) {
        if backup.exists() {
            restore_backup(&target, &backup).map_err(|restore_error| {
                io::Error::other(format!(
                    "Обновление не удалось, а предыдущий файл не удалось вернуть. Резервная копия сохранена здесь: {}. Ошибка обновления: {error}. Ошибка возврата: {restore_error}",
                    backup.display()
                ))
            })?;
        }
        let _ = start_updated_application(&target, &replacement);
        return Err(error);
    }

    if let Err(launch_error) = start_updated_application(&target, &replacement) {
        return Err(rollback_update(
            &target,
            &backup,
            &replacement,
            launch_error,
        ));
    }
    Ok(())
}

fn invalid_helper_arguments() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidInput,
        "Аргументы обновления RustText некорректны.",
    )
}

fn validate_update_paths(helper: &Path, target: &Path, replacement: &Path) -> io::Result<PathBuf> {
    let helper = fs::canonicalize(helper)?;
    let target = fs::canonicalize(target)?;
    let replacement = fs::canonicalize(replacement)?;
    let directory = replacement.parent().ok_or_else(invalid_helper_arguments)?;
    let target_directory = target.parent().ok_or_else(invalid_helper_arguments)?;
    let helper_name = helper
        .file_name()
        .and_then(OsStr::to_str)
        .unwrap_or_default();
    let directory_name = directory
        .file_name()
        .and_then(OsStr::to_str)
        .unwrap_or_default();
    if helper.parent() != Some(directory)
        || helper_name != "rusttext-updater.exe"
        || directory.parent() != Some(target_directory)
        || !directory_name.starts_with(".rusttext-update-")
        || replacement.file_name() != Some(OsStr::new("replacement.exe"))
        || fs::symlink_metadata(&replacement)?.file_type().is_symlink()
    {
        return Err(invalid_helper_arguments());
    }
    Ok(target)
}

fn validate_executable(path: &Path) -> io::Result<()> {
    let mut executable = File::open(path)?;
    let size = executable.metadata()?.len();
    let mut dos = [0_u8; 64];
    executable
        .read_exact(&mut dos)
        .map_err(invalid_executable_on_eof)?;
    if dos[..2] != *b"MZ" {
        return Err(invalid_executable());
    }
    let pe_offset = u32::from_le_bytes(dos[60..64].try_into().expect("fixed PE offset")) as u64;
    if pe_offset > 1024 * 1024 || pe_offset + 6 > size {
        return Err(invalid_executable());
    }
    executable.seek(SeekFrom::Start(pe_offset))?;
    let mut header = [0_u8; 6];
    executable
        .read_exact(&mut header)
        .map_err(invalid_executable_on_eof)?;
    if &header[..4] != b"PE\0\0" || u16::from_le_bytes([header[4], header[5]]) != 0x8664 {
        return Err(invalid_executable());
    }
    Ok(())
}

fn invalid_executable_on_eof(error: io::Error) -> io::Error {
    if error.kind() == io::ErrorKind::UnexpectedEof {
        invalid_executable()
    } else {
        error
    }
}

fn invalid_executable() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "Файл обновления не является исполняемым приложением Windows x64.",
    )
}

#[cfg(windows)]
fn wait_for_parent(process_id: u32) -> io::Result<()> {
    let process = open_parent(process_id)?;
    wait_for_open_parent(&process)
}

#[cfg(windows)]
fn open_parent(process_id: u32) -> io::Result<std::os::windows::io::OwnedHandle> {
    use std::os::windows::io::{FromRawHandle, OwnedHandle};
    use windows::Win32::System::Threading::{OpenProcess, PROCESS_SYNCHRONIZE};

    // This owned process handle avoids a process-id reuse race during the wait.
    let raw = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, false, process_id) }
        .map_err(|error| io::Error::from_raw_os_error(error.code().0 as u32 as i32))?;
    Ok(unsafe { OwnedHandle::from_raw_handle(raw.0) })
}

#[cfg(windows)]
fn wait_for_open_parent(process: &std::os::windows::io::OwnedHandle) -> io::Result<()> {
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::{
        Foundation::{HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT},
        System::Threading::WaitForSingleObject,
    };
    let outcome = unsafe { WaitForSingleObject(HANDLE(process.as_raw_handle()), PARENT_WAIT_MS) };
    if outcome == WAIT_OBJECT_0 {
        Ok(())
    } else if outcome == WAIT_TIMEOUT {
        Err(io::Error::new(
            io::ErrorKind::TimedOut,
            "RustText не завершился вовремя. Обновление не установлено.",
        ))
    } else {
        Err(io::Error::last_os_error())
    }
}

#[cfg(not(windows))]
fn wait_for_parent(_process_id: u32) -> io::Result<()> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "Установка обновлений поддерживается только в Windows.",
    ))
}

#[cfg(not(windows))]
fn open_parent(_process_id: u32) -> io::Result<()> {
    Err(io::Error::new(io::ErrorKind::Unsupported, "Windows only"))
}

#[cfg(not(windows))]
fn wait_for_open_parent(_process: &()) -> io::Result<()> {
    Err(io::Error::new(io::ErrorKind::Unsupported, "Windows only"))
}

struct UpdateLock {
    file: Option<File>,
    path: PathBuf,
}

impl Drop for UpdateLock {
    fn drop(&mut self) {
        drop(self.file.take());
        let _ = fs::remove_file(&self.path);
    }
}

fn acquire_update_lock(target: &Path) -> io::Result<UpdateLock> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        let lock_path = target.with_file_name(format!(
            ".{}.update.lock",
            target
                .file_name()
                .and_then(OsStr::to_str)
                .unwrap_or("rusttext")
        ));
        let file = OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .share_mode(0)
            .open(&lock_path)?;
        Ok(UpdateLock {
            file: Some(file),
            path: lock_path,
        })
    }
    #[cfg(not(windows))]
    {
        let _ = target;
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Установка обновлений поддерживается только в Windows.",
        ))
    }
}

#[cfg(windows)]
fn replace_file(target: &Path, replacement: &Path, backup: Option<&Path>) -> io::Result<()> {
    use windows::Win32::Storage::FileSystem::{REPLACE_FILE_FLAGS, ReplaceFileW};
    use windows::core::PCWSTR;

    let target = wide_path(target);
    let replacement = wide_path(replacement);
    let backup = backup.map(wide_path);
    unsafe {
        ReplaceFileW(
            PCWSTR(target.as_ptr()),
            PCWSTR(replacement.as_ptr()),
            backup
                .as_ref()
                .map_or(PCWSTR::null(), |path| PCWSTR(path.as_ptr())),
            REPLACE_FILE_FLAGS(0),
            None,
            None,
        )
    }
    .map_err(|error| io::Error::from_raw_os_error(error.code().0 as u32 as i32))
}

fn restore_backup(target: &Path, backup: &Path) -> io::Result<()> {
    if !backup.exists() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "Резервная копия предыдущей версии не найдена.",
        ));
    }
    if target.exists() {
        replace_file(target, backup, None)
    } else {
        fs::rename(backup, target)
    }
}

#[cfg(not(windows))]
fn replace_file(_target: &Path, _replacement: &Path, _backup: Option<&Path>) -> io::Result<()> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "Установка обновлений поддерживается только в Windows.",
    ))
}

fn start_updated_application(target: &Path, staged: &Path) -> io::Result<()> {
    let staging_directory = staged.parent().ok_or_else(invalid_helper_arguments)?;
    let mut command = Command::new(target);
    command
        .env(CLEANUP_PID_ENV, std::process::id().to_string())
        .env(CLEANUP_DIRECTORY_ENV, staging_directory);
    set_hidden(&mut command);
    let mut child = command.spawn()?;
    wait_for_child_startup(&mut child)
}

fn wait_for_child_startup(child: &mut std::process::Child) -> io::Result<()> {
    let started = std::time::Instant::now();
    loop {
        if let Some(status) = child.try_wait()? {
            if status.success() {
                return Ok(());
            }
            return Err(io::Error::other(format!(
                "Обновлённый RustText завершился с ошибкой при запуске: {status}"
            )));
        }
        if started.elapsed() >= CHILD_STARTUP_CHECK {
            return Ok(());
        }
        thread::sleep(std::time::Duration::from_millis(50));
    }
}

fn rollback_update(
    target: &Path,
    backup: &Path,
    staged: &Path,
    launch_error: io::Error,
) -> io::Error {
    match restore_backup(target, backup) {
        Ok(()) => match start_updated_application(target, staged) {
            Ok(()) => io::Error::other(format!(
                "Не удалось запустить новую версию RustText. Запущена предыдущая версия. {launch_error}"
            )),
            Err(previous_error) => io::Error::other(format!(
                "Новая версия не запустилась, а предыдущая восстановлена, но тоже не смогла запуститься. Резервная копия сохранена здесь: {}. {launch_error}. {previous_error}",
                backup.display()
            )),
        },
        Err(rollback_error) => io::Error::other(format!(
            "Не удалось запустить новую версию RustText. Предыдущий файл сохранён здесь: {}. {launch_error} Ошибка возврата: {rollback_error}",
            backup.display()
        )),
    }
}

fn cleanup_request_from_environment() -> Option<(u32, PathBuf)> {
    let process_id = env::var(CLEANUP_PID_ENV).ok()?.parse::<u32>().ok()?;
    let directory = PathBuf::from(env::var_os(CLEANUP_DIRECTORY_ENV)?);
    (process_id != 0).then_some((process_id, directory))
}

fn clear_cleanup_environment() {
    // Startup calls this before GPUI starts its threads.
    unsafe {
        env::remove_var(CLEANUP_PID_ENV);
        env::remove_var(CLEANUP_DIRECTORY_ENV);
    }
}

fn start_cleanup_thread(process_id: u32, directory: PathBuf) {
    let executable = env::current_exe();
    thread::spawn(move || {
        let executable_directory = executable
            .ok()
            .and_then(|path| path.parent().map(fs::canonicalize))
            .and_then(Result::ok);
        let canonical_staging_directory = fs::canonicalize(&directory).ok();
        if wait_for_parent(process_id).is_ok()
            && canonical_staging_directory
                .as_deref()
                .and_then(Path::parent)
                == executable_directory.as_deref()
            && canonical_staging_directory
                .as_deref()
                .and_then(Path::file_name)
                .and_then(OsStr::to_str)
                .is_some_and(|name| name.starts_with(".rusttext-update-"))
            && let Some(staging_directory) = canonical_staging_directory
        {
            cleanup_staging_directory(&staging_directory);
        }
    });
}

fn cleanup_staging_directory(directory: &Path) {
    for name in [
        "replacement.exe",
        ".rusttext-previous.exe",
        ".helper-ready",
        "rusttext-updater.exe",
    ] {
        let path = directory.join(name);
        if fs::symlink_metadata(&path).is_ok_and(|metadata| metadata.file_type().is_file()) {
            let _ = fs::remove_file(path);
        }
    }
    let _ = fs::remove_dir(directory);
}

fn set_hidden(command: &mut Command) {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    #[cfg(not(windows))]
    let _ = command;
}

#[cfg(windows)]
fn wide_path(path: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    path.as_os_str().encode_wide().chain(Some(0)).collect()
}

#[cfg(test)]
#[path = "../../tests/unit/updater_install.rs"]
mod tests;
