use std::{
    cell::Cell,
    fs::File,
    io::{self, Read, Seek, SeekFrom},
    path::Path,
    rc::Rc,
};

use gpui_kit::{
    base::{Scrollbar, ScrollbarHandle, ScrollbarMode, input::EditorScrollbarContext},
    component::input::{EditorState, RopeExt},
    *,
};

use crate::{appearance, text_format::TextEncoding};

pub(crate) const WIDTH: f32 = 88.0;
const SAMPLES: usize = 160;
const SAMPLE_BYTES: usize = 256;

#[derive(Clone, Copy)]
pub(crate) struct ScrollbarMetrics {
    pub(crate) viewport: Bounds<Pixels>,
    pub(crate) content: Size<Pixels>,
    pub(crate) offset: Point<Pixels>,
    pub(crate) content_height_f64: f64,
    pub(crate) scroll_y_f64: f64,
}

impl ScrollbarMetrics {
    pub(crate) fn from_context(scroll: &EditorScrollbarContext) -> Self {
        Self {
            viewport: scroll.viewport_bounds(),
            content: scroll.content_size(),
            offset: scroll.offset(),
            content_height_f64: scroll.content_height_f64(),
            scroll_y_f64: scroll.scroll_y_f64(),
        }
    }
}

#[derive(Clone)]
pub(crate) struct MinimapState {
    editor: WeakEntity<EditorState>,
    metrics: Rc<Cell<Option<ScrollbarMetrics>>>,
    dragging: Rc<Cell<Option<f32>>>,
}

pub(crate) fn install(
    editor: &Entity<EditorState>,
    cx: &mut App,
) -> (crate::search_highlight::ViewportTracker, MinimapState) {
    let viewport = crate::search_highlight::ViewportTracker::default();
    let rendered_viewport = viewport.clone();
    let metrics = Rc::new(Cell::new(None));
    let recorded_metrics = metrics.clone();
    let weak = editor.downgrade();
    let dragging = Rc::new(Cell::new(None));
    editor.update(cx, |editor, _| {
        editor.set_scrollbar_renderer(Some(Rc::new(move |scroll, window, _| {
            rendered_viewport.record(&scroll, window);
            let current = ScrollbarMetrics::from_context(&scroll);
            recorded_metrics.set(Some(current));
            div()
                .size_full()
                .relative()
                .children(
                    (current.content.width > current.viewport.size.width).then(|| {
                        Scrollbar::horizontal(&scroll)
                            .mode(ScrollbarMode::Always)
                            .styles(|_| appearance::scrollbar_styles())
                    }),
                )
                .into_any_element()
        })));
    });
    (
        viewport,
        MinimapState {
            editor: weak,
            metrics,
            dragging,
        },
    )
}

pub(crate) fn strip(state: &MinimapState) -> impl IntoElement {
    MinimapStrip {
        state: state.clone(),
    }
}

struct MinimapStrip {
    state: MinimapState,
}

impl IntoElement for MinimapStrip {
    type Element = Self;

    fn into_element(self) -> Self::Element {
        self
    }
}

impl Element for MinimapStrip {
    type RequestLayoutState = ();
    type PrepaintState = Option<AnyElement>;

    fn id(&self) -> Option<ElementId> {
        None
    }

    fn source_location(&self) -> Option<&'static std::panic::Location<'static>> {
        None
    }

    fn request_layout(
        &mut self,
        _: Option<&GlobalElementId>,
        _: Option<&gpui_kit::InspectorElementId>,
        window: &mut Window,
        cx: &mut App,
    ) -> (LayoutId, Self::RequestLayoutState) {
        let mut style = Style::default();
        style.size.width = px(WIDTH).into();
        style.size.height = relative(1.).into();
        style.flex_shrink = 0.;
        (window.request_layout(style, [], cx), ())
    }

