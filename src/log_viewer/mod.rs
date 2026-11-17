mod controls;
mod file_actions;
mod find;
mod global_scrollbar;
mod index;
mod viewport;

use std::{
    cell::Cell,
    ops::Range,
    path::PathBuf,
    rc::Rc,
    sync::Arc,
    sync::atomic::{AtomicBool, AtomicU64, Ordering},
    time::Duration,
};

use gpui_kit::{
    component::input::{EditorState, InputEvent, InputState},
    *,
};

use crate::{
    log_file::{LogBookmark, Observation},
    log_io::{LineIndex, Page},
    text_format::TextEncoding,
};

use self::{find::LogSearch, viewport::Destination};

actions!(
    log_viewer,
    [LogStart, LogEnd, LogPrevious, LogNext, LogFind]
);

pub(crate) fn bind_keys(cx: &mut App) {
    let text_area = Some("LogViewer > (Input && mode == editor)");
    cx.bind_keys([
        KeyBinding::new("ctrl-home", LogStart, text_area),
        KeyBinding::new("ctrl-end", LogEnd, text_area),
        KeyBinding::new("pageup", LogPrevious, text_area),
        KeyBinding::new("pagedown", LogNext, text_area),
        KeyBinding::new("ctrl-f", LogFind, Some("LogViewer")),
    ]);
}

struct Progress {
    cancel: Arc<AtomicBool>,
    done: Arc<AtomicU64>,
    total: Arc<AtomicU64>,
}

impl Progress {
    fn new(total: u64) -> Self {
        Self {
            cancel: Arc::new(AtomicBool::new(false)),
            done: Arc::new(AtomicU64::new(0)),
            total: Arc::new(AtomicU64::new(total)),
        }
    }

    fn set_cancelled(&self) {
        self.cancel.store(true, Ordering::Relaxed);
    }
}

pub(crate) struct LogViewer {
    path: PathBuf,
    encoding_override: Option<TextEncoding>,
    encoding: TextEncoding,
    bookmark: LogBookmark,
    observation: Option<Observation>,
    page: Option<Page>,
    line_range: Option<(u64, u64)>,
    line_rows: Vec<(usize, u64)>,
    hidden_line_count: Option<u64>,
    index: LineIndex,
    index_paused: bool,
    indexing: Option<Progress>,
    viewer: Entity<EditorState>,
    search_viewport: crate::search_highlight::ViewportTracker,
    scroll_bottom: Rc<Cell<Pixels>>,
    scrollbar_metrics: Rc<Cell<Option<crate::minimap::ScrollbarMetrics>>>,
    scrollbar_drag: Rc<global_scrollbar::DragState>,
    wrap: bool,
    percentage: Entity<InputState>,
    trim_percentage: Entity<InputState>,
    search: Option<LogSearch>,
    search_match: Option<(u64, Option<Range<usize>>)>,
    search_highlight: Option<crate::search_highlight::SearchHighlight>,
    all_search_highlight: Option<crate::search_highlight::SearchHighlight>,
    notice: Option<SharedString>,
    reading: bool,
    pending_destination: Option<Destination>,
    minimap_samples: Vec<String>,
    minimap_stamp: Option<(crate::log_file::FileStamp, u64)>,
    minimap_reading: bool,
    generation: u64,
    preparing_trim: bool,
    preparation: Option<Progress>,
    trimming: Option<Progress>,
    _submit_percentage: Subscription,
}

impl LogViewer {
    pub(crate) fn new(
        path: PathBuf,
        windows_1251: bool,
        bookmark: LogBookmark,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let encoding_override = windows_1251.then_some(TextEncoding::Windows1251);
        let log_viewer = cx.entity().downgrade();
        let scroll_bottom = Rc::new(Cell::new(px(0.)));
        let editor_scroll_bottom = scroll_bottom.clone();
        let scrollbar_metrics = Rc::new(Cell::new(None));
        let editor_scrollbar_metrics = scrollbar_metrics.clone();
        let scrollbar_drag = Rc::new(global_scrollbar::DragState::default());
        let search_viewport = crate::search_highlight::ViewportTracker::default();
        let rendered_viewport = search_viewport.clone();
        let viewer = cx.new(|cx| {
            let mut editor = EditorState::new(window, cx)
                .language("text")
                .line_number(false)
                .searchable(false)
                .scroll_beyond_last_line(Some(0))
                .soft_wrap(true);
            editor.set_scrollbar_renderer(Some(Rc::new(move |scroll, window, cx| {
                rendered_viewport.record(&scroll, window);
                global_scrollbar::render(
                    scroll,
                    log_viewer.clone(),
                    &editor_scroll_bottom,
                    &editor_scrollbar_metrics,
                    window,
                    cx,
                )
                .into_any_element()
            })));
            editor
        });
        let percentage = cx.new(|cx| InputState::new(window, cx).placeholder("0–100"));
        let trim_percentage = cx.new(|cx| InputState::new(window, cx).default_value("50"));
        let submit_percentage =
            cx.subscribe_in(&percentage, window, |this, _, event, window, cx| {
                if matches!(event, InputEvent::PressEnter { .. }) {
                    this.jump_percent(window, cx);
                }
            });
        let mut this = Self {
            path,
            encoding_override,
            encoding: encoding_override.unwrap_or_default(),
            bookmark,
            observation: None,
            page: None,
            line_range: None,
            line_rows: Vec::new(),
            hidden_line_count: None,
            index: LineIndex::default(),
            index_paused: false,
            indexing: None,
            viewer,
            search_viewport,
            scroll_bottom,
            scrollbar_metrics,
            scrollbar_drag,
            wrap: true,
            percentage,
            trim_percentage,
            search: None,
            search_match: None,
            search_highlight: None,
            all_search_highlight: None,
            notice: None,
            reading: false,
            pending_destination: None,
            minimap_samples: Vec::new(),
            minimap_stamp: None,
            minimap_reading: false,
            generation: 0,
            preparing_trim: false,
            preparation: None,
            trimming: None,
            _submit_percentage: submit_percentage,
        };
        this.load_page(Destination::Restore, window, cx);
        this.start_monitor(window, cx);
        this
    }

