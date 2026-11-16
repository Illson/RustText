use std::{
    ops::Range,
    time::{Duration, Instant},
};

use gpui_kit::{
    App, Entity, Task,
    base::input::{RangeDecoration, RangeDecorationCollection, RangeDecorationStyle},
    component::input::EditorState,
    rgb,
};

use crate::appearance;

const PULSE_DURATION: Duration = Duration::from_millis(720);
const FRAME_INTERVAL: Duration = Duration::from_millis(30);

pub(crate) struct SearchHighlight {
    decorations: RangeDecorationCollection,
    _pulse: Option<Task<()>>,
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
        Self {
            decorations,
            _pulse: Some(pulse),
        }
    }

    pub(crate) fn steady(editor: &Entity<EditorState>, range: Range<usize>, cx: &mut App) -> Self {
        let decorations = editor.update(cx, |editor, cx| {
            editor.create_range_decorations_collection(decorations_at(range, 1.0), cx)
        });
        Self {
            decorations,
            _pulse: None,
        }
    }

    pub(crate) fn clear(self, cx: &mut App) {
        self.decorations.dispose(cx);
    }
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
