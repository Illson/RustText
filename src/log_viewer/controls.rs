use std::{cell::Cell, sync::atomic::Ordering};

use gpui_kit::{
    base::{Scrollbar, ScrollbarHandle, input::EditorScrollbarContext},
    component::{
        Disableable, Selectable, Sizable,
        button::{Button, ButtonVariants},
        input::{Editor, Input},
        label::Label,
    },
    *,
};

use super::{
    LogEnd, LogFind, LogNext, LogPrevious, LogStart, LogViewer, find::progress_percent,
    viewport::Destination,
};

impl LogViewer {
    pub(super) fn scrollbar(
        scroll: EditorScrollbarContext,
        viewer: WeakEntity<Self>,
        scroll_bottom: &Cell<Pixels>,
    ) -> impl IntoElement {
        let bottom =
            (scroll.viewport_bounds().size.height - scroll.content_size().height).min(px(0.));
        scroll_bottom.set(bottom);
        div()
            .size_full()
            .relative()
            .child(Scrollbar::new(&scroll).axis(scroll.axis()))
            .child(
                canvas(
                    |bounds, window, _| window.insert_hitbox(bounds, HitboxBehavior::Normal),
                    move |_, hitbox, window, _| {
                        window.on_mouse_event(move |event: &ScrollEvent, phase, window, cx| {
                            if !phase.capture() || !hitbox.should_handle_scroll(window) {
                                return;
                            }
                            let delta = event.delta.pixel_delta(px(1.));
                            if delta.y == px(0.) || delta.x.abs() > delta.y.abs() {
                                return;
                            }
                            let forward = delta.y < px(0.);
                            let offset = scroll.offset().y;
                            // The editor consumes wheel events even at its scroll limits.
                            let edge = if forward { bottom } else { px(0.) };
                            if (offset - edge).abs() <= px(0.5) {
                                let _ = viewer.update(cx, |viewer, cx| {
                                    if viewer.turn_page(forward, window, cx) {
                                        cx.stop_propagation();
                                    }
                                });
                            }
                        });
                    },
                )
                .absolute()
                .size_full(),
            )
    }

