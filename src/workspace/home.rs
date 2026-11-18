use std::{
    collections::HashMap,
    path::{Path, PathBuf},
    time::Duration,
};

use gpui_kit::{
    component::{
        Disableable, Sizable,
        button::{Button, ButtonVariants},
        input::{Input, InputEvent, InputState, Rope, RopeExt},
        label::Label,
    },
    *,
};

use super::{Workspace, display_path, file_name};
use crate::{
    appearance,
    recent_files::{RecentFile, RecentFiles, RecentPosition, now_millis},
};

const PAGE_SIZE: usize = 40;
const MINUTE_MS: u64 = 60_000;
const HOUR_MS: u64 = 60 * MINUTE_MS;
const DAY_MS: u64 = 24 * HOUR_MS;

pub(super) struct Home {
    query: Entity<InputState>,
    page: usize,
    refresh: Option<(u64, Task<()>)>,
    _change: Subscription,
    pub(super) open: bool,
    pub(super) positions: HashMap<PathBuf, RecentPosition>,
    line_counts: HashMap<PathBuf, Option<u64>>,
    pending: HashMap<PathBuf, RecentFile>,
    writing: bool,
    pub(super) close_pending: bool,
    pub(super) close_recorded: bool,
}

impl Home {
    pub(super) fn new(window: &mut Window, cx: &mut Context<Workspace>) -> Self {
        let query = cx.new(|cx| InputState::new(window, cx).placeholder("Поиск по имени и пути"));
        let change = cx.subscribe(&query, |this, _, event, cx| {
            if matches!(event, InputEvent::Change) {
                this.home.page = 0;
                cx.notify();
            }
        });
        Self {
            query,
            page: 0,
            refresh: None,
            _change: change,
            open: false,
            positions: HashMap::new(),
            line_counts: HashMap::new(),
            pending: HashMap::new(),
            writing: false,
            close_pending: false,
            close_recorded: false,
        }
    }

    pub(super) fn stop_refresh(&mut self) {
        self.refresh = None;
    }

    fn schedule_refresh(
        &mut self,
        deadline: Option<u64>,
        now_ms: u64,
        cx: &mut Context<Workspace>,
    ) {
        if self.refresh.as_ref().map(|(deadline, _)| *deadline) == deadline {
            return;
        }
        self.refresh = deadline.map(|deadline| {
            let delay = Duration::from_millis(deadline.saturating_sub(now_ms));
            let executor = cx.background_executor().clone();
            let task = cx.spawn(async move |this, cx| {
                executor.timer(delay).await;
                let _ = this.update(cx, |this, cx| {
                    this.home.stop_refresh();
                    cx.notify();
                });
            });
            (deadline, task)
        });
    }
}

pub(super) fn text_line_count(text: &Rope) -> u64 {
    if text.len() == 0 {
        0
    } else {
        text.lines_len() as u64
    }
}

impl Workspace {
    pub(super) fn remember_position(
        &mut self,
        id: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.record_position(id, false, window, cx);
    }

    pub(super) fn remember_activity(
        &mut self,
        id: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.record_position(id, true, window, cx);
    }

    fn record_position(
        &mut self,
        id: u64,
        force_activity: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(doc) = self.documents.iter().find(|doc| doc.id == id) else {
            return;
        };
        let Some(file) = &doc.file else { return };
        let (tab, _) = doc.session_snapshot(false, cx);
        let position = RecentPosition {
            json_validation_disabled: tab.json_validation_disabled,
            selection: tab.selection,
            reversed: tab.reversed,
            scroll: tab.scroll,
            log: tab.log,
        };
        let line_count = doc.history_line_count(cx);
        let stored = self.recent.entry(&file.path);
        let known_position = self
            .home
            .positions
            .get(&file.path)
            .or_else(|| stored.and_then(|entry| entry.position.as_ref()));
        let known_lines = self
            .home
            .line_counts
            .get(&file.path)
            .copied()
            .flatten()
            .or_else(|| stored.and_then(|entry| entry.line_count));
        let position_changed = known_position != Some(&position);
        let lines_changed = line_count.is_some() && known_lines != line_count;
        if position_changed || lines_changed || force_activity {
            let mut recent = RecentFile::new(file.path.clone(), doc.format.encoding);
            recent.view_as_log = doc.log.is_some();
            recent.position = Some(position.clone());
            recent.line_count = line_count;
            recent.last_worked_ms = Some(now_millis());
            self.home.positions.insert(file.path.clone(), position);
            if line_count.is_some() {
                self.home.line_counts.insert(file.path.clone(), line_count);
            }
            self.recent.update_entry_in_memory(&recent);
            self.home.pending.insert(file.path.clone(), recent);
        }
        self.flush_history(window, cx);
    }

