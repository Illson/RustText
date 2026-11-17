use gpui_kit::component::{
    Disableable, Selectable, Sizable,
    button::{Button, ButtonVariants},
    checkbox::Checkbox,
    input::{Input, InputEvent, InputState},
    label::Label,
};
use gpui_kit::prelude::FluentBuilder;
use gpui_kit::*;
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::time::Instant;

use super::{Document, Workspace};
use crate::{
    appearance,
    search::{
        self, Command, Hit, NavigationProgress, NavigationStart, Outcome, Query, SearchMatch,
        Source,
    },
    search_highlight::SearchHighlight,
};

struct SearchResume {
    query: Query,
    origin: Hit,
    versions: Vec<(u64, u64)>,
    progress: NavigationProgress,
}

pub(super) struct SearchPanel {
    query_input: Entity<InputState>,
    replacement_input: Entity<InputState>,
    options: Query,
    last_hit: Option<(Query, Hit, u64)>,
    resume: Option<SearchResume>,
    replacing: bool,
    visible_matches: Vec<SearchMatch>,
    match_revisions: Vec<(u64, u64)>,
    match_highlight: Option<(u64, SearchHighlight)>,
    only_current: bool,
    all_highlights: Vec<(u64, SearchHighlight)>,
    request_generation: u64,
    message: SharedString,
    task: Option<Task<()>>,
    cancellation: Arc<AtomicBool>,
    _subscriptions: [Subscription; 2],
}

#[derive(Clone, Copy)]
enum OptionToggle {
    Regex,
    Case,
    WholeWord,
    AllTabs,
}

impl SearchPanel {
    fn new(window: &mut Window, cx: &mut Context<Workspace>) -> Self {
        let query_input = cx.new(|cx| InputState::new(window, cx).placeholder("Текст поиска"));
        let replacement_input = cx.new(|cx| InputState::new(window, cx).placeholder("Замена"));
        let query = cx.subscribe_in(
            &query_input,
            window,
            |this, _, event, window, cx| match event {
                InputEvent::PressEnter { shift, .. } => this.run_search(
                    if *shift {
                        Command::Previous
                    } else {
                        Command::List
                    },
                    window,
                    cx,
                ),
                InputEvent::Change => this.reset_search(cx),
                _ => {}
            },
        );
        let replacement = cx.subscribe_in(
            &replacement_input,
            window,
            |this, _, event, window, cx| match event {
                InputEvent::PressEnter { .. } => this.run_search(Command::Replace, window, cx),
                InputEvent::Change => this.reset_search(cx),
                _ => {}
            },
        );
        Self {
            query_input,
            replacement_input,
            options: Query::default(),
            last_hit: None,
            resume: None,
            replacing: false,
            visible_matches: Vec::new(),
            match_revisions: Vec::new(),
            match_highlight: None,
            only_current: true,
            all_highlights: Vec::new(),
            request_generation: 0,
            message: "Введите текст поиска".into(),
            task: None,
            cancellation: Arc::new(AtomicBool::new(false)),
            _subscriptions: [query, replacement],
        }
    }

    fn query(&self, cx: &App) -> Query {
        Query {
            text: self.query_input.read(cx).value().to_string(),
            ..self.options.clone()
        }
    }

    fn clear_match_highlight(&mut self, cx: &mut Context<Workspace>) {
        for (_, highlight) in self.all_highlights.drain(..) {
            highlight.clear(cx);
        }
        if let Some((_, highlight)) = self.match_highlight.take() {
            highlight.clear(cx);
        }
    }

