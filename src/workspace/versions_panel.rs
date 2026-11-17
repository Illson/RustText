use std::{
    path::{Path, PathBuf},
    sync::Arc,
    time::SystemTime,
};

use gpui_kit::{
    component::{
        Disableable, Selectable, Sizable,
        button::{Button, ButtonVariants},
        input::{Editor, EditorState, Input, InputState},
        label::Label,
    },
    *,
};

use super::Workspace;
use crate::{
    appearance,
    file_versions::{VersionInfo, VersionSettings},
};

const MIB: u64 = 1024 * 1024;
const PREVIEW_BYTES: usize = 1024 * 1024;

pub(super) struct VersionsPanel {
    document: u64,
    path: Option<PathBuf>,
    token: Arc<()>,
    selection_token: Arc<()>,
    versions: Vec<VersionInfo>,
    selected: Option<String>,
    preview: Entity<EditorState>,
    max_file: Entity<InputState>,
    quota: Entity<InputState>,
    unavailable: Option<SharedString>,
    error: Option<SharedString>,
    status: Option<SharedString>,
    loading: bool,
    preview_loading: bool,
    preview_ready: bool,
    settings_ready: bool,
}

impl VersionsPanel {
    fn render(
        &self,
        disabled_reason: Option<&'static str>,
        dirty: bool,
        cx: &mut Context<Workspace>,
    ) -> impl IntoElement {
        let disabled = disabled_reason.is_some() || self.unavailable.is_some();
        div()
            .w(relative(0.94))
            .h(relative(0.92))
            .max_w(px(1000.))
            .max_h(px(840.))
            .p_3()
            .flex()
            .flex_col()
            .gap_2()
            .rounded_lg()
            .border_1()
            .border_color(rgb(appearance::ACCENT))
            .bg(rgb(appearance::SURFACE))
            .child(
                div()
                    .flex()
                    .items_center()
                    .child(Label::new("Версии файла").text_lg())
                    .child(div().flex_1())
                    .child(
                        Button::new("versions-close")
                            .small()
                            .ghost()
                            .label("×")
                            .tooltip("Закрыть версии файла")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.close_versions(window, cx)),
                            ),
                    ),
            )
            .child(
                div()
                    .id("versions-content")
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scroll()
                    .flex()
                    .flex_col()
                    .gap_2()
                    .children(
                        self.path
                            .as_ref()
                            .map(|path| div().text_sm().child(path.display().to_string())),
                    )
                    .children(
                        self.unavailable
                            .iter()
                            .map(|reason| div().text_sm().child(reason.clone())),
                    )
                    .children(disabled_reason.map(|reason| div().text_sm().child(reason)))
                    .children(dirty.then(|| {
                        div().text_sm().child(
                            "Перед восстановлением сохраните текущие изменения или отмените их.",
                        )
                    }))
                    .child(Label::new("Максимальный размер файла, МиБ").text_sm())
                    .child(
                        Input::new(&self.max_file)
                            .disabled(disabled || self.loading || !self.settings_ready),
                    )
                    .child(Label::new("Общий объём хранилища, МиБ").text_sm())
                    .child(
                        Input::new(&self.quota)
                            .disabled(disabled || self.loading || !self.settings_ready),
                    )
                    .child(
                        Button::new("versions-settings")
                            .small()
                            .label("Применить лимиты")
                            .disabled(disabled || self.loading || !self.settings_ready)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.save_version_settings(window, cx)
                            })),
                    )
                    .child(div().text_sm().child(
                        "Квота удаляет старые версии, сохраняя последнюю версию каждого файла. Слишком маленькая квота не применяется.",
                    ))
                    .child(
                        Label::new(if self.loading {
                            "Загрузка версий…"
                        } else if self.versions.is_empty() {
                            "Сохранённых версий нет"
                        } else {
                            "Сохранённые версии, сначала новые"
                        })
                        .text_sm(),
                    )
                    .child(
                        div()
                            .id("versions-list")
                            .max_h(px(150.))
                            .flex_shrink_0()
                            .overflow_y_scroll()
                            .flex()
                            .flex_col()
                            .gap_1()
                            .children(self.versions.iter().enumerate().map(|(index, version)| {
                                let id = version.id.clone();
                                Button::new(("version", index))
                                    .small()
                                    .ghost()
                                    .label(version_label(version))
                                    .selected(self.selected.as_ref() == Some(&id))
                                    .disabled(disabled || self.loading)
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.preview_file_version(id.clone(), window, cx)
                                    }))
                            })),
                    )
                    .child(
                        Label::new(if self.preview_loading {
                            "Загрузка предпросмотра…"
                        } else {
                            "Предпросмотр, только чтение"
                        })
                        .text_sm(),
                    )
                    .child(
                        div().h(px(240.)).flex_shrink_0().child(
                            Editor::new(&self.preview)
                                .readonly(true)
                                .size_full()
                                .font_family("Consolas"),
                        ),
                    )
                    .children(
                        self.status
                            .iter()
                            .map(|status| div().text_sm().child(status.clone())),
                    )
                    .children(self.error.iter().map(|error| {
                        div()
                            .text_sm()
                            .text_color(rgb(appearance::ERROR))
                            .child(error.clone())
                    })),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(
                        Button::new("versions-refresh")
                            .small()
                            .label("Обновить")
                            .disabled(disabled || self.loading)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.load_file_versions(window, cx)
                            })),
                    )
                    .child(div().flex_1())
                    .child(
                        Button::new("versions-restore")
                            .small()
                            .primary()
                            .label("Восстановить")
                            .disabled(disabled || dirty || self.loading || !self.preview_ready)
                            .on_click(cx.listener(|this, _, window, cx| {
                                if !this.versions_available() || this.active_document_opt().is_none_or(|doc| doc.dirty) {
                                    return;
                                }
                                let selected = this.versions.as_ref().and_then(|panel| {
                                    if !panel.preview_ready {
                                        return None;
                                    }
                                    Some((panel.path.clone()?, panel.selected.clone()?))
                                });
                                if let Some((path, id)) = selected {
                                    this.restore_file_version(path, id, window, cx);
                                }
                            })),
                    ),
            )
    }
}

