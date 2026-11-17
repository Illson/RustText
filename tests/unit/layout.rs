use super::*;

fn documents(layout: &Layout) -> Vec<u64> {
    layout
        .groups()
        .into_iter()
        .flat_map(|(_, g)| g.documents.iter().copied())
        .collect()
}

#[test]
fn closing_another_tab_keeps_an_intentionally_empty_group_and_its_ratio() {
    let mut layout = Layout::new(vec![10, 20], Some(10));
    let empty = layout.split(1, Axis::Horizontal).unwrap();
    layout.set_ratio(layout.root.id, 0.7);
    layout.focus(empty);
    layout.remove(10);
    assert_eq!(layout.groups().len(), 2);
    assert_eq!(layout.focused, empty);
    assert_eq!(layout.active(), None);
    assert_eq!(layout.root.split.as_ref().unwrap().ratio, 0.7);
    assert_eq!(documents(&layout), vec![20]);
    assert!(layout.move_document(20, empty, None));
    assert_eq!(layout.groups().len(), 1);
    assert_eq!(layout.focused, empty);
    assert_eq!(layout.active(), Some(20));
}

#[test]
fn nested_split_moves_without_copying_and_collapses_vacated_group() {
    let mut layout = Layout::new(vec![10, 20, 30], Some(10));
    let right = layout.split(1, Axis::Horizontal).unwrap();
    assert!(layout.move_document(20, right, None));
    let bottom = layout.split(right, Axis::Vertical).unwrap();
    assert!(layout.move_document(30, bottom, None));
    assert_eq!(layout.groups().len(), 3);
    assert!(layout.move_document(20, bottom, Some(30)));
    assert_eq!(layout.groups().len(), 2);
    assert_eq!(layout.groups().last().unwrap().1.documents, vec![20, 30]);
    let mut all = documents(&layout);
    all.sort_unstable();
    assert_eq!(all, vec![10, 20, 30]);
    assert_eq!(layout.focused, bottom);
}

#[test]
fn close_selects_adjacent_tab_and_leaves_one_empty_group() {
    let mut layout = Layout::new(vec![10, 20, 30], Some(20));
    layout.remove(20);
    assert_eq!(layout.groups()[0].1.active, Some(30));
    layout.remove(30);
    assert_eq!(layout.groups()[0].1.active, Some(10));
    layout.remove(10);
    assert_eq!(layout.groups().len(), 1);
    assert_eq!(layout.groups()[0].1.active, None);
    layout.attach(40);
    assert_eq!(documents(&layout), vec![40]);
}

#[test]
fn selecting_document_focuses_its_group() {
    let mut layout = Layout::new(vec![10, 20], Some(10));
    let right = layout.split(1, Axis::Horizontal).unwrap();
    layout.move_document(20, right, None);
    assert!(layout.select(10));
    assert_ne!(layout.focused, right);
    assert!(!layout.select(99));
    assert!(!layout.move_document(10, 99, None));
    assert_eq!(documents(&layout), vec![10, 20]);
}

#[test]
fn persisted_tree_keeps_ratios_order_and_focus() {
    let mut layout = Layout::new(vec![10, 20], Some(10));
    let right = layout.split(1, Axis::Vertical).unwrap();
    layout.move_document(20, right, None);
    layout.set_ratio(1, 0.71);
    let encoded = serde_json::to_vec(&layout).unwrap();
    let mut restored: Layout = serde_json::from_slice(&encoded).unwrap();
    restored.normalize(&[10, 20]);
    assert_eq!(restored, layout);
}

#[test]
fn recovery_remaps_runtime_ids_and_drops_failed_documents() {
    let mut layout = Layout::new(vec![10, 20, 30], Some(20));
    let right = layout.split(1, Axis::Horizontal).unwrap();
    layout.move_document(20, right, None);
    layout.remap(&HashMap::from([(10, 101), (20, 102)]));
    layout.normalize(&[101, 102, 103]);
    assert_eq!(documents(&layout), vec![101, 102, 103]);
    assert_eq!(layout.focused, right);
    assert!(layout.select(102));
    assert_eq!(layout.groups().last().unwrap().1.active, Some(102));
}

#[test]
fn malformed_membership_ids_focus_and_ratios_are_repaired() {
    let mut layout = Layout::new(vec![10, 20], Some(10));
    let right = layout.split(1, Axis::Horizontal).unwrap();
    let split = layout.root.split.as_mut().unwrap();
    split.ratio = f32::NAN;
    split.axis = Axis::None;
    split.second.id = split.first.id;
    split.first.group.as_mut().unwrap().documents = vec![10, 10, 999];
    split.first.group.as_mut().unwrap().active = Some(999);
    split.second.group.as_mut().unwrap().documents = vec![10, 20];
    layout.focused = 999;
    layout.normalize(&[10, 20, 30]);
    let split = layout.root.split.as_ref().unwrap();
    assert_eq!(split.ratio, 0.5);
    assert_eq!(split.axis, Axis::Horizontal);
    assert_ne!(split.first.id, split.second.id);
    assert_ne!(layout.focused, right);
    let mut all = documents(&layout);
    all.sort_unstable();
    assert_eq!(all, vec![10, 20, 30]);
    for (_, group) in layout.groups() {
        assert!(group.active.is_none_or(|id| group.documents.contains(&id)));
    }
}

#[test]
fn malformed_node_and_nonfinite_resize_cannot_lose_documents() {
    let mut layout = Layout {
        root: Node::default(),
        focused: 0,
    };
    layout.normalize(&[10, 20]);
    assert_eq!(documents(&layout), vec![10, 20]);
    layout.split(layout.focused, Axis::Horizontal).unwrap();
    let root = layout.root.id;
    layout.set_ratio(root, 2.0);
    assert_eq!(layout.root.split.as_ref().unwrap().ratio, 0.95);
    layout.set_ratio(root, f32::INFINITY);
    assert_eq!(layout.root.split.as_ref().unwrap().ratio, 0.95);
    assert_eq!(layout.split(999, Axis::Horizontal), None);
    assert_eq!(layout.split(layout.focused, Axis::None), None);
}

#[test]
fn reorder_in_same_group_moves_to_target_position() {
    let mut layout = Layout::new(vec![10, 20, 30], Some(10));
    assert!(layout.move_document(30, 1, Some(10)));
    assert_eq!(documents(&layout), vec![30, 10, 20]);
    assert!(layout.move_document(30, 1, Some(30)));
    assert_eq!(documents(&layout), vec![30, 10, 20]);
}

#[test]
fn closes_empty_split_group_only_when_empty_and_not_last() {
    let mut layout = Layout::new(vec![10], Some(10));
    assert!(!layout.close_empty_group(1));

    let empty = layout.split(1, Axis::Horizontal).unwrap();
    let populated = layout.groups()[0].0;
    assert!(!layout.close_empty_group(populated));
    assert!(!layout.close_empty_group(999));
    assert_eq!(layout.focused, empty);

    assert!(layout.close_empty_group(empty));
    assert_eq!(layout.groups().len(), 1);
    assert_eq!(layout.focused, populated);
    assert_eq!(layout.active(), Some(10));

    layout.remove(10);
    assert_eq!(layout.groups().len(), 1);
    assert!(!layout.close_empty_group(layout.focused));
}
