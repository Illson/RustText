#[cfg(not(windows))]
use std::fs;
use std::{
    fs::{File, Metadata},
    io::{self, Read, Write},
    path::{Path, PathBuf},
    time::SystemTime,
};

use serde::{Deserialize, Serialize};
use tempfile::NamedTempFile;

use crate::text_format::TextFormat;

#[cfg(windows)]
pub(crate) mod windows_save;

pub(crate) const MAX_EDITOR_BYTES: u64 = 50 * 1024 * 1024;

#[derive(Clone, Copy, Eq, Serialize, Deserialize)]
pub struct FileVersion {
    bytes: u64,
    modified: SystemTime,
    created: Option<SystemTime>,
    #[serde(skip)]
    file_id: Option<(u64, u128)>,
}

impl PartialEq for FileVersion {
    fn eq(&self, other: &Self) -> bool {
        self.bytes == other.bytes
            && self.modified == other.modified
            && self.created == other.created
            && match (self.file_id, other.file_id) {
                (Some(left), Some(right)) => left == right,
                _ => true,
            }
    }
}

impl FileVersion {
    fn from_metadata_with_id(
        metadata: &Metadata,
        file_id: Option<(u64, u128)>,
    ) -> io::Result<Self> {
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
            file_id,
        })
    }

    #[cfg(not(windows))]
    fn from_metadata(metadata: &Metadata) -> io::Result<Self> {
        Self::from_metadata_with_id(metadata, None)
    }

    fn from_file(file: &File) -> io::Result<Self> {
        #[cfg(windows)]
        let file_id = windows_save::file_id(file)?;
        #[cfg(not(windows))]
        let file_id = None;
        Self::from_metadata_with_id(&file.metadata()?, file_id)
    }

    pub fn at(path: &Path) -> io::Result<Option<Self>> {
        #[cfg(windows)]
        {
            match windows_save::open_metadata_file(path)? {
                Some(file) => Self::from_file(&file).map(Some),
                None => Ok(None),
            }
        }
        #[cfg(not(windows))]
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
    let version = FileVersion::from_file(&file)?;
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
    #[cfg(windows)]
    let mut pending = if target.version.is_some() {
        windows_save::private_temporary_file(directory)?
    } else {
        NamedTempFile::new_in(directory)?
    };
    #[cfg(not(windows))]
    let mut pending = NamedTempFile::new_in(directory)?;
    pending.write_all(&bytes)?;
    pending.as_file().sync_all()?;

    publish(&target, pending)?;
    target.version = Some(
        FileVersion::at(&target.path)?.ok_or_else(|| io::Error::from(io::ErrorKind::NotFound))?,
    );
    Ok(target)
}

pub(crate) fn publish(target: &FileTarget, pending: NamedTempFile) -> io::Result<()> {
    publish_pending(target, pending, || Ok(()))
}

fn publish_pending(
    target: &FileTarget,
    pending: NamedTempFile,
    before_replace: impl FnOnce() -> io::Result<()>,
) -> io::Result<()> {
    let directory = target
        .path
        .parent()
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidData, "Укажите папку файла"))?;
    #[cfg(windows)]
    let guard = if target.version.is_some() {
        Some(windows_save::replacement_guard(&target.path)?)
    } else {
        None
    };
    #[cfg(windows)]
    if let Some(guard) = &guard
        && Some(FileVersion::from_file(guard)?) != target.version
    {
        return Err(file_changed());
    }
    if FileVersion::at(&target.path)? != target.version {
        return Err(file_changed());
    }
    before_replace()?;
    if target.version.is_some() {
        #[cfg(windows)]
        windows_save::replace(
            &target.path,
            guard.expect("replacement guard must exist for existing target"),
            pending,
            directory,
        )?;
        #[cfg(not(windows))]
        pending.persist(&target.path).map_err(|error| error.error)?;
    } else {
        pending
            .persist_noclobber(&target.path)
            .map_err(|error| error.error)?;
    }
    Ok(())
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
#[path = "../tests/unit/files.rs"]
mod tests;
