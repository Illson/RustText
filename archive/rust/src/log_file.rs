use std::{
    fs::File,
    io::{self, Read, Seek, SeekFrom},
    path::Path,
    time::SystemTime,
};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

const WITNESS_BYTES: u64 = 256;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct FileIdentity {
    volume: u64,
    id: [u8; 16],
}

impl FileIdentity {
    #[cfg(windows)]
    pub(crate) fn read(file: &File) -> io::Result<Self> {
        use std::os::windows::io::AsRawHandle;
        use windows::Win32::{
            Foundation::HANDLE,
            Storage::FileSystem::{FILE_ID_INFO, FileIdInfo, GetFileInformationByHandleEx},
        };

        let mut info = FILE_ID_INFO::default();
        // The handle is valid for this call and the buffer has the layout required by FileIdInfo.
        unsafe {
            GetFileInformationByHandleEx(
                HANDLE(file.as_raw_handle()),
                FileIdInfo,
                (&mut info as *mut FILE_ID_INFO).cast(),
                size_of::<FILE_ID_INFO>() as u32,
            )
        }
        .map_err(|error| io::Error::from_raw_os_error(i32::from(error.code().0 as u16)))?;
        Ok(Self {
            volume: info.VolumeSerialNumber,
            id: info.FileId.Identifier,
        })
    }

    #[cfg(unix)]
    pub(crate) fn read(file: &File) -> io::Result<Self> {
        use std::os::unix::fs::MetadataExt as _;
        let metadata = file.metadata()?;
        let mut id = [0; 16];
        id[..8].copy_from_slice(&metadata.ino().to_le_bytes());
        Ok(Self {
            volume: metadata.dev(),
            id,
        })
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct FileStamp {
    pub identity: FileIdentity,
    pub len: u64,
    modified: Option<SystemTime>,
}

impl FileStamp {
    pub(crate) fn read(file: &File) -> io::Result<Self> {
        let metadata = file.metadata()?;
        if !metadata.is_file() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Выберите обычный файл.",
            ));
        }
        Ok(Self {
            identity: FileIdentity::read(file)?,
            len: metadata.len(),
            modified: metadata.modified().ok(),
        })
    }
}

#[derive(Clone)]
pub(crate) struct Observation {
    pub stamp: FileStamp,
    head: Vec<u8>,
    tail: Vec<u8>,
}

impl Observation {
    fn sample(file: &mut File, stamp: FileStamp) -> io::Result<Self> {
        let count = stamp.len.min(WITNESS_BYTES);
        let head = read_at(file, 0, count)?;
        let tail = read_at(file, stamp.len - count, count)?;
        Ok(Self { stamp, head, tail })
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Change {
    Same,
    Appended,
    Truncated,
    Replaced,
    Rewritten,
}

pub(crate) fn observe(
    path: &Path,
    previous: Option<&Observation>,
) -> io::Result<(Observation, Change)> {
    observe_file(&mut File::open(path)?, previous)
}

pub(crate) fn observe_file(
    file: &mut File,
    previous: Option<&Observation>,
) -> io::Result<(Observation, Change)> {
    let new = FileStamp::read(file)?;
    let change = match previous {
        None => Change::Replaced,
        Some(previous) if previous.stamp == new => {
            let current = Observation::sample(file, new)?;
            let change = if current.head == previous.head && current.tail == previous.tail {
                Change::Same
            } else {
                Change::Rewritten
            };
            return Ok((current, change));
        }
        Some(previous) if previous.stamp.identity != new.identity => Change::Replaced,
        Some(previous) if new.len < previous.stamp.len => Change::Truncated,
        Some(previous) if new.len > previous.stamp.len => {
            let head = read_at(file, 0, previous.head.len() as u64)?;
            let old_end = previous.stamp.len;
            let tail_len = previous.tail.len() as u64;
            let tail = read_at(file, old_end - tail_len, tail_len)?;
            if head == previous.head && tail == previous.tail {
                Change::Appended
            } else {
                Change::Rewritten
            }
        }
        Some(_) => Change::Rewritten,
    };
    Ok((Observation::sample(file, new)?, change))
}

fn read_at(file: &mut File, offset: u64, count: u64) -> io::Result<Vec<u8>> {
    file.seek(SeekFrom::Start(offset))?;
    let mut bytes = vec![0; count as usize];
    file.read_exact(&mut bytes)?;
    Ok(bytes)
}

fn boundary_digest(head: &[u8], tail: &[u8]) -> [u8; 32] {
    let mut hash = Sha256::new();
    hash.update(head);
    hash.update(tail);
    hash.finalize().into()
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct LogFingerprint {
    modified: Option<SystemTime>,
    digest: [u8; 32],
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct LogBookmark {
    pub identity: Option<FileIdentity>,
    pub size: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fingerprint: Option<LogFingerprint>,
    pub offset: u64,
    pub hidden: u64,
    pub auto_refresh: bool,
    pub follow_end: bool,
}

impl Default for LogBookmark {
    fn default() -> Self {
        Self {
            identity: None,
            size: 0,
            fingerprint: None,
            offset: 0,
            hidden: 0,
            auto_refresh: true,
            follow_end: false,
        }
    }
}

impl LogBookmark {
    pub(crate) fn record_observation(&mut self, observation: &Observation) {
        self.identity = Some(observation.stamp.identity);
        self.size = observation.stamp.len;
        self.fingerprint = Some(LogFingerprint {
            modified: observation.stamp.modified,
            digest: boundary_digest(&observation.head, &observation.tail),
        });
    }

    pub(crate) fn matches_stamp(&self, stamp: &FileStamp) -> bool {
        self.identity == Some(stamp.identity) && stamp.len >= self.size
    }

    pub(crate) fn can_restore(
        &self,
        file: &mut File,
        observation: &Observation,
    ) -> io::Result<bool> {
        let at_start = self.offset == 0 && self.hidden == 0;
        if self.identity.is_none() {
            return Ok(at_start);
        }
        if !self.matches_stamp(&observation.stamp) {
            return Ok(false);
        }
        let Some(saved) = &self.fingerprint else {
            // Older sessions have no recorded evidence for a nonzero boundary.
            return Ok(at_start);
        };

        let digest = if observation.stamp.len == self.size {
            if matches!((saved.modified, observation.stamp.modified), (Some(old), Some(new)) if old != new)
            {
                return Ok(false);
            }
            boundary_digest(&observation.head, &observation.tail)
        } else {
            // Appending moves EOF. Compare the same byte ranges we sampled
            // when saving this bookmark.
            let count = self.size.min(WITNESS_BYTES);
            let tail = read_at(file, self.size - count, count)?;
            boundary_digest(&observation.head[..count as usize], &tail)
        };
        Ok(digest == saved.digest)
    }
}

#[cfg(test)]
#[path = "../tests/unit/log_file.rs"]
mod tests;
