use std::path::{Path, PathBuf};

use gpui_kit::*;

use super::{Workspace, file_name, syntax_for};
use crate::files::{self, FileTarget, OpenedFile};

impl Workspace {
    pub(super) fn open_picker(
        &mut self,
        windows_1251: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.busy.is_some() {
            return;
        }
        self.busy = Some("Выбор файла…");
        self.error = None;
        let result = cx.prompt_for_paths(PathPromptOptions {
            files: true,
            directories: false,
            multiple: true,
            prompt: Some("Открыть".into()),
        });
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let result = result.await;
            this.update_in(cx, |this, window, cx| {
                this.busy = None;
                match result {
                    Ok(Ok(Some(paths))) if !paths.is_empty() => {
                        this.open_paths(paths, windows_1251, window, cx);
                    }
                    Ok(Ok(_)) => {}
                    Ok(Err(error)) => {
                        this.error = Some(format!("Не удалось открыть диалог. {error}").into())
                    }
                    Err(_) => this.error = Some("Диалог открытия прерван".into()),
                }
                cx.notify();
            })
            .ok();
        })
        .detach();
    }

    pub(super) fn open_paths(
        &mut self,
        paths: Vec<PathBuf>,
        windows_1251: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if paths.is_empty() || self.busy.is_some() {
            return;
        }

        self.busy = Some("Открытие…");
        self.error = None;
        let executor = cx.background_executor().clone();
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let mut first_error = None;
            let mut failures = 0;
            for path in paths {
                let name = file_name(&path);
                match executor
                    .spawn(async move { files::open(&path, windows_1251) })
                    .await
                {
                    Ok(opened) => {
                        if this
                            .update_in(cx, |this, window, cx| this.show_opened(opened, window, cx))
                            .is_err()
                        {
                            return;
                        }
                    }
                    Err(error) => {
                        failures += 1;
                        first_error
                            .get_or_insert_with(|| format!("Не удалось открыть «{name}». {error}"));
                    }
                }
            }

            this.update_in(cx, |this, _, cx| {
                this.busy = None;
                this.error = first_error.map(|error| {
                    if failures == 1 {
                        error.into()
                    } else {
                        format!("{error} Ошибок при открытии: {failures}").into()
                    }
                });
                cx.notify();
            })
            .ok();
        })
        .detach();
    }

    fn show_opened(&mut self, opened: OpenedFile, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(doc) = self.documents.iter().find(|doc| {
            doc.file
                .as_ref()
                .is_some_and(|file| file.path == opened.target.path)
        }) {
            self.select(doc.id, window, cx);
            return;
        }

        if self.documents.len() == 1
            && self.active_document().file.is_none()
            && !self.active_document().dirty
        {
            self.documents.clear();
        }
        self.add_document(Some(opened), window, cx);
    }

    pub(super) fn save(&mut self, save_as: bool, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() {
            return;
        }
        self.error = None;
        let doc = self.active_document();
        let id = doc.id;

        if !save_as && let Some(file) = doc.file.clone() {
            self.write_file(id, file, window, cx);
            return;
        }

        let directory = doc
            .file
            .as_ref()
            .and_then(|file| file.path.parent())
            .unwrap_or_else(|| Path::new(""));
        let suggested = if doc.file.is_some() {
            doc.name.as_ref()
        } else {
            "Без имени.txt"
        };
        let result = cx.prompt_for_new_path(directory, Some(suggested));
        self.busy = Some("Выбор имени файла…");
        let executor = cx.background_executor().clone();
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let target = match result.await {
                Ok(Ok(Some(path))) => executor
                    .spawn(async move { FileTarget::from_picker(path) })
                    .await
                    .map(Some)
                    .map_err(|error| error.to_string()),
                Ok(Ok(None)) => Ok(None),
                Ok(Err(error)) => Err(error.to_string()),
                Err(_) => Err("Диалог сохранения прерван".into()),
            };
            this.update_in(cx, |this, window, cx| {
                this.busy = None;
                match target {
                    Ok(Some(target)) => this.write_file(id, target, window, cx),
                    Ok(None) => {}
                    Err(error) => {
                        this.error = Some(format!("Не удалось выбрать файл. {error}").into())
                    }
                }
                cx.notify();
            })
            .ok();
        })
        .detach();
    }

    fn write_file(
        &mut self,
        id: u64,
        mut target: FileTarget,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if let Some(other) = self.documents.iter().find(|doc| {
            doc.id != id
                && doc
                    .file
                    .as_ref()
                    .is_some_and(|file| file.path == target.path)
        }) {
            self.error = Some(format!("Файл «{}» уже открыт в другой вкладке", other.name).into());
            cx.notify();
            return;
        }

        let Some(doc) = self.documents.iter().find(|doc| doc.id == id) else {
            return;
        };
        if let Some(file) = &doc.file
            && file.path == target.path
        {
            target.version = file.version;
        }

        let snapshot = doc.editor.read(cx).text().clone();
        let format = doc.format;
        self.busy = Some("Сохранение…");
        cx.notify();
        let task = cx.background_spawn(async move {
            let result = files::save(target, format, &snapshot.to_string());
            (snapshot, result)
        });
        cx.spawn_in(window, async move |this, cx| {
            let (snapshot, result) = task.await;
            this.update_in(cx, |this, _, cx| {
                this.busy = None;
                match result {
                    Ok(file) => {
                        if let Some(doc) = this.documents.iter_mut().find(|doc| doc.id == id) {
                            let language = syntax_for(&file.path);
                            doc.editor
                                .update(cx, |editor, cx| editor.set_highlighter(language, cx));
                            doc.name = file_name(&file.path);
                            doc.file = Some(file);
                            doc.saved_text = snapshot;
                            doc.dirty = doc.editor.read(cx).text() != &doc.saved_text;
                        }
                    }
                    Err(error) => {
                        this.error = Some(format!("Не удалось сохранить файл. {error}").into())
                    }
                }
                cx.notify();
            })
            .ok();
        })
        .detach();
    }
}
