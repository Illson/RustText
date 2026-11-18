use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Instant,
};

use gpui_kit::prelude::FluentBuilder;
use gpui_kit::{
    component::{
        Disableable, Selectable, Sizable,
        button::{Button, ButtonVariants},
        checkbox::Checkbox,
        input::{Input, InputEvent, InputState},
        label::Label,
    },
    *,
};

use super::{LogViewer, Progress, measurement_start};
use crate::log_io::{
    self, LogError, LogReader, LogSearchQuery, SearchHit, SearchLimits, SearchPage,
};

const SEARCH_BATCH_BYTES: u64 = 8 * 1024 * 1024;
const SEARCH_PAGE_SIZE: usize = 32;

pub(super) struct LogSearch {
    input: Entity<InputState>,
    regex: bool,
    case_sensitive: bool,
    whole_word: bool,
    only_current: bool,
    results: Option<SearchPage>,
    message: SharedString,
    pub(super) progress: Option<Progress>,
    cancelled_at: Option<Instant>,
    _enter: Subscription,
}

impl LogSearch {
    fn new(window: &mut Window, cx: &mut Context<LogViewer>) -> Self {
        let input = cx.new(|cx| InputState::new(window, cx).placeholder("Текст или время в логе"));
        let enter = cx.subscribe_in(&input, window, |viewer, _, event, window, cx| match event {
            InputEvent::PressEnter { .. } => viewer.start_search(false, window, cx),
            InputEvent::Change => {
                viewer.reset_search(cx);
                viewer.refresh_all_search_highlights(cx);
                cx.notify();
            }
            _ => {}
        });
        Self {
            input,
            regex: false,
            case_sensitive: false,
            whole_word: false,
            only_current: true,
            results: None,
            progress: None,
            message: "Введите текст или время.".into(),
            cancelled_at: None,
            _enter: enter,
        }
    }

    fn finish(&mut self, cancel: &Arc<AtomicBool>) -> bool {
        if !self
            .progress
            .as_ref()
            .is_some_and(|progress| Arc::ptr_eq(&progress.cancel, cancel))
        {
            return false;
        }
        self.progress = None;
        true
    }

    fn render(
        &self,
        enabled: bool,
        file_size: u64,
        first_line: Option<u64>,
        window: &Window,
        cx: &mut Context<LogViewer>,
    ) -> impl IntoElement {
        let running = self.progress.is_some();
        let can_continue = self
            .results
            .as_ref()
            .is_some_and(|page| can_continue(page, file_size));
        let max_height = if window.viewport_size().height < px(460.) {
            px(100.)
        } else {
            px(180.)
        };
        let results_height = max_height - px(58.);
        let message = self.progress.as_ref().map_or_else(
            || self.message.clone(),
            |progress| {
                let done = progress.done.load(Ordering::Relaxed);
                let total = progress.total.load(Ordering::Relaxed);
                if progress.cancel.load(Ordering::Relaxed) {
                    "Остановка поиска…".into()
                } else if total > 0 {
                    format!("Поиск… {}%", progress_percent(done, total)).into()
                } else {
                    "Поиск…".into()
                }
            },
        );

        div()
            .flex()
            .flex_col()
            .flex_shrink(1.)
            .max_h(max_height)
            .overflow_hidden()
            .gap_0()
            .px_2()
            .py_1()
            .bg(rgb(crate::appearance::SURFACE))
            .border_b_1()
            .border_color(rgb(crate::appearance::BORDER))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .child(Input::new(&self.input).disabled(running)),
                    )
                    .child(
                        Button::new("log-find-new")
                            .small()
                            .ghost()
                            .label("Найти")
                            .disabled(running || !enabled)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.start_search(false, window, cx)
                            })),
                    )
                    .child(
                        Button::new("log-find-time")
                            .small()
                            .ghost()
                            .label("Ко времени")
                            .tooltip("Введите дату и время или только время. Если даты нет, используется день просматриваемой записи.")
                            .disabled(running || !enabled)
                            .on_click(cx.listener(|this, _, window, cx| this.jump_to_timestamp(window, cx))),
                    )
                    .child(
                        Button::new("log-find-more")
                            .small()
                            .ghost()
                            .label("Ещё")
                            .disabled(running || !enabled || !can_continue)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.start_search(true, window, cx)
                            })),
                    )
                    .child(
                        Button::new("log-find-cancel")
                            .small()
                            .ghost()
                            .label("Отмена")
                            .tooltip("Отменить поиск")
                            .disabled(!running)
                            .on_click(cx.listener(|this, _, _, cx| this.cancel_search(cx))),
                    )
                    .child(
                        Button::new("log-find-close")
                            .small()
                            .ghost()
                            .label("×")
                            .tooltip("Закрыть поиск")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.reset_search(cx);
                                this.search = None;
                                window.focus(&this.focus_handle(cx), cx);
                                cx.notify();
                            })),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(option_button(
                        "log-regex",
                        ".*",
                        "Регулярное выражение",
                        self.regex,
                        running,
                        |search| &mut search.regex,
                        cx,
                    ))
                    .child(option_button(
                        "log-case",
                        "Aa",
                        "Учитывать регистр",
                        self.case_sensitive,
                        running,
                        |search| &mut search.case_sensitive,
                        cx,
                    ))
                    .child(option_button(
                        "log-word",
                        "Слово",
                        "Искать целое слово",
                        self.whole_word,
                        running,
                        |search| &mut search.whole_word,
                        cx,
                    ))
                    .child(Checkbox::new("log-only-current")
                        .label("Только текущее")
                        .checked(self.only_current)
                        .on_click(cx.listener(|this, checked, _, cx| {
                            if let Some(search) = &mut this.search { search.only_current = *checked; }
                            this.refresh_all_search_highlights(cx);
                            cx.notify();
                        })))
                    .child(Label::new(message).text_sm().truncate()),
            )
            .children(
                self.results
                    .as_ref()
                    .filter(|page| !page.hits.is_empty())
                    .map(|page| {
                        div()
                            .id("log-search-results")
                            .max_h(results_height)
                            .overflow_y_scroll()
                            .flex()
                            .flex_col()
                            .children(page.hits.iter().map(|hit| result_button(hit, first_line, cx)))
                    }),
            )
    }
}

