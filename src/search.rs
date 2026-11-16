use std::ops::Range;

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
}

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

pub enum Outcome {
    Found {
        hit: Option<Hit>,
        total: usize,
    },
    Matches(MatchList),
    Replaced {
        range: Range<usize>,
        text: String,
        count: usize,
    },
    NoReplacements,
}

pub fn run(
    query: &Query,
    command: Command,
    sources: Vec<Source>,
    origin: &Hit,
    skip_current: bool,
    replacement: &str,
) -> Result<Outcome, String> {
    let regex = compile(query)?;
    if command == Command::List {
        return Ok(Outcome::Matches(list_matches(
            &regex,
            sources,
            MAX_VISIBLE_MATCHES,
        )));
    }
    if command.replaces() {
        let source = sources.into_iter().find(|source| source.tab == origin.tab);
        return Ok(match source {
            Some(source) => replace(
                &regex,
                query.regex,
                &source.text.to_string(),
                origin.range.clone(),
                replacement,
                command == Command::ReplaceAll,
            ),
            None => Outcome::NoReplacements,
        });
    }
    Ok(navigate(
        &regex,
        sources,
        origin,
        skip_current,
        command == Command::Previous,
    ))
}

fn list_matches(regex: &Regex, sources: Vec<Source>, limit: usize) -> MatchList {
    let limit = limit.min(MAX_VISIBLE_MATCHES);
    let mut matches = Vec::with_capacity(limit.min(MAX_VISIBLE_MATCHES));
    let mut total: usize = 0;

    for source in sources {
        let text = source.text.to_string();
        let mut line_start = 0;
        let mut line_cursor = 0;
        let mut line_number = 1;

        for matched in regex.find_iter(&text) {
            while let Some(newline) = text[line_cursor..matched.start()].find('\n') {
                line_start = line_cursor + newline + 1;
                line_cursor = line_start;
                line_number += 1;
            }
            line_cursor = matched.start();

            total = total.saturating_add(1);
            if matches.len() >= limit {
                continue;
            }

            let line_end = text[matched.end()..]
                .find('\n')
                .map_or(text.len(), |offset| matched.end() + offset);
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

    MatchList { matches, total }
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
) -> Outcome {
    let tab_index = sources.iter().position(|source| source.tab == origin.tab);
    let mut first = None;
    let mut last = None;
    let mut nearest = None;
    let mut total = 0;
    for (index, source) in sources.into_iter().enumerate() {
        let text = source.text.to_string();
        for matched in regex.find_iter(&text) {
            total += 1;
            let hit = Hit {
                tab: source.tab,
                range: matched.range(),
            };
            let position = (index, hit.range.start);
            if backwards {
                let before_cursor =
                    tab_index.is_none_or(|tab| position < (tab, origin.range.start));
                if before_cursor {
                    nearest = Some(hit.clone());
                }
                last = Some(hit);
            } else {
                let after_cursor =
                    tab_index.is_none_or(|tab| position >= (tab, origin.range.start));
                if nearest.is_none() && after_cursor && !(skip_current && hit == *origin) {
                    nearest = Some(hit.clone());
                }
                first.get_or_insert(hit);
            }
        }
    }
    Outcome::Found {
        hit: nearest.or(if backwards { last } else { first }),
        total,
    }
}

fn replace(
    regex: &Regex,
    expand_captures: bool,
    source: &str,
    selection: Range<usize>,
    replacement: &str,
    all: bool,
) -> Outcome {
    let mut matches = regex.captures_iter(source);
    if all {
        let mut count = 0;
        let mut last_end = 0;
        let mut result = String::new();
        for captures in matches {
            let range = captures.get(0).expect("complete match").range();
            result.push_str(&source[last_end..range.start]);
            append_replacement(&captures, expand_captures, replacement, &mut result);
            last_end = range.end;
            count += 1;
        }
        if count == 0 {
            return Outcome::NoReplacements;
        }
        result.push_str(&source[last_end..]);
        Outcome::Replaced {
            range: 0..source.len(),
            text: result,
            count,
        }
    } else {
        let first = matches.next();
        let matches_selection = |captures: &Captures<'_>| {
            captures.get(0).expect("complete match").start() >= selection.start
        };
        let captures = if first.as_ref().is_some_and(matches_selection) {
            first
        } else {
            matches.find(matches_selection).or(first)
        };
        let Some(captures) = captures else {
            return Outcome::NoReplacements;
        };
        let mut result = String::new();
        append_replacement(&captures, expand_captures, replacement, &mut result);
        Outcome::Replaced {
            range: captures.get(0).expect("complete match").range(),
            text: result,
            count: 1,
        }
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