    fn render(
        &self,
        busy: bool,
        can_replace: bool,
        documents: &[Document],
        window: &Window,
        cx: &mut Context<Workspace>,
    ) -> impl IntoElement {
        let controls_disabled = busy || self.task.is_some();
        let replacements_disabled = controls_disabled || self.options.all_tabs || !can_replace;
        let query = self.query(cx);
        let panel_height = (f32::from(window.viewport_size().height) - 230.0).clamp(120.0, 190.0);
        let results_height =
            (panel_height - 70.0 - if self.replacing { 32.0 } else { 0.0 }).max(18.0);
        let visible_matches = self
            .visible_matches
            .iter()
            .enumerate()
            .map(|(index, result)| {
                let result = result.clone();
                let tab_name = documents
                    .iter()
                    .find(|document| document.id == result.hit.tab)
                    .map_or_else(|| "Закрытая вкладка".to_owned(), |doc| doc.name.to_string());
                let label = format!("{tab_name} · строка {} · {}", result.line, result.excerpt);
                let title = label.clone();
                let revisions = self.match_revisions.clone();
                let query = query.clone();
                Button::new(("search-result", index))
                    .w_full()
                    .ghost()
                    .accessibility_label(title.clone())
                    .tooltip(title)
                    .child(
                        div()
                            .w_full()
                            .min_w_0()
                            .overflow_hidden()
                            .text_left()
                            .child(Label::new(label).truncate()),
                    )
                    .on_click(cx.listener(move |this, _, window, cx| {
                        let versions =
                            search_documents(&this.documents, query.all_tabs, result.hit.tab)
                                .map(|doc| (doc.id, doc.revision));
                        if !versions.eq(revisions.iter().copied()) {
                            this.reset_search(cx);
                            this.search_message("Текст изменился. Обновите список совпадений.", cx);
                            return;
                        }

                        this.select(result.hit.tab, window, cx);
                        this.active_document().editor.update(cx, |editor, cx| {
                            editor.set_selected_range(result.hit.range.clone(), cx);
                        });
                        this.show_search_highlight(result.hit.tab, result.hit.range.clone(), cx);
                        let revision = this.active_document().revision;
                        if let Some(panel) = &mut this.search {
                            panel.last_hit = Some((query.clone(), result.hit.clone(), revision));
                            panel.resume = None;
                        }
                        this.search_message(format!("Строка {}", result.line), cx);
                    }))
            })
            .collect::<Vec<_>>();
        div()
            .flex()
            .flex_col()
            .flex_shrink(1.)
            .max_h(px(panel_height))
            .overflow_hidden()
            .gap_0()
            .px_2()
            .py_1()
            .bg(rgb(appearance::SURFACE))
            .border_b_1()
            .border_color(rgb(appearance::BORDER))
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .child(Input::new(&self.query_input)),
                    )
                    .child(command_button(
                        "find-list",
                        "Найти",
                        Command::List,
                        controls_disabled,
                        cx,
                    ))
                    .child(command_button(
                        "find-prev",
                        "↑",
                        Command::Previous,
                        controls_disabled,
                        cx,
                    ))
                    .child(command_button(
                        "find-next",
                        "↓",
                        Command::Next,
                        controls_disabled,
                        cx,
                    ))
                    .child(
                        Button::new("toggle-replace")
                            .small()
                            .ghost()
                            .label(if self.replacing { "×" } else { "Замена" })
                            .tooltip(if self.replacing {
                                "Скрыть поле замены"
                            } else {
                                "Найти и заменить · Ctrl+H"
                            })
                            .on_click(cx.listener(|this, _, window, cx| {
                                let Some(panel) = &mut this.search else {
                                    return;
                                };
                                panel.replacing = !panel.replacing;
                                if panel.replacing {
                                    panel.replacement_input.update(cx, |input, cx| {
                                        input.focus(window, cx);
                                    });
                                }
                                cx.notify();
                            })),
                    )
                    .child(
                        Button::new("close-search")
                            .small()
                            .ghost()
                            .label("×")
                            .tooltip("Закрыть поиск")
                            .on_click(
                                cx.listener(|this, _, window, cx| this.close_search(window, cx)),
                            ),
                    ),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(option_button(
                        "pattern-regex",
                        ".*",
                        OptionToggle::Regex,
                        self.options.regex,
                        cx,
                    ))
                    .child(option_button(
                        "pattern-case",
                        "Aa",
                        OptionToggle::Case,
                        self.options.case_sensitive,
                        cx,
                    ))
                    .child(option_button(
                        "pattern-word",
                        "Слово",
                        OptionToggle::WholeWord,
                        self.options.whole_word,
                        cx,
                    ))
                    .child(
                        Checkbox::new("search-only-current")
                            .label("Только текущее")
                            .checked(self.only_current)
                            .on_click(cx.listener(|this, checked, _, cx| {
                                if let Some(panel) = &mut this.search {
                                    panel.only_current = *checked;
                                }
                                this.refresh_all_search_highlights(cx);
                            })),
                    )
                    .child(option_button(
                        "scope-all",
                        "Вкладки",
                        OptionToggle::AllTabs,
                        self.options.all_tabs,
                        cx,
                    ))
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .overflow_hidden()
                            .text_xs()
                            .text_color(rgb(appearance::MUTED))
                            .child(Label::new(self.message.clone())),
                    ),
            )
            .children(self.replacing.then(|| {
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(
                        div().flex_1().min_w_0().child(
                            Input::new(&self.replacement_input).disabled(replacements_disabled),
                        ),
                    )
                    .child(command_button(
                        "replace-one",
                        "Заменить",
                        Command::Replace,
                        replacements_disabled,
                        cx,
                    ))
                    .child(command_button(
                        "replace-all",
                        "Всё",
                        Command::ReplaceAll,
                        replacements_disabled,
                        cx,
                    ))
            }))
            .children((!visible_matches.is_empty()).then(|| {
                div()
                    .id("text-search-results")
                    .max_h(px(results_height))
                    .overflow_y_scroll()
                    .flex()
                    .flex_col()
                    .children(visible_matches)
            }))
    }
}

