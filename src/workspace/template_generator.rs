use std::{
    path::Path,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
    },
    time::Duration,
};

use gpui_kit::{
    component::{
        Disableable, Sizable,
        button::{Button, ButtonVariants},
        input::{Input, InputEvent, InputState},
        label::Label,
    },
    *,
};

use super::Workspace;
use crate::{appearance, files::FileTarget, template};

pub(super) struct TemplateGenerator {
    template: Entity<InputState>,
    rows: Entity<InputState>,
    error: Option<SharedString>,
    generating: bool,
    progress: Arc<GeneratorProgress>,
    status: Option<SharedString>,
    _submit: [Subscription; 2],
}

impl TemplateGenerator {
    fn new(window: &mut Window, cx: &mut Context<Workspace>) -> Self {
        let template =
            cx.new(|cx| InputState::new(window, cx).default_value("strokna {int_from_5}"));
        let rows = cx.new(|cx| InputState::new(window, cx).default_value("10"));
        let submit_template = cx.subscribe_in(&template, window, |this, _, event, window, cx| {
            if matches!(event, InputEvent::PressEnter { .. }) {
                this.generate_template(window, cx);
            }
        });
        let submit_rows = cx.subscribe_in(&rows, window, |this, _, event, window, cx| {
            if matches!(event, InputEvent::PressEnter { .. }) {
                this.generate_template(window, cx);
            }
        });
        Self {
            template,
            rows,
            error: None,
            generating: false,
            progress: Arc::default(),
            status: None,
            _submit: [submit_template, submit_rows],
        }
    }

    fn render(&self, cx: &mut Context<Workspace>) -> impl IntoElement {
        let generating = self.generating;
        div()
            .w_full()
            .max_w(px(560.))
            .mx_4()
            .p_4()
            .flex()
            .flex_col()
            .gap_3()
            .rounded_lg()
            .border_1()
            .border_color(rgb(appearance::ACCENT))
            .bg(rgb(appearance::SURFACE))
            .shadow_lg()
            .child(
                div()
                    .flex()
                    .items_center()
                    .child(
                        Label::new("Генератор строк")
                            .text_lg()
                            .text_color(rgb(appearance::FOREGROUND)),
                    )
                    .child(div().flex_1())
                    .child(
                        Button::new("template-generator-close")
                            .small()
                            .ghost()
                            .label("×")
                            .tooltip(if generating { "Отменить генерацию" } else { "Закрыть генератор" })
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.close_template_generator(window, cx)
                            })),
                    ),
            )
            .child(Label::new("Шаблон одной строки").text_sm())
            .child(Input::new(&self.template).disabled(generating))
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .text_sm()
                    .text_color(rgb(appearance::MUTED))
                    .child(Label::new(
                        "{int_from_5} увеличивает число на каждой строке. {guid} создаёт UUID.",
                    ))
                    .child(Label::new(
                        "{rand_string} создаёт 12 случайных букв и цифр, {rand_string_16} – строку из 16 символов.",
                    ))
                    .child(Label::new(
                        "В одном шаблоне можно сочетать несколько правил.",
                    ))
                    .child(Label::new(
                        "strokna {int_from_5} → strokna 5, strokna 6, …",
                    )),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .child(Label::new("Количество строк"))
                    .child(
                        div()
                            .w(px(110.))
                            .child(Input::new(&self.rows).disabled(generating)),
                    )
                    .child(div().flex_1())
                    .child(
                        Button::new("template-generator-file")
                            .small()
                            .label("В файл…")
                            .disabled(generating)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.generate_template_output(true, window, cx)
                            })),
                    )
                    .child(
                        Button::new("template-generator-run")
                            .small()
                            .primary()
                            .label(if generating { "Создание…" } else { "Создать" })
                            .disabled(generating)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.generate_template(window, cx)
                            })),
                    ),
            )
            .children(generating.then(|| {
                Label::new(format!(
                    "Создано строк: {}, байт: {}. Нажмите × или Esc для отмены.",
                    self.progress.rows.load(Ordering::Relaxed),
                    self.progress.bytes.load(Ordering::Relaxed),
                )).text_sm()
            }))
            .children(self.status.as_ref().map(|status| Label::new(status.clone()).text_sm()))
            .children(self.error.as_ref().map(|error| {
                Label::new(error.clone())
                    .text_sm()
                    .text_color(rgb(appearance::ERROR))
            }))
    }
}