    fn toolbar(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let enabled = self.available();
        let at_end = self.page.as_ref().is_none_or(|page| page.eof);
        div()
            .flex()
            .flex_col()
            .flex_shrink_0()
            .gap_1()
            .px_3()
            .py_2()
            .bg(rgb(0x18202b))
            .border_b_1()
            .border_color(rgb(0x293442))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(
                        Button::new("log-start")
                            .small()
                            .ghost()
                            .label("Начало")
                            .disabled(!enabled)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.navigate(Destination::Start, window, cx)
                            })),
                    )
                    .child(
                        Button::new("log-prev")
                            .small()
                            .ghost()
                            .label("Назад")
                            .disabled(!enabled || self.bookmark.offset <= self.bookmark.hidden)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.turn_page(false, window, cx);
                            })),
                    )
                    .child(
                        Button::new("log-next")
                            .small()
                            .ghost()
                            .label("Дальше")
                            .disabled(!enabled || at_end)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.turn_page(true, window, cx);
                            })),
                    )
                    .child(
                        Button::new("log-end")
                            .small()
                            .ghost()
                            .label("Конец")
                            .disabled(!enabled)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.navigate(Destination::End, window, cx)
                            })),
                    )
                    .child(
                        div()
                            .w(px(72.))
                            .child(Input::new(&self.percentage).disabled(!enabled)),
                    )
                    .child(
                        Button::new("log-percent")
                            .small()
                            .ghost()
                            .label("Перейти, %")
                            .disabled(!enabled)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.jump_percent(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("log-search")
                            .small()
                            .ghost()
                            .label("Поиск в логе")
                            .disabled(!enabled)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.open_search(window, cx)),
                            ),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(
                        Button::new("log-refresh")
                            .small()
                            .outline()
                            .label(if self.bookmark.auto_refresh {
                                "Автообновление: вкл."
                            } else {
                                "Автообновление: пауза"
                            })
                            .selected(self.bookmark.auto_refresh)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.bookmark.auto_refresh = !this.bookmark.auto_refresh;
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("log-follow")
                            .small()
                            .outline()
                            .label(if self.bookmark.follow_end {
                                "Следовать за концом: вкл."
                            } else {
                                "Следовать за концом: выкл."
                            })
                            .selected(self.bookmark.follow_end)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.bookmark.follow_end = !this.bookmark.follow_end;
                                if this.bookmark.follow_end && this.bookmark.auto_refresh {
                                    this.navigate(Destination::End, window, cx);
                                }
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("log-refresh-now")
                            .small()
                            .ghost()
                            .label("Обновить")
                            .disabled(self.writing_file() || self.preparing_trim)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.load_page(Destination::Restore, window, cx)
                            })),
                    )
                    .children((self.indexing.is_some() || self.index_paused).then(|| {
                        Button::new("log-index")
                            .small()
                            .ghost()
                            .label(if self.index_paused {
                                "Продолжить индекс"
                            } else {
                                "Отменить индекс"
                            })
                            .on_click(
                                cx.listener(|this, _, window, cx| this.toggle_index(window, cx)),
                            )
                    })),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(Label::new("Начало, %").text_sm())
                    .child(
                        div()
                            .w(px(63.))
                            .child(Input::new(&self.trim_percentage).disabled(!enabled)),
                    )
                    .child(
                        Button::new("log-hide")
                            .small()
                            .ghost()
                            .label("Скрыть начало")
                            .disabled(!enabled)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.hide_start(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("log-trim")
                            .small()
                            .ghost()
                            .label("Удалить начало…")
                            .disabled(!enabled)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.confirm_trim(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("log-reveal")
                            .small()
                            .ghost()
                            .label("Показать весь файл")
                            .disabled(!enabled || self.bookmark.hidden == 0)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.reveal_start(window, cx)),
                            ),
                    )
                    .child(
                        Button::new("log-trim-cancel")
                            .small()
                            .ghost()
                            .label("Отменить обрезку")
                            .disabled(!self.can_cancel_trim())
                            .on_click(cx.listener(|this, _, _, cx| this.cancel_trim(cx))),
                    ),
            )
    }

    fn status(&self) -> impl IntoElement {
        let message = if let Some(progress) = &self.trimming {
            let done = progress.done.load(Ordering::Relaxed);
            let total = progress.total.load(Ordering::Relaxed);
            format!(
                "Обрезка… {}%. Отмена доступна до изменения исходника.",
                progress_percent(done, total)
            )
            .into()
        } else if let Some(progress) = &self.preparation {
            format!(
                "Поиск границы… {}%",
                progress_percent(
                    progress.done.load(Ordering::Relaxed),
                    progress.total.load(Ordering::Relaxed)
                )
            )
            .into()
        } else if self.preparing_trim {
            "Проверка границы…".into()
        } else if self.reading {
            "Чтение лога…".into()
        } else if let Some(notice) = &self.notice {
            notice.clone()
        } else if let Some(progress) = &self.indexing {
            format!(
                "Индексирование… {}%",
                progress_percent(
                    progress.done.load(Ordering::Relaxed),
                    progress.total.load(Ordering::Relaxed)
                )
            )
            .into()
        } else if self.index.scanned_to() > 0 {
            format!("Строк в индексе: {}", self.index.line_count()).into()
        } else {
            "Просмотр лога".into()
        };
        let page_end = self.page.as_ref().map_or(0, |page| page.next_offset);
        let mut position = format!(
            "Байты {}–{} из {}",
            self.bookmark.offset, page_end, self.bookmark.size
        );
        if self.bookmark.hidden > 0 {
            position.push_str(&format!(" · скрыто {}", self.bookmark.hidden));
        }
        div()
            .flex()
            .items_center()
            .justify_between()
            .flex_shrink_0()
            .h(px(29.))
            .gap_3()
            .px_3()
            .bg(rgb(0x18202b))
            .border_t_1()
            .border_color(rgb(0x293442))
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .truncate()
                    .child(Label::new(message).text_sm().truncate()),
            )
            .child(Label::new(position).text_sm())
            .child(Label::new(self.encoding.label()).text_sm())
    }
}

impl Render for LogViewer {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .id("log-viewer")
            .key_context("LogViewer")
            .size_full()
            .flex()
            .flex_col()
            .on_action(cx.listener(|this, _: &LogStart, window, cx| {
                this.navigate(Destination::Start, window, cx)
            }))
            .on_action(cx.listener(|this, _: &LogEnd, window, cx| {
                this.navigate(Destination::End, window, cx)
            }))
            .on_action(cx.listener(|this, _: &LogPrevious, window, cx| {
                this.scroll_screen(false, window, cx);
            }))
            .on_action(cx.listener(|this, _: &LogNext, window, cx| {
                this.scroll_screen(true, window, cx);
            }))
            .on_action(cx.listener(|this, _: &LogFind, window, cx| this.open_search(window, cx)))
            .child(self.toolbar(cx))
            .children(self.search_panel(cx))
            .child(
                div().flex_1().min_h_0().w_full().overflow_hidden().child(
                    Editor::new(&self.viewer)
                        .readonly(true)
                        .bordered(false)
                        .h_full()
                        .w_full()
                        .font_family("Consolas")
                        .text_size(px(15.)),
                ),
            )
            .child(self.status())
    }
}
