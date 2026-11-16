use std::{io, ops::Range};

use gpui_kit::*;

use super::LogViewer;
use crate::{
    log_file::{self, Change, LogBookmark, Observation},
    log_io::{LineIndex, LogReader, Page, PageOptions, align_offset},
    text_format::TextEncoding,
};

const MAX_BACKWARD_SCAN: u64 = 2 * 1024 * 1024;

#[derive(Clone, Copy)]
pub(super) enum Destination {
    Restore,
    Start,
    End,
    At(u64),
    Before(u64),
    Percentage(f64),
    Match(u64),
    Append(bool),
}

struct PreviousPage {
    observation: Option<Observation>,
    page: Option<Page>,
}

#[derive(Default)]
struct PageLineNumbers {
    range: Option<(u64, u64)>,
    rows: Vec<(usize, u64)>,
}

struct RenderedPage {
    observation: Observation,
    page: Page,
    start: u64,
    hidden: u64,
    encoding: TextEncoding,
    line_range: Option<(u64, u64)>,
    line_rows: Vec<(usize, u64)>,
    hidden_line_count: Option<u64>,
    reset: bool,
    scroll_to_bottom: bool,
}

impl LogViewer {
    pub(super) fn refresh_line_numbers(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let (Some(observation), Some(page)) = (self.observation.clone(), self.page.clone()) else {
            return;
        };
        let page_start = page
            .rows
            .first()
            .map_or(self.bookmark.offset, |row| row.start);
        let path = self.path.clone();
        let encoding = self.encoding_override;
        let index = self.index.clone();
        let hidden = self.bookmark.hidden;
        let generation = self.generation;
        let observation_stamp = observation.stamp.clone();

        let job = cx.background_spawn(async move {
            let mut reader = LogReader::open(path, encoding).map_err(|error| error.to_string())?;
            let mut file = reader
                .file()
                .try_clone()
                .map_err(|error| error.to_string())?;
            let (_, change) = log_file::observe_file(&mut file, Some(&observation))
                .map_err(|error| error.to_string())?;
            if matches!(
                change,
                Change::Replaced | Change::Truncated | Change::Rewritten
            ) {
                return Err("Содержимое лога изменилось".to_owned());
            }
            reader.set_line_index(index);
            let PageLineNumbers {
                range: line_range,
                rows: line_rows,
            } = line_positions(&mut reader, &page)?;
            let hidden_line_count = count_hidden_lines(&mut reader, hidden)?;
            Ok((line_range, line_rows, hidden_line_count))
        });

        cx.spawn_in(window, async move |this, cx| {
            let Ok((line_range, line_rows, hidden_line_count)) = job.await else {
                return;
            };
            let _ = this.update_in(cx, |viewer, _, cx| {
                let page_still_matches = viewer.page.as_ref().is_some_and(|page| {
                    page.rows
                        .first()
                        .map_or(viewer.bookmark.offset, |row| row.start)
                        == page_start
                });
                let observation_still_matches = viewer
                    .observation
                    .as_ref()
                    .is_some_and(|observation| observation.stamp == observation_stamp);
                if viewer.generation == generation
                    && page_still_matches
                    && observation_still_matches
                {
                    viewer.line_range = line_range;
                    viewer.line_rows = line_rows;
                    viewer.hidden_line_count = hidden_line_count;
                    cx.notify();
                }
            });
        })
        .detach();
    }

    pub(super) fn load_page(
        &mut self,
        destination: Destination,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.writing_file() {
            return;
        }
        let path = self.path.clone();
        let bookmark = self.bookmark.clone();
        let encoding = self.encoding_override;
        let previous = PreviousPage {
            observation: self.observation.clone(),
            page: self.page.clone(),
        };
        let index = self.index.clone();
        let rows = ((f32::from(window.viewport_size().height) - 150.0) / 23.0)
            .ceil()
            .clamp(12.0, 120.0) as usize
            + 2;
        let options = PageOptions {
            max_rows: rows,
            max_row_bytes: 2048,
        };
        self.generation = self.generation.wrapping_add(1);
        let generation = self.generation;
        self.reading = true;

        let job = cx.background_spawn(async move {
            read_page(
                &path,
                encoding,
                &bookmark,
                destination,
                options,
                previous,
                index,
            )
        });
        cx.spawn_in(window, async move |this, cx| {
            let result = job.await;
            let _ = this.update_in(cx, |viewer, window, cx| {
                if viewer.generation != generation {
                    return;
                }
                viewer.reading = false;
                match result {
                    Ok(rendered) => viewer.apply_page(rendered, window, cx),
                    Err(error) => {
                        viewer.notice = Some(format!("Не удалось прочитать лог. {error}").into());
                    }
                }
                cx.notify();
            });
        })
        .detach();
        cx.notify();
    }

