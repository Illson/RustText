//! Safe, streaming operations used by the large-log view.
//!
//! A trim plan records a byte offset in the original file. The caller can show
//! that offset as a hidden-start boundary, then pass the same plan to `trim`.
//! The operation revalidates the file before making any changes.

use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::atomic::{AtomicBool, Ordering},
    time::{SystemTime, UNIX_EPOCH},
};

use crate::text_format::TextEncoding;

const BUFFER_SIZE: usize = 1024 * 1024;

/// A validated trim position, in bytes from the start of the original file.
#[derive(Clone)]
pub struct TrimPlan {
    pub percent: u8,
    pub original_size: u64,
    /// Number of bytes the viewer hides and a physical trim removes.
    pub removable_bytes: u64,
    /// Byte offset of the first retained complete line in the original file.
    pub boundary_offset: u64,
    encoding: TextEncoding,
    modified: Option<SystemTime>,
    identity: FileIdentity,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct FileIdentity {
    volume: Option<u32>,
    index: Option<u64>,
}

#[derive(Clone, Debug)]
pub struct TrimOutcome {
    pub backup_path: PathBuf,
    pub removed_bytes: u64,
    pub remaining_bytes: u64,
}

trait TrimIoHooks {
    fn before_backup_create(&mut self, _path: &Path) -> io::Result<()> {
        Ok(())
    }

    fn before_backup_chunk(&mut self, _copied: u64) -> io::Result<()> {
        Ok(())
    }

    fn after_shift_chunk(&mut self, _shifted: u64) -> io::Result<()> {
        Ok(())
    }
}

struct NoopTrimIoHooks;

impl TrimIoHooks for NoopTrimIoHooks {}

/// Prepare a trim against an exclusively opened file. The returned boundary is
/// tied to the observed file identity, size, and modification time and is
/// revalidated by `trim` immediately before mutation. Progress reports bytes
/// scanned from the percentage target toward EOF; cancellation is checked
/// between read blocks and leaves the source untouched.
pub fn prepare_with_progress<F>(
    path: &Path,
    percent: u8,
    encoding: TextEncoding,
    cancelled: &AtomicBool,
    mut progress: F,
) -> io::Result<TrimPlan>
where
    F: FnMut(u64, u64),
{
    validate_percent(percent)?;
    let mut file = open_exclusive(path, false)?;
    let metadata = file.metadata()?;
    let boundary = find_boundary(
        &mut file,
        metadata.len(),
        percent,
        encoding,
        cancelled,
        &mut progress,
    )?;
    let identity = file_identity(&file)?;
    Ok(make_plan(percent, metadata, boundary, encoding, identity))
}

/// Shared-read counterpart of `prepare_with_progress` for the viewer's hidden
/// start marker. Re-run `prepare_with_progress` before a physical trim.
pub fn hidden_start_with_progress<F>(
    path: &Path,
    percent: u8,
    encoding: TextEncoding,
    cancelled: &AtomicBool,
    mut progress: F,
) -> io::Result<TrimPlan>
where
    F: FnMut(u64, u64),
{
    validate_percent(percent)?;
    let mut file = File::open(path)?;
    let metadata = file.metadata()?;
    let boundary = find_boundary(
        &mut file,
        metadata.len(),
        percent,
        encoding,
        cancelled,
        &mut progress,
    )?;
    let identity = file_identity(&file)?;
    Ok(make_plan(percent, metadata, boundary, encoding, identity))
}

#[allow(dead_code)]
pub fn prepare(path: &Path, percent: u8, encoding: TextEncoding) -> io::Result<TrimPlan> {
    prepare_with_progress(path, percent, encoding, &AtomicBool::new(false), |_, _| {})
}

#[allow(dead_code)]
pub fn hidden_start(path: &Path, percent: u8, encoding: TextEncoding) -> io::Result<TrimPlan> {
    hidden_start_with_progress(path, percent, encoding, &AtomicBool::new(false), |_, _| {})
}

/// Back up the full file and then remove the planned prefix in bounded memory.
/// Cancellation is honored during backup and at the final pre-mutation point;
/// once shifting starts the operation runs to completion so cancellation
/// cannot knowingly leave a half-compacted log. On an I/O failure, restoration
/// from the retained backup is attempted and the backup is never deleted.
pub fn trim<F>(
    path: &Path,
    plan: &TrimPlan,
    cancelled: &AtomicBool,
    progress: F,
) -> io::Result<TrimOutcome>
where
    F: FnMut(u64, u64),
{
    trim_with_hooks(path, plan, cancelled, progress, &mut NoopTrimIoHooks)
}

fn trim_with_hooks<F, H>(
    path: &Path,
    plan: &TrimPlan,
    cancelled: &AtomicBool,
    mut progress: F,
    hooks: &mut H,
) -> io::Result<TrimOutcome>
where
    F: FnMut(u64, u64),
    H: TrimIoHooks,
{
    let mut source = open_exclusive(path, true)?;
    validate_plan(&mut source, plan)?;
    if cancelled.load(Ordering::Relaxed) {
        return Err(cancelled_error());
    }

    let backup_path = backup_path(path)?;
    hooks.before_backup_create(&backup_path)?;
    // Ownership is established only after create_new succeeds. We must never
    // clean up a candidate path that another process created in the meantime.
    let backup_file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&backup_path)?;
    let backup_result = copy_to_backup(
        &mut source,
        backup_file,
        plan.original_size,
        cancelled,
        &mut progress,
        hooks,
    );
    if let Err(error) = backup_result {
        // An incomplete backup is not a recovery point. The source is still
        // untouched, so remove only this newly-created partial file.
        let _ = fs::remove_file(&backup_path);
        return Err(error);
    }
    if cancelled.load(Ordering::Relaxed) {
        return Err(io::Error::new(
            io::ErrorKind::Interrupted,
            format!(
                "Операция отменена до изменения файла. Полная резервная копия сохранена: {}",
                backup_path.display()
            ),
        ));
    }
    if let Err(error) = validate_plan(&mut source, plan) {
        return Err(io::Error::new(
            error.kind(),
            format!(
                "Файл изменился до начала обрезки; исходник не изменён. Резервная копия сохранена: {}. Причина: {error}",
                backup_path.display()
            ),
        ));
    }

