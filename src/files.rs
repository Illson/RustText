use std::{
    fs::{self, File, Metadata},
    io::{self, Read, Write},
    path::{Path, PathBuf},
    time::SystemTime,
};

use serde::{Deserialize, Serialize};
use tempfile::NamedTempFile;

use crate::text_format::TextFormat;

const MAX_EDITOR_BYTES: u64 = 50 * 1024 * 1024;

#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileVersion {
    bytes: u64,
    modified: SystemTime,
    created: Option<SystemTime>,
}

impl FileVersion {
    fn from_metadata(metadata: &Metadata) -> io::Result<Self> {
        if !metadata.is_file() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Выберите обычный файл",
            ));
        }

        Ok(Self {
            bytes: metadata.len(),
            modified: metadata.modified()?,
            created: metadata.created().ok(),
        })
    }

    pub fn at(path: &Path) -> io::Result<Option<Self>> {
        match fs::metadata(path) {
            Ok(metadata) => Self::from_metadata(&metadata).map(Some),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error),
        }
    }
}

#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileTarget {
    pub path: PathBuf,
    pub version: Option<FileVersion>,
}

impl FileTarget {
    pub fn from_picker(path: PathBuf) -> io::Result<Self> {
        let version = FileVersion::at(&path)?;
        let path = match version {
            Some(_) => path.canonicalize()?,
            None => {
                let name = path.file_name().ok_or_else(|| {
                    io::Error::new(io::ErrorKind::InvalidData, "Укажите имя файла")
                })?;
                let parent = path.parent().ok_or_else(|| {
                    io::Error::new(io::ErrorKind::InvalidData, "Укажите папку файла")
                })?;
                parent.canonicalize()?.join(name)
            }
        };
        Ok(Self { path, version })
    }
}

pub struct OpenedFile {
    pub target: FileTarget,
    pub text: String,
    pub format: TextFormat,
}

pub fn open(path: &Path, windows_1251: bool) -> io::Result<OpenedFile> {
    let path = path.canonicalize()?;
    let file = File::open(&path)?;
    let version = FileVersion::from_metadata(&file.metadata()?)?;
    if version.bytes > MAX_EDITOR_BYTES {
        return Err(file_too_large());
    }

    let mut bytes = Vec::new();
    file.take(MAX_EDITOR_BYTES + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > MAX_EDITOR_BYTES {
        return Err(file_too_large());
    }
    if FileVersion::at(&path)? != Some(version) {
        return Err(file_changed());
    }

    let (text, format) = TextFormat::decode(&bytes, windows_1251)?;
    Ok(OpenedFile {
        target: FileTarget {
            path,
            version: Some(version),
        },
        text,
        format,
    })
}

pub fn save(mut target: FileTarget, format: TextFormat, text: &str) -> io::Result<FileTarget> {
    let bytes = format.encode(text)?;
    let directory = target
        .path
        .parent()
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "Укажите папку файла"))?;
    let mut pending = NamedTempFile::new_in(directory)?;
    pending.write_all(&bytes)?;
    pending.as_file().sync_all()?;

    if FileVersion::at(&target.path)? != target.version {
        return Err(file_changed());
    }

    let version = FileVersion::from_metadata(&pending.as_file().metadata()?)?;
    if target.version.is_some() {
        pending.persist(&target.path).map_err(|error| error.error)?;
    } else {
        pending
            .persist_noclobber(&target.path)
            .map_err(|error| error.error)?;
    }
    target.version = Some(version);
    Ok(target)
}

fn file_too_large() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "Лимит редактора — 50 МиБ. Просмотр больших файлов появится на следующем этапе",
    )
}

fn file_changed() -> io::Error {
    io::Error::other(
        "Файл изменился на диске. Откройте его заново или сохраните правки в отдельный файл",
    )
}

#[cfg(test)]
mod tests {
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
}