impl Workspace {
    pub(super) fn open_versions(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(doc) = self.active_document_opt() else {
            return;
        };
        let unavailable = if self.version_store.is_none() {
            Some(
                "Хранилище версий недоступно. Перезапустите приложение после устранения ошибки хранилища.",
            )
        } else if doc.log.is_some() {
            Some("Версии доступны для текстовых файлов. Откройте файл в редакторе, а не как лог.")
        } else if doc.file.is_none() {
            Some("Сначала сохраните документ в файл.")
        } else if doc.placeholder {
            Some("Дождитесь загрузки файла.")
        } else {
            None
        };
        let document = doc.id;
        let path = doc.file.as_ref().map(|file| file.path.clone());
        self.versions = Some(VersionsPanel {
            document,
            path,
            token: Arc::new(()),
            selection_token: Arc::new(()),
            versions: Vec::new(),
            selected: None,
            preview: cx.new(|cx| EditorState::new(window, cx).line_number(true)),
            max_file: cx.new(|cx| InputState::new(window, cx).default_value("5")),
            quota: cx.new(|cx| InputState::new(window, cx).default_value("100")),
            unavailable: unavailable.map(Into::into),
            error: None,
            status: None,
            loading: false,
            preview_loading: false,
            preview_ready: false,
            settings_ready: false,
        });
        self.load_file_versions(window, cx);
        cx.notify();
    }

    fn close_versions(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.versions = None;
        self.select(self.active, window, cx);
        cx.notify();
    }

    fn versions_available(&self) -> bool {
        let Some(doc) = self.active_document_opt() else {
            return false;
        };
        self.busy.is_none()
            && !self.applying_update()
            && self.version_store.is_some()
            && self.versions.as_ref().is_some_and(|panel| {
                panel.unavailable.is_none()
                    && panel.document == doc.id
                    && doc.log.is_none()
                    && !doc.placeholder
                    && doc.file.as_ref().map(|file| &file.path) == panel.path.as_ref()
            })
    }

    fn version_request_current(&self, token: &Arc<()>, document: u64, path: &Path) -> bool {
        let Some(doc) = self.active_document_opt() else {
            return false;
        };
        self.versions.as_ref().is_some_and(|panel| {
            Arc::ptr_eq(&panel.token, token)
                && panel.document == document
                && panel.path.as_deref() == Some(path)
                && doc.id == document
                && doc.file.as_ref().map(|file| file.path.as_path()) == Some(path)
                && doc.log.is_none()
                && !doc.placeholder
        })
    }

