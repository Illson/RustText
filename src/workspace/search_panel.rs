use gpui_kit::component::{
    Disableable, Selectable, Sizable,
    button::{Button, ButtonVariants},
    input::{Input, InputEvent, InputState},
    label::Label,
};
use gpui_kit::*;

use super::{Document, Workspace};
use crate::{
    appearance,
    search::{self, Command, Hit, Outcome, Query, SearchMatch, Source},
};

pub(super) struct SearchPanel {
    query_input: Entity<InputState>,
    replacement_input: Entity<InputState>,
    options: Query,
    last_hit: Option<(Query, Hit)>,
    replacing: bool,
    visible_matches: Vec<SearchMatch>,
    match_revisions: Vec<(u64, u64)>,
    request_generation: u64,
    message: SharedString,
    task: Option<Task<()>>,
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
            replacing: false,
            visible_matches: Vec::new(),
            match_revisions: Vec::new(),
            request_generation: 0,
            message: "Введите текст поиска".into(),
            task: None,
            _subscriptions: [query, replacement],
        }
    }

    fn query(&self, cx: &App) -> Query {
        Query {
            text: self.query_input.read(cx).value().to_string(),
            ..self.options.clone()
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
                        let stale = revisions.iter().any(|(id, revision)| {
                            this.documents
                                .iter()
                                .find(|doc| doc.id == *id)
                                .is_none_or(|document| document.revision != *revision)
                        });
                        if stale {
                            this.reset_search(cx);
                            this.search_message("Текст изменился. Обновите список совпадений.", cx);
                            return;
                        }

                        this.select(result.hit.tab, window, cx);
                        this.active_document().editor.update(cx, |editor, cx| {
                            editor.set_selected_range(result.hit.range.clone(), cx);
                        });
                        if let Some(panel) = &mut this.search {
                            panel.last_hit = Some((query.clone(), result.hit.clone()));
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
    pub(super) fn open_search(
        &mut self,
        replace: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
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
        let active = self.active_document();
        let can_replace = active.log.is_none() && !active.placeholder;
        let busy = self.busy.is_some() || self.applying_update();
        self.search
            .as_ref()
            .map(|panel| panel.render(busy, can_replace, &self.documents, window, cx))
    }

    fn close_search(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.search.take().is_some() {
            self.select(self.active, window, cx);
        }
    }

    fn reset_search(&mut self, cx: &mut Context<Self>) {
        if let Some(panel) = &mut self.search {
            panel.task = None;
            panel.last_hit = None;
            panel.request_generation = panel.request_generation.wrapping_add(1);
            panel.visible_matches.clear();
            panel.match_revisions.clear();
            panel.message = if panel.options.all_tabs {
                "Поиск по всем вкладкам. Замена доступна в текущей вкладке.".into()
            } else {
                "Enter — показать совпадения".into()
            };
            cx.notify();
        }
    }

    pub(super) fn run_search(
        &mut self,
        command: Command,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
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
        let panel = self.search.as_mut().expect("search panel is present");
        let query = panel.query(cx);
        if panel.task.is_some() || (command.replaces() && query.all_tabs) {
            return;
        }
        panel.request_generation = panel.request_generation.wrapping_add(1);
        let request_generation = panel.request_generation;

        let replacement = panel.replacement_input.read(cx).value().to_string();
        let skip_current = panel
            .last_hit
            .as_ref()
            .is_some_and(|(last_query, hit)| *last_query == query && *hit == origin);
        let mut versions = Vec::new();
        let documents = self
            .documents
            .iter()
            .filter(|doc| doc.log.is_none() && (query.all_tabs || doc.id == origin.tab))
            .map(|doc| {
                versions.push((doc.id, doc.revision));
                Source {
                    tab: doc.id,
                    text: doc.editor.read(cx).text().clone(),
                }
            })
            .collect();
        panel.message = "Поиск…".into();
        let origin_id = origin.tab;
        let result_versions = versions.clone();
        let job = cx.background_spawn({
            let query = query.clone();
            async move {
                search::run(
                    &query,
                    command,
                    documents,
                    &origin,
                    skip_current,
                    &replacement,
                )
            }
        });
        let task = cx.spawn_in(window, async move |this, cx| {
            let outcome = job.await;
            let _ = this.update_in(cx, |this, window, cx| {
                let Some(panel) = &mut this.search else {
                    return;
                };
                if panel.request_generation != request_generation {
                    return;
                }
                panel.task = None;
                let inputs_changed = versions.iter().any(|(id, revision)| {
                    !this
                        .documents
                        .iter()
                        .any(|doc| doc.id == *id && doc.revision == *revision)
                });
                if this.busy.is_some() || inputs_changed || this.active != origin_id {
                    this.search_message("Текст или вкладка изменились. Повторите поиск.", cx);
                } else {
                    this.apply_search_outcome(
                        outcome,
                        query,
                        origin_id,
                        result_versions,
                        window,
                        cx,
                    );
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
        origin_id: u64,
        versions: Vec<(u64, u64)>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        match outcome {
            Err(error) => self.search_message(error, cx),
            Ok(Outcome::Found {
                hit: Some(hit),
                total,
            }) => {
                self.select(hit.tab, window, cx);
                self.active_document().editor.update(cx, |editor, cx| {
                    editor.set_selected_range(hit.range.clone(), cx);
                });
                if let Some(panel) = &mut self.search {
                    panel.last_hit = Some((query, hit));
                }
                self.search_message(format!("Совпадений: {total}"), cx);
            }
            Ok(Outcome::NoReplacements | Outcome::Found { hit: None, .. }) => {
                if let Some(panel) = &mut self.search {
                    panel.visible_matches.clear();
                    panel.match_revisions.clear();
                }
                self.search_message("Совпадений нет", cx);
            }
            Ok(Outcome::Replaced { range, text, count }) => {
                if let Some(panel) = &mut self.search {
                    panel.visible_matches.clear();
                    panel.match_revisions.clear();
                }
                if let Some(doc) = self.documents.iter().find(|doc| doc.id == origin_id) {
                    doc.editor.update(cx, |editor, cx| {
                        editor.set_selected_range(range, cx);
                        editor.replace(text, window, cx);
                        editor.focus(window, cx);
                    });
                }
                if let Some(panel) = &mut self.search {
                    panel.last_hit = None;
                }
                self.search_message(format!("Заменено: {count}"), cx);
            }
            Ok(Outcome::Matches(list)) => {
                let first = list.matches.first().cloned();
                if let Some(first) = first {
                    self.select(first.hit.tab, window, cx);
                    self.active_document().editor.update(cx, |editor, cx| {
                        editor.set_selected_range(first.hit.range.clone(), cx);
                    });
                    if let Some(panel) = &mut self.search {
                        panel.last_hit = Some((query, first.hit.clone()));
                    }
                }
                if let Some(panel) = &mut self.search {
                    panel.visible_matches = list.matches;
                    panel.match_revisions = versions;
                }
                let message = if list.total > search::MAX_VISIBLE_MATCHES {
                    format!(
                        "Совпадений: {} · показаны первые {}",
                        list.total,
                        search::MAX_VISIBLE_MATCHES
                    )
                } else {
                    format!("Совпадений: {}", list.total)
                };
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
        .on_click(cx.listener(move |this, _, _, cx| this.toggle_search_option(toggle, cx)))
}
