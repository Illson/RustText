use std::path::PathBuf;

use gpui_kit::*;

use super::Workspace;
use crate::files::FileVersion;

impl Workspace {
    pub(super) fn restore_file_version(
        &mut self,
        path: PathBuf,
        version_id: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.versions = None;
        if self.busy.is_some() || self.applying_update() {
            return;
        }
        let Some(doc) = self.active_document_opt() else {
            return;
        };
        if doc.dirty || doc.placeholder || doc.log.is_some() {
            self.error = Some("Сначала сохраните текущий документ, затем выберите версию.".into());
            cx.notify();
            return;
        }
        let Some(target) = doc.file.clone().filter(|file| file.path == path) else {
            return;
        };
        let Some(store) = self.version_store.clone() else {
            self.error = Some("История версий недоступна.".into());
            cx.notify();
            return;
        };
        let id = doc.id;
        let revision = doc.revision;
        let current_encoding = doc.format.encoding;
        self.session_notice = None;
        self.busy = Some("Подготовка версии…");
        let job = cx.background_spawn(async move {
            let check = || -> std::io::Result<()> {
                if FileVersion::at(&target.path)? != target.version {
                    return Err(std::io::Error::other(
                        "Исходный файл изменился на диске. Откройте его заново.",
                    ));
                }
                Ok(())
            };
            check()?;
            let content = store.prepare_restore(&path, &version_id, current_encoding)?;
            let decoded = content.decode()?;
            check()?;
            Ok::<_, std::io::Error>((decoded.0, decoded.1, content.info.id))
        });
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let result = job.await;
            let _ = this.update_in(cx, |this, window, cx| {
                this.busy = None;
                if this.active_document_opt().is_none_or(|doc| doc.id != id) {
                    cx.notify();
                    return;
                }
                let Some(doc) = this.documents.iter_mut().find(|doc| doc.id == id) else { return };
                if doc.revision != revision || doc.dirty {
                    this.error = Some("Документ изменился во время подготовки версии.".into());
                    cx.notify();
                    return;
                }
                match result {
                    Ok((text, format, restored_id)) => {
                        doc.format = format;
                        doc.editor.update(cx, |editor, cx| {
                            editor.set_selected_range(0..editor.text().len(), cx);
                            editor.replace(text, window, cx);
                            editor.focus(window, cx);
                        });
                        doc.dirty = doc.saved_text.as_ref() != Some(doc.editor.read(cx).text());
                        this.session_notice = Some(format!("Версия {restored_id} восстановлена в редакторе. Ctrl+S сохранит файл; Ctrl+Z отменит замену текста.").into());
                        this.schedule_json_validation(id, cx);
                    }
                    Err(error) => this.error = Some(format!("Не удалось восстановить версию. {error}").into()),
                }
                cx.notify();
            });
        }).detach();
    }
}
