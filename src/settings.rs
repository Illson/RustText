use std::{
    env,
    fs::{self, File},
    io::{self, Write},
    path::{Path, PathBuf},
};

use gpui_kit::component::input::{Rope, RopeExt};
use serde::{Deserialize, Serialize};
use tempfile::NamedTempFile;

pub(crate) const DEFAULT_UNTITLED_TAB_LIMIT: usize = 10;
pub(crate) const MIN_UNTITLED_TAB_LIMIT: usize = 1;
pub(crate) const MAX_UNTITLED_TAB_LIMIT: usize = 200;
const DEFAULT_UNTITLED_NAME: &str = "Без имени";

fn default_untitled_tab_limit() -> usize {
    DEFAULT_UNTITLED_TAB_LIMIT
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct Settings {
    #[serde(default = "default_untitled_tab_limit")]
    pub untitled_tab_limit: usize,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            untitled_tab_limit: DEFAULT_UNTITLED_TAB_LIMIT,
        }
    }
}

impl Settings {
    pub(crate) fn load() -> (Self, Option<io::Error>) {
        let Some(path) = settings_path() else {
            return (Self::default(), None);
        };
        match read_settings(&path) {
            Ok(settings) => (settings, None),
            Err(error) => (Self::default(), Some(error)),
        }
    }

    pub(crate) fn save(&self) -> io::Result<()> {
        let path =
            settings_path().ok_or_else(|| io::Error::other("Папка LOCALAPPDATA недоступна"))?;
        write_settings(&path, self)
    }

    fn normalized(mut self) -> Self {
        self.untitled_tab_limit = clamp_untitled_tab_limit(self.untitled_tab_limit);
        self
    }
}

pub(crate) fn clamp_untitled_tab_limit(limit: usize) -> usize {
    limit.clamp(MIN_UNTITLED_TAB_LIMIT, MAX_UNTITLED_TAB_LIMIT)
}

pub(crate) fn parse_untitled_tab_limit(raw: &str) -> Result<usize, &'static str> {
    let value = raw
        .trim()
        .parse::<usize>()
        .map_err(|_| "Укажите целое число от 1 до 200.")?;
    if !(MIN_UNTITLED_TAB_LIMIT..=MAX_UNTITLED_TAB_LIMIT).contains(&value) {
        return Err("Лимит символов должен быть от 1 до 200.");
    }
    Ok(value)
}

pub(crate) fn untitled_tab_name(first_line: &str, limit: usize) -> String {
    let limit = clamp_untitled_tab_limit(limit);
    let line = first_line.split(['\r', '\n']).next().unwrap_or("");
    let trimmed = line.trim();
    if trimmed.is_empty() {
        DEFAULT_UNTITLED_NAME.to_owned()
    } else {
        trimmed.chars().take(limit).collect()
    }
}

pub(crate) fn untitled_tab_name_rope(text: &Rope, limit: usize) -> String {
    if text.len() == 0 {
        return DEFAULT_UNTITLED_NAME.to_owned();
    }
    let limit = clamp_untitled_tab_limit(limit);
    let mut chars = text
        .slice_line(0)
        .chars()
        .take_while(|&character| character != '\r' && character != '\n');
    let mut out = String::new();
    let mut count = 0usize;
    let mut started = false;
    for character in chars.by_ref() {
        if !started && character.is_whitespace() {
            continue;
        }
        started = true;
        out.push(character);
        count += 1;
        if count == limit {
            break;
        }
    }
    if out.is_empty() {
        return DEFAULT_UNTITLED_NAME.to_owned();
    }
    if count == limit
        && out.chars().last().is_some_and(char::is_whitespace)
        && chars.any(|character| !character.is_whitespace())
    {
        return out;
    }
    let trimmed_len = out.trim_end().len();
    if trimmed_len == 0 {
        DEFAULT_UNTITLED_NAME.to_owned()
    } else {
        out.truncate(trimmed_len);
        out
    }
}

fn settings_path() -> Option<PathBuf> {
    env::var_os("LOCALAPPDATA")
        .filter(|directory| !directory.is_empty())
        .map(PathBuf::from)
        .map(|directory| directory.join("RustText").join("settings.json"))
}

fn read_settings(path: &Path) -> io::Result<Settings> {
    let bytes = match fs::read(path) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(Settings::default()),
        Err(error) => return Err(error),
    };
    let settings: Settings = serde_json::from_slice(&bytes)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    Ok(settings.normalized())
}

fn write_settings(path: &Path, settings: &Settings) -> io::Result<()> {
    let directory = path
        .parent()
        .ok_or_else(|| io::Error::other("Некорректный путь настроек"))?;
    fs::create_dir_all(directory)?;
    let mut pending = NamedTempFile::new_in(directory)?;
    serde_json::to_writer_pretty(pending.as_file_mut(), &settings.clone().normalized())?;
    pending.as_file_mut().flush()?;
    File::sync_all(pending.as_file())?;
    pending.persist(path).map_err(|error| error.error)?;
    Ok(())
}

#[cfg(test)]
#[path = "../tests/unit/settings.rs"]
mod tests;
