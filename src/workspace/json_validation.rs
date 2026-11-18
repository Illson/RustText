use std::{path::Path, time::Duration};

use gpui_kit::{
    base::input::{Diagnostic, DiagnosticSeverity},
    component::input::{EditorState, Position, Rope},
    *,
};
use serde::de::IgnoredAny;

use super::Workspace;
use crate::json_format::MAX_JSON_BYTES;

const VALIDATION_DELAY: Duration = Duration::from_millis(300);

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) enum Status {
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

pub(super) fn should_validate(path: Option<&Path>, mut source: impl Iterator<Item = char>) -> bool {
    path.is_some_and(is_json_file)
        || source
            .find(|character| !character.is_whitespace() && *character != '\u{feff}')
            .is_some_and(|character| matches!(character, '{' | '['))
}

#[derive(Default)]
pub(super) struct Validation {
    active_revision: Option<u64>,
    generation: u64,
    result: Option<(u64, Status)>,
}

impl Validation {
    fn set_active_revision(&mut self, revision: Option<u64>) -> bool {
        if self.active_revision == revision {
            return false;
        }
        self.active_revision = revision;
        self.generation = self.generation.wrapping_add(1);
        true
    }

    fn is_current(&self, revision: u64, generation: u64) -> bool {
        self.active_revision == Some(revision) && self.generation == generation
    }

    pub(super) fn status(&self) -> Option<&Status> {
        let (revision, status) = self.result.as_ref()?;
        (self.active_revision == Some(*revision)).then_some(status)
    }

    fn complete(&mut self, revision: u64, generation: u64, result: Status) -> bool {
        if !self.is_current(revision, generation) {
            return false;
        }
        self.result = Some((revision, result));
        true
    }
}

pub(super) fn validate_rope(source: &Rope) -> Status {
    if source.len() > MAX_JSON_BYTES {
        return Status::TooLarge;
    }
    validate(&source.to_string())
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

fn update_diagnostics(editor: &Entity<EditorState>, status: Option<&Status>, cx: &mut App) {
    editor.update(cx, |state, cx| {
        let text = state.text().clone();
        if let Some(diagnostics) = state.diagnostics_mut() {
            diagnostics.reset(&text);
            if let Some(Status::Invalid(problem)) = status {
                diagnostics.push(diagnostic(problem));
            }
        }
        cx.notify();
    });
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
    pub(super) fn dismiss_json_validation(&mut self, cx: &mut Context<Self>) {
        let id = self.active;
        if let Some(doc) = self.documents.iter_mut().find(|doc| doc.id == id) {
            doc.json_validation_disabled = true;
        }
        self.schedule_json_validation(id, cx);
        cx.notify();
    }

    pub(super) fn enable_json_validation(&mut self, cx: &mut Context<Self>) {
        let id = self.active;
        if let Some(doc) = self.documents.iter_mut().find(|doc| doc.id == id) {
            doc.json_validation_disabled = false;
        }
        self.schedule_json_validation(id, cx);
        cx.notify();
    }

    pub(super) fn toggle_json_validation(&mut self, cx: &mut Context<Self>) {
        if self.active_document().json_validation_disabled {
            self.enable_json_validation(cx);
        } else {
            self.dismiss_json_validation(cx);
        }
    }

    pub(super) fn schedule_json_validation(&mut self, id: u64, cx: &mut Context<Self>) {
        let Some(doc) = self.documents.iter_mut().find(|doc| doc.id == id) else {
            return;
        };
        let enabled = !doc.json_validation_disabled
            && !doc.placeholder
            && should_validate(
                doc.file.as_ref().map(|file| file.path.as_path()),
                doc.editor.read(cx).text().chars(),
            );
        let revision = doc.revision;
        if !doc
            .json_validation
            .set_active_revision(enabled.then_some(revision))
        {
            return;
        }

        let editor = doc.editor.clone();
        let status = doc.json_validation.status();
        if !enabled || status.is_some() {
            update_diagnostics(&editor, status, cx);
            return;
        }

        let generation = doc.json_validation.generation;
        let executor = cx.background_executor().clone();
        let validation_executor = executor.clone();
        let task = cx.spawn(async move |this, cx| {
            executor.timer(VALIDATION_DELAY).await;
            let snapshot = this
                .update(cx, |workspace, cx| {
                    workspace
                        .documents
                        .iter()
                        .find(|doc| {
                            doc.id == id
                                && doc.revision == revision
                                && doc.json_validation.is_current(revision, generation)
                        })
                        .map(|doc| doc.editor.read(cx).text().clone())
                })
                .ok()
                .flatten();
            let Some(snapshot) = snapshot else {
                return;
            };
            let result = if snapshot.len() > MAX_JSON_BYTES {
                Status::TooLarge
            } else {
                validation_executor
                    .spawn(async move { validate_rope(&snapshot) })
                    .await
            };
            let _ = this.update(cx, |workspace, cx| {
                let Some(doc) = workspace
                    .documents
                    .iter_mut()
                    .find(|doc| doc.id == id && doc.revision == revision)
                else {
                    return;
                };
                if doc.json_validation.complete(revision, generation, result) {
                    update_diagnostics(&editor, doc.json_validation.status(), cx);
                    cx.notify();
                }
            });
        });
        task.detach();
    }
}

#[cfg(test)]
#[path = "../../tests/unit/json_validation.rs"]
mod tests;