impl Workspace {
    pub(super) fn invalidate_document_search(&mut self, tab: u64, cx: &mut Context<Self>) {
        let Some(panel) = &mut self.search else {
            return;
        };
        invalidate_document_highlights(
            tab,
            &mut panel.all_highlights,
            &mut panel.match_highlight,
            |highlight| highlight.clear(cx),
        );
        panel.cancellation.store(true, Ordering::Relaxed);
        panel.task = None;
        panel.cancellation = Arc::new(AtomicBool::new(false));
        panel.request_generation = panel.request_generation.wrapping_add(1);
        panel.last_hit = None;
        panel.resume = None;
        panel.visible_matches.clear();
        panel.match_revisions.clear();
    }

    pub(super) fn refresh_all_search_highlights(&mut self, cx: &mut Context<Self>) {
        let Some(panel) = &mut self.search else {
            return;
        };
        if self.active == 0 {
            panel.clear_match_highlight(cx);
            return;
        }
        let query = panel.query(cx);
        let visible = visible_highlight_tabs(
            &self.layout,
            self.active,
            panel.only_current,
            query.all_tabs,
        );
        sync_all_highlights(
            &visible,
            &mut panel.all_highlights,
            cx,
            |cx, highlight| highlight.clear(cx),
            |cx, id| {
                let document = self
                    .documents
                    .iter()
                    .find(|document| document.log.is_none() && document.id == id)?;
                SearchHighlight::visible_matches(
                    &document.editor,
                    &query,
                    &document.search_viewport,
                    cx,
                )
            },
        );
    }

    fn show_search_highlight(
        &mut self,
        tab: u64,
        range: std::ops::Range<usize>,
        cx: &mut Context<Self>,
    ) {
        if let Some(panel) = &mut self.search
            && let Some((_, highlight)) = panel.match_highlight.take()
        {
            highlight.clear(cx);
        }
        let Some(document) = self.documents.iter().find(|document| document.id == tab) else {
            return;
        };
        let highlight = SearchHighlight::new(&document.editor, range, cx);
        if let Some(panel) = &mut self.search {
            panel.match_highlight = Some((tab, highlight));
        }
    }

    pub(super) fn open_search(
        &mut self,
        replace: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.showing_home() {
            if !replace {
                self.focus_home_query(window, cx);
            }
            return;
        }
        if self.active_document_opt().is_none() {
            return;
        }
        if let Some(log) = self.active_document().log.clone() {
            log.update(cx, |viewer, cx| viewer.open_search(window, cx));
            return;
        }
        if self.search.is_none() {
            self.search = Some(SearchPanel::new(window, cx));
        }
        let panel = self.search.as_mut().expect("search panel is present");
        panel.replacing = replace && !panel.options.all_tabs;
        let input = if replace && !panel.options.all_tabs {
            &panel.replacement_input
        } else {
            &panel.query_input
        };
        input.update(cx, |state, cx| {
            state.focus(window, cx);
            state.select_all(window, cx);
        });
        cx.notify();
    }