impl LogViewer {
    pub(crate) fn open_search(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.search.is_none() {
            self.search = Some(LogSearch::new(window, cx));
        }
        if let Some(search) = &self.search {
            search.input.update(cx, |input, cx| {
                input.focus(window, cx);
                input.select_all(window, cx);
            });
        }
        cx.notify();
    }

    pub(super) fn search_panel(
        &self,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> Option<impl IntoElement> {
        let first_line = if self.bookmark.hidden == 0 {
            Some(1)
        } else {
            self.hidden_line_count.map(|count| count + 1)
        };
        self.search.as_ref().map(|search| {
            search.render(self.available(), self.bookmark.size, first_line, window, cx)
        })
    }

    pub(super) fn refresh_all_search_highlights(&mut self, cx: &mut Context<LogViewer>) {
        if let Some(highlight) = self.all_search_highlight.take() {
            highlight.clear(cx);
        }
        let Some(search) = &self.search else {
            return;
        };
        if search.only_current {
            return;
        }
        let query = crate::search::Query {
            text: search.input.read(cx).value().to_string(),
            regex: search.regex,
            case_sensitive: search.case_sensitive,
            whole_word: search.whole_word,
            all_tabs: false,
        };
        self.all_search_highlight = crate::search_highlight::SearchHighlight::visible_log_matches(
            &self.viewer,
            &query,
            &self.search_viewport,
            cx,
        );
    }

    pub(super) fn reset_search(&mut self, cx: &mut Context<LogViewer>) {
        if let Some(highlight) = self.all_search_highlight.take() {
            highlight.clear(cx);
        }
        self.clear_search_highlight(cx);
        if let Some(search) = &mut self.search {
            if let Some(progress) = search.progress.take() {
                progress.set_cancelled();
            }
            search.results = None;
            search.cancelled_at = None;
            search.message = "Готово к поиску.".into();
        }
    }

    fn cancel_search(&mut self, cx: &mut Context<Self>) {
        if let Some(search) = &mut self.search
            && let Some(progress) = &search.progress
        {
            progress.set_cancelled();
            search.cancelled_at.get_or_insert_with(Instant::now);
            cx.notify();
        }
    }

    fn first_visible_record_offset(&self, cx: &App) -> u64 {
        let Some(page) = &self.page else {
            return self.bookmark.offset;
        };
        let editor = self.viewer.read(cx);
        let top = editor.input_bounds().top();
        let mut text_offset = 0;
        for (index, row) in page.rows.iter().enumerate() {
            if editor
                .range_to_bounds(&(text_offset..text_offset))
                .is_some_and(|bounds| bounds.bottom() > top)
            {
                return row.start;
            }
            text_offset +=
                row.text.len() + usize::from(row.terminated && index + 1 < page.rows.len());
        }
        self.bookmark.offset
    }

    fn jump_to_timestamp(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.available() {
            return;
        }
        self.clear_search_highlight(cx);
        let reference = self.first_visible_record_offset(cx);
        let Some(search) = &mut self.search else {
            return;
        };
        if search.progress.is_some() {
            return;
        }

        let target = search.input.read(cx).value().trim().to_owned();
        let progress = Progress::new(0);
        let cancel = progress.cancel.clone();
        search.progress = Some(progress);
        search.results = None;
        search.cancelled_at = None;

        let path = self.path.clone();
        let snapshot = self.retain_snapshot();
        let hidden = self.bookmark.hidden;
        let encoding = self.encoding_override;
        let operation = cx.background_spawn({
            let cancel = cancel.clone();
            async move {
                let _snapshot = snapshot;
                let mut reader = LogReader::open(path, encoding)?;
                reader.seek_timestamp_near(hidden, reference, &target, &cancel)
            }
        });
        cx.spawn_in(window, async move |this, cx| {
            let result = operation.await;
            let _ = this.update_in(cx, |viewer, window, cx| {
                let Some(search) = &mut viewer.search else {
                    return;
                };
                if !search.finish(&cancel) {
                    return;
                }
                if cancel.load(Ordering::Relaxed) {
                    search.cancelled_at = None;
                    search.message = "Поиск отменён.".into();
                } else {
                    match result {
                        Ok(Some(offset)) => {
                            search.message = "Переход к записи по времени.".into();
                            viewer.highlight_log_record(offset, window, cx);
                        }
                        Ok(None) => {
                            search.message = "Подходящих записей с датой и временем нет.".into()
                        }
                        Err(error) => search.message = error.to_string().into(),
                    }
                }
                cx.notify();
            });
        })
        .detach();
        cx.notify();
    }

    fn start_search(&mut self, more: bool, window: &mut Window, cx: &mut Context<Self>) {
        self.refresh_all_search_highlights(cx);
        if !self.available() {
            return;
        }
        self.clear_search_highlight(cx);
        let Some(search) = &mut self.search else {
            return;
        };
        if search.progress.is_some() {
            return;
        }
        let pattern = search.input.read(cx).value().to_string();
        if pattern.is_empty() {
            search.message = "Введите текст или шаблон поиска.".into();
            cx.notify();
            return;
        }
        let fresh = if search.regex {
            LogSearchQuery::regex(pattern, search.case_sensitive)
        } else {
            LogSearchQuery::literal(pattern, search.case_sensitive)
        };
        let query = LogSearchQuery {
            whole_word: search.whole_word,
            ..fresh
        };
        let (offset, next_line_index) = if more {
            search
                .results
                .as_ref()
                .map_or((self.bookmark.hidden, 0), |page| {
                    (page.next_offset, page.next_line_index)
                })
        } else {
            search.results = None;
            (self.bookmark.hidden, 0)
        };
        search.cancelled_at = None;
        let size = self.bookmark.size;
        let progress = Progress::new(size.saturating_sub(offset));
        let cancel = progress.cancel.clone();
        let done = progress.done.clone();
        search.progress = Some(progress);

        let path = self.path.clone();
        let snapshot = self.retain_snapshot();
        let encoding = self.encoding_override;
        let operation = cx.background_spawn({
            let cancel = cancel.clone();
            async move {
                let _snapshot = snapshot;
                let limits = SearchLimits {
                    max_scanned_bytes: SEARCH_BATCH_BYTES,
                    preview_bytes: 300,
                    ..SearchLimits::default()
                };
                let mut position = offset;
                let mut line_index = next_line_index;
                loop {
                    let mut page = match log_io::search_page(
                        &path,
                        encoding,
                        &query,
                        position,
                        SEARCH_PAGE_SIZE,
                        limits,
                        &cancel,
                    ) {
                        Err(LogError::LineLimit {
                            line_index: within_page,
                            limit,
                        }) => {
                            return Err(LogError::LineLimit {
                                line_index: line_index + within_page,
                                limit,
                            });
                        }
                        result => result?,
                    };
                    for hit in &mut page.hits {
                        hit.line_index += line_index;
                    }
                    page.next_line_index += line_index;
                    done.store(page.next_offset.saturating_sub(offset), Ordering::Relaxed);
                    if page.complete
                        || page.pending_line_start.is_some()
                        || !page.hits.is_empty()
                        || page.next_offset <= position
                    {
                        return Ok(page);
                    }
                    position = page.next_offset;
                    line_index = page.next_line_index;
                }
            }
        });
        cx.spawn_in(window, async move |this, cx| {
            let result = operation.await;
            let _ = this.update_in(cx, |viewer, _, cx| {
                let Some(search) = &mut viewer.search else {
                    return;
                };
                if !search.finish(&cancel) {
                    return;
                }
                if let Some(at) = search.cancelled_at.take() {
                    if measurement_start().is_some() {
                        println!("RUSTTEXT cancel_search_ms={}", at.elapsed().as_millis());
                    }
                    search.message = "Поиск отменён.".into();
                    cx.notify();
                    return;
                }
                match result {
                    Ok(page) => {
                        search.message = if page.pending_line_start.is_some() {
                            format!(
                                "Строк с совпадениями: {}. После дописывания файла нажмите «Ещё», чтобы проверить последнюю строку.",
                                page.hits.len()
                            )
                            .into()
                        } else if page.hits.is_empty() {
                            "Совпадений больше нет.".into()
                        } else if page.complete {
                            format!(
                                "Строк с совпадениями: {}. Достигнут конец файла.",
                                page.hits.len()
                            )
                            .into()
                        } else {
                            format!(
                                "Строк с совпадениями: {}. Нажмите «Ещё» для продолжения.",
                                page.hits.len()
                            )
                            .into()
                        };
                        search.results = Some(page);
                    }
                    Err(LogError::Cancelled) => search.message = "Поиск отменён.".into(),
                    Err(LogError::LineLimit { line_index, limit }) => {
                        let hidden = if viewer.bookmark.hidden == 0 {
                            Some(0)
                        } else {
                            viewer.hidden_line_count
                        };
                        search.message = match hidden {
                            Some(hidden) => format!(
                                "Поиск остановлен. Размер строки {} превышает лимит в {limit} байт.",
                                hidden + line_index + 1
                            ),
                            None => format!("Поиск остановлен. Строка превышает лимит в {limit} байт."),
                        }.into();
                    }
                    Err(error) => search.message = format!("Поиск остановлен. {error}").into(),
                }
                cx.notify();
            });
        })
        .detach();
        cx.notify();
    }
}

pub(super) fn progress_percent(done: u64, total: u64) -> u64 {
    done.saturating_mul(100)
        .checked_div(total)
        .unwrap_or(0)
        .min(100)
}

fn can_continue(page: &SearchPage, current_file_size: u64) -> bool {
    !page.complete
        && page
            .pending_line_start
            .is_none_or(|_| page.file_size < current_file_size)
}

fn option_button(
    id: &'static str,
    label: &'static str,
    tooltip: &'static str,
    selected: bool,
    disabled: bool,
    field: fn(&mut LogSearch) -> &mut bool,
    cx: &mut Context<LogViewer>,
) -> Button {
    Button::new(id)
        .small()
        .outline()
        .label(label)
        .tooltip(tooltip)
        .selected(selected)
        .when(selected, |button| {
            button.border_color(rgb(crate::appearance::ACCENT))
        })
        .disabled(disabled)
        .on_click(cx.listener(move |this, _, _, cx| {
            let changed = if let Some(search) = &mut this.search {
                let value = field(search);
                *value = !*value;
                true
            } else {
                false
            };
            if changed {
                this.reset_search(cx);
                this.refresh_all_search_highlights(cx);
                cx.notify();
            }
        }))
}

fn result_button(hit: &SearchHit, first_line: Option<u64>, cx: &mut Context<LogViewer>) -> Button {
    let offset = hit.line_start;
    let match_range = hit.match_range.clone();
    let label = match first_line {
        Some(first_line) => format!("Строка {} · {}", first_line + hit.line_index, hit.preview),
        None => hit.preview.clone(),
    };
    let tooltip = label.clone();
    Button::new(("log-hit", offset))
        .w_full()
        .ghost()
        .accessibility_label(tooltip.clone())
        .tooltip(tooltip)
        .child(
            div()
                .w_full()
                .min_w_0()
                .overflow_hidden()
                .text_left()
                .child(Label::new(label).truncate()),
        )
        .on_click(cx.listener(move |this, _, window, cx| {
            this.highlight_log_match(offset, Some(match_range.clone()), window, cx)
        }))
}

#[cfg(test)]
#[path = "../../tests/unit/log_search.rs"]
mod tests;
