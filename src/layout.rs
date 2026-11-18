use std::collections::{HashMap, HashSet};

use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) enum Axis {
    #[default]
    None = 0,
    Horizontal = 1,
    Vertical = 2,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub(crate) struct Group {
    pub documents: Vec<u64>,
    pub active: Option<u64>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub(crate) struct Split {
    pub axis: Axis,
    pub ratio: f32,
    pub first: Box<Node>,
    pub second: Box<Node>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub(crate) struct Node {
    pub id: u64,
    pub group: Option<Group>,
    pub split: Option<Split>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub(crate) struct Layout {
    pub root: Node,
    pub focused: u64,
}

impl Default for Layout {
    fn default() -> Self {
        Self::new(Vec::new(), None)
    }
}

impl Node {
    fn same_structure(&self, other: &Self) -> bool {
        if self.id != other.id {
            return false;
        }
        let group_eq = match (&self.group, &other.group) {
            (Some(left), Some(right)) => left.documents == right.documents,
            (None, None) => true,
            _ => false,
        };
        let split_eq = match (&self.split, &other.split) {
            (Some(left), Some(right)) => {
                left.axis == right.axis
                    && left.first.same_structure(&right.first)
                    && left.second.same_structure(&right.second)
            }
            (None, None) => true,
            _ => false,
        };
        group_eq && split_eq
    }

    fn leaf(id: u64, documents: Vec<u64>, active: Option<u64>) -> Self {
        Self {
            id,
            group: Some(Group { documents, active }),
            split: None,
        }
    }

    fn find_mut(&mut self, id: u64) -> Option<&mut Self> {
        if self.id == id {
            return Some(self);
        }
        let split = self.split.as_mut()?;
        split
            .first
            .find_mut(id)
            .or_else(|| split.second.find_mut(id))
    }

    fn visit<'a>(&'a self, groups: &mut Vec<(u64, &'a Group)>) {
        if let Some(group) = &self.group {
            groups.push((self.id, group));
        }
        if let Some(split) = &self.split {
            split.first.visit(groups);
            split.second.visit(groups);
        }
    }

    fn collapse(&mut self, vacated: &[u64]) {
        let Some(split) = &mut self.split else {
            return;
        };
        split.first.collapse(vacated);
        split.second.collapse(vacated);
        if vacated.contains(&split.first.id)
            && split
                .first
                .group
                .as_ref()
                .is_some_and(|g| g.documents.is_empty())
        {
            *self = *split.second.clone();
        } else if vacated.contains(&split.second.id)
            && split
                .second
                .group
                .as_ref()
                .is_some_and(|g| g.documents.is_empty())
        {
            *self = *split.first.clone();
        }
    }
}

impl Layout {
    pub fn same_structure(&self, other: &Self) -> bool {
        self.root.same_structure(&other.root)
    }

    pub fn new(documents: Vec<u64>, active: Option<u64>) -> Self {
        let mut layout = Self {
            root: Node::leaf(1, documents.clone(), active),
            focused: 1,
        };
        layout.normalize(&documents);
        layout
    }

    pub fn groups(&self) -> Vec<(u64, &Group)> {
        let mut groups = Vec::new();
        self.root.visit(&mut groups);
        groups
    }

    pub fn active(&self) -> Option<u64> {
        self.groups()
            .into_iter()
            .find(|(id, _)| *id == self.focused)
            .and_then(|(_, group)| group.active)
    }

    pub fn focus(&mut self, group: u64) -> bool {
        if self.groups().iter().any(|(id, _)| *id == group) {
            self.focused = group;
            true
        } else {
            false
        }
    }

    pub fn select(&mut self, document: u64) -> bool {
        let Some(id) = self
            .groups()
            .into_iter()
            .find(|(_, g)| g.documents.contains(&document))
            .map(|(id, _)| id)
        else {
            return false;
        };
        self.focused = id;
        self.root
            .find_mut(id)
            .unwrap()
            .group
            .as_mut()
            .unwrap()
            .active = Some(document);
        true
    }

    pub fn attach(&mut self, document: u64) {
        if self.select(document) {
            return;
        }
        let id = self.focused;
        let Some(group) = self.root.find_mut(id).and_then(|n| n.group.as_mut()) else {
            return;
        };
        group.documents.push(document);
        group.active = Some(document);
    }

    pub fn remove(&mut self, document: u64) {
        let ids: Vec<_> = self.groups().iter().map(|(id, _)| *id).collect();
        let mut vacated = Vec::new();
        for id in ids {
            let group = self.root.find_mut(id).unwrap().group.as_mut().unwrap();
            if let Some(index) = group.documents.iter().position(|id| *id == document) {
                group.documents.remove(index);
                if group.documents.is_empty() {
                    vacated.push(id);
                }
                if group.active == Some(document) {
                    group.active = group
                        .documents
                        .get(index.min(group.documents.len().saturating_sub(1)))
                        .copied();
                }
            }
        }
        self.root.collapse(&vacated);
        if !self.groups().iter().any(|(id, _)| *id == self.focused) {
            self.focused = self.groups()[0].0;
        }
    }

    pub fn move_document(&mut self, document: u64, target: u64, before: Option<u64>) -> bool {
        if !self.groups().iter().any(|(id, _)| *id == target)
            || !self
                .groups()
                .iter()
                .any(|(_, g)| g.documents.contains(&document))
        {
            return false;
        }
        if before == Some(document) {
            return self.select(document);
        }
        let ids: Vec<_> = self.groups().iter().map(|(id, _)| *id).collect();
        let mut vacated = Vec::new();
        for id in ids {
            let group = self.root.find_mut(id).unwrap().group.as_mut().unwrap();
            if group.documents.len() == 1 && group.documents[0] == document {
                vacated.push(id);
            }
            group.documents.retain(|id| *id != document);
            if group.active == Some(document) {
                group.active = group.documents.first().copied();
            }
        }
        let group = self.root.find_mut(target).unwrap().group.as_mut().unwrap();
        let index = before
            .and_then(|id| group.documents.iter().position(|doc| *doc == id))
            .unwrap_or(group.documents.len());
        group.documents.insert(index, document);
        group.active = Some(document);
        self.focused = target;
        self.root.collapse(&vacated);
        true
    }

    pub fn split(&mut self, group: u64, axis: Axis) -> Option<u64> {
        if axis == Axis::None {
            return None;
        }
        let next = self.max_id().checked_add(1)?;
        let second = next.checked_add(1)?;
        let node = self.root.find_mut(group)?;
        let existing = node.group.take()?;
        node.split = Some(Split {
            axis,
            ratio: 0.5,
            first: Box::new(Node::leaf(next, existing.documents, existing.active)),
            second: Box::new(Node::leaf(second, Vec::new(), None)),
        });
        self.focused = second;
        Some(second)
    }

    pub fn close_empty_group(&mut self, group: u64) -> bool {
        if self.groups().len() <= 1
            || !self
                .groups()
                .into_iter()
                .any(|(id, current)| id == group && current.documents.is_empty())
        {
            return false;
        }
        self.root.collapse(&[group]);
        if !self.groups().iter().any(|(id, _)| *id == self.focused) {
            self.focused = self.groups()[0].0;
        }
        true
    }

    fn max_id(&self) -> u64 {
        fn visit(node: &Node) -> u64 {
            node.split.as_ref().map_or(node.id, |split| {
                node.id.max(visit(&split.first)).max(visit(&split.second))
            })
        }
        visit(&self.root)
    }

    pub fn set_ratio(&mut self, node: u64, ratio: f32) {
        if ratio.is_finite()
            && let Some(split) = self.root.find_mut(node).and_then(|n| n.split.as_mut())
        {
            split.ratio = ratio.clamp(0.05, 0.95);
        }
    }

    pub fn remap(&mut self, ids: &HashMap<u64, u64>) {
        fn visit(node: &mut Node, ids: &HashMap<u64, u64>) {
            if let Some(group) = &mut node.group {
                group.documents = group
                    .documents
                    .iter()
                    .filter_map(|id| ids.get(id).copied())
                    .collect();
                group.active = group.active.and_then(|id| ids.get(&id).copied());
            }
            if let Some(split) = &mut node.split {
                visit(&mut split.first, ids);
                visit(&mut split.second, ids);
            }
        }
        visit(&mut self.root, ids);
    }

    pub fn normalize(&mut self, documents: &[u64]) {
        fn visit(
            node: &mut Node,
            valid: &HashSet<u64>,
            seen: &mut HashSet<u64>,
            nodes: &mut HashSet<u64>,
            next: &mut u64,
            depth: usize,
        ) {
            if node.id == 0 || !nodes.insert(node.id) {
                while nodes.contains(next) {
                    *next += 1;
                }
                node.id = *next;
                nodes.insert(node.id);
            }
            if node.group.is_some() || node.split.is_none() || depth >= 32 {
                node.split = None;
                let group = node.group.get_or_insert_with(Group::default);
                group
                    .documents
                    .retain(|id| valid.contains(id) && seen.insert(*id));
                if !group.active.is_some_and(|id| group.documents.contains(&id)) {
                    group.active = group.documents.first().copied();
                }
            } else if let Some(split) = &mut node.split {
                if split.axis == Axis::None {
                    split.axis = Axis::Horizontal;
                }
                split.ratio = if split.ratio.is_finite() {
                    split.ratio.clamp(0.05, 0.95)
                } else {
                    0.5
                };
                visit(&mut split.first, valid, seen, nodes, next, depth + 1);
                visit(&mut split.second, valid, seen, nodes, next, depth + 1);
            }
        }
        let mut seen = HashSet::new();
        visit(
            &mut self.root,
            &documents.iter().copied().collect(),
            &mut seen,
            &mut HashSet::new(),
            &mut 1,
            0,
        );
        if !self.groups().iter().any(|(id, _)| *id == self.focused) {
            self.focused = self.groups()[0].0;
        }
        for id in documents {
            if seen.insert(*id) {
                self.attach(*id);
            }
        }
    }
}

#[cfg(test)]
#[path = "../tests/unit/layout.rs"]
mod tests;
