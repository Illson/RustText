use std::{
    path::Path,
    sync::atomic::{AtomicBool, Ordering},
};

use gpui_kit::*;

use super::{LogViewer, Progress, viewport::Destination};
use crate::{
    log_trim::{self, TrimPlan},
    text_format::TextEncoding,
};

impl LogViewer {
    fn boundary_task<T: Send + 'static>(
        &mut self,
        percent: u8,
        exclusive: bool,
        cx: &Context<Self>,
        prepare: impl FnOnce(
            &Path,
            u8,
            TextEncoding,
            &AtomicBool,
            &mut dyn FnMut(u64, u64),
        ) -> std::io::Result<T>
        + Send
        + 'static,
    ) -> Task<std::io::Result<T>> {
        let path = self.path.clone();
        let encoding = self.encoding;
        let progress = Progress::new(self.bookmark.size);
        let cancel = progress.cancel.clone();
        let done = progress.done.clone();
        let total = progress.total.clone();
        self.preparation = Some(progress);
        let timer = cx.background_executor().clone();
        cx.background_spawn(async move {
            let mut report = |completed, expected| {
                done.store(completed, Ordering::Relaxed);
                total.store(expected, Ordering::Relaxed);
            };
            if exclusive {
                // Let the cancelled viewport indexing/search jobs release their shared handles.
                timer.timer(std::time::Duration::from_millis(45)).await;
            }
            prepare(&path, percent, encoding, &cancel, &mut report)
        })
    }

    pub(super) fn can_cancel_trim(&self) -> bool {
        !self.immutable_snapshot && (self.preparation.is_some() || self.trimming.is_some())
    }

    fn trim_percent(&mut self, cx: &mut Context<Self>) -> Option<u8> {
        let parsed = self.trim_percentage.read(cx).value().trim().parse::<u8>();
        match parsed {
            Ok(percent @ 0..=100) => Some(percent),
            _ => {
                self.notice = Some("Введите целый процент от 0 до 100.".into());
                cx.notify();
                None
            }
        }
    }

    pub(super) fn hide_start(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.immutable_snapshot || !self.available() {
            return;
        }
        let Some(percent) = self.trim_percent(cx) else {
            return;
        };
        self.preparing_trim = true;
        self.notice = None;
        self.reset_search(cx);
        let job = self.boundary_task(
            percent,
            false,
            cx,
            |path, percent, encoding, cancel, report| {
                log_trim::hidden_start_with_progress(path, percent, encoding, cancel, report)
            },
        );
        cx.spawn_in(window, async move |this, cx| {
            let result = job.await;
            let _ = this.update_in(cx, |viewer, window, cx| {
                viewer.preparing_trim = false;
                viewer.preparation = None;
                match result {
                    Ok(boundary) => {
                        viewer.bookmark.hidden = boundary;
                        viewer.load_page(Destination::Start, window, cx);
                    }
                    Err(error) => {
                        viewer.notice = Some(format!("Не удалось скрыть начало. {error}").into());
                        viewer.start_index(window, cx);
                    }
                }
                cx.notify();
            });
        })
        .detach();
        cx.notify();
    }

    pub(super) fn reveal_start(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.immutable_snapshot && self.available() {
            self.bookmark.hidden = 0;
            self.reset_search(cx);
            self.navigate(Destination::Start, window, cx);
        }
    }

    pub(super) fn confirm_trim(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.immutable_snapshot || !self.available() {
            return;
        }
        let Some(percent) = self.trim_percent(cx) else {
            return;
        };
        if percent == 0 {
            self.notice = Some("Укажите процент удаления больше нуля.".into());
            cx.notify();
            return;
        }

        self.preparing_trim = true;
        self.stop_index();
        self.reset_search(cx);
        let task = self.boundary_task(
            percent,
            true,
            cx,
            |path, percent, encoding, cancel, report| {
                log_trim::prepare_with_progress(path, percent, encoding, cancel, report)
            },
        );
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let Ok(prompt) = this.update_in(cx, |viewer, window, cx| {
                viewer.preparation = None;
                match result {
                    Ok(plan) if plan.removable_bytes > 0 => {
                        let name = viewer.path.file_name().unwrap_or_default().to_string_lossy();
                        let detail = format!(
                            "Запрошено {}%. Строк к удалению с начала файла «{name}»: {}. Перед записью RustText сохранит полную резервную копию рядом с логом.",
                            plan.percent, plan.removed_lines,
                        );
                        let answer = window.prompt(
                            PromptLevel::Warning,
                            "Удалить начало исходного файла?",
                            Some(&detail),
                            &[PromptButton::cancel("Отмена"), PromptButton::new("Удалить")],
                            cx,
                        );
                        Some((plan, answer))
                    }
                    Ok(_) => {
                        viewer.preparing_trim = false;
                        viewer.notice = Some("Удаляемый участок пуст.".into());
                        viewer.start_index(window, cx);
                        cx.notify();
                        None
                    }
                    Err(error) => {
                        viewer.preparing_trim = false;
                        viewer.notice = Some(format!("Не удалось подготовить обрезку. {error}").into());
                        viewer.start_index(window, cx);
                        cx.notify();
                        None
                    }
                }
            }) else { return };
            if let Some((plan, answer)) = prompt {
                let approved = answer.await.ok() == Some(1);
                let _ = this.update_in(cx, |viewer, window, cx| {
                    viewer.preparing_trim = false;
                    if approved {
                        viewer.run_trim(plan, window, cx);
                    } else {
                        viewer.start_index(window, cx);
                    }
                    cx.notify();
                });
            }
        }).detach();
        cx.notify();
    }

    pub(super) fn cancel_trim(&mut self, cx: &mut Context<Self>) {
        if let Some(progress) = self.trimming.as_ref().or(self.preparation.as_ref()) {
            progress.set_cancelled();
            cx.notify();
        }
    }

    fn run_trim(&mut self, plan: TrimPlan, window: &mut Window, cx: &mut Context<Self>) {
        let progress = Progress::new(plan.original_size);
        let cancel = progress.cancel.clone();
        let done = progress.done.clone();
        let total = progress.total.clone();
        self.trimming = Some(progress);
        self.notice = None;
        self.generation = self.generation.wrapping_add(1);
        self.reading = false;
        let path = self.path.clone();
        let task = cx.background_spawn(async move {
            log_trim::trim(&path, &plan, &cancel, |completed, expected| {
                done.store(completed, Ordering::Relaxed);
                total.store(expected, Ordering::Relaxed);
            })
        });
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let _ = this.update_in(cx, |viewer, window, cx| {
                viewer.trimming = None;
                match result {
                    Ok(outcome) => {
                        viewer.notice = Some(
                            format!(
                                "Удалено строк: {}. Копия: {}",
                                outcome.removed_lines,
                                outcome.backup_path.display(),
                            )
                            .into(),
                        );
                        viewer.reset_index();
                        viewer.bookmark.identity = None;
                        viewer.bookmark.fingerprint = None;
                        viewer.bookmark.hidden = 0;
                        viewer.bookmark.offset = 0;
                    }
                    Err(error) => {
                        viewer.notice = Some(format!("Обрезка не завершена. {error}").into())
                    }
                }
                viewer.load_page(Destination::Restore, window, cx);
                cx.notify();
            });
        })
        .detach();
        cx.notify();
    }
}