    pub(crate) fn bookmark(&self) -> LogBookmark {
        self.bookmark.clone()
    }

    pub(crate) fn total_line_count(&self) -> Option<u64> {
        let observation = self.observation.as_ref()?;
        let size = crate::log_io::align_offset(observation.stamp.len, self.encoding);
        (self.index.scanned_to() >= size)
            .then(|| self.index.physical_line_count(self.encoding.bom_len()))
    }

    pub(crate) fn focus_handle(&self, cx: &App) -> FocusHandle {
        self.viewer.read(cx).focus_handle(cx)
    }

    pub(crate) fn wraps_lines(&self) -> bool {
        self.wrap
    }

    pub(crate) fn toggle_wrap(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.wrap = !self.wrap;
        self.viewer
            .update(cx, |editor, cx| editor.set_soft_wrap(self.wrap, window, cx));
        cx.notify();
    }

    pub(super) fn clear_search_highlight(&mut self, cx: &mut Context<Self>) {
        self.search_match = None;
        if let Some(highlight) = self.search_highlight.take() {
            highlight.clear(cx);
        }
    }

    pub(super) fn highlight_log_match(
        &mut self,
        offset: u64,
        match_range: Option<Range<usize>>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !self.available() {
            return;
        }
        self.clear_search_highlight(cx);
        self.search_match = Some((offset, match_range));
        self.notice = None;
        self.load_page(Destination::Match(offset), window, cx);
    }

    pub(super) fn highlight_log_record(
        &mut self,
        offset: u64,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.highlight_log_match(offset, None, window, cx);
    }

    pub(crate) fn writing_file(&self) -> bool {
        self.trimming.is_some()
    }

    fn available(&self) -> bool {
        self.observation.is_some() && !self.preparing_trim && !self.writing_file()
    }

    fn start_monitor(&self, window: &mut Window, cx: &mut Context<Self>) {
        let executor = cx.background_executor().clone();
        cx.spawn_in(window, async move |this, cx| {
            let mut cycle = 0_u8;
            loop {
                executor.timer(Duration::from_millis(125)).await;
                cycle = (cycle + 1) % 4;
                let Ok(poll) = this.update_in(cx, |viewer, _, cx| {
                    if viewer.indexing.is_some()
                        || viewer.preparation.is_some()
                        || viewer.writing_file()
                        || viewer
                            .search
                            .as_ref()
                            .is_some_and(|search| search.progress.is_some())
                    {
                        cx.notify();
                    }
                    if cycle != 0
                        || !viewer.bookmark.auto_refresh
                        || viewer.reading
                        || viewer.preparing_trim
                        || viewer.writing_file()
                    {
                        return None;
                    }
                    Some((
                        viewer.path.clone(),
                        viewer.observation.clone(),
                        viewer.generation,
                    ))
                }) else {
                    break;
                };
                if let Some((path, previous, generation)) = poll {
                    let observed = executor
                        .spawn(async move { crate::log_file::observe(&path, previous.as_ref()) })
                        .await;
                    let _ = this.update_in(cx, |viewer, window, cx| {
                        if viewer.generation == generation && viewer.bookmark.auto_refresh {
                            viewer.apply_observation(observed, window, cx);
                        }
                    });
                }
            }
        })
        .detach();
    }
}

impl Drop for LogViewer {
    fn drop(&mut self) {
        self.stop_index();
        if let Some(progress) = &self.preparation {
            progress.set_cancelled();
        }
        if let Some(progress) = &self.trimming {
            progress.set_cancelled();
        }
        if let Some(search) = &self.search
            && let Some(progress) = &search.progress
        {
            progress.set_cancelled();
        }
    }
}

static MEASUREMENT_START: std::sync::OnceLock<std::time::Instant> = std::sync::OnceLock::new();

pub(crate) fn enable_measurements() {
    if std::env::var_os("RUSTTEXT_MEASURE").is_some() {
        MEASUREMENT_START.set(std::time::Instant::now()).ok();
    }
}

fn measurement_start() -> Option<std::time::Instant> {
    MEASUREMENT_START.get().copied()
}
