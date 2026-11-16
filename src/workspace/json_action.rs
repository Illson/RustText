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
        let source = state.text().slice(range.clone()).to_string();
        self.error = None;
        match json_format::format(&source, newline) {
            Ok(formatted) if formatted != source => {
                editor.update(cx, |state, cx| {
                    state.set_selected_range(range, cx);
                    state.replace(formatted, window, cx);
                    state.focus(window, cx);
                });
            }
            Ok(_) => {}
            Err(error) => self.error = Some(error.into()),
        }
        cx.notify();
    }
}