impl Workspace {
    pub(super) fn open_template_generator(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.busy.is_some() || self.applying_update() {
            return;
        }
        if self.template_generator.is_none() {
            self.template_generator = Some(TemplateGenerator::new(window, cx));
        }
        if let Some(generator) = &self.template_generator {
            generator.template.update(cx, |input, cx| {
                input.focus(window, cx);
                input.select_all(window, cx);
            });
        }
        cx.notify();
    }

    fn close_template_generator(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(generator) = &self.template_generator
            && generator.generating
        {
            generator.progress.cancelled.store(true, Ordering::Relaxed);
            return;
        }
        self.template_generator = None;
        self.select(self.active, window, cx);
    }

    pub(super) fn template_generator_panel(&self, cx: &mut Context<Self>) -> Option<AnyElement> {
        self.template_generator.as_ref().map(|generator| {
            div()
                .id("template-generator")
                .absolute()
                .inset_0()
                .flex()
                .items_center()
                .justify_center()
                .occlude()
                .tab_group()
                .bg(rgb(appearance::BACKGROUND).opacity(0.82))
                .on_key_down(cx.listener(|this, event: &KeyDownEvent, window, cx| {
                    if event.keystroke.key == "escape" {
                        this.close_template_generator(window, cx);
                        cx.stop_propagation();
                    }
                }))
                .child(generator.render(cx))
                .into_any_element()
        })
    }

    fn generate_template(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.generate_template_output(false, window, cx);
    }

    fn generate_template_output(
        &mut self,
        file_output: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(generator) = &mut self.template_generator else {
            return;
        };
        if generator.generating {
            return;
        }
        let template_text = generator.template.read(cx).value().to_string();
        let rows_text = generator.rows.read(cx).value().to_string();
        let rows = match rows_text.trim().parse::<usize>() {
            Ok(rows) if rows > 0 => rows,
            _ => {
                generator.error = Some("Укажите положительное целое число строк.".into());
                cx.notify();
                return;
            }
        };
        let upper_bound = match template::output_upper_bound(&template_text, rows) {
            Ok(bytes) => bytes,
            Err(error) => {
                generator.error = Some(error.to_string().into());
                cx.notify();
                return;
            }
        };
        generator.generating = true;
        generator.error = None;
        generator.status = None;
        generator.progress = Arc::default();
        let progress = generator.progress.clone();
        cx.notify();
        if file_output || upper_bound > template::NEW_TAB_BYTES {
            let picker = cx.prompt_for_new_path(Path::new(""), Some("Генерация.txt"));
            let executor = cx.background_executor().clone();
            cx.spawn_in(window, async move |this, cx| {
                let target = match picker.await {
                    Ok(Ok(Some(path))) => executor
                        .spawn(async move { FileTarget::from_picker(path) })
                        .await
                        .map(Some)
                        .map_err(|error| error.to_string()),
                    Ok(Ok(None)) => Ok(None),
                    Ok(Err(error)) => Err(error.to_string()),
                    Err(_) => Err("Диалог сохранения прерван".to_string()),
                };
                let _ = this.update_in(cx, |this, window, cx| {
                    if progress.cancelled.load(Ordering::Relaxed) {
                        this.finish_template_generation(
                            Err(template::TemplateError::Cancelled),
                            window,
                            cx,
                        );
                        return;
                    }
                    match target {
                        Ok(Some(target)) => {
                            if this.documents.iter().any(|document| {
                                document
                                    .file
                                    .as_ref()
                                    .is_some_and(|file| file.path == target.path)
                            }) {
                                if let Some(generator) = &mut this.template_generator {
                                    generator.generating = false;
                                    generator.error = Some(
                                        "Файл уже открыт во вкладке. Выберите другой путь.".into(),
                                    );
                                }
                                cx.notify();
                                return;
                            }
                            this.start_template_generation(
                                template_text,
                                rows,
                                Some(target),
                                window,
                                cx,
                            );
                        }
                        Ok(None) => {
                            if let Some(generator) = &mut this.template_generator {
                                generator.generating = false;
                            }
                            cx.notify();
                        }
                        Err(error) => {
                            if let Some(generator) = &mut this.template_generator {
                                generator.generating = false;
                                generator.error = Some(error.into());
                            }
                            cx.notify();
                        }
                    }
                });
            })
            .detach();
        } else {
            self.start_template_generation(template_text, rows, None, window, cx);
        }
    }