    let retained_len = plan.original_size.saturating_sub(plan.boundary_offset);
    let bom_len = bom_length(plan.encoding).min(plan.original_size);
    let shift_result = shift_prefix(&mut source, plan, bom_len, &mut progress, hooks);
    if let Err(error) = shift_result {
        return match restore_from_backup(&mut source, &backup_path) {
            Ok(()) => Err(io::Error::new(
                error.kind(),
                format!(
                    "Не удалось обрезать файл; исходное содержимое восстановлено. Резервная копия: {}. Причина: {error}",
                    backup_path.display()
                ),
            )),
            Err(restore_error) => Err(io::Error::new(
                error.kind(),
                format!(
                    "Не удалось обрезать файл и восстановить исходник. Сохраните резервную копию: {}. Ошибка обработки: {error}; ошибка восстановления: {restore_error}",
                    backup_path.display()
                ),
            )),
        };
    }

    Ok(TrimOutcome {
        backup_path,
        removed_bytes: plan.removable_bytes,
        remaining_bytes: bom_len.saturating_add(retained_len),
    })
}

fn make_plan(
    percent: u8,
    metadata: fs::Metadata,
    boundary: u64,
    encoding: TextEncoding,
    identity: FileIdentity,
) -> TrimPlan {
    TrimPlan {
        percent,
        original_size: metadata.len(),
        removable_bytes: boundary.saturating_sub(bom_length(encoding).min(metadata.len())),
        boundary_offset: boundary,
        encoding,
        modified: metadata.modified().ok(),
        identity,
    }
}

fn validate_percent(percent: u8) -> io::Result<()> {
    if (1..=100).contains(&percent) {
        Ok(())
    } else {
        Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Процент обрезки должен быть от 1 до 100",
        ))
    }
}

fn validate_plan(file: &mut File, plan: &TrimPlan) -> io::Result<()> {
    let metadata = file.metadata()?;
    if metadata.len() != plan.original_size
        || metadata.modified().ok() != plan.modified
        || file_identity(file)? != plan.identity
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Файл изменился после расчёта границы. Обновите позицию обрезки и повторите",
        ));
    }
    if plan.boundary_offset > plan.original_size {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Граница обрезки находится за концом файла",
        ));
    }
    file.seek(SeekFrom::Start(0))?;
    Ok(())
}

