use std::{
    cell::RefCell,
    ops::Range,
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    time::{Duration, Instant},
};

use gpui_kit::{
    AnyWindowHandle, App, AppContext, Bounds, Entity, Pixels, Point, Subscription, Task, Window,
    base::input::{RangeDecoration, RangeDecorationCollection, RangeDecorationStyle},
    component::input::{EditorState, InputEvent, RopeExt},
    px, rgb,
};

use crate::appearance;

const PULSE_DURATION: Duration = Duration::from_millis(720);
const FRAME_INTERVAL: Duration = Duration::from_millis(30);

type CapturedViewport = (AnyWindowHandle, Bounds<Pixels>);

#[derive(Clone, Default)]
pub(crate) struct ViewportTracker(Rc<RefCell<Option<CapturedViewport>>>);

#[derive(Clone, PartialEq)]
struct ProjectionStamp {
    owner: CapturedViewport,
    input: Bounds<Pixels>,
    text: Option<Bounds<Pixels>>,
    scroll: Point<Pixels>,
    line_height: Option<Pixels>,
    display_rows: usize,
    wrap_rows: usize,
    buffer_rows: Option<Range<usize>>,
    bytes: usize,
    revision: u64,
}

impl ViewportTracker {
    fn stamp(
        &self,
        editor: &Entity<EditorState>,
        revision: u64,
        cx: &App,
    ) -> Option<ProjectionStamp> {
        let owner = (*self.0.borrow())?;
        let editor = editor.read(cx);
        Some(ProjectionStamp {
            owner,
            input: editor.input_bounds(),
            text: editor.text_bounds(),
            scroll: editor.scroll_offset(),
            line_height: editor.line_height(),
            display_rows: editor.display_map().display_row_count(),
            wrap_rows: editor.display_map().wrap_row_count(),
            buffer_rows: editor.visible_row_range(),
            bytes: editor.text().len(),
            revision,
        })
    }

    pub(crate) fn record(
        &self,
        scroll: &gpui_kit::base::input::EditorScrollbarContext,
        window: &Window,
    ) {
        use gpui_kit::base::ScrollbarHandle;
        *self.0.borrow_mut() = Some((window.window_handle(), scroll.viewport_bounds()));
    }

    fn ranges(&self, editor: &Entity<EditorState>, cx: &mut App) -> Option<Vec<Range<usize>>> {
        let (handle, bounds) = (*self.0.borrow())?;
        cx.update_window(handle, |_, _, cx| {
            visible_byte_ranges(editor.read(cx), bounds)
        })
        .ok()
    }
}

struct ProjectionCache<K> {
    projected: Option<(K, Vec<Range<usize>>)>,
}

impl<K> Default for ProjectionCache<K> {
    fn default() -> Self {
        Self { projected: None }
    }
}

impl<K: PartialEq> ProjectionCache<K> {
    fn get(
        &mut self,
        stamp: K,
        project: impl FnOnce() -> Option<Vec<Range<usize>>>,
    ) -> Option<Vec<Range<usize>>> {
        if let Some((previous, ranges)) = &self.projected
            && *previous == stamp
        {
            return Some(ranges.clone());
        }
        let ranges = project()?;
        self.projected = Some((stamp, ranges.clone()));
        Some(ranges)
    }
}

fn visible_byte_ranges(editor: &EditorState, captured: Bounds<Pixels>) -> Vec<Range<usize>> {
    use gpui_kit::base::input::DisplayPoint;
    let input = editor.input_bounds();
    let Some(top) = editor.viewport_top_in_content() else {
        return Vec::new();
    };
    let Some(height) = editor.line_height().filter(|height| *height > px(0.)) else {
        return Vec::new();
    };
    let left = captured.left().max(input.left());
    let right = captured.right().min(input.right());
    if right <= left {
        return Vec::new();
    }
    let height = f64::from(f32::from(height));
    let first = (top / height).floor().max(0.) as usize;
    let last = ((top + f64::from(f32::from(input.size.height))) / height)
        .ceil()
        .max(0.) as usize;
    let count = editor.display_map().display_row_count();
    let mut ranges: Vec<Range<usize>> = Vec::new();
    for row in first..last.min(count) {
        let start_point = editor
            .display_map()
            .display_pos_to_buffer_pos(DisplayPoint::new(row, 0));
        let start = editor.text().line_start_offset(start_point.line) + start_point.col;
        let end = if row + 1 < count {
            let point = editor
                .display_map()
                .display_pos_to_buffer_pos(DisplayPoint::new(row + 1, 0));
            editor.text().line_start_offset(point.line) + point.col
        } else {
            editor.text().len()
        };
        let first = editor.text().byte_to_char_idx(start);
        let last = editor.text().byte_to_char_idx(end);
        let Some(origin) = editor.range_to_bounds(&(start..start)) else {
            continue;
        };
        let Some(window) =
            projected_window(first..last, f32::from(left)..f32::from(right), |index| {
                let offset = editor.text().char_to_byte_idx(index);
                if offset == end && offset < editor.text().len() {
                    return Some(f32::INFINITY);
                }
                let bounds = editor.range_to_bounds(&(offset..offset))?;
                Some(if bounds.top() > origin.top() {
                    f32::INFINITY
                } else {
                    f32::from(bounds.left())
                })
            })
        else {
            continue;
        };
        let left_hit = editor.text().char_to_byte_idx(window.start);
        let end = editor.text().char_to_byte_idx(window.end);
        if left_hit < end {
            if let Some(previous) = ranges
                .last_mut()
                .filter(|previous| previous.end >= left_hit)
            {
                previous.end = previous.end.max(end);
            } else {
                ranges.push(left_hit..end);
            }
        }
    }
    ranges
}