    pub(super) fn focus_home_query(&self, window: &mut Window, cx: &mut Context<Self>) {
        let query = self.home.query.clone();
        query.update(cx, |input, cx| input.focus(window, cx));
    }

    pub(super) fn toggle_home(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.documents.is_empty() {
            self.home.page = 0;
            let query = self.home.query.clone();
            query.update(cx, |input, cx| input.focus(window, cx));
            cx.notify();
            return;
        }
        if self.home.open {
            self.close_home(window, cx);
        } else {
            if self.active != 0 {
                self.remember_activity(self.active, window, cx);
            }
            self.home.open = true;
            self.home.page = 0;
            let query = self.home.query.clone();
            query.update(cx, |input, cx| input.focus(window, cx));
            cx.notify();
        }
    }

    pub(super) fn close_home(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if !self.home.open {
            return;
        }
        self.home.open = false;
        if self.active != 0 {
            self.focus_active_document(window, cx);
        } else {
            self.fallback_focus.focus(window, cx);
        }
        cx.notify();
    }

    pub(super) fn history_is_writing(&self) -> bool {
        self.home.writing || !self.home.pending.is_empty()
    }

    fn flush_history(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.home.writing || self.home.pending.is_empty() {
            return;
        }
        let files: Vec<_> = std::mem::take(&mut self.home.pending)
            .into_values()
            .collect();
        let retry = files.clone();
        self.home.writing = true;
        let task = cx.background_spawn(async move {
            let (mut recent, _) = RecentFiles::load();
            recent.record_positions(files).map(|()| recent)
        });
        cx.spawn_in(window, async move |this, cx| {
            let result = task.await;
            let _ = this.update_in(cx, |this, window, cx| {
                this.home.writing = false;
                match result {
                    Ok(recent) => {
                        for entry in recent.entries() {
                            if !this.home.pending.contains_key(&entry.path) {
                                if let Some(position) = &entry.position {
                                    this.home
                                        .positions
                                        .insert(entry.path.clone(), position.clone());
                                }
                                if entry.line_count.is_some() {
                                    this.home
                                        .line_counts
                                        .insert(entry.path.clone(), entry.line_count);
                                }
                            }
                        }
                        this.recent.merge_loaded(recent);
                        if !this.home.pending.is_empty() {
                            this.flush_history(window, cx);
                        } else if std::mem::take(&mut this.home.close_pending)
                            && this.request_window_close(window, cx)
                        {
                            window.remove_window();
                        }
                    }
                    Err(error) => {
                        for file in retry {
                            this.home.pending.entry(file.path.clone()).or_insert(file);
                        }
                        this.home.close_pending = false;
                        this.home.close_recorded = false;
                        this.error = Some(super::recent_warning(error));
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }

    pub(super) fn home_page(&mut self, cx: &mut Context<Self>) -> AnyElement {
        let now_ms = now_millis();
        let query = self.home.query.read(cx).value().to_lowercase();
        let entries: Vec<_> = self
            .recent
            .entries()
            .iter()
            .filter(|entry| {
                query.is_empty() || display_path(&entry.path).to_lowercase().contains(&query)
            })
            .collect();
        let page = self
            .home
            .page
            .min(entries.len().saturating_sub(1) / PAGE_SIZE);
        let start = page * PAGE_SIZE;
        let drafts: Vec<_> = self
            .closed_tab_entries()
            .filter(|entry| {
                entry.is_draft && (query.is_empty() || entry.label.to_lowercase().contains(&query))
            })
            .collect();
        let visible_entries = entries.iter().skip(start).take(PAGE_SIZE);
        let timestamps = visible_entries
            .clone()
            .filter_map(|entry| entry.last_worked_ms)
            .chain(drafts.iter().filter_map(|entry| entry.last_worked_ms));
        self.home
            .schedule_refresh(next_history_refresh(timestamps, now_ms), now_ms, cx);
        div()
            .size_full()
            .p_4()
            .flex()
            .flex_col()
            .gap_3()
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(Label::new("История файлов").text_lg())
                    .child(div().flex_1())
                    .children((!self.documents.is_empty()).then(|| {
                        Button::new("home-back")
                            .small()
                            .ghost()
                            .label("К редактору")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.close_home(window, cx)),
                            )
                    }))
                    .child(
                        Button::new("home-new")
                            .small()
                            .label("Новый документ")
                            .on_click(cx.listener(|this, _, window, cx| this.new_file(window, cx))),
                    )
                    .child(
                        Button::new("home-open")
                            .small()
                            .label("Открыть файл…")
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.open_picker(super::file_actions::OpenMode::NORMAL, window, cx)
                            })),
                    ),
            )
            .child(Input::new(&self.home.query))
            .children(
                self.error
                    .as_ref()
                    .map(|error| Label::new(error.clone()).text_color(rgb(appearance::ERROR))),
            )
            .children(
                self.session_error
                    .as_ref()
                    .map(|error| Label::new(error.clone()).text_color(rgb(appearance::ERROR))),
            )
            .children(
                self.session_notice
                    .as_ref()
                    .map(|notice| Label::new(notice.clone()).text_color(rgb(appearance::MUTED))),
            )
            .child(
                div()
                    .id("home-history")
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scroll()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .children(entries.is_empty().then(|| {
                        Label::new(if query.is_empty() {
                            "Открытые файлы появятся здесь."
                        } else {
                            "Ничего не найдено."
                        })
                        .text_color(rgb(appearance::MUTED))
                    }))
                    .children(visible_entries.enumerate().map(|(index, entry)| {
                        let entry = (*entry).clone();
                        let path = display_path(&entry.path);
                        let name = file_name(&entry.path);
                        let meta = history_meta(entry.line_count, entry.last_worked_ms, now_ms);
                        div()
                            .flex()
                            .items_center()
                            .gap_2()
                            .min_w_0()
                            .child(
                                Button::new(("home-file", start + index))
                                    .small()
                                    .ghost()
                                    .label(name)
                                    .tooltip(path.clone())
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.open_recent(entry.clone(), window, cx)
                                    })),
                            )
                            .child(
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .overflow_hidden()
                                    .text_sm()
                                    .text_color(rgb(appearance::MUTED))
                                    .child(Label::new(path).truncate()),
                            )
                            .children((!meta.is_empty()).then(|| {
                                div()
                                    .flex_shrink_0()
                                    .text_sm()
                                    .text_color(rgb(appearance::MUTED))
                                    .child(Label::new(meta))
                            }))
                    }))
                    .children(
                        (!drafts.is_empty()).then(|| Label::new("Закрытые черновики").text_lg()),
                    )
                    .children(drafts.into_iter().map(|entry| {
                        let id = entry.id;
                        let meta = history_meta(entry.line_count, entry.last_worked_ms, now_ms);
                        div()
                            .flex()
                            .items_center()
                            .justify_between()
                            .gap_2()
                            .child(
                                Button::new(("home-draft", id))
                                    .small()
                                    .ghost()
                                    .label(entry.label)
                                    .on_click(cx.listener(move |this, _, window, cx| {
                                        this.reopen_closed_tab(id, window, cx)
                                    })),
                            )
                            .children((!meta.is_empty()).then(|| {
                                Label::new(meta)
                                    .flex_shrink_0()
                                    .text_sm()
                                    .text_color(rgb(appearance::MUTED))
                            }))
                    })),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(
                        Label::new(format!(
                            "Файлов: {} · страница {} из {}",
                            entries.len(),
                            page + 1,
                            entries.len().div_ceil(PAGE_SIZE).max(1)
                        ))
                        .text_sm(),
                    )
                    .child(div().flex_1())
                    .child(
                        Button::new("history-prev")
                            .small()
                            .ghost()
                            .label("Назад")
                            .disabled(page == 0)
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.home.page = this.home.page.saturating_sub(1);
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("history-next")
                            .small()
                            .ghost()
                            .label("Дальше")
                            .disabled(start + PAGE_SIZE >= entries.len())
                            .on_click(cx.listener(|this, _, _, cx| {
                                this.home.page += 1;
                                cx.notify();
                            })),
                    ),
            )
            .into_any_element()
    }
}

