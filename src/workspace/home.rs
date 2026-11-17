use std::{collections::HashMap, path::PathBuf};

use gpui_kit::{
    component::{
        Disableable, Sizable,
        button::{Button, ButtonVariants},
        input::{Input, InputEvent, InputState},
        label::Label,
    },
    *,
};

use super::{Workspace, display_path, file_name};
use crate::appearance;
use crate::recent_files::{RecentFile, RecentFiles, RecentPosition};

const PAGE_SIZE: usize = 40;

pub(super) struct Home {
    query: Entity<InputState>,
    page: usize,
    _change: Subscription,
    pub(super) positions: HashMap<PathBuf, RecentPosition>,
    pending: HashMap<PathBuf, RecentFile>,
    writing: bool,
    pub(super) close_pending: bool,
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
            _change: change,
            positions: HashMap::new(),
            pending: HashMap::new(),
            writing: false,
            close_pending: false,
        }
    }
}

impl Workspace {
    pub(super) fn remember_position(
        &mut self,
        id: u64,
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
        if self.home.positions.get(&file.path) != Some(&position) {
            let mut recent = RecentFile::new(file.path.clone(), doc.format.encoding);
            recent.view_as_log = doc.log.is_some();
            recent.position = Some(position.clone());
            self.home.positions.insert(file.path.clone(), position);
            self.home.pending.insert(file.path.clone(), recent);
        }
        self.flush_history(window, cx);
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
                            if !this.home.pending.contains_key(&entry.path)
                                && let Some(position) = &entry.position
                            {
                                this.home
                                    .positions
                                    .insert(entry.path.clone(), position.clone());
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
                        this.error = Some(super::recent_warning(error));
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }

    pub(super) fn home_page(&self, _window: &mut Window, cx: &mut Context<Self>) -> AnyElement {
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
            .into_iter()
            .filter(|(id, name)| {
                self.closed
                    .iter()
                    .any(|entry| entry.tab.id == *id && entry.tab.file.is_none())
                    && (query.is_empty() || name.to_lowercase().contains(&query))
            })
            .collect();
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
                    .children(entries.iter().skip(start).take(PAGE_SIZE).enumerate().map(
                        |(index, entry)| {
                            let entry = (*entry).clone();
                            let path = display_path(&entry.path);
                            let name = file_name(&entry.path);
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
                                        .child(path),
                                )
                        },
                    ))
                    .children(
                        (!drafts.is_empty()).then(|| Label::new("Закрытые черновики").text_lg()),
                    )
                    .children(drafts.into_iter().map(|(id, name)| {
                        Button::new(("home-draft", id))
                            .small()
                            .ghost()
                            .label(name)
                            .on_click(cx.listener(move |this, _, window, cx| {
                                this.reopen_closed_tab(id, window, cx)
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