    fn start_template_generation(
        &mut self,
        template_text: String,
        rows: usize,
        target: Option<FileTarget>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(generator) = &self.template_generator else {
            return;
        };
        let progress = generator.progress.clone();
        let monitor = progress.clone();
        let operation = cx.background_spawn(async move {
            let report = |state: template::GenerationProgress| {
                progress.rows.store(state.rows, Ordering::Relaxed);
                progress.bytes.store(state.bytes, Ordering::Relaxed);
                !progress.cancelled.load(Ordering::Relaxed)
            };
            let result = if let Some(target) = target {
                let path = target.path.clone();
                template::generate_file(&template_text, rows, target, report)
                    .map(|_| (None, Some(path)))
            } else {
                template::generate_text(&template_text, rows, report).map(|text| (Some(text), None))
            };
            progress.finished.store(true, Ordering::Relaxed);
            result
        });
        let executor = cx.background_executor().clone();
        cx.spawn_in(window, async move |this, cx| {
            while !monitor.finished.load(Ordering::Relaxed) {
                executor.timer(Duration::from_millis(100)).await;
                if this.update_in(cx, |_, _, cx| cx.notify()).is_err() {
                    monitor.cancelled.store(true, Ordering::Relaxed);
                    break;
                }
            }
        })
        .detach();
        cx.spawn_in(window, async move |this, cx| {
            let result = operation.await;
            let _ = this.update_in(cx, |this, window, cx| {
                this.finish_template_generation(result, window, cx);
            });
        })
        .detach();
    }

    fn finish_template_generation(
        &mut self,
        result: Result<(Option<String>, Option<std::path::PathBuf>), template::TemplateError>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let Some(generator) = &mut self.template_generator else {
            return;
        };
        generator.generating = false;
        let result = match result {
            Ok((Some(text), path)) => {
                template::completed_text(text, generator.progress.cancelled.load(Ordering::Relaxed))
                    .map(|text| (Some(text), path))
            }
            result => result,
        };
        match result {
            Ok((Some(text), _)) => {
                self.template_generator = None;
                self.open_generated_document(text, window, cx);
            }
            Ok((_, Some(path))) => {
                generator.status = Some(format!("Результат сохранён: {}", path.display()).into());
            }
            Ok(_) => {}
            Err(template::TemplateError::Cancelled) => {
                generator.status = Some("Генерация отменена. Файл назначения не изменён.".into());
            }
            Err(error) => generator.error = Some(error.to_string().into()),
        }
        cx.notify();
    }

    pub(super) fn open_generated_document(
        &mut self,
        text: String,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let dirty = !text.is_empty();
        let id = self.insert_document(
            None,
            text,
            crate::text_format::TextFormat::default(),
            window,
            cx,
        );
        if let Some(document) = self.documents.last_mut() {
            document.saved_text = Some(gpui_kit::component::input::Rope::new());
            document.dirty = dirty;
        }
        self.select(id, window, cx);
    }
}

#[derive(Default)]
struct GeneratorProgress {
    cancelled: AtomicBool,
    finished: AtomicBool,
    rows: AtomicUsize,
    bytes: AtomicU64,
}

impl Drop for TemplateGenerator {
    fn drop(&mut self) {
        self.progress.cancelled.store(true, Ordering::Relaxed);
    }
}