#[cfg(windows)]
fn open_exclusive(path: &Path, write: bool) -> io::Result<File> {
    use std::os::windows::fs::OpenOptionsExt;
    let mut options = OpenOptions::new();
    options.read(true).write(write).share_mode(0);
    options.open(path)
}

#[cfg(not(windows))]
fn open_exclusive(path: &Path, write: bool) -> io::Result<File> {
    let mut options = OpenOptions::new();
    options.read(true).write(write);
    options.open(path)
}

fn find_boundary(
    file: &mut File,
    size: u64,
    percent: u8,
    encoding: TextEncoding,
    cancelled: &AtomicBool,
    progress: &mut impl FnMut(u64, u64),
) -> io::Result<u64> {
    if cancelled.load(Ordering::Relaxed) {
        return Err(cancelled_error());
    }
    let bom = bom_length(encoding).min(size);
    let target = ((size / 100) * u64::from(percent))
        .saturating_add((size % 100) * u64::from(percent) / 100)
        .max(bom);
    if target >= size {
        progress(0, 0);
        if cancelled.load(Ordering::Relaxed) {
            return Err(cancelled_error());
        }
        return Ok(size);
    }

    let utf16 = matches!(encoding, TextEncoding::Utf16Le | TextEncoding::Utf16Be);
    let cursor = if utf16 { target.max(bom) } else { target };
    if utf16 && !(cursor - bom).is_multiple_of(2) {
        return find_boundary_from(file, size, cursor + 1, encoding, cancelled, progress);
    }
    if cursor < size && is_line_start(file, cursor, bom, encoding)? {
        progress(0, size - cursor);
        if cancelled.load(Ordering::Relaxed) {
            return Err(cancelled_error());
        }
        return Ok(cursor);
    }
    find_boundary_from(file, size, cursor, encoding, cancelled, progress)
}

fn find_boundary_from(
    file: &mut File,
    size: u64,
    cursor: u64,
    encoding: TextEncoding,
    cancelled: &AtomicBool,
    progress: &mut impl FnMut(u64, u64),
) -> io::Result<u64> {
    let utf16 = matches!(encoding, TextEncoding::Utf16Le | TextEncoding::Utf16Be);
    file.seek(SeekFrom::Start(cursor))?;

    let mut buffer = [0u8; BUFFER_SIZE];
    let mut pending_cr: Option<(u64, u64)> = None;
    let mut absolute = cursor;
    let mut carry: Option<(u8, u64)> = None;
    loop {
        if cancelled.load(Ordering::Relaxed) {
            return Err(cancelled_error());
        }
        let read = file.read(&mut buffer)?;
        if read == 0 {
            progress(size - cursor, size - cursor);
            return Ok(pending_cr.map_or(size, |(start, width)| start + width));
        }
        let mut index = 0;
        let mut found = None;
        if utf16 {
            if let Some((first, start)) = carry.take() {
                found = process_line_unit(
                    decode_utf16_unit(first, buffer[0], encoding),
                    start,
                    2,
                    &mut pending_cr,
                );
                index = 1;
            }
            while found.is_none() && index + 1 < read {
                found = process_line_unit(
                    decode_utf16_unit(buffer[index], buffer[index + 1], encoding),
                    absolute + index as u64,
                    2,
                    &mut pending_cr,
                );
                if found.is_none() {
                    index += 2;
                }
            }
            if found.is_none() && index < read {
                carry = Some((buffer[index], absolute + index as u64));
            }
        } else {
            while found.is_none() && index < read {
                found = process_line_unit(
                    u16::from(buffer[index]),
                    absolute + index as u64,
                    1,
                    &mut pending_cr,
                );
                index += 1;
            }
        }
        absolute += read as u64;
        progress(absolute - cursor, size - cursor);
        if cancelled.load(Ordering::Relaxed) {
            return Err(cancelled_error());
        }
        if let Some(boundary) = found {
            return Ok(boundary);
        }
        if absolute >= size {
            return Ok(pending_cr.map_or(size, |(start, width)| start + width));
        }
    }
}

