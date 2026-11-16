use std::{path::Path, time::Duration};

use gpui_kit::{
    base::input::{Diagnostic, DiagnosticSeverity},
    component::input::Position,
    *,
};
use serde::de::IgnoredAny;

use super::Workspace;
use crate::json_format::MAX_JSON_BYTES;

const VALIDATION_DELAY: Duration = Duration::from_millis(300);

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) enum Status {
    Checking,
    Valid,
    Invalid(Problem),
    TooLarge,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct Problem {
    pub line: usize,
    pub column: usize,
    pub message: String,
    pub range: std::ops::Range<Position>,
}

pub(super) fn is_json_file(path: &Path) -> bool {
    path.extension()
        .is_some_and(|extension| extension.to_string_lossy().eq_ignore_ascii_case("json"))
}

pub(super) fn validate(source: &str) -> Status {
    if source.len() > MAX_JSON_BYTES {
        return Status::TooLarge;
    }

    match serde_json::from_str::<IgnoredAny>(source) {
        Ok(_) => Status::Valid,
        Err(error) => {
            let line = error.line().max(1);
            let (column, range) = diagnostic_range(source, line, error.column().max(1));
            let detail = error
                .to_string()
                .split(" at line ")
                .next()
                .unwrap_or("Неверная структура JSON")
                .to_owned();
            Status::Invalid(Problem {
                line,
                column,
                message: detail,
                range,
            })
        }
    }
}

pub(super) fn diagnostic(problem: &Problem) -> Diagnostic {
    Diagnostic::new(problem.range.clone(), problem.message.clone())
        .with_severity(DiagnosticSeverity::Error)
        .with_source("RustText JSON")
}

fn diagnostic_range(
    source: &str,
    line: usize,
    byte_column: usize,
) -> (usize, std::ops::Range<Position>) {
    let line_text = source
        .split('\n')
        .nth(line.saturating_sub(1))
        .unwrap_or("")
        .trim_end_matches('\r');
    let byte_offset = line_text.floor_char_boundary(byte_column.saturating_sub(1));
    let display_column = line_text[..byte_offset].chars().count() + 1;
    let highlight_offset = if byte_offset == line_text.len() {
        line_text
            .char_indices()
            .next_back()
            .map_or(0, |(offset, _)| offset)
    } else {
        byte_offset
    };
    let start_column = line_text[..highlight_offset].encode_utf16().count() as u32;
    let end_column = start_column
        + line_text[highlight_offset..]
            .chars()
            .next()
            .map_or(0, |character| character.len_utf16() as u32);
    let row = line.saturating_sub(1).min(u32::MAX as usize) as u32;
    (
        display_column,
        Position::new(row, start_column)..Position::new(row, end_column),
    )
}

impl Workspace {
    pub(super) fn schedule_json_validation(&mut self, id: u64, cx: &mut Context<Self>) {
        let Some(doc) = self.documents.iter().find(|doc| doc.id == id) else {
            return;
        };
        if !doc
            .file
            .as_ref()
            .is_some_and(|file| is_json_file(&file.path))
        {
            let editor = self
                .documents
                .iter_mut()
                .find(|doc| doc.id == id)
                .and_then(|doc| doc.json_status.take().map(|_| doc.editor.clone()));
            if let Some(editor) = editor {
                editor.update(cx, |state, cx| {
                    let text = state.text().clone();
                    if let Some(diagnostics) = state.diagnostics_mut() {
                        diagnostics.reset(&text);
                    }
                    cx.notify();
                });
            }
            return;
        }
        if doc.placeholder {
            return;
        }

        let revision = doc.revision;
        let editor = doc.editor.clone();
        let executor = cx.background_executor().clone();
        let validation_executor = executor.clone();
        let task = cx.spawn(async move |this, cx| {
            executor.timer(VALIDATION_DELAY).await;
            let snapshot = this
                .update(cx, |workspace, cx| {
                    workspace
                        .documents
                        .iter()
                        .find(|doc| doc.id == id && doc.revision == revision)
                        .map(|doc| doc.editor.read(cx).text().clone())
                })
                .ok()
                .flatten();
            let Some(snapshot) = snapshot else {
                return;
            };
            let result = validation_executor
                .spawn(async move { validate(&snapshot.to_string()) })
                .await;
            let _ = this.update(cx, |workspace, cx| {
                let Some(doc) = workspace
                    .documents
                    .iter_mut()
                    .find(|doc| doc.id == id && doc.revision == revision)
                else {
                    return;
                };
                if !doc
                    .file
                    .as_ref()
                    .is_some_and(|file| is_json_file(&file.path))
                {
                    return;
                }
                doc.json_status = match &result {
                    Status::Valid => None,
                    Status::Checking => Some(Status::Checking),
                    Status::Invalid(_) | Status::TooLarge => Some(result.clone()),
                };
                editor.update(cx, |state, cx| {
                    let text = state.text().clone();
                    if let Some(diagnostics) = state.diagnostics_mut() {
                        diagnostics.reset(&text);
                        if let Status::Invalid(problem) = &result {
                            diagnostics.push(diagnostic(problem));
                        }
                    }
                    cx.notify();
                });
                cx.notify();
            });
        });
        task.detach();
        if let Some(doc) = self.documents.iter_mut().find(|doc| doc.id == id) {
            doc.json_status = Some(Status::Checking);
        }
    }
}

#[cfg(test)]
#[path = "../../tests/unit/json_validation.rs"]
mod tests;