    fn prepaint(
        &mut self,
        _: Option<&GlobalElementId>,
        _: Option<&gpui_kit::InspectorElementId>,
        bounds: Bounds<Pixels>,
        _: &mut Self::RequestLayoutState,
        window: &mut Window,
        cx: &mut App,
    ) -> Self::PrepaintState {
        let metrics = self.state.metrics.get();
        let samples = self
            .state
            .editor
            .upgrade()
            .map(|editor| {
                let editor = editor.read(cx);
                let text = editor.text();
                let lines = text.lines_len();
                let count = lines.min(SAMPLES);
                (0..count)
                    .map(|index| {
                        let row =
                            sample_offset(lines.saturating_sub(1) as u64, index, count) as usize;
                        text.slice_line(row).chars().take(64).collect::<String>()
                    })
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();
        let mut element = render_strip(
            bounds,
            metrics,
            &samples,
            self.state.editor.clone(),
            self.state.dragging.clone(),
        )
        .into_any_element();
        element.prepaint_as_root(bounds.origin, bounds.size.into(), window, cx);
        Some(element)
    }

    fn paint(
        &mut self,
        _: Option<&GlobalElementId>,
        _: Option<&gpui_kit::InspectorElementId>,
        _: Bounds<Pixels>,
        _: &mut Self::RequestLayoutState,
        prepaint: &mut Self::PrepaintState,
        window: &mut Window,
        cx: &mut App,
    ) {
        if let Some(element) = prepaint.as_mut() {
            element.paint(window, cx);
        }
    }
}

pub(crate) fn sample_file(
    path: &Path,
    encoding: TextEncoding,
    hidden: u64,
    size: u64,
) -> io::Result<Vec<String>> {
    let mut file = File::open(path)?;
    let span = size.saturating_sub(hidden);
    let count = (span as usize).min(SAMPLES);
    let mut samples = Vec::with_capacity(count);
    for index in 0..count {
        let offset = crate::log_io::align_offset(
            hidden.saturating_add(sample_offset(span.saturating_sub(1), index, count)),
            encoding,
        );
        file.seek(SeekFrom::Start(offset))?;
        let mut bytes = [0; SAMPLE_BYTES];
        let read = file.read(&mut bytes)?;
        let text = crate::log_io::decode(&bytes[..read], encoding, offset == 0);
        samples.push(
            text.chars()
                .take_while(|c| *c != '\n' && *c != '\r')
                .take(64)
                .collect(),
        );
    }
    Ok(samples)
}

fn sample_offset(span: u64, index: usize, count: usize) -> u64 {
    ((span as u128 * index as u128) / count.saturating_sub(1).max(1) as u128) as u64
}

pub(crate) fn preview(samples: &[String], height: f32) -> impl IntoElement {
    div()
        .absolute()
        .top_0()
        .bottom_0()
        .left_0()
        .right(px(16.))
        .overflow_hidden()
        .children(samples.iter().enumerate().map(|(index, sample)| {
            let top = index as f32 / samples.len().max(1) as f32 * height;
            div()
                .absolute()
                .left(px(3.))
                .top(px(top))
                .text_size(px(2.))
                .line_height(px(3.))
                .text_color(rgb(appearance::MUTED))
                .whitespace_nowrap()
                .child(sample.clone())
        }))
}

fn thumb_grab_offset(pointer_y: f32, thumb_top: f32, thumb_height: f32) -> f32 {
    if pointer_y >= thumb_top && pointer_y <= thumb_top + thumb_height {
        pointer_y - thumb_top
    } else {
        thumb_height * 0.5
    }
}

fn drag_fraction(pointer_y: f32, grab_offset: f32, height: f32, thumb_height: f32) -> f32 {
    let travel = (height - thumb_height).max(1.);
    ((pointer_y - grab_offset) / travel).clamp(0., 1.)
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct StripThumbLayout {
    range_f64: f64,
    thumb_height: f32,
    thumb_top: f32,
}

fn strip_thumb_layout(
    content_height_f64: f64,
    scroll_y_f64: f64,
    viewport_height: f32,
) -> StripThumbLayout {
    let height = viewport_height.max(1.);
    let range_f64 = (content_height_f64 - f64::from(viewport_height)).max(0.0);
    let thumb_height = ((f64::from(height) * f64::from(height))
        / content_height_f64.max(f64::from(height))) as f32;
    let thumb_height = thumb_height.clamp(24_f32.min(height), height);
    let travel = (height - thumb_height).max(1.);
    let fraction = if range_f64 > 0.0 {
        (-scroll_y_f64 / range_f64).clamp(0.0, 1.0) as f32
    } else {
        0.
    };
    StripThumbLayout {
        range_f64,
        thumb_height,
        thumb_top: fraction * travel,
    }
}

fn seek_scroll_y(fraction: f32, range_f64: f64) -> f64 {
    -range_f64 * f64::from(fraction.clamp(0., 1.))
}

fn render_strip(
    bounds: Bounds<Pixels>,
    metrics: Option<ScrollbarMetrics>,
    samples: &[String],
    editor: WeakEntity<EditorState>,
    dragging: Rc<Cell<Option<f32>>>,
) -> impl IntoElement {
    let height = metrics
        .map(|metrics| f32::from(metrics.viewport.size.height))
        .unwrap_or_else(|| f32::from(bounds.size.height))
        .max(1.);
    let StripThumbLayout {
        range_f64,
        thumb_height,
        thumb_top,
    } = if let Some(metrics) = metrics {
        strip_thumb_layout(
            metrics.content_height_f64,
            metrics.scroll_y_f64,
            f32::from(metrics.viewport.size.height),
        )
    } else {
        StripThumbLayout {
            range_f64: 0.0,
            thumb_height: height,
            thumb_top: 0.,
        }
    };
    let handle = canvas(
        |bounds, window, _| window.insert_hitbox(bounds, HitboxBehavior::Normal),
        move |bounds, hitbox, window, _| {
            window.set_cursor_style(CursorStyle::Arrow, &hitbox);
            if dragging.get().is_some() {
                window.set_window_cursor_style(CursorStyle::Arrow);
            }
            let scroll_target = editor.clone();
            let scroll_hitbox = hitbox.clone();
            window.on_mouse_event(move |event: &ScrollEvent, phase, window, cx| {
                if !phase.capture() || !scroll_hitbox.is_hovered(window) || range_f64 <= 0.0 {
                    return;
                }
                let delta = event.delta.pixel_delta(px(20.));
                if delta.y == px(0.) || delta.x.abs() > delta.y.abs() {
                    return;
                }
                let _ = scroll_target.update(cx, |editor, cx| {
                    editor.scroll_by_y_f64(f64::from(f32::from(delta.y)), cx);
                });
                cx.stop_propagation();
            });
            let state = dragging.clone();
            let target = editor.clone();
            let down_hitbox = hitbox.clone();
            window.on_mouse_event(move |event: &MouseDownEvent, phase, window, cx| {
                if phase.capture()
                    && event.button == MouseButton::Left
                    && down_hitbox.is_hovered(window)
                {
                    let pointer_y = f32::from(event.position.y - bounds.top());
                    let grab_offset = thumb_grab_offset(pointer_y, thumb_top, thumb_height);
                    state.set(Some(grab_offset));
                    seek(
                        &target,
                        drag_fraction(pointer_y, grab_offset, height, thumb_height),
                        range_f64,
                        cx,
                    );
                    cx.stop_propagation();
                }
            });
            let state = dragging.clone();
            let target = editor.clone();
            window.on_mouse_event(move |event: &MouseMoveEvent, phase, window, cx| {
                if phase.capture()
                    && let Some(grab_offset) = state.get()
                {
                    if !event.dragging() {
                        state.set(None);
                        window.refresh();
                        return;
                    }
                    let pointer_y = f32::from(event.position.y - bounds.top());
                    seek(
                        &target,
                        drag_fraction(pointer_y, grab_offset, height, thumb_height),
                        range_f64,
                        cx,
                    );
                }
            });
            let state = dragging.clone();
            window.on_mouse_event(move |event: &MouseUpEvent, phase, window, _| {
                if phase.capture() && event.button == MouseButton::Left && state.take().is_some() {
                    window.refresh();
                }
            });
        },
    )
    .absolute()
    .size_full();
    div()
        .size_full()
        .relative()
        .cursor_default()
        .bg(rgb(appearance::SURFACE))
        .border_l_1()
        .border_color(rgb(appearance::BORDER))
        .child(preview(samples, height))
        .child(
            div()
                .absolute()
                .top(px(thumb_top))
                .right_0()
                .w(px(WIDTH))
                .h(px(thumb_height))
                .bg(rgb(appearance::ACCENT).opacity(0.16))
                .border_1()
                .border_color(rgb(appearance::ACCENT)),
        )
        .child(handle)
}

fn seek(editor: &WeakEntity<EditorState>, fraction: f32, range_f64: f64, cx: &mut App) {
    let _ = editor.update(cx, |editor, cx| {
        editor.set_scroll_y_f64(seek_scroll_y(fraction, range_f64), cx);
    });
}

#[cfg(test)]
#[path = "../tests/unit/minimap.rs"]
mod tests;
