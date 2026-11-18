use std::path::Path;

use gpui_kit::SharedString;

use super::{DocumentSaveState, status_message};
use crate::{appearance, workspace::disk_status::DiskStatus};

#[test]
fn save_state_distinguishes_readonly_dirty_untitled_saved_and_disk_states() {
    let readonly = DocumentSaveState::from_document(true, true, true, DiskStatus::Changed);
    assert_eq!(readonly, DocumentSaveState::ReadOnly);
    assert_eq!(readonly.label(), "Только чтение");
    assert_eq!(readonly.color(), appearance::MUTED);

    for (has_file, disk_status) in [
        (false, DiskStatus::Current),
        (true, DiskStatus::Current),
        (true, DiskStatus::Changed),
    ] {
        let unsaved = DocumentSaveState::from_document(false, true, has_file, disk_status);
        assert_eq!(unsaved, DocumentSaveState::Unsaved);
        assert_eq!(unsaved.label(), "Не сохранён");
        assert_eq!(unsaved.color(), appearance::ACCENT);
    }

    let untitled_clean = DocumentSaveState::from_document(false, false, false, DiskStatus::Current);
    assert_eq!(untitled_clean, DocumentSaveState::UntitledClean);
    assert_eq!(untitled_clean.label(), "Не сохранён");
    assert_eq!(untitled_clean.color(), appearance::MUTED);

    let saved = DocumentSaveState::from_document(false, false, true, DiskStatus::Current);
    assert_eq!(saved, DocumentSaveState::Saved);
    assert_eq!(saved.label(), "Сохранён");
    assert_eq!(saved.color(), appearance::SUCCESS);

    let disk_changed = DocumentSaveState::from_document(false, false, true, DiskStatus::Changed);
    assert_eq!(disk_changed, DocumentSaveState::DiskChanged);
    assert_eq!(disk_changed.label(), "Изменён на диске");
    assert_eq!(disk_changed.color(), appearance::ERROR);

    let disk_missing = DocumentSaveState::from_document(false, false, true, DiskStatus::Missing);
    assert_eq!(disk_missing, DocumentSaveState::DiskMissing);
    assert_eq!(disk_missing.label(), "Отсутствует");
    assert_eq!(disk_missing.color(), appearance::ERROR);

    let disk_unavailable =
        DocumentSaveState::from_document(false, false, true, DiskStatus::Unavailable);
    assert_eq!(disk_unavailable, DocumentSaveState::DiskUnavailable);
    assert_eq!(disk_unavailable.label(), "Недоступен");
    assert_eq!(disk_unavailable.color(), appearance::ERROR);
}

#[test]
fn status_message_follows_priority_and_keeps_busy_or_placeholder_non_error() {
    let err: SharedString = "Ошибка сохранения".into();
    let session_err: SharedString = "Ошибка сессии".into();
    let session_note: SharedString = "Сессия восстановлена".into();
    let path = Path::new(r"\\?\C:\logs\app.txt");

    let top_error = status_message(
        Some(&err),
        Some("Сохранение…"),
        DiskStatus::Missing,
        true,
        Some(&session_err),
        Some(&session_note),
        Some(path),
    );
    assert_eq!(top_error.text.as_ref(), "Ошибка сохранения");
    assert!(top_error.is_error);
    assert_eq!(top_error.color(), appearance::ERROR);

    let busy = status_message(
        None,
        Some("Сохранение…"),
        DiskStatus::Changed,
        true,
        Some(&session_err),
        Some(&session_note),
        Some(path),
    );
    assert_eq!(busy.text.as_ref(), "Сохранение…");
    assert!(!busy.is_error);
    assert_eq!(busy.color(), appearance::MUTED);

    let disk = status_message(
        None,
        None,
        DiskStatus::Missing,
        true,
        Some(&session_err),
        Some(&session_note),
        Some(path),
    );
    assert_eq!(disk.text.as_ref(), "Файл отсутствует на диске.");
    assert!(disk.is_error);

    let placeholder = status_message(
        None,
        None,
        DiskStatus::Current,
        true,
        Some(&session_err),
        Some(&session_note),
        Some(path),
    );
    assert_eq!(
        placeholder.text.as_ref(),
        "Файл снова доступен. Откройте его заново."
    );
    assert!(!placeholder.is_error);

    let sess_err = status_message(
        None,
        None,
        DiskStatus::Current,
        false,
        Some(&session_err),
        Some(&session_note),
        Some(path),
    );
    assert_eq!(sess_err.text.as_ref(), "Ошибка сессии");
    assert!(sess_err.is_error);

    let sess_notice = status_message(
        None,
        None,
        DiskStatus::Current,
        false,
        None,
        Some(&session_note),
        Some(path),
    );
    assert_eq!(sess_notice.text.as_ref(), "Сессия восстановлена");
    assert!(!sess_notice.is_error);

    let file = status_message(
        None,
        None,
        DiskStatus::Current,
        false,
        None,
        None,
        Some(path),
    );
    assert_eq!(file.text.as_ref(), r"C:\logs\app.txt");
    assert!(!file.is_error);

    let untitled = status_message(None, None, DiskStatus::Current, false, None, None, None);
    assert_eq!(untitled.text.as_ref(), "Новый документ");
    assert!(!untitled.is_error);
}