fn projected_window(
    characters: Range<usize>,
    visible: Range<f32>,
    mut position: impl FnMut(usize) -> Option<f32>,
) -> Option<Range<usize>> {
    fn boundary(
        mut first: usize,
        mut last: usize,
        mut after: impl FnMut(usize) -> Option<bool>,
    ) -> Option<usize> {
        while first < last {
            let middle = first + (last - first) / 2;
            if after(middle)? {
                last = middle;
            } else {
                first = middle + 1;
            }
        }
        Some(first)
    }
    fn gallop(
        origin: usize,
        stop: usize,
        mut step: usize,
        mut after: impl FnMut(usize) -> Option<bool>,
    ) -> Option<usize> {
        let mut first = origin;
        let mut last = stop;
        while let Some(probe) = origin.checked_add(step).filter(|&probe| probe < stop) {
            if after(probe)? {
                last = probe;
                break;
            }
            first = probe + 1;
            step = step.saturating_mul(2);
        }
        boundary(first, last, after)
    }
    if characters.is_empty() || visible.start >= visible.end {
        return None;
    }
    let stop = characters.end.checked_add(1)?;
    let left = gallop(characters.start, stop, 64, |index| {
        Some(position(index)? > visible.start)
    })?;
    if left == stop {
        return None;
    }
    let start = left.saturating_sub(1).max(characters.start);
    let right = gallop(left, stop, 64, |index| {
        Some(position(index)? >= visible.end)
    })?;
    let end = if right == stop {
        characters.end
    } else {
        let cluster_edge = position(right)?;
        gallop(right + 1, stop, 1, |index| {
            Some(position(index)? > cluster_edge)
        })?
        .saturating_sub(1)
    };
    (start < end).then_some(start..end)
}

fn clear_on_change(
    editor: &Entity<EditorState>,
    decorations: &RangeDecorationCollection,
    cx: &mut App,
) -> Subscription {
    let decorations = decorations.clone();
    cx.subscribe(editor, move |_, event: &InputEvent, cx| {
        if matches!(event, InputEvent::Change) {
            decorations.clear(cx);
        }
    })
}

pub(crate) struct SearchHighlight {
    decorations: RangeDecorationCollection,
    _pulse: Option<Task<()>>,
    _changes: Option<Subscription>,
    cancel: Option<Arc<AtomicBool>>,
}

impl SearchHighlight {
    pub(crate) fn new(editor: &Entity<EditorState>, range: Range<usize>, cx: &mut App) -> Self {
        let decorations = editor.update(cx, |editor, cx| {
            editor.create_range_decorations_collection(decorations_at(range, 0.0), cx)
        });
        let pulse = cx.spawn({
            let decorations = decorations.clone();
            async move |cx| {
                let executor = cx.background_executor().clone();
                let started = Instant::now();
                loop {
                    executor.timer(FRAME_INTERVAL).await;
                    let progress =
                        (started.elapsed().as_secs_f32() / PULSE_DURATION.as_secs_f32()).min(1.0);
                    let visible = cx.update(|cx| {
                        let Some(range) = decorations.get_ranges(cx).into_iter().next() else {
                            return false;
                        };
                        decorations.set(decorations_at(range, progress), cx);
                        true
                    });
                    if !visible || progress >= 1.0 {
                        break;
                    }
                }
            }
        });
        let changes = clear_on_change(editor, &decorations, cx);
        Self {
            decorations,
            _pulse: Some(pulse),
            _changes: Some(changes),
            cancel: None,
        }
    }

    pub(crate) fn steady(editor: &Entity<EditorState>, range: Range<usize>, cx: &mut App) -> Self {
        let decorations = editor.update(cx, |editor, cx| {
            editor.create_range_decorations_collection(decorations_at(range, 1.0), cx)
        });
        let changes = clear_on_change(editor, &decorations, cx);
        Self {
            decorations,
            _pulse: None,
            _changes: Some(changes),
            cancel: None,
        }
    }

