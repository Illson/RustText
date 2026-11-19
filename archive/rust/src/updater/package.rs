use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Seek, SeekFrom, Write},
    path::Path,
    time::Duration,
};

use sha2::{Digest, Sha256};
use tempfile::NamedTempFile;
use zip::ZipArchive;

use super::{Release, release};

const MAX_EXECUTABLE_BYTES: u64 = 256 * 1024 * 1024;
const MAX_ZIP_ENTRIES: usize = 4096;

pub fn download_and_verify(release: &Release, replacement_path: &Path) -> Result<(), String> {
    release::validate_release(release)?;
    if replacement_path.exists() {
        return Err("Путь для обновления уже занят.".to_owned());
    }

    let agent = release::new_agent(Duration::from_secs(120));
    let mut archive_file = NamedTempFile::new()
        .map_err(|error| format!("Не удалось создать временный архив. {error}"))?;
    let downloaded_size = release::with_response(
        &agent,
        &release.zip_url,
        release::max_archive_bytes(),
        |response| {
            let mut body = response.into_body();
            let mut body = body
                .with_config()
                .limit(release::max_archive_bytes())
                .reader();
            io::copy(&mut body, archive_file.as_file_mut()).map_err(|error| error.to_string())
        },
    )?;
    if downloaded_size != release.archive_size {
        return Err("Размер скачанного архива не совпадает с данными выпуска.".to_owned());
    }
    archive_file
        .as_file()
        .sync_all()
        .map_err(|error| format!("Не удалось сохранить архив RustText. {error}"))?;
    let downloaded_digest = hash_archive(archive_file.as_file_mut())?;
    if release
        .archive_sha256
        .as_ref()
        .is_some_and(|expected| !expected.eq_ignore_ascii_case(&downloaded_digest))
    {
        return Err("Контрольная сумма архива не совпадает с GitHub Releases.".to_owned());
    }

    let checksum_text = release::with_response(
        &agent,
        &release.sha256_url,
        release::max_checksum_bytes(),
        |response| {
            let mut body = response.into_body();
            body.with_config()
                .limit(release::max_checksum_bytes())
                .read_to_string()
                .map_err(|error| error.to_string())
        },
    )?;
    let expected_digest = parse_checksum(&checksum_text)?;
    if !expected_digest.eq_ignore_ascii_case(&downloaded_digest) {
        return Err("Контрольная сумма архива RustText не совпадает с файлом SHA-256.".to_owned());
    }

    let archive_handle = archive_file
        .reopen()
        .map_err(|error| format!("Не удалось открыть архив RustText. {error}"))?;
    let mut archive = ZipArchive::new(archive_handle)
        .map_err(|error| format!("Архив RustText не является корректным ZIP-файлом. {error}"))?;
    write_verified_executable(&mut archive, replacement_path)
}

fn hash_archive(file: &mut File) -> Result<String, String> {
    file.seek(SeekFrom::Start(0))
        .map_err(|error| format!("Не удалось прочитать архив RustText. {error}"))?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let count = file
            .read(&mut buffer)
            .map_err(|error| format!("Не удалось прочитать архив RustText. {error}"))?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }

    let mut output = String::with_capacity(64);
    for byte in digest.finalize() {
        use std::fmt::Write as _;
        let _ = write!(output, "{byte:02x}");
    }
    Ok(output)
}

