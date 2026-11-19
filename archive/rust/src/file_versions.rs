use std::{
    collections::{HashMap, HashSet},
    env,
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tempfile::NamedTempFile;

use crate::{
    files::FileVersion,
    text_format::{TextEncoding, TextFormat},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct VersionSettings {
    pub max_file_bytes: u64,
    pub total_bytes: u64,
}

impl Default for VersionSettings {
    fn default() -> Self {
        Self {
            max_file_bytes: 5 * 1024 * 1024,
            total_bytes: 100 * 1024 * 1024,
        }
    }
}

#[derive(Clone, Serialize, Deserialize)]
pub(crate) struct VersionInfo {
    pub id: String,
    pub path: PathBuf,
    pub captured_millis: u64,
    pub bytes: u64,
    checksum: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    encoding: Option<TextEncoding>,
}

pub(crate) struct VersionContent {
    pub info: VersionInfo,
    pub bytes: Vec<u8>,
}

impl VersionContent {
    pub(crate) fn decode(&self) -> io::Result<(String, TextFormat)> {
        let encoding = self.info.encoding.ok_or_else(|| invalid("Кодировка старой версии неизвестна. Восстановление недоступно; сохранённые байты не изменены."))?;
        let (text, format) =
            TextFormat::decode(&self.bytes, encoding == TextEncoding::Windows1251)?;
        if format.encoding != encoding {
            return Err(invalid(
                "Кодировка версии не совпадает с сохранёнными данными",
            ));
        }
        Ok((text, format))
    }
}

#[derive(Default, Serialize, Deserialize)]
struct Manifest {
    settings: VersionSettings,
    sequence: u64,
    versions: Vec<VersionInfo>,
}

pub(crate) struct VersionStore {
    directory: PathBuf,
}

impl VersionStore {
    pub(crate) fn load() -> io::Result<Self> {
        let directory = env::var_os("LOCALAPPDATA")
            .filter(|directory| !directory.is_empty())
            .map(PathBuf::from)
            .ok_or_else(|| io::Error::other("Папка LOCALAPPDATA недоступна"))?;
        Self::open(directory.join("RustText").join("versions"))
    }

    pub(crate) fn open(directory: PathBuf) -> io::Result<Self> {
        fs::create_dir_all(&directory)?;
        require_directory(&directory)?;
        let directory = directory.canonicalize()?;
        let store = Self { directory };
        let _lock = store.lock()?;
        store.read_manifest()?;
        Ok(store)
    }

    pub(crate) fn settings(&self) -> io::Result<VersionSettings> {
        let _lock = self.lock()?;
        Ok(self.read_manifest()?.settings)
    }

    pub(crate) fn set_settings(&self, settings: VersionSettings) -> io::Result<()> {
        let _lock = self.lock()?;
        let mut manifest = self.read_manifest()?;
        manifest.settings = settings;
        let removed = prune(&mut manifest)?;
        self.write_manifest(&manifest)?;
        self.remove_unused(&manifest, &removed)
    }

    pub(crate) fn capture(
        &self,
        path: &Path,
        encoding: TextEncoding,
    ) -> io::Result<Option<VersionInfo>> {
        let _lock = self.lock()?;
        self.capture_locked(path, encoding, false)
    }

    pub(crate) fn list(&self, path: &Path) -> io::Result<Vec<VersionInfo>> {
        let _lock = self.lock()?;
        let path = normalized_path(path)?;
        Ok(self
            .read_manifest()?
            .versions
            .into_iter()
            .filter(|version| version.path == path)
            .collect())
    }

    pub(crate) fn load_version(&self, path: &Path, id: &str) -> io::Result<VersionContent> {
        let _lock = self.lock()?;
        self.load_locked(path, id)
    }

    pub(crate) fn prepare_restore(
        &self,
        path: &Path,
        id: &str,
        current_encoding: TextEncoding,
    ) -> io::Result<VersionContent> {
        let _lock = self.lock()?;
        let content = self.load_locked(path, id)?;
        content.decode()?;
        self.capture_locked(path, current_encoding, true)?;
        Ok(content)
    }

    fn capture_locked(
        &self,
        path: &Path,
        encoding: TextEncoding,
        required: bool,
    ) -> io::Result<Option<VersionInfo>> {
        let path = path.canonicalize()?;
        if path.starts_with(&self.directory) {
            return Err(invalid("Нельзя сохранять версии файлов хранилища"));
        }
        let mut manifest = self.read_manifest()?;
        let before = FileVersion::at(&path)?;
        let file = File::open(&path)?;
        if file.metadata()?.len() > manifest.settings.max_file_bytes {
            return if required {
                Err(invalid("Текущий файл превышает лимит версий"))
            } else {
                Ok(None)
            };
        }
        let mut bytes = Vec::new();
        file.take(manifest.settings.max_file_bytes.saturating_add(1))
            .read_to_end(&mut bytes)?;
        if bytes.len() as u64 > manifest.settings.max_file_bytes
            || FileVersion::at(&path)? != before
        {
            return Err(invalid("Файл изменился во время сохранения версии"));
        }
        let checksum = checksum(&bytes);
        if let Some(latest) = manifest
            .versions
            .iter()
            .find(|version| version.path == path)
            && latest.checksum == checksum
            && latest.encoding == Some(encoding)
        {
            let latest = latest.clone();
            self.read_blob(&latest)?;
            return Ok(Some(latest));
        }
        manifest.sequence = manifest
            .sequence
            .checked_add(1)
            .ok_or_else(|| invalid("Исчерпаны номера версий"))?;
        let info = VersionInfo {
            id: format!("{:016x}", manifest.sequence),
            path,
            captured_millis: SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_millis()
                .min(u64::MAX as u128) as u64,
            bytes: bytes.len() as u64,
            checksum,
            encoding: Some(encoding),
        };
        let previously_referenced = manifest
            .versions
            .iter()
            .any(|version| version.checksum == info.checksum);
        manifest.versions.insert(0, info.clone());
        let removed = prune(&mut manifest)?;
        let blobs = self.directory.join("blobs");
        fs::create_dir_all(&blobs)?;
        require_directory(&blobs)?;
        let blob = blobs.join(&info.checksum);
        let created = match fs::symlink_metadata(&blob) {
            Ok(_) => {
                self.read_blob(&info)?;
                false
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                atomic_write(&blob, &bytes)?;
                true
            }
            Err(error) => return Err(error),
        };
        if let Err(error) = self.write_manifest(&manifest) {
            if created && !previously_referenced {
                fs::remove_file(&blob)?;
            }
            return Err(error);
        }
        self.remove_unused(&manifest, &removed)?;
        Ok(Some(info))
    }

    fn load_locked(&self, path: &Path, id: &str) -> io::Result<VersionContent> {
        if !valid_hex(id, 16) {
            return Err(invalid("Некорректный номер версии"));
        }
        let path = normalized_path(path)?;
        let info = self
            .read_manifest()?
            .versions
            .into_iter()
            .find(|version| version.id == id && version.path == path)
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "Версия не найдена"))?;
        let bytes = self.read_blob(&info)?;
        Ok(VersionContent { info, bytes })
    }

    fn lock(&self) -> io::Result<File> {
        require_directory(&self.directory)?;
        let path = self.directory.join("versions.lock");
        require_regular_if_exists(&path)?;
        let lock = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(path)?;
        lock.lock()?;
        Ok(lock)
    }

    fn read_manifest(&self) -> io::Result<Manifest> {
        let path = self.directory.join("manifest.json");
        require_regular_if_exists(&path)?;
        let bytes = match fs::read(path) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Manifest::default()),
            Err(error) => return Err(error),
        };
        let manifest: Manifest =
            serde_json::from_slice(&bytes).map_err(|error| invalid(error.to_string()))?;
        let mut ids = HashSet::new();
        let mut previous = u64::MAX;
        for version in &manifest.versions {
            if !valid_hex(&version.id, 16)
                || !valid_hex(&version.checksum, 64)
                || !version.path.is_absolute()
                || !ids.insert(&version.id)
            {
                return Err(invalid("Повреждены данные версий"));
            }
            let sequence = u64::from_str_radix(&version.id, 16)
                .map_err(|_| invalid("Некорректный номер версии"))?;
            if sequence > manifest.sequence || sequence >= previous {
                return Err(invalid("Повреждён порядок версий"));
            }
            previous = sequence;
        }
        Ok(manifest)
    }

    fn read_blob(&self, version: &VersionInfo) -> io::Result<Vec<u8>> {
        if !valid_hex(&version.checksum, 64) {
            return Err(invalid("Некорректная контрольная сумма версии"));
        }
        let blobs = self.directory.join("blobs");
        require_directory(&blobs)?;
        let path = blobs.join(&version.checksum);
        require_regular_if_exists(&path)?;
        let file = File::open(path)?;
        if file.metadata()?.len() != version.bytes {
            return Err(invalid("Размер версии не совпадает"));
        }
        let mut bytes = Vec::new();
        file.take(version.bytes.saturating_add(1))
            .read_to_end(&mut bytes)?;
        if bytes.len() as u64 != version.bytes || checksum(&bytes) != version.checksum {
            return Err(invalid("Версия повреждена"));
        }
        Ok(bytes)
    }

    fn write_manifest(&self, manifest: &Manifest) -> io::Result<()> {
        let bytes = serde_json::to_vec(manifest)?;
        atomic_write(&self.directory.join("manifest.json"), &bytes)
    }

    fn remove_unused(&self, manifest: &Manifest, removed: &[VersionInfo]) -> io::Result<()> {
        let blobs = self.directory.join("blobs");
        if removed.is_empty() {
            return Ok(());
        }
        require_directory(&blobs)?;
        let retained: HashSet<_> = manifest
            .versions
            .iter()
            .map(|version| &version.checksum)
            .collect();
        for version in removed {
            if !retained.contains(&version.checksum) {
                let path = blobs.join(&version.checksum);
                require_regular_if_exists(&path)?;
                match fs::remove_file(path) {
                    Ok(()) => {}
                    Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                    Err(error) => return Err(error),
                }
            }
        }
        Ok(())
    }
}