    fn apply_page(&mut self, loaded: RenderedPage, window: &mut Window, cx: &mut Context<Self>) {
        let first = self.page.is_none();
        if loaded.reset {
            self.reset_index();
            self.reset_search(cx);
            self.notice.get_or_insert(
                "Прежнее содержимое лога не подтверждено. Позиция и скрытая граница сброшены."
                    .into(),
            );
        }
        self.encoding = loaded.encoding;
        self.line_range = loaded.line_range;
        self.line_rows = loaded.line_rows;
        self.hidden_line_count = loaded.hidden_line_count;
        self.bookmark.record_observation(&loaded.observation);
        self.bookmark.offset = loaded.start;
        self.bookmark.hidden = loaded.hidden;
        self.observation = Some(loaded.observation);
        let keep_steady = self.search_highlight.is_some();
        if let Some(highlight) = self.search_highlight.take() {
            highlight.clear(cx);
        }
        let text = assemble_page_text(&loaded.page);
        self.viewer.update(cx, |viewer, cx| {
            viewer.set_value(text, window, cx);
            if loaded.scroll_to_bottom {
                // The editor clamps this against the content after laying out any wrapped rows.
                viewer.set_scroll_offset(point(px(0.), px(-f32::MAX)), cx);
            }
        });
        self.page = Some(loaded.page);
        if let (Some((offset, match_range)), Some(page)) =
            (self.search_match.as_ref(), self.page.as_ref())
            && let Some(range) = local_match_range(page, *offset, match_range.as_ref())
        {
            self.search_highlight = Some(if keep_steady {
                crate::search_highlight::SearchHighlight::steady(&self.viewer, range, cx)
            } else {
                crate::search_highlight::SearchHighlight::new(&self.viewer, range, cx)
            });
        }
        self.start_index(window, cx);

        if first && let Some(start) = crate::log_viewer::measurement_start() {
            window.on_next_frame(move |_, _| {
                println!(
                    "RUSTTEXT first_log_frame_ms={}",
                    start.elapsed().as_millis()
                );
            });
        }
    }

    pub(super) fn apply_observation(
        &mut self,
        result: io::Result<(Observation, Change)>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.preparing_trim || self.writing_file() {
            return;
        }
        match result {
            Ok((_, Change::Same)) => {}
            Ok((observation, Change::Appended)) => {
                self.bookmark.record_observation(&observation);
                self.observation = Some(observation);
                if self.page.as_ref().is_some_and(|page| page.eof) {
                    self.load_page(Destination::Append(self.bookmark.follow_end), window, cx);
                } else if self.bookmark.follow_end {
                    self.load_page(Destination::End, window, cx);
                } else {
                    self.start_index(window, cx);
                }
                cx.notify();
            }
            Ok((observation, change)) => {
                self.reset_index();
                self.reset_search(cx);
                self.notice = Some(
                    match change {
                        Change::Truncated => "Лог очищен или сокращён. Позиция просмотра сброшена.",
                        Change::Rewritten => {
                            "Содержимое лога изменилось. Позиция просмотра сброшена."
                        }
                        _ => "Файл лога появился или заменён. Позиция просмотра сброшена.",
                    }
                    .into(),
                );
                self.bookmark.hidden = 0;
                self.bookmark.offset = 0;
                self.bookmark.record_observation(&observation);
                self.observation = Some(observation);
                self.load_page(
                    if self.bookmark.follow_end {
                        Destination::End
                    } else {
                        Destination::Start
                    },
                    window,
                    cx,
                );
            }
            Err(error) => {
                let message: SharedString = if error.kind() == io::ErrorKind::NotFound {
                    "Файл лога отсутствует. Ожидание повторного создания.".into()
                } else {
                    format!("Файл лога временно недоступен. {error}").into()
                };
                if self.observation.take().is_some() || self.notice.as_ref() != Some(&message) {
                    self.reset_search(cx);
                    self.reset_index();
                    self.page = None;
                    self.notice = Some(message);
                    self.viewer
                        .update(cx, |viewer, cx| viewer.set_value("", window, cx));
                    cx.notify();
                }
            }
        }
    }

    pub(super) fn jump_percent(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let input = self.percentage.read(cx).value();
        let percent = input.trim().replace(',', ".").parse::<f64>();
        match percent {
            Ok(percent) if (0.0..=100.0).contains(&percent) => {
                self.navigate(Destination::Percentage(percent), window, cx)
            }
            _ => {
                self.notice = Some("Введите положение от 0 до 100%.".into());
                cx.notify();
            }
        }
    }

