#[cfg(test)]
use std::cell::Cell;
use std::{
    ops::Range,
    sync::atomic::{AtomicBool, Ordering},
    time::Instant,
};

use gpui_kit::component::input::Rope;
use regex::{Captures, Regex, RegexBuilder};

#[derive(Clone, Default, PartialEq, Eq)]
pub struct Query {
    pub text: String,
    pub regex: bool,
    pub case_sensitive: bool,
    pub whole_word: bool,
    pub all_tabs: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Hit {
    pub tab: u64,
    pub range: Range<usize>,
}

pub const MAX_VISIBLE_MATCHES: usize = 100;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SearchMatch {
    pub hit: Hit,
    pub line: usize,
    pub excerpt: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MatchList {
    pub matches: Vec<SearchMatch>,
    pub total: usize,
    pub complete: bool,
}

#[derive(Clone)]
pub struct Source {
    pub tab: u64,
    pub text: Rope,
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Command {
    Next,
    Previous,
    List,
    Replace,
    ReplaceAll,
}

impl Command {
    pub fn replaces(self) -> bool {
        matches!(self, Self::Replace | Self::ReplaceAll)
    }
}

#[derive(Debug)]
pub enum Outcome {
    Found {
        hit: Option<Hit>,
    },
    Matches(MatchList),
    Incomplete {
        progress: NavigationProgress,
    },
    Cancelled,
    Replaced {
        range: Range<usize>,
        text: String,
        count: usize,
    },
    NoReplacements,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct NavigationProgress {
    pub tab: u64,
    pub boundary: usize,
    pub last_match: Option<Range<usize>>,
    pub wrapped: bool,
}

pub struct NavigationStart {
    pub skip_current: bool,
    pub progress: Option<NavigationProgress>,
}

impl NavigationProgress {
    pub fn after_hit(tab: u64, range: Range<usize>) -> Self {
        Self {
            tab,
            boundary: range.end,
            last_match: Some(range),
            wrapped: false,
        }
    }
}

pub struct WorkBudget<'a> {
    cancelled: &'a AtomicBool,
    deadline: Instant,
    #[cfg(test)]
    remaining_checks: Option<Cell<usize>>,
}

impl<'a> WorkBudget<'a> {
    pub fn new(cancelled: &'a AtomicBool, deadline: Instant) -> Self {
        Self {
            cancelled,
            deadline,
            #[cfg(test)]
            remaining_checks: None,
        }
    }

    #[cfg(test)]
    fn with_check_limit(cancelled: &'a AtomicBool, limit: usize) -> Self {
        Self {
            cancelled,
            deadline: Instant::now() + std::time::Duration::from_secs(5),
            remaining_checks: Some(Cell::new(limit)),
        }
    }

    fn check(&self) -> Result<bool, String> {
        if self.cancelled.load(Ordering::Relaxed) {
            return Ok(true);
        }
        #[cfg(test)]
        if let Some(remaining) = &self.remaining_checks {
            let checks = remaining.get();
            if checks == 0 {
                return Err("Поиск приостановлен из-за лимита вычислений.".into());
            }
            remaining.set(checks - 1);
        }
        if Instant::now() >= self.deadline {
            return Err("Поиск превысил лимит времени 400 мс.".into());
        }
        Ok(false)
    }
}

pub fn run(
    query: &Query,
    command: Command,
    sources: Vec<Source>,
    origin: &Hit,
    replacement: &str,
    work: &WorkBudget<'_>,
    navigation: NavigationStart,
) -> Result<Outcome, String> {
    if work.check()? {
        return Ok(Outcome::Cancelled);
    }
    let regex = compile(query)?;
    if command == Command::List {
        return list_matches(&regex, sources, work);
    }
    if command.replaces() {
        let source = sources.into_iter().find(|source| source.tab == origin.tab);
        return match source {
            Some(source) => {
                if !source_within_limit(&source) {
                    return Err(
                        "Документ больше 64 МиБ. Замена в редакторе ограничена этим размером."
                            .into(),
                    );
                }
                replace(
                    &regex,
                    query.regex,
                    &source.text.to_string(),
                    origin.range.clone(),
                    replacement,
                    command == Command::ReplaceAll,
                    work,
                )
            }
            None => Ok(Outcome::NoReplacements),
        };
    }
    navigate(
        &regex,
        sources,
        origin,
        navigation.skip_current,
        command == Command::Previous,
        work,
        navigation.progress,
    )
}

fn list_matches(
    regex: &Regex,
    sources: Vec<Source>,
    work: &WorkBudget<'_>,
) -> Result<Outcome, String> {
    let mut matches = Vec::with_capacity(MAX_VISIBLE_MATCHES);
    let mut total: usize = 0;

    for source in sources {
        match work.check() {
            Ok(true) => return Ok(Outcome::Cancelled),
            Ok(false) => {}
            Err(_) => {
                return Ok(Outcome::Matches(MatchList {
                    matches,
                    total,
                    complete: false,
                }));
            }
        }
        if !source_within_limit(&source) {
            return Err("Поиск ограничен документами до 64 МиБ.".into());
        }
        let text = source.text.to_string();
        let mut line_start = 0;
        let mut line_cursor = 0;
        let mut line_number = 1;
        let mut line_end = 0;
        let mut search = regex.find_iter(&text);

        loop {
            match work.check() {
                Ok(true) => return Ok(Outcome::Cancelled),
                Ok(false) => {}
                Err(_) => {
                    return Ok(Outcome::Matches(MatchList {
                        matches,
                        total,
                        complete: false,
                    }));
                }
            }
            let matched = search.next();
            match work.check() {
                Ok(true) => return Ok(Outcome::Cancelled),
                Ok(false) => {}
                Err(_) => {
                    return Ok(Outcome::Matches(MatchList {
                        matches,
                        total,
                        complete: false,
                    }));
                }
            }
            let Some(matched) = matched else {
                break;
            };

            total = total.saturating_add(1);
            if matches.len() >= MAX_VISIBLE_MATCHES {
                continue;
            }

            while let Some(newline) = text[line_cursor..matched.start()].find('\n') {
                line_start = line_cursor + newline + 1;
                line_cursor = line_start;
                line_number += 1;
            }
            line_cursor = matched.start();

            if matched.start() >= line_end || matched.end() > line_end {
                line_end = text[matched.end()..]
                    .find('\n')
                    .map_or(text.len(), |offset| matched.end() + offset);
            }
            let line = &text[line_start..line_end];
            let range = matched.start() - line_start..matched.end() - line_start;
            matches.push(SearchMatch {
                hit: Hit {
                    tab: source.tab,
                    range: matched.range(),
                },
                line: line_number,
                excerpt: excerpt(line, range),
            });
        }
    }

    Ok(Outcome::Matches(MatchList {
        matches,
        total,
        complete: true,
    }))
}

fn excerpt(line: &str, range: Range<usize>) -> String {
    const CONTEXT: usize = 36;

    let mut start = range.start.saturating_sub(CONTEXT);
    while !line.is_char_boundary(start) {
        start += 1;
    }

    let match_end = if range.end.saturating_sub(range.start) > CONTEXT {
        range.start.saturating_add(CONTEXT)
    } else {
        range.end.saturating_add(CONTEXT)
    };
    let mut end = match_end.min(line.len());
    while !line.is_char_boundary(end) {
        end -= 1;
    }

    let prefix = if start > 0 { "…" } else { "" };
    let suffix = if end < line.len() { "…" } else { "" };
    format!("{prefix}{}{suffix}", &line[start..end])
}

fn compile(query: &Query) -> Result<Regex, String> {
    if query.text.is_empty() {
        return Err("Введите текст или регулярное выражение.".into());
    }
    if query.text.len() > MAX_QUERY_BYTES {
        return Err("Запрос поиска не может быть длиннее 4 КиБ.".into());
    }
    let mut pattern = if query.regex {
        query.text.clone()
    } else {
        regex::escape(&query.text)
    };
    if query.whole_word {
        pattern = format!(r"\b(?:{pattern})\b");
    }
    RegexBuilder::new(&pattern)
        .case_insensitive(!query.case_sensitive)
        .build()
        .map_err(|error| format!("Ошибка регулярного выражения: {error}"))
}

fn navigate(
    regex: &Regex,
    sources: Vec<Source>,
    origin: &Hit,
    skip_current: bool,
    backwards: bool,
    work: &WorkBudget<'_>,
    progress: Option<NavigationProgress>,
) -> Result<Outcome, String> {
    let tab_index = sources.iter().position(|source| source.tab == origin.tab);
    let Some(tab_index) = tab_index else {
        return Ok(Outcome::Found { hit: None });
    };
    if !backwards {
        return navigate_forward(
            regex,
            &sources,
            tab_index,
            origin,
            skip_current,
            work,
            progress,
        );
    }
    if work.check()? {
        return Ok(Outcome::Cancelled);
    }

    let mut order = Vec::with_capacity(sources.len());
    order.push((tab_index, true));
    order.extend((0..tab_index).rev().map(|index| (index, false)));
    order.extend(
        (tab_index + 1..sources.len())
            .rev()
            .map(|index| (index, false)),
    );

    for (index, cursor_tab) in &order {
        if work.check()? {
            return Ok(Outcome::Cancelled);
        }
        let source = &sources[*index];
        if !source_within_limit(source) {
            return Err("Поиск ограничен документами до 64 МиБ.".into());
        }
        let text = source.text.to_string();
        let hit = last_match_before(
            regex,
            source.tab,
            &text,
            (*cursor_tab).then_some(origin.range.start),
            work,
        )?;
        if work.check()? {
            return Ok(Outcome::Cancelled);
        }
        if hit.is_some() {
            return Ok(Outcome::Found { hit });
        }
    }
    if !work.check()? {
        let source = &sources[tab_index];
        if !source_within_limit(source) {
            return Err("Поиск ограничен документами до 64 МиБ.".into());
        }
        let text = source.text.to_string();
        let hit = last_match_before(regex, source.tab, &text, None, work)?;
        return Ok(Outcome::Found { hit });
    }
    Ok(Outcome::Found { hit: None })
}

fn navigate_forward(
    regex: &Regex,
    sources: &[Source],
    origin_index: usize,
    origin: &Hit,
    skip_current: bool,
    work: &WorkBudget<'_>,
    progress: Option<NavigationProgress>,
) -> Result<Outcome, String> {
    let mut targets = Vec::with_capacity(sources.len() + 1);
    targets.push((
        origin_index,
        false,
        origin.range.start,
        skip_current.then_some(origin),
    ));
    targets.extend((origin_index + 1..sources.len()).map(|index| (index, false, 0, None)));
    targets.extend((0..origin_index).map(|index| (index, false, 0, None)));
    targets.push((origin_index, true, 0, None));

    let resume_target = progress.as_ref().and_then(|saved| {
        targets.iter().position(|(index, wrapped, _, _)| {
            sources[*index].tab == saved.tab && *wrapped == saved.wrapped
        })
    });

    for (target_index, (source_index, wrapped, minimum_start, skip)) in targets.iter().enumerate() {
        if resume_target.is_some_and(|resume_index| target_index < resume_index) {
            continue;
        }
        let source = &sources[*source_index];
        let mut scan = if resume_target == Some(target_index) {
            progress.clone().expect("resume progress exists")
        } else {
            NavigationProgress {
                tab: source.tab,
                boundary: 0,
                last_match: None,
                wrapped: *wrapped,
            }
        };
        match work.check() {
            Ok(true) => return Ok(Outcome::Cancelled),
            Ok(false) => {}
            Err(_) => {
                return Ok(Outcome::Incomplete { progress: scan });
            }
        }
        if !source_within_limit(source) {
            return Err("Поиск ограничен документами до 64 МиБ.".into());
        }
        let text = source.text.to_string();
        match scan_forward(regex, &text, &mut scan, *minimum_start, *skip, work) {
            ForwardScan::Found(hit) => return Ok(Outcome::Found { hit: Some(hit) }),
            ForwardScan::Complete => {}
            ForwardScan::Incomplete => return Ok(Outcome::Incomplete { progress: scan }),
            ForwardScan::Cancelled => return Ok(Outcome::Cancelled),
        }
    }
    Ok(Outcome::Found { hit: None })
}

enum ForwardScan {
    Found(Hit),
    Complete,
    Incomplete,
    Cancelled,
}

fn scan_forward(
    regex: &Regex,
    text: &str,
    progress: &mut NavigationProgress,
    minimum_start: usize,
    skip: Option<&Hit>,
    work: &WorkBudget<'_>,
) -> ForwardScan {
    loop {
        match work.check() {
            Ok(true) => return ForwardScan::Cancelled,
            Ok(false) => {}
            Err(_) => return ForwardScan::Incomplete,
        }
        if progress.boundary > text.len() {
            return ForwardScan::Complete;
        }
        let Some(matched) = regex.find_at(text, progress.boundary) else {
            return ForwardScan::Complete;
        };
        if let Some(previous) = &progress.last_match
            && matched.is_empty()
            && matched.end() == previous.end
        {
            progress.boundary = next_char_boundary(text, matched.end());
            progress.last_match = Some(matched.range());
            continue;
        }
        let range = matched.range();
        progress.boundary = range.end;
        progress.last_match = Some(range.clone());
        let hit = Hit {
            tab: progress.tab,
            range,
        };
        if hit.range.start >= minimum_start && skip != Some(&hit) {
            return ForwardScan::Found(hit);
        }
    }
}

fn next_char_boundary(text: &str, offset: usize) -> usize {
    if offset == text.len() {
        text.len().saturating_add(1)
    } else {
        offset
            + text[offset..]
                .chars()
                .next()
                .expect("offset is before the end of the text")
                .len_utf8()
    }
}

const MAX_SEARCH_BYTES: usize = 64 * 1024 * 1024;
const MAX_QUERY_BYTES: usize = 4 * 1024;
const MAX_PREVIOUS_MATCHES: usize = 10_000;
pub const MAX_SEARCH_TIME: std::time::Duration = std::time::Duration::from_millis(400);

fn source_within_limit(source: &Source) -> bool {
    source.text.len() <= MAX_SEARCH_BYTES
}

fn last_match_before(
    regex: &Regex,
    tab: u64,
    text: &str,
    before: Option<usize>,
    work: &WorkBudget<'_>,
) -> Result<Option<Hit>, String> {
    let mut last = None;
    let mut examined = 0;
    for matched in regex.find_iter(text) {
        if work.check()? {
            return Ok(None);
        }
        if before.is_some_and(|cursor| matched.start() >= cursor) {
            break;
        }
        examined += 1;
        if examined > MAX_PREVIOUS_MATCHES {
            return Err("Поиск назад ограничен 10 000 совпадениями за один запуск.".into());
        }
        last = Some(Hit {
            tab,
            range: matched.range(),
        });
    }
    Ok(last)
}

fn replace(
    regex: &Regex,
    expand_captures: bool,
    source: &str,
    selection: Range<usize>,
    replacement: &str,
    all: bool,
    work: &WorkBudget<'_>,
) -> Result<Outcome, String> {
    if work.check()? {
        return Ok(Outcome::Cancelled);
    }
    let mut matches = regex.captures_iter(source);
    if all {
        let mut count = 0;
        let mut last_end = 0;
        let mut result = String::new();
        for captures in matches {
            if work.check()? {
                return Ok(Outcome::Cancelled);
            }
            if count == MAX_PREVIOUS_MATCHES {
                return Err("Замена ограничена 10 000 совпадениями за один запуск.".into());
            }
            let range = captures.get(0).expect("complete match").range();
            result.push_str(&source[last_end..range.start]);
            append_replacement(&captures, expand_captures, replacement, &mut result);
            last_end = range.end;
            count += 1;
        }
        if count == 0 {
            return Ok(Outcome::NoReplacements);
        }
        result.push_str(&source[last_end..]);
        Ok(Outcome::Replaced {
            range: 0..source.len(),
            text: result,
            count,
        })
    } else {
        let first = matches.next();
        let captures = if first.as_ref().is_some_and(|captures| {
            captures.get(0).expect("complete match").start() >= selection.start
        }) {
            first
        } else {
            let mut selected = None;
            for captures in matches {
                if work.check()? {
                    return Ok(Outcome::Cancelled);
                }
                if captures.get(0).expect("complete match").start() >= selection.start {
                    selected = Some(captures);
                    break;
                }
            }
            selected.or(first)
        };
        let Some(captures) = captures else {
            return Ok(Outcome::NoReplacements);
        };
        if work.check()? {
            return Ok(Outcome::Cancelled);
        }
        let mut result = String::new();
        append_replacement(&captures, expand_captures, replacement, &mut result);
        Ok(Outcome::Replaced {
            range: captures.get(0).expect("complete match").range(),
            text: result,
            count: 1,
        })
    }
}

fn append_replacement(
    captures: &Captures<'_>,
    expand_captures: bool,
    replacement: &str,
    result: &mut String,
) {
    if expand_captures {
        captures.expand(replacement, result);
    } else {
        result.push_str(replacement);
    }
}

#[cfg(test)]
#[path = "../tests/unit/search.rs"]
mod tests;