    pub(super) fn search_panel(
        &self,
        window: &Window,
        cx: &mut Context<Self>,
    ) -> Option<impl IntoElement> {
        let active = self.active_document_opt()?;
        let can_replace = active.log.is_none() && !active.placeholder;
        let busy = self.busy.is_some() || self.applying_update();
        self.search
            .as_ref()
            .map(|panel| panel.render(busy, can_replace, &self.documents, window, cx))
    }

    fn close_search(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.clear_search(cx);
        self.select(self.active, window, cx);
    }

    pub(super) fn clear_search(&mut self, cx: &mut Context<Self>) {
        if let Some(panel) = &mut self.search {
            panel.cancellation.store(true, Ordering::Relaxed);
            panel.clear_match_highlight(cx);
        }
        self.search = None;
    }

    fn reset_search(&mut self, cx: &mut Context<Self>) {
        let Some(panel) = &mut self.search else {
            return;
        };
        panel.cancellation.store(true, Ordering::Relaxed);
        panel.task = None;
        panel.cancellation = Arc::new(AtomicBool::new(false));
        panel.last_hit = None;
        panel.clear_match_highlight(cx);
        panel.request_generation = panel.request_generation.wrapping_add(1);
        panel.visible_matches.clear();
        panel.match_revisions.clear();
        panel.resume = None;
        panel.message = if panel.options.all_tabs {
            "Поиск по всем вкладкам. Замена доступна в текущей вкладке.".into()
        } else {
            "Enter — показать совпадения".into()
        };
        self.refresh_all_search_highlights(cx);
        cx.notify();
    }

    pub(super) fn run_search(
        &mut self,
        command: Command,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.active_document_opt().is_none() {
            return;
        }
        if self.busy.is_some() || self.applying_update() || self.search.is_none() {
            return;
        }
        let active = self.active_document();
        if active.log.is_some() || (active.placeholder && command.replaces()) {
            return;
        }
        let origin = Hit {
            tab: active.id,
            range: active.editor.read(cx).selected_range(),
        };
        let origin_revision = active.revision;
        self.refresh_all_search_highlights(cx);
        let panel = self.search.as_mut().expect("search panel is present");
        let query = panel.query(cx);
        if panel.task.is_some() || (command.replaces() && query.all_tabs) {
            return;
        }
        if let Some((_, highlight)) = panel.match_highlight.take() {
            highlight.clear(cx);
        }
        panel.request_generation = panel.request_generation.wrapping_add(1);
        let request_generation = panel.request_generation;

        let replacement = panel.replacement_input.read(cx).value().to_string();
        panel.cancellation.store(true, Ordering::Relaxed);
        let cancellation = Arc::new(AtomicBool::new(false));
        panel.cancellation = cancellation.clone();
        let last_hit = panel
            .last_hit
            .as_ref()
            .filter(|(last_query, hit, revision)| {
                *last_query == query && hit.tab == origin.tab && *revision == origin_revision
            })
            .map(|(_, hit, _)| hit);
        let skip_current = last_hit == Some(&origin);
        let mut versions = Vec::new();
        let documents = search_documents(&self.documents, query.all_tabs, origin.tab)
            .map(|doc| {
                versions.push((doc.id, doc.revision));
                Source {
                    tab: doc.id,
                    text: doc.editor.read(cx).text().clone(),
                }
            })
            .collect();
        let resume = panel
            .resume
            .as_ref()
            .filter(|resume| {
                resume.query == query
                    && resume.origin.tab == origin.tab
                    && resume.versions == versions
                    && (resume.origin.range == origin.range
                        || (!resume.progress.wrapped
                            && resume.progress.boundary <= origin.range.start
                            && origin.range.start >= resume.origin.range.start))
            })
            .map(|resume| resume.progress.clone())
            .or_else(|| {
                last_hit
                    .filter(|hit| skip_current || hit.range.start < origin.range.start)
                    .map(|hit| NavigationProgress::after_hit(hit.tab, hit.range.clone()))
            });
        panel.message = "Поиск…".into();
        let job = cx.background_spawn({
            let query = query.clone();
            let cancellation = cancellation.clone();
            let resume = resume.clone();
            let origin = origin.clone();
            async move {
                let deadline = Instant::now() + search::MAX_SEARCH_TIME;
                let work = search::WorkBudget::new(&cancellation, deadline);
                search::run(
                    &query,
                    command,
                    documents,
                    &origin,
                    &replacement,
                    &work,
                    NavigationStart {
                        skip_current,
                        progress: resume,
                    },
                )
            }
        });
        let task = cx.spawn_in(window, async move |this, cx| {
            let outcome = job.await;
            if cancellation.load(Ordering::Relaxed) {
                return;
            }
            let _ = this.update_in(cx, |this, window, cx| {
                let Some(panel) = &mut this.search else {
                    return;
                };
                if panel.request_generation != request_generation {
                    return;
                }
                panel.task = None;
                let current_versions =
                    search_documents(&this.documents, query.all_tabs, origin.tab)
                        .map(|doc| (doc.id, doc.revision));
                let inputs_changed = !current_versions.eq(versions.iter().copied());
                let caret_changed = this
                    .documents
                    .iter()
                    .find(|doc| doc.id == origin.tab)
                    .is_none_or(|doc| doc.editor.read(cx).selected_range() != origin.range);
                if this.busy.is_some()
                    || this.applying_update()
                    || inputs_changed
                    || this.active != origin.tab
                    || caret_changed
                {
                    this.search_message("Текст или вкладка изменились. Повторите поиск.", cx);
                } else {
                    this.apply_search_outcome(outcome, query, origin, versions, window, cx);
                }
            });
        });
        self.search.as_mut().expect("search panel is present").task = Some(task);
        cx.notify();
    }