    pub(crate) fn visible_matches(
        editor: &Entity<EditorState>,
        query: &crate::search::Query,
        viewport: &ViewportTracker,
        cx: &mut App,
    ) -> Option<Self> {
        Self::matching(editor, query, viewport.clone(), false, cx)
    }

    pub(crate) fn visible_log_matches(
        editor: &Entity<EditorState>,
        query: &crate::search::Query,
        viewport: &ViewportTracker,
        cx: &mut App,
    ) -> Option<Self> {
        Self::matching(editor, query, viewport.clone(), true, cx)
    }

    fn matching(
        editor: &Entity<EditorState>,
        query: &crate::search::Query,
        viewport: ViewportTracker,
        linewise: bool,
        cx: &mut App,
    ) -> Option<Self> {
        if editor.read(cx).text().len() > crate::search::MAX_SEARCH_BYTES {
            return None;
        }
        let regex = crate::search::compile(query).ok()?;
        let decorations = editor.update(cx, |editor, cx| {
            editor.create_range_decorations_collection(Vec::new(), cx)
        });
        let revision = Arc::new(AtomicU64::new(0));
        let cancel = Arc::new(AtomicBool::new(false));
        let changes = cx.subscribe(editor, {
            let revision = revision.clone();
            let decorations = decorations.clone();
            move |_, event: &InputEvent, cx| {
                if matches!(event, InputEvent::Change) {
                    revision.fetch_add(1, Ordering::Relaxed);
                    decorations.set(Vec::new(), cx);
                }
            }
        });
        let task = cx.spawn({
            let editor = editor.downgrade();
            let decorations = decorations.clone();
            let cancel = cancel.clone();
            async move |cx| {
                let executor = cx.background_executor().clone();
                let mut previous: Option<(ProjectionStamp, Vec<Range<usize>>)> = None;
                let mut cached_revision = None;
                let mut cache = MatchCache::default();
                let mut projection = ProjectionCache::default();
                loop {
                    executor.timer(Duration::from_millis(80)).await;
                    let version = revision.load(Ordering::Relaxed);
                    let snapshot = cx.update(|cx| {
                        let editor = editor.upgrade()?;
                        if editor.read(cx).text().len() > crate::search::MAX_SEARCH_BYTES {
                            return None;
                        }
                        let stamp = viewport.stamp(&editor, version, cx)?;
                        if previous
                            .as_ref()
                            .is_some_and(|(applied, _)| applied == &stamp)
                        {
                            return None;
                        }
                        let ranges =
                            projection.get(stamp.clone(), || viewport.ranges(&editor, cx))?;
                        let editor = editor.read(cx);
                        Some((
                            (stamp, ranges),
                            (cached_revision != Some(version)).then(|| editor.text().clone()),
                        ))
                    });
                    let Some((snapshot, rope)) = snapshot else {
                        if cx.update(|_| editor.upgrade().is_none()) {
                            break;
                        }
                        continue;
                    };
                    if previous.as_ref() == Some(&snapshot) {
                        continue;
                    }
                    if let Some(rope) = rope {
                        let regex = regex.clone();
                        let cancel = cancel.clone();
                        let revision = revision.clone();
                        let built = executor
                            .spawn(async move {
                                MatchCache::build(&regex, &rope.to_string(), linewise, || {
                                    cancel.load(Ordering::Relaxed)
                                        || revision.load(Ordering::Relaxed) != version
                                })
                            })
                            .await;
                        let Some(built) = built else {
                            continue;
                        };
                        cache = built;
                        cached_revision = Some(version);
                    }
                    let ranges: Vec<_> = cache
                        .visible_windows(&snapshot.1)
                        .into_iter()
                        .map(|range| {
                            RangeDecoration::new(range)
                                .with_style(RangeDecorationStyle::Fill)
                                .with_color(rgb(appearance::ACCENT).opacity(0.20).into())
                        })
                        .collect();
                    let applied = cx.update(|cx| {
                        let Some(editor) = editor.upgrade() else {
                            return false;
                        };
                        if cancel.load(Ordering::Relaxed)
                            || revision.load(Ordering::Relaxed) != version
                            || viewport.stamp(&editor, version, cx).as_ref() != Some(&snapshot.0)
                        {
                            return false;
                        }
                        if std::env::var_os("RUSTTEXT_MEASURE").is_some() {
                            println!(
                                "RUSTTEXT search_highlight_ranges={} visible_bytes={}",
                                ranges.len(),
                                snapshot
                                    .1
                                    .iter()
                                    .map(|range| range.end - range.start)
                                    .sum::<usize>()
                            );
                        }
                        decorations.set(ranges, cx);
                        true
                    });
                    if applied {
                        previous = Some(snapshot);
                    }
                }
            }
        });
        Some(Self {
            decorations,
            _pulse: Some(task),
            _changes: Some(changes),
            cancel: Some(cancel),
        })
    }