    pub(super) fn navigate(
        &mut self,
        destination: Destination,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !self.available() {
            return;
        }
        self.clear_search_highlight(cx);
        self.notice = None;
        self.load_page(destination, window, cx);
    }

    pub(super) fn scroll_screen(
        &mut self,
        forward: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let editor = self.viewer.read(cx);
        let offset = editor.scroll_offset();
        let edge = if forward {
            self.scroll_bottom.get()
        } else {
            px(0.)
        };
        if (offset.y - edge).abs() <= px(0.5) {
            self.turn_page(forward, window, cx);
            return;
        }
        let line_height = editor.line_height().unwrap_or(window.line_height());
        let step = (editor.input_bounds().size.height - line_height).max(line_height);
        let y = if forward {
            offset.y - step
        } else {
            offset.y + step
        };
        self.viewer.update(cx, |editor, cx| {
            editor.set_scroll_offset(point(offset.x, y), cx)
        });
    }

    pub(super) fn turn_page(
        &mut self,
        forward: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> bool {
        if !self.available() || self.reading {
            return false;
        }
        let destination = if forward {
            let Some(page) = &self.page else {
                return false;
            };
            if page.eof || page.next_offset <= self.bookmark.offset {
                return false;
            }
            Destination::At(page.next_offset)
        } else if self.bookmark.offset > self.bookmark.hidden {
            Destination::Before(self.bookmark.offset)
        } else {
            return false;
        };
        self.navigate(destination, window, cx);
        true
    }
}

fn local_match_range(
    page: &Page,
    line_start: u64,
    match_range: Option<&Range<usize>>,
) -> Option<Range<usize>> {
    let mut text_offset = 0usize;
    for (index, row) in page.rows.iter().enumerate() {
        if row.start == line_start {
            let start = text_offset;
            let mut end = start + row.text.len();
            let mut fragment = index;
            while page.rows[fragment].continues && fragment + 1 < page.rows.len() {
                fragment += 1;
                end += page.rows[fragment].text.len();
            }

            if let Some(range) = match_range
                && range.start < range.end
                && range.end <= end - start
            {
                return Some((start + range.start)..(start + range.end));
            }
            return (end > start).then_some(start..end);
        }
        text_offset += row.text.len();
        if row.terminated && index + 1 < page.rows.len() {
            text_offset += 1;
        }
    }
    None
}

fn assemble_page_text(page: &Page) -> String {
    let mut text = String::new();
    for (index, row) in page.rows.iter().enumerate() {
        text.push_str(&row.text);
        if row.terminated && index + 1 < page.rows.len() {
            text.push('\n');
        }
    }
    text
}

fn read_page(
    path: &std::path::Path,
    encoding: Option<TextEncoding>,
    bookmark: &LogBookmark,
    destination: Destination,
    options: PageOptions,
    previous: PreviousPage,
    index: LineIndex,
) -> Result<RenderedPage, String> {
    let mut reader = LogReader::open(path, encoding).map_err(|error| error.to_string())?;
    let mut file = reader
        .file()
        .try_clone()
        .map_err(|error| error.to_string())?;
    let (observation, change) = log_file::observe_file(&mut file, previous.observation.as_ref())
        .map_err(|error| error.to_string())?;
    reader.refresh_size().map_err(|error| error.to_string())?;
    let reset = if previous.observation.is_some() {
        matches!(
            change,
            Change::Replaced | Change::Truncated | Change::Rewritten
        )
    } else {
        !bookmark
            .can_restore(&mut file, &observation)
            .map_err(|error| error.to_string())?
    };
    let floor = if reset {
        0
    } else {
        bookmark.hidden.min(reader.size())
    };
    if !reset {
        reader.set_line_index(index.clone());
    }
    let start = reader.seek_start().max(floor);
    let destination = if reset && !matches!(destination, Destination::End) {
        Destination::Start
    } else {
        destination
    };

    let scroll_to_bottom = match destination {
        Destination::Restore => bookmark.follow_end && bookmark.auto_refresh,
        Destination::Before(_) | Destination::End | Destination::Append(true) => true,
        Destination::Percentage(percent) => percent >= 100.,
        _ => false,
    };

    let mut page = match destination {
        Destination::Restore if bookmark.follow_end && bookmark.auto_refresh => {
            reader.page_before(reader.size(), options, MAX_BACKWARD_SCAN)
        }
        Destination::Restore => reader.render_page(bookmark.offset.max(start), options),
        Destination::Start => reader.render_page(start, options),
        Destination::End => reader.page_before(reader.size(), options, MAX_BACKWARD_SCAN),
        Destination::At(offset) => reader.render_page(offset.max(start), options),
        Destination::Match(offset) => reader.render_page(offset.max(start), options),
        Destination::Before(offset) => reader.page_before(offset, options, MAX_BACKWARD_SCAN),
        Destination::Percentage(percent) if percent >= 100.0 => {
            reader.page_before(reader.size(), options, MAX_BACKWARD_SCAN)
        }
        Destination::Percentage(percent) => {
            let offset = reader.seek_percent(percent).max(start);
            let sample = index
                .entries()
                .iter()
                .rev()
                .find(|entry| entry.offset <= offset);
            if !reset && sample.is_some_and(|sample| offset - sample.offset <= MAX_BACKWARD_SCAN) {
                reader.page_at_line(offset, options)
            } else {
                reader.render_page(offset, options)
            }
        }
        Destination::Append(follow) => match previous.page {
            Some(previous) => append_page(&mut reader, previous, options, follow),
            None => reader.render_page(start, options),
        },
    }
    .map_err(|error| error.to_string())?;

    if page.rows.first().is_some_and(|row| row.start < start) {
        page = match destination {
            Destination::Before(offset) => reader.render_page_until(start, offset, options),
            _ => reader.render_page(start, options),
        }
        .map_err(|error| error.to_string())?;
    }

    let start = page
        .rows
        .first()
        .map_or(page.next_offset.max(start), |row| row.start);
    let PageLineNumbers {
        range: line_range,
        rows: line_rows,
    } = line_positions(&mut reader, &page)?;
    let hidden_line_count = count_hidden_lines(&mut reader, floor)?;
    Ok(RenderedPage {
        observation,
        page,
        start,
        hidden: floor,
        encoding: reader.encoding(),
        line_range,
        line_rows,
        hidden_line_count,
        reset,
        scroll_to_bottom,
    })
}

fn line_positions(reader: &mut LogReader, page: &Page) -> Result<PageLineNumbers, String> {
    let Some(first_row) = page.rows.first() else {
        return Ok(PageLineNumbers::default());
    };
    let Some(first_line) = reader
        .line_number_at(first_row.start)
        .map_err(|error| error.to_string())?
    else {
        return Ok(PageLineNumbers::default());
    };

    let mut current_line = first_line;
    let mut last_line = first_line;
    let mut text_offset = 0;
    let mut line_rows = Vec::new();
    for (index, row) in page.rows.iter().enumerate() {
        if index > 0 && page.rows[index - 1].terminated {
            current_line = current_line.saturating_add(1);
        }
        if index == 0 || page.rows[index - 1].terminated {
            line_rows.push((text_offset, current_line));
        }
        last_line = current_line;
        text_offset += row.text.len();
        if row.terminated && index + 1 < page.rows.len() {
            text_offset += 1;
        }
    }
    Ok(PageLineNumbers {
        range: Some((first_line, last_line)),
        rows: line_rows,
    })
}

fn count_hidden_lines(reader: &mut LogReader, hidden: u64) -> Result<Option<u64>, String> {
    if hidden == 0 {
        return Ok(Some(0));
    }
    if hidden >= reader.size() {
        let index = reader.line_index();
        return Ok(
            (index.scanned_to() >= align_offset(reader.size(), reader.encoding()))
                .then(|| index.physical_line_count(reader.encoding().bom_len())),
        );
    }
    reader
        .line_number_at(hidden)
        .map(|line| line.map(|line| line.saturating_sub(1)))
        .map_err(|error| error.to_string())
}

fn append_page(
    reader: &mut LogReader,
    mut previous: Page,
    options: PageOptions,
    follow: bool,
) -> Result<Page, crate::log_io::LogError> {
    // A partial final line may acquire more text or the terminating newline.
    // Restart at that fragment only; all complete visible rows stay in memory.
    let start = if previous.rows.last().is_some_and(|row| !row.terminated) {
        previous.rows.pop().expect("partial row exists").start
    } else {
        previous.next_offset
    };
    let slots = if follow {
        options.max_rows
    } else {
        options.max_rows.saturating_sub(previous.rows.len())
    };
    if slots == 0 {
        previous.file_size = reader.size();
        previous.eof = previous.next_offset >= previous.file_size;
        return Ok(previous);
    }

    let fresh = reader.render_page(
        start,
        PageOptions {
            max_rows: slots,
            ..options
        },
    )?;
    if follow && !fresh.eof {
        return reader.page_before(reader.size(), options, MAX_BACKWARD_SCAN);
    }
    previous.rows.extend(fresh.rows);
    if previous.rows.len() > options.max_rows && follow {
        let excess = previous.rows.len() - options.max_rows;
        previous.rows.drain(..excess);
    }
    previous.file_size = fresh.file_size;
    previous.eof = fresh.eof;
    previous.next_offset = fresh.next_offset;
    Ok(previous)
}

#[cfg(test)]
#[path = "../../tests/unit/log_viewport.rs"]
mod tests;
