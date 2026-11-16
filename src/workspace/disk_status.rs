use std::{io, time::Duration};

use gpui_kit::*;

use super::Workspace;
use crate::files::{FileTarget, FileVersion};

#[derive(Clone, Copy, Default, PartialEq, Eq)]
pub(super) enum DiskStatus {
    #[default]
    Current,
    Changed,
    Missing,
    Unavailable,
}

impl DiskStatus {
    pub(super) fn from_error(error: &io::Error) -> Self {
        if error.kind() == io::ErrorKind::NotFound {
            Self::Missing
        } else {
            Self::Unavailable
        }
    }

    fn check(file: &FileTarget) -> Self {
        match FileVersion::at(&file.path) {
            Ok(Some(version)) if Some(version) == file.version => Self::Current,
            Ok(Some(_)) => Self::Changed,
            Ok(None) => Self::Missing,
            Err(error) => Self::from_error(&error),
        }
    }

    pub(super) fn tab_suffix(self) -> Option<&'static str> {
        match self {
            Self::Current => None,
            Self::Changed => Some("изменён на диске"),
            Self::Missing => Some("отсутствует"),
            Self::Unavailable => Some("недоступен"),
        }
    }

    pub(super) fn notice(self) -> Option<&'static str> {
        match self {
            Self::Current => None,
            Self::Changed => {
                Some("Файл изменился на диске. Откройте его заново или сохраните правки отдельно.")
            }
            Self::Missing => Some("Файл отсутствует на диске."),
            Self::Unavailable => Some("Файл недоступен. Проверьте путь и права доступа."),
        }
    }
}

impl Workspace {
    pub(super) fn start_disk_monitor(&self, window: &mut Window, cx: &mut Context<Self>) {
        let executor = cx.background_executor().clone();
        cx.spawn_in(window, async move |this, cx| {
            loop {
                executor.timer(Duration::from_secs(1)).await;
                let Ok(files) = this.update_in(cx, |this, _, _| {
                    this.documents
                        .iter()
                        .filter_map(|doc| doc.file.clone().map(|file| (doc.id, file)))
                        .collect::<Vec<_>>()
                }) else {
                    break;
                };
                if files.is_empty() {
                    continue;
                }
                let statuses = executor
                    .spawn(async move {
                        files
                            .into_iter()
                            .map(|(id, file)| (id, DiskStatus::check(&file), file))
                            .collect::<Vec<_>>()
                    })
                    .await;
                let _ = this.update_in(cx, |this, _, cx| {
                    let mut changed = false;
                    for (id, status, file) in statuses {
                        if let Some(doc) = this
                            .documents
                            .iter_mut()
                            .find(|doc| doc.id == id && doc.file.as_ref() == Some(&file))
                            && doc.disk_status != status
                        {
                            doc.disk_status = status;
                            changed = true;
                        }
                    }
                    if changed {
                        cx.notify();
                    }
                });
            }
        })
        .detach();
    }
}

#[cfg(test)]
#[path = "../../tests/unit/disk_status.rs"]
mod tests;