    fn search_message(&mut self, message: impl Into<SharedString>, cx: &mut Context<Self>) {
        if let Some(panel) = &mut self.search {
            panel.message = message.into();
            cx.notify();
        }
    }

    fn apply_search_outcome(
        &mut self,
        outcome: Result<Outcome, String>,
        query: Query,
        origin: Hit,
        versions: Vec<(u64, u64)>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match outcome {
            Err(error) => self.search_message(error, cx),
            Ok(Outcome::Found { hit: Some(hit) }) => {
                self.select(hit.tab, window, cx);
                self.active_document().editor.update(cx, |editor, cx| {
                    editor.set_selected_range(hit.range.clone(), cx);
                });
                self.show_search_highlight(hit.tab, hit.range.clone(), cx);
                let hit_revision = self.active_document().revision;
                if let Some(panel) = &mut self.search {
                    panel.last_hit = Some((query, hit, hit_revision));
                    panel.resume = None;
                }
                self.search_message("Совпадение выбрано", cx);
            }
            Ok(Outcome::Incomplete { progress }) => {
                if let Some(panel) = &mut self.search {
                    panel.resume = Some(SearchResume {
                        query,
                        origin,
                        versions,
                        progress,
                    });
                }
                self.search_message("Поиск приостановлен. Нажмите F3, чтобы продолжить.", cx);
            }
            Ok(Outcome::NoReplacements | Outcome::Found { hit: None } | Outcome::Cancelled) => {
                if let Some(panel) = &mut self.search {
                    panel.resume = None;
                    panel.last_hit = None;
                    panel.visible_matches.clear();
                    panel.match_revisions.clear();
                    panel.clear_match_highlight(cx);
                }
                self.search_message("Совпадений нет", cx);
            }
            Ok(Outcome::Replaced { range, text, count }) => {
                if let Some(panel) = &mut self.search {
                    panel.clear_match_highlight(cx);
                }
                if let Some(doc) = self.documents.iter().find(|doc| doc.id == origin.tab) {
                    doc.editor.update(cx, |editor, cx| {
                        editor.set_selected_range(range, cx);
                        editor.replace(text, window, cx);
                        editor.focus(window, cx);
                    });
                }
                if let Some(panel) = &mut self.search {
                    panel.last_hit = None;
                    panel.resume = None;
                    panel.visible_matches.clear();
                    panel.match_revisions.clear();
                }
                self.search_message(format!("Заменено: {count}"), cx);
            }
            Ok(Outcome::Matches(list)) => {
                let last_hit = list.matches.first().map(|first| {
                    self.select(first.hit.tab, window, cx);
                    self.active_document().editor.update(cx, |editor, cx| {
                        editor.set_selected_range(first.hit.range.clone(), cx);
                    });
                    self.show_search_highlight(first.hit.tab, first.hit.range.clone(), cx);
                    (query, first.hit.clone(), self.active_document().revision)
                });
                let mut message = if list.complete {
                    format!("Совпадений: {}", list.total)
                } else {
                    format!("Подсчёт остановлен · найдено {}", list.total)
                };
                if list.total > search::MAX_VISIBLE_MATCHES {
                    message.push_str(&format!(
                        " · показаны первые {}",
                        search::MAX_VISIBLE_MATCHES
                    ));
                }
                if let Some(panel) = &mut self.search {
                    panel.last_hit = last_hit;
                    panel.resume = None;
                    panel.visible_matches = list.matches;
                    panel.match_revisions = versions;
                }
                self.search_message(message, cx);
            }
        }
    }

