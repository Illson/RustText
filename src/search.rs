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

pub struct Source {
    pub tab: u64,
    pub text: Rope,
}

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum Command {
    Next,
    Previous,
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
    Cancelled,
    Replaced {
        range: Range<usize>,
        text: String,
        count: usize,
    },
    NoReplacements,
}

#[derive(Clone, Copy)]
pub struct WorkBudget<'a> {
    cancelled: &'a AtomicBool,
    deadline: Instant,
}

impl<'a> WorkBudget<'a> {
    pub fn new(cancelled: &'a AtomicBool, deadline: Instant) -> Self {
        Self {
            cancelled,
            deadline,
        }
    }

    fn check(self) -> Result<bool, String> {
        if Instant::now() >= self.deadline {
            return Err("Поиск превысил лимит времени 400 мс.".into());
        }
        Ok(self.cancelled.load(Ordering::Relaxed))
    }
}

pub fn run(
    query: &Query,
    command: Command,
    sources: Vec<Source>,
    origin: &Hit,
    skip_current: bool,
    replacement: &str,
    work: WorkBudget<'_>,
) -> Result<Outcome, String> {
    if work.check()? {
        return Ok(Outcome::Cancelled);
    }
    let regex = compile(query)?;
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
        skip_current,
        command == Command::Previous,
        work,
    )
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
    work: WorkBudget<'_>,
) -> Result<Outcome, String> {
    let tab_index = sources.iter().position(|source| source.tab == origin.tab);
    let Some(tab_index) = tab_index else {
        return Ok(Outcome::Found { hit: None });
    };
    if work.check()? {
        return Ok(Outcome::Cancelled);
    }

    let mut order = Vec::with_capacity(sources.len());
    if backwards {
        order.push((tab_index, true));
        order.extend((0..tab_index).rev().map(|index| (index, false)));
        order.extend(
            (tab_index + 1..sources.len())
                .rev()
                .map(|index| (index, false)),
        );
    } else {
        order.push((tab_index, true));
        order.extend((tab_index + 1..sources.len()).map(|index| (index, false)));
        order.extend((0..tab_index).map(|index| (index, false)));
    }

    for (index, cursor_tab) in &order {
        if work.check()? {
            return Ok(Outcome::Cancelled);
        }
        let source = &sources[*index];
        if !source_within_limit(source) {
            return Err("Поиск ограничен документами до 64 МиБ.".into());
        }
        let text = source.text.to_string();
        let hit = if backwards {
            last_match_before(
                regex,
                source.tab,
                &text,
                (*cursor_tab).then_some(origin.range.start),
                work,
            )?
        } else {
            let start = if *cursor_tab { origin.range.start } else { 0 };
            first_match(
                regex,
                source.tab,
                &text,
                start,
                (skip_current && *cursor_tab).then_some(origin),
                work,
            )?
        };
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
        if backwards {
            if let Some(hit) = last_match_before(regex, source.tab, &text, None, work)? {
                return Ok(Outcome::Found { hit: Some(hit) });
            }
        } else if let Some(hit) = first_match(regex, source.tab, &text, 0, None, work)? {
            return Ok(Outcome::Found { hit: Some(hit) });
        }
    }
    Ok(Outcome::Found { hit: None })
}

const MAX_SEARCH_BYTES: usize = 64 * 1024 * 1024;
const MAX_QUERY_BYTES: usize = 4 * 1024;
const MAX_PREVIOUS_MATCHES: usize = 10_000;
pub const MAX_SEARCH_TIME: std::time::Duration = std::time::Duration::from_millis(400);

fn source_within_limit(source: &Source) -> bool {
    source.text.len() <= MAX_SEARCH_BYTES
}

fn first_match(
    regex: &Regex,
    tab: u64,
    text: &str,
    start: usize,
    skip: Option<&Hit>,
    work: WorkBudget<'_>,
) -> Result<Option<Hit>, String> {
    for matched in regex.find_iter(text) {
        if work.check()? {
            return Ok(None);
        }
        if matched.start() < start {
            continue;
        }
        let hit = Hit {
            tab,
            range: matched.range(),
        };
        if skip != Some(&hit) {
            return Ok(Some(hit));
        }
    }
    Ok(None)
}

fn last_match_before(
    regex: &Regex,
    tab: u64,
    text: &str,
    before: Option<usize>,
    work: WorkBudget<'_>,
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
    work: WorkBudget<'_>,
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