fn prune(manifest: &mut Manifest) -> io::Result<Vec<VersionInfo>> {
    let mut newest = HashMap::new();
    let mut total = 0u64;
    for version in &manifest.versions {
        total = total
            .checked_add(version.bytes)
            .ok_or_else(|| invalid("Размер хранилища переполнен"))?;
        newest.entry(&version.path).or_insert(&version.id);
    }
    let mut removed = Vec::new();
    for index in (0..manifest.versions.len()).rev() {
        if total <= manifest.settings.total_bytes {
            break;
        }
        let version = &manifest.versions[index];
        if newest.get(&version.path).copied() != Some(&version.id) {
            total -= version.bytes;
            removed.push(index);
        }
    }
    if total > manifest.settings.total_bytes {
        return Err(invalid(
            "Квота не вмещает последние версии файлов. Увеличьте квоту",
        ));
    }
    Ok(removed
        .into_iter()
        .map(|index| manifest.versions.remove(index))
        .collect())
}

fn normalized_path(path: &Path) -> io::Result<PathBuf> {
    match path.canonicalize() {
        Ok(path) => Ok(path),
        Err(error) if error.kind() == io::ErrorKind::NotFound && path.is_absolute() => {
            Ok(path.to_path_buf())
        }
        Err(error) => Err(error),
    }
}

fn checksum(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect()
}

fn valid_hex(value: &str, length: usize) -> bool {
    value.len() == length
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.into())
}

fn require_directory(path: &Path) -> io::Result<()> {
    if !fs::symlink_metadata(path)?.is_dir() {
        return Err(invalid("Папка версий недоступна"));
    }
    Ok(())
}

fn require_regular_if_exists(path: &Path) -> io::Result<()> {
    match fs::symlink_metadata(path) {
        Ok(metadata) if !metadata.is_file() => Err(invalid("Файл версий недоступен")),
        Ok(_) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error),
    }
}

fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    require_regular_if_exists(path)?;
    let directory = path
        .parent()
        .ok_or_else(|| invalid("Не указана папка версий"))?;
    let mut pending = NamedTempFile::new_in(directory)?;
    pending.write_all(bytes)?;
    pending.as_file().sync_all()?;
    pending.persist(path).map_err(|error| error.error)?;
    Ok(())
}

#[cfg(test)]
#[path = "../tests/unit/file_versions.rs"]
mod tests;