    fn toggle_search_option(&mut self, toggle: OptionToggle, cx: &mut Context<Self>) {
        if let Some(panel) = &mut self.search {
            let option = match toggle {
                OptionToggle::Regex => &mut panel.options.regex,
                OptionToggle::Case => &mut panel.options.case_sensitive,
                OptionToggle::WholeWord => &mut panel.options.whole_word,
                OptionToggle::AllTabs => &mut panel.options.all_tabs,
            };
            *option = !*option;
            self.reset_search(cx);
        }
    }
}

fn search_documents(
    documents: &[Document],
    all_tabs: bool,
    tab: u64,
) -> impl Iterator<Item = &Document> {
    documents
        .iter()
        .filter(move |doc| doc.log.is_none() && (all_tabs || doc.id == tab))
}

fn command_button(
    id: &'static str,
    label: &'static str,
    command: Command,
    disabled: bool,
    cx: &mut Context<Workspace>,
) -> Button {
    Button::new(id)
        .small()
        .ghost()
        .label(label)
        .disabled(disabled)
        .on_click(cx.listener(move |this, _, window, cx| this.run_search(command, window, cx)))
}

fn option_button(
    id: &'static str,
    label: &'static str,
    toggle: OptionToggle,
    selected: bool,
    cx: &mut Context<Workspace>,
) -> Button {
    Button::new(id)
        .small()
        .outline()
        .label(label)
        .selected(selected)
        .when(selected, |button| {
            button.border_color(rgb(appearance::ACCENT))
        })
        .on_click(cx.listener(move |this, _, _, cx| this.toggle_search_option(toggle, cx)))
}

fn visible_highlight_tabs(
    layout: &crate::layout::Layout,
    active: u64,
    only_current: bool,
    all_tabs: bool,
) -> Vec<u64> {
    if active == 0 || only_current {
        return Vec::new();
    }
    layout
        .groups()
        .into_iter()
        .filter_map(|(_, group)| group.active)
        .filter(|id| all_tabs || *id == active)
        .collect()
}

fn sync_all_highlights<T, C>(
    visible: &[u64],
    all: &mut Vec<(u64, T)>,
    ctx: &mut C,
    mut clear: impl FnMut(&mut C, T),
    mut build: impl FnMut(&mut C, u64) -> Option<T>,
) {
    let mut retained = Vec::new();
    for (id, highlight) in all.drain(..) {
        if visible.contains(&id) {
            retained.push((id, highlight));
        } else {
            clear(ctx, highlight);
        }
    }
    *all = retained;
    for &id in visible {
        if all.iter().any(|(existing, _)| *existing == id) {
            continue;
        }
        if let Some(highlight) = build(ctx, id) {
            all.push((id, highlight));
        }
    }
}

fn invalidate_document_highlights<T>(
    tab: u64,
    all: &mut Vec<(u64, T)>,
    current: &mut Option<(u64, T)>,
    mut clear: impl FnMut(T),
) {
    let mut retained = Vec::new();
    for (id, highlight) in all.drain(..) {
        if id == tab {
            clear(highlight);
        } else {
            retained.push((id, highlight));
        }
    }
    *all = retained;
    if current.as_ref().is_some_and(|(id, _)| *id == tab)
        && let Some((_, highlight)) = current.take()
    {
        clear(highlight);
    }
}

#[cfg(test)]
#[path = "../../tests/unit/search_panel.rs"]
mod tests;
