use std::collections::HashSet;

#[derive(Debug, Default, PartialEq, Eq)]
pub(crate) struct ComparedLists {
    pub common: Vec<String>,
    pub left_only: Vec<String>,
    pub right_only: Vec<String>,
}

pub(crate) fn compare(left: &str, right: &str) -> ComparedLists {
    let right_set: HashSet<_> = lines(right).collect();
    let mut left_set = HashSet::new();
    let mut result = ComparedLists::default();
    for line in lines(left) {
        if left_set.insert(line) {
            if right_set.contains(line) {
                result.common.push(line.to_owned());
            } else {
                result.left_only.push(line.to_owned());
            }
        }
    }
    let mut seen = HashSet::new();
    for line in lines(right) {
        if seen.insert(line) && !left_set.contains(line) {
            result.right_only.push(line.to_owned());
        }
    }
    result
}

fn lines(text: &str) -> impl Iterator<Item = &str> {
    text.split_inclusive('\n').flat_map(|part| {
        let part = part.strip_suffix('\n').unwrap_or(part);
        part.strip_suffix('\r').unwrap_or(part).split('\r')
    })
}

#[cfg(test)]
#[path = "../tests/unit/list_compare.rs"]
mod tests;