    pub(super) fn versions_panel(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        let doc = self.active_document_opt()?;
        self.versions.as_ref().map(|panel| {
            let disabled_reason = if self.busy.is_some() || self.applying_update() {
                Some("Дождитесь завершения текущей операции.")
            } else if panel.unavailable.is_none() && !self.versions_available() {
                Some("Активный файл изменился. Закройте панель и откройте версии нужного файла.")
            } else {
                None
            };
            div()
                .id("file-versions")
                .absolute()
                .inset_0()
                .flex()
                .items_center()
                .justify_center()
                .occlude()
                .tab_group()
                .bg(rgb(appearance::BACKGROUND).opacity(0.82))
                .on_key_down(cx.listener(|this, event: &KeyDownEvent, window, cx| {
                    if event.keystroke.key == "escape" {
                        this.close_versions(window, cx);
                        cx.stop_propagation();
                    }
                }))
                .child(panel.render(disabled_reason, doc.dirty, cx))
                .into_any_element()
        })
    }

    fn load_file_versions(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.versions_available() {
            return;
        }
        let store = self.version_store.clone().unwrap();
        let panel = self.versions.as_mut().unwrap();
        if panel.loading {
            return;
        }
        panel.loading = true;
        panel.error = None;
        panel.status = None;
        panel.preview_ready = false;
        panel.preview_loading = false;
        panel.selected = None;
        panel.selection_token = Arc::new(());
        panel
            .preview
            .update(cx, |editor, cx| editor.set_value("", window, cx));
        let token = panel.token.clone();
        let path = panel.path.clone().unwrap();
        let request_path = path.clone();
        let document = panel.document;
        let task = cx.background_spawn(async move {
            let settings = store.settings()?;
            let versions = store.list(&path)?;
            Ok::<_, std::io::Error>((settings, versions))
        });
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let _ = this.update_in(cx, |this, window, cx| {
                if !this.version_request_current(&token, document, &request_path) {
                    return;
                }
                let Some(panel) = &mut this.versions else {
                    return;
                };
                if !Arc::ptr_eq(&panel.token, &token) {
                    return;
                }
                panel.loading = false;
                match result {
                    Ok((settings, versions)) => {
                        panel.settings_ready = true;
                        panel.versions = versions;
                        panel.max_file.update(cx, |input, cx| {
                            input.set_value(
                                settings.max_file_bytes.div_ceil(MIB).to_string(),
                                window,
                                cx,
                            )
                        });
                        panel.quota.update(cx, |input, cx| {
                            input.set_value(
                                settings.total_bytes.div_ceil(MIB).to_string(),
                                window,
                                cx,
                            )
                        });
                    }
                    Err(error) => {
                        panel.error = Some(format!("Не удалось загрузить версии: {error}").into())
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }

    fn preview_file_version(&mut self, id: String, window: &mut Window, cx: &mut Context<Self>) {
        if !self.versions_available() {
            return;
        }
        let store = self.version_store.clone().unwrap();
        let panel = self.versions.as_mut().unwrap();
        if panel.loading || !panel.versions.iter().any(|version| version.id == id) {
            return;
        }
        panel.selected = Some(id.clone());
        panel.preview_loading = true;
        panel.preview_ready = false;
        panel.error = None;
        panel.status = None;
        panel
            .preview
            .update(cx, |editor, cx| editor.set_value("", window, cx));
        panel.selection_token = Arc::new(());
        let token = panel.token.clone();
        let selection = panel.selection_token.clone();
        let document = panel.document;
        let path = panel.path.clone().unwrap();
        let request_path = path.clone();
        let task = cx.background_spawn(async move {
            let content = store.load_version(&path, &id)?;
            let (text, _) = content.decode()?;
            Ok::<_, std::io::Error>(preview_text(text))
        });
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let _ = this.update_in(cx, |this, window, cx| {
                if !this.version_request_current(&token, document, &request_path) {
                    return;
                }
                let Some(panel) = &mut this.versions else { return };
                if !Arc::ptr_eq(&panel.token, &token) || !Arc::ptr_eq(&panel.selection_token, &selection) {
                    return;
                }
                panel.preview_loading = false;
                match result {
                    Ok((text, truncated)) => {
                        panel.preview.update(cx, |editor, cx| editor.set_value(text, window, cx));
                        panel.preview_ready = true;
                        panel.status = Some(if truncated {
                            "Показан первый МиБ текста. При восстановлении используется полная версия."
                        } else {
                            "Восстановление заменит текст в редакторе. Сохраните файл, чтобы записать его на диск."
                        }.into());
                    }
                    Err(error) => panel.error = Some(format!("Не удалось прочитать версию: {error}").into()),
                }
                cx.notify();
            });
        }).detach();
    }

    fn save_version_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.versions_available() {
            return;
        }
        let store = self.version_store.clone().unwrap();
        let panel = self.versions.as_mut().unwrap();
        if panel.loading || !panel.settings_ready {
            return;
        }
        let settings = parse_settings(
            &panel.max_file.read(cx).value(),
            &panel.quota.read(cx).value(),
        );
        let settings = match settings {
            Ok(settings) => settings,
            Err(error) => {
                panel.error = Some(error.into());
                cx.notify();
                return;
            }
        };
        panel.loading = true;
        panel.error = None;
        let token = panel.token.clone();
        let document = panel.document;
        let path = panel.path.clone().unwrap();
        let task = cx.background_spawn(async move { store.set_settings(settings) });
        cx.notify();
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let _ = this.update_in(cx, |this, window, cx| {
                if !this.version_request_current(&token, document, &path) {
                    return;
                }
                let Some(panel) = &mut this.versions else {
                    return;
                };
                if !Arc::ptr_eq(&panel.token, &token) {
                    return;
                }
                panel.loading = false;
                match result {
                    Ok(()) => this.load_file_versions(window, cx),
                    Err(error) => {
                        panel.error = Some(format!("Не удалось сохранить лимиты: {error}").into())
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }
}

fn parse_settings(max_file: &str, quota: &str) -> Result<VersionSettings, &'static str> {
    let parse = |value: &str| {
        value
            .trim()
            .parse::<u64>()
            .ok()
            .and_then(|value| value.checked_mul(MIB))
            .ok_or("Укажите целые неотрицательные значения в МиБ без переполнения.")
    };
    Ok(VersionSettings {
        max_file_bytes: parse(max_file)?,
        total_bytes: parse(quota)?,
    })
}

fn preview_text(mut text: String) -> (String, bool) {
    if text.len() <= PREVIEW_BYTES {
        return (text, false);
    }
    let mut end = PREVIEW_BYTES;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    text.truncate(end);
    (text, true)
}

fn version_label(version: &VersionInfo) -> String {
    let now = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis();
    let seconds = now.saturating_sub(version.captured_millis as u128) / 1000;
    let age = if seconds < 60 {
        format!("{seconds} с назад")
    } else if seconds < 3600 {
        format!("{} мин назад", seconds / 60)
    } else if seconds < 86400 {
        format!("{} ч назад", seconds / 3600)
    } else {
        format!("{} дн назад", seconds / 86400)
    };
    format!("{} – {age}, {} байт", version.id, version.bytes)
}

#[cfg(test)]
mod tests {
    use super::{PREVIEW_BYTES, VersionSettings, parse_settings, preview_text};

    #[test]
    fn settings_reject_overflow_and_invalid_values() {
        for value in ["-1", "1.5", "abc", "18446744073709551615"] {
            assert!(parse_settings(value, "100").is_err());
            assert!(parse_settings("5", value).is_err());
        }
        assert_eq!(
            parse_settings(" 5 ", "100").unwrap(),
            VersionSettings::default()
        );
        assert_eq!(parse_settings("0", "0").unwrap().total_bytes, 0);
    }

    #[test]
    fn preview_limits_multibyte_text_at_character_boundary() {
        let text = format!("a{}", "я".repeat(PREVIEW_BYTES));
        let (preview, truncated) = preview_text(text.clone());
        assert!(truncated);
        assert!(preview.len() <= PREVIEW_BYTES);
        assert!(text.starts_with(&preview));
        assert_eq!(preview_text("привет".into()), ("привет".into(), false));
    }
}
