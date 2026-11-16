use gpui_kit::*;

use super::Workspace;
use crate::json_format;

impl Workspace {
    pub(super) fn format_json(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() {
            return;
        }
        let doc = self.active_document();
        if doc.placeholder || doc.log.is_some() {
            return;
        }
        let newline = if doc.format.line_endings_label() == "CRLF" {
            "\r\n"
        } else {
            "\n"
        };
        let editor = doc.editor.clone();
        let state = editor.read(cx);
        let selected = state.selected_range();
        let range = if selected.is_empty() {
            0..state.text().len()
        } else {
            selected
        };
        if range.len() > json_format::MAX_JSON_BYTES {
            self.error = Some("Выбранный JSON превышает лимит 64 МиБ".into());
            cx.notify();
            return;
        }
        let (id, revision, snapshot) = (doc.id, doc.revision, state.text().clone());
        let input = range.clone();
        self.error = None;
        self.busy = Some("Форматирование JSON…");
        cx.notify();
        let task = cx.background_spawn(async move {
            let source = snapshot.slice(input).to_string();
            json_format::format(&source, newline)
                .map(|formatted| (formatted != source).then_some(formatted))
        });
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let _ = this.update_in(cx, |this, window, cx| {
                this.busy = None;
                let active = this.active_document();
                if active.id != id || active.revision != revision {
                    this.error =
                        Some("Вкладка или текст изменились во время форматирования".into());
                } else {
                    match result {
                        Ok(Some(formatted)) => editor.update(cx, |state, cx| {
                            state.set_selected_range(range, cx);
                            state.replace(formatted, window, cx);
                            state.focus(window, cx);
                        }),
                        Ok(None) => {}
                        Err(error) => this.error = Some(error.into()),
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }
}