pub(super) fn history_file_label(path: &Path) -> String {
    let name = file_name(path);
    let directory = path.parent().map(display_path).unwrap_or_default();
    format!("{name} — {directory}")
}

pub(super) fn history_meta(
    line_count: Option<u64>,
    last_worked_ms: Option<u64>,
    now_ms: u64,
) -> String {
    let mut parts = Vec::new();
    if let Some(lines) = line_count {
        parts.push(format!("{lines} стр."));
    }
    if let Some(last_worked_ms) = last_worked_ms {
        parts.push(format_last_worked(last_worked_ms, now_ms));
    }
    parts.join(" · ")
}

pub(crate) fn format_last_worked(last_worked_ms: u64, now_ms: u64) -> String {
    let age = now_ms.saturating_sub(last_worked_ms);
    if age < MINUTE_MS {
        "только что".to_owned()
    } else if age < HOUR_MS {
        format!("{} мин назад", age / MINUTE_MS)
    } else if age < DAY_MS {
        format!("{} ч назад", age / HOUR_MS)
    } else {
        format_local_datetime(last_worked_ms)
    }
}

fn next_history_refresh(timestamps: impl Iterator<Item = u64>, now_ms: u64) -> Option<u64> {
    timestamps
        .filter_map(|timestamp| {
            let age = now_ms.saturating_sub(timestamp);
            let interval = match age {
                ..HOUR_MS => MINUTE_MS,
                HOUR_MS..DAY_MS => HOUR_MS,
                _ => return None,
            };
            Some(now_ms.saturating_add(interval - age % interval))
        })
        .min()
}