    pub(crate) fn clear(self, cx: &mut App) {
        self.decorations.dispose(cx);
    }
}

impl Drop for SearchHighlight {
    fn drop(&mut self) {
        if let Some(cancel) = &self.cancel {
            cancel.store(true, Ordering::Relaxed);
        }
    }
}

#[derive(Default)]
struct MatchCache {
    starts: Vec<u64>,
    ends: Vec<u64>,
}

impl MatchCache {
    fn build(
        regex: &regex::Regex,
        text: &str,
        linewise: bool,
        cancelled: impl Fn() -> bool,
    ) -> Option<Self> {
        if text.len() > crate::search::MAX_SEARCH_BYTES || cancelled() {
            return None;
        }
        let words = text.len() / 64 + 1;
        let mut cache = Self {
            starts: vec![0; words],
            ends: vec![0; words],
        };
        let mut insert = |start: usize, end: usize| {
            if start < end {
                cache.starts[start / 64] |= 1 << (start % 64);
                cache.ends[end / 64] |= 1 << (end % 64);
            }
        };
        if linewise {
            let mut start = 0;
            for line in text.split_inclusive('\n') {
                for matched in regex.find_iter(line.trim_end_matches(['\r', '\n'])) {
                    if cancelled() {
                        return None;
                    }
                    insert(start + matched.start(), start + matched.end());
                }
                start += line.len();
            }
        } else {
            for matched in regex.find_iter(text) {
                if cancelled() {
                    return None;
                }
                insert(matched.start(), matched.end());
            }
        }
        (!cancelled()).then_some(cache)
    }

    fn visible_windows(&self, windows: &[Range<usize>]) -> Vec<Range<usize>> {
        let mut ranges: Vec<_> = windows
            .iter()
            .flat_map(|window| self.visible(window.clone()))
            .collect();
        ranges.sort_unstable_by_key(|range| range.start);
        ranges.dedup();
        ranges
    }

    fn visible(&self, visible: Range<usize>) -> Vec<Range<usize>> {
        if visible.is_empty() {
            return Vec::new();
        }
        let mut ranges = Vec::new();
        if let Some(start) = previous_boundary(&self.starts, visible.start)
            && let Some(end) = next_boundary(&self.ends, start + 1)
            && end > visible.start
        {
            ranges.push(start..end);
        }
        let mut cursor = ranges.last().map_or(visible.start, |range| range.start + 1);
        while let Some(start) = next_boundary(&self.starts, cursor) {
            if start >= visible.end {
                break;
            }
            let Some(end) = next_boundary(&self.ends, start + 1) else {
                break;
            };
            ranges.push(start..end);
            cursor = start + 1;
        }
        ranges
    }
}

fn next_boundary(bits: &[u64], offset: usize) -> Option<usize> {
    let word = offset / 64;
    let value = *bits.get(word)? & (u64::MAX << (offset % 64));
    if value != 0 {
        return Some(word * 64 + value.trailing_zeros() as usize);
    }
    bits.get(word + 1..)?
        .iter()
        .position(|value| *value != 0)
        .map(|index| {
            let word = word + 1 + index;
            word * 64 + bits[word].trailing_zeros() as usize
        })
}

fn previous_boundary(bits: &[u64], offset: usize) -> Option<usize> {
    let word = (offset / 64).min(bits.len().checked_sub(1)?);
    let value = bits[word] & (u64::MAX >> (63 - offset % 64));
    if value != 0 {
        return Some(word * 64 + 63 - value.leading_zeros() as usize);
    }
    bits[..word]
        .iter()
        .rposition(|value| *value != 0)
        .map(|word| word * 64 + 63 - bits[word].leading_zeros() as usize)
}

#[cfg(test)]
fn matching_ranges(
    regex: &regex::Regex,
    text: &str,
    visible: Range<usize>,
    linewise: bool,
) -> Vec<Range<usize>> {
    MatchCache::build(regex, text, linewise, || false)
        .unwrap()
        .visible(visible)
}

fn decorations_at(range: Range<usize>, progress: f32) -> Vec<RangeDecoration> {
    let color = rgb(appearance::ACCENT);
    let frame = RangeDecoration::new(range.clone()).with_color(color.into());
    if progress >= 1.0 {
        return vec![frame];
    }

    let opacity = (0.16 + 0.56 * (std::f32::consts::PI * progress).sin()) * (1.0 - progress);
    let fill = RangeDecoration::new(range)
        .with_style(RangeDecorationStyle::Fill)
        .with_color(color.opacity(opacity).into());
    vec![frame, fill]
}

#[cfg(test)]
#[path = "../tests/unit/search_highlight.rs"]
mod tests;