fn write_verified_executable<R: Read + Seek>(
    archive: &mut ZipArchive<R>,
    replacement_path: &Path,
) -> Result<(), String> {
    if archive.len() > MAX_ZIP_ENTRIES {
        return Err("В архиве RustText слишком много файлов.".to_owned());
    }

    let mut found_index = None;
    for index in 0..archive.len() {
        let entry = archive
            .by_index(index)
            .map_err(|error| format!("Не удалось прочитать список файлов архива. {error}"))?;
        if entry.name() == "rusttext.exe" {
            if found_index.replace(index).is_some() || entry.is_dir() {
                return Err("Архив содержит повторный или некорректный rusttext.exe.".to_owned());
            }
            if entry.size() == 0 || entry.size() > MAX_EXECUTABLE_BYTES {
                return Err("Исполняемый файл в архиве имеет недопустимый размер.".to_owned());
            }
            if entry
                .unix_mode()
                .is_some_and(|mode| mode & 0o170000 == 0o120000)
            {
                return Err("Исполняемый файл в архиве является ссылкой.".to_owned());
            }
        }
    }
    let index = found_index.ok_or_else(|| "В архиве не найден rusttext.exe.".to_owned())?;
    let mut executable = archive
        .by_index(index)
        .map_err(|error| format!("Не удалось открыть rusttext.exe. {error}"))?;
    let declared_size = executable.size();
    write_new_file(replacement_path, &mut executable, declared_size)
}

fn parse_checksum(text: &str) -> Result<String, String> {
    let line = text.trim();
    let (hash, filename) = line
        .split_once(char::is_whitespace)
        .ok_or_else(|| "Файл SHA-256 имеет неправильный формат.".to_owned())?;
    if !release::is_sha256(hash) {
        return Err("Файл SHA-256 содержит некорректную контрольную сумму.".to_owned());
    }
    let filename = filename.trim().trim_start_matches('*');
    if filename != "RustText.zip" {
        return Err("Файл SHA-256 ссылается на другой архив.".to_owned());
    }
    Ok(hash.to_ascii_lowercase())
}

fn verify_pe_x64(executable: &mut (impl Read + Seek)) -> Result<(), String> {
    let mut dos_header = [0_u8; 64];
    executable
        .read_exact(&mut dos_header)
        .map_err(|_| "Файл обновления не является исполняемым файлом Windows.".to_owned())?;
    if &dos_header[..2] != b"MZ" {
        return Err("Файл обновления не является исполняемым файлом Windows.".to_owned());
    }
    let pe_offset = u32::from_le_bytes(dos_header[60..64].try_into().unwrap()) as u64;
    if pe_offset > 1024 * 1024 {
        return Err("Заголовок rusttext.exe повреждён.".to_owned());
    }
    executable
        .seek(SeekFrom::Start(pe_offset))
        .map_err(|_| "Заголовок rusttext.exe повреждён.".to_owned())?;
    let mut pe_header = [0_u8; 26];
    executable
        .read_exact(&mut pe_header)
        .map_err(|_| "Заголовок rusttext.exe повреждён.".to_owned())?;
    if &pe_header[..4] != b"PE\0\0"
        || u16::from_le_bytes([pe_header[4], pe_header[5]]) != 0x8664
        || u16::from_le_bytes([pe_header[24], pe_header[25]]) != 0x20b
    {
        return Err("Файл обновления не является исполняемым файлом Windows x64.".to_owned());
    }
    Ok(())
}

fn write_new_file(
    path: &Path,
    executable: &mut impl Read,
    expected_size: u64,
) -> Result<(), String> {
    let mut file = OpenOptions::new()
        .read(true)
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|error| format!("Не удалось создать файл обновления. {error}"))?;
    let result = (|| {
        let copied = io::copy(&mut executable.take(MAX_EXECUTABLE_BYTES + 1), &mut file)?;
        if copied != expected_size {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Размер распакованного rusttext.exe не совпадает с архивом.",
            ));
        }
        file.seek(SeekFrom::Start(0))?;
        verify_pe_x64(&mut file)
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
        file.flush()?;
        file.sync_all()
    })();
    if let Err(error) = result {
        drop(file);
        let _ = fs::remove_file(path);
        return Err(format!("Не удалось сохранить rusttext.exe. {error}"));
    }
    Ok(())
}

#[cfg(test)]
fn hex_sha256(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    let mut output = String::with_capacity(digest.len() * 2);
    for byte in digest {
        use std::fmt::Write as _;
        let _ = write!(output, "{byte:02x}");
    }
    output
}

#[cfg(test)]
#[path = "../../tests/unit/updater_package.rs"]
mod tests;