fn format_local_datetime(epoch_ms: u64) -> String {
    let (year, month, day, hour, minute) = local_datetime_parts(epoch_ms);
    format!("{day:02}.{month:02}.{year:04} {hour:02}:{minute:02}")
}

#[cfg(windows)]
fn local_datetime_parts(epoch_ms: u64) -> (u16, u16, u16, u16, u16) {
    #[repr(C)]
    #[derive(Clone, Copy, Default)]
    struct FileTime {
        low: u32,
        high: u32,
    }

    #[repr(C)]
    #[derive(Clone, Copy, Default)]
    struct SystemTimeFields {
        year: u16,
        month: u16,
        day_of_week: u16,
        day: u16,
        hour: u16,
        minute: u16,
        second: u16,
        milliseconds: u16,
    }

    unsafe extern "system" {
        fn FileTimeToSystemTime(
            lpfiletime: *const FileTime,
            lpsystemtime: *mut SystemTimeFields,
        ) -> i32;
        fn SystemTimeToTzSpecificLocalTime(
            lptimezoneinformation: *const std::ffi::c_void,
            lpuniversaltime: *const SystemTimeFields,
            lplocaltime: *mut SystemTimeFields,
        ) -> i32;
    }

    const WINDOWS_EPOCH_OFFSET_100NS: u64 = 116_444_736_000_000_000;
    let ticks = (epoch_ms as u128)
        .saturating_mul(10_000)
        .saturating_add(WINDOWS_EPOCH_OFFSET_100NS as u128)
        .min(u64::MAX as u128) as u64;
    let utc = FileTime {
        low: ticks as u32,
        high: (ticks >> 32) as u32,
    };
    let mut utc_system = SystemTimeFields::default();
    let mut local_system = SystemTimeFields::default();
    unsafe {
        if FileTimeToSystemTime(&utc, &mut utc_system) != 0
            && SystemTimeToTzSpecificLocalTime(std::ptr::null(), &utc_system, &mut local_system)
                != 0
        {
            return (
                local_system.year,
                local_system.month,
                local_system.day,
                local_system.hour,
                local_system.minute,
            );
        }
    }
    utc_datetime_parts(epoch_ms)
}

#[cfg(not(windows))]
fn local_datetime_parts(epoch_ms: u64) -> (u16, u16, u16, u16, u16) {
    utc_datetime_parts(epoch_ms)
}

fn utc_datetime_parts(epoch_ms: u64) -> (u16, u16, u16, u16, u16) {
    let total_seconds = (epoch_ms / 1000) as i64;
    let days = total_seconds.div_euclid(86_400);
    let secs_of_day = total_seconds.rem_euclid(86_400);
    let hour = (secs_of_day / 3600) as u16;
    let minute = ((secs_of_day % 3600) / 60) as u16;

    // Howard Hinnant's civil-from-days algorithm.
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u16;
    let month = (if mp < 10 { mp + 3 } else { mp - 9 }) as u16;
    let year = (y + if month <= 2 { 1 } else { 0 }) as u16;
    (year, month, day, hour, minute)
}

#[cfg(test)]
#[path = "../../tests/unit/home.rs"]
mod tests;