fn process_line_unit(
    unit: u16,
    unit_start: u64,
    width: u64,
    pending_cr: &mut Option<(u64, u64)>,
) -> Option<u64> {
    if let Some((cr_start, cr_width)) = pending_cr.take() {
        return Some(if unit == 0x0A {
            unit_start + width
        } else {
            cr_start + cr_width
        });
    }
    match unit {
        0x0D => *pending_cr = Some((unit_start, width)),
        0x0A => return Some(unit_start + width),
        _ => {}
    }
    None
}

fn decode_utf16_unit(first: u8, second: u8, encoding: TextEncoding) -> u16 {
    if matches!(encoding, TextEncoding::Utf16Le) {
        u16::from_le_bytes([first, second])
    } else {
        u16::from_be_bytes([first, second])
    }
}

fn is_line_start(
    file: &mut File,
    offset: u64,
    bom: u64,
    encoding: TextEncoding,
) -> io::Result<bool> {
    if offset <= bom {
        return Ok(true);
    }
    let utf16 = matches!(encoding, TextEncoding::Utf16Le | TextEncoding::Utf16Be);
    let width = if utf16 { 2u64 } else { 1 };
    if offset < bom + width {
        return Ok(false);
    }
    let mut previous = [0u8; 2];
    file.seek(SeekFrom::Start(offset - width))?;
    file.read_exact(&mut previous[..width as usize])?;
    let previous = if utf16 {
        decode_utf16_unit(previous[0], previous[1], encoding)
    } else {
        u16::from(previous[0])
    };
    if previous == 0x0A {
        return Ok(true);
    }
    if previous != 0x0D {
        return Ok(false);
    }
    if offset + width > file.metadata()?.len() {
        return Ok(true);
    }
    let mut next = [0u8; 2];
    file.seek(SeekFrom::Start(offset))?;
    file.read_exact(&mut next[..width as usize])?;
    let next = if utf16 {
        decode_utf16_unit(next[0], next[1], encoding)
    } else {
        u16::from(next[0])
    };
    Ok(next != 0x0A)
}

fn bom_length(encoding: TextEncoding) -> u64 {
    match encoding {
        TextEncoding::Utf8Bom => 3,
        TextEncoding::Utf16Le | TextEncoding::Utf16Be => 2,
        TextEncoding::Utf8 | TextEncoding::Windows1251 => 0,
    }
}

#[cfg(windows)]
fn file_identity(file: &File) -> io::Result<FileIdentity> {
    use std::{ffi::c_void, os::windows::io::AsRawHandle};

    #[repr(C)]
    #[derive(Default)]
    struct ByHandleFileInformation {
        attributes: u32,
        creation_time_low: u32,
        creation_time_high: u32,
        access_time_low: u32,
        access_time_high: u32,
        write_time_low: u32,
        write_time_high: u32,
        volume_serial_number: u32,
        size_high: u32,
        size_low: u32,
        links: u32,
        index_high: u32,
        index_low: u32,
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetFileInformationByHandle(
            handle: *mut c_void,
            info: *mut ByHandleFileInformation,
        ) -> i32;
    }

    let mut info = ByHandleFileInformation::default();
    // Windows documents this call for any valid file handle, including the
    // exclusively opened handle used for planning and trimming.
    let success = unsafe { GetFileInformationByHandle(file.as_raw_handle().cast(), &mut info) };
    if success == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(FileIdentity {
        volume: Some(info.volume_serial_number),
        index: Some((u64::from(info.index_high) << 32) | u64::from(info.index_low)),
    })
}

#[cfg(not(windows))]
fn file_identity(_file: &File) -> io::Result<FileIdentity> {
    Ok(FileIdentity {
        volume: None,
        index: None,
    })
}

fn backup_path(path: &Path) -> io::Result<PathBuf> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    let name = path.file_name().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "Не удалось определить имя файла",
        )
    })?;
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    for attempt in 0..100u32 {
        let mut backup_name = name.to_os_string();
        backup_name.push(format!(
            ".rusttext-{stamp}-{}-{attempt}.bak",
            std::process::id()
        ));
        let candidate = parent.join(backup_name);
        if !candidate.exists() {
            return Ok(candidate);
        }
    }
    Err(io::Error::new(
        io::ErrorKind::AlreadyExists,
        "Не удалось подобрать имя резервной копии",
    ))
}

fn copy_to_backup<F, H>(
    source: &mut File,
    mut backup: File,
    size: u64,
    cancelled: &AtomicBool,
    progress: &mut F,
    hooks: &mut H,
) -> io::Result<()>
where
    F: FnMut(u64, u64),
    H: TrimIoHooks,
{
    source.seek(SeekFrom::Start(0))?;
    let mut buffer = vec![0u8; BUFFER_SIZE];
    let mut copied = 0u64;
    while copied < size {
        if cancelled.load(Ordering::Relaxed) {
            return Err(cancelled_error());
        }
        hooks.before_backup_chunk(copied)?;
        let buffer_len = buffer.len() as u64;
        let limit = (size - copied).min(buffer_len) as usize;
        let count = source.read(&mut buffer[..limit])?;
        if count == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "Файл неожиданно укоротился при создании копии",
            ));
        }
        backup.write_all(&buffer[..count])?;
        copied += count as u64;
        progress(copied, size);
    }
    backup.flush()?;
    backup.sync_all()?;
    Ok(())
}

fn shift_prefix<F, H>(
    source: &mut File,
    plan: &TrimPlan,
    bom: u64,
    progress: &mut F,
    hooks: &mut H,
) -> io::Result<()>
where
    F: FnMut(u64, u64),
    H: TrimIoHooks,
{
    let total = plan.original_size.saturating_sub(plan.boundary_offset);
    let mut buffer = vec![0u8; BUFFER_SIZE];
    let mut read_at = plan.boundary_offset;
    let mut write_at = bom;
    let mut shifted = 0u64;
    while shifted < total {
        let buffer_len = buffer.len() as u64;
        let limit = (total - shifted).min(buffer_len) as usize;
        let count = source.read_at(&mut buffer[..limit], read_at)?;
        if count == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "Файл неожиданно укоротился при переносе строк",
            ));
        }
        source.seek(SeekFrom::Start(write_at))?;
        source.write_all(&buffer[..count])?;
        read_at += count as u64;
        write_at += count as u64;
        shifted += count as u64;
        progress(shifted, total);
        hooks.after_shift_chunk(shifted)?;
    }
    source.set_len(bom.saturating_add(total))?;
    source.flush()?;
    source.sync_all()
}

#[cfg(windows)]
trait ReadAt {
    fn read_at(&self, buf: &mut [u8], offset: u64) -> io::Result<usize>;
}

#[cfg(windows)]
impl ReadAt for File {
    fn read_at(&self, buf: &mut [u8], offset: u64) -> io::Result<usize> {
        use std::os::windows::fs::FileExt;
        self.seek_read(buf, offset)
    }
}

#[cfg(not(windows))]
trait ReadAt {
    fn read_at(&self, buf: &mut [u8], offset: u64) -> io::Result<usize>;
}

#[cfg(not(windows))]
impl ReadAt for File {
    fn read_at(&self, buf: &mut [u8], offset: u64) -> io::Result<usize> {
        let mut file = self.try_clone()?;
        file.seek(SeekFrom::Start(offset))?;
        file.read(buf)
    }
}

fn restore_from_backup(source: &mut File, backup_path: &Path) -> io::Result<()> {
    let mut backup = File::open(backup_path)?;
    source.seek(SeekFrom::Start(0))?;
    backup.seek(SeekFrom::Start(0))?;
    let mut buffer = vec![0u8; BUFFER_SIZE];
    let mut restored = 0u64;
    loop {
        let count = backup.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        source.write_all(&buffer[..count])?;
        restored += count as u64;
    }
    source.set_len(restored)?;
    source.flush()?;
    source.sync_all()
}

fn cancelled_error() -> io::Error {
    io::Error::new(
        io::ErrorKind::Interrupted,
        "Операция отменена; исходный файл не изменён",
    )
}

#[cfg(test)]
#[path = "../tests/unit/log_trim.rs"]
mod tests;
