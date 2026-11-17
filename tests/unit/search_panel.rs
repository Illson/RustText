use super::{invalidate_document_highlights, sync_all_highlights, visible_highlight_tabs};
use crate::layout::{Axis, Layout};

#[test]
fn silent_document_replacement_invalidates_its_cached_and_current_hits_only() {
    let mut all = vec![(1, "old same-length cache"), (2, "other document cache")];
    let mut current = Some((1, "old current match"));
    let mut disposed = Vec::new();
    invalidate_document_highlights(1, &mut all, &mut current, |value| disposed.push(value));
    assert_eq!(all, vec![(2, "other document cache")]);
    assert!(current.is_none());
    assert_eq!(disposed, vec!["old same-length cache", "old current match"]);
    all.push((1, "replacement cache"));
    current = Some((2, "other current match"));
    invalidate_document_highlights(1, &mut all, &mut current, |value| disposed.push(value));
    assert_eq!(all, vec![(2, "other document cache")]);
    assert_eq!(current, Some((2, "other current match")));
    assert_eq!(disposed.last(), Some(&"replacement cache"));
}

#[test]
fn sync_all_highlights_rebuilds_visible_tabs_after_reset_and_clears_when_empty_group_active() {
    let mut layout = Layout::new(vec![10, 20], Some(10));
    let right = layout.split(1, Axis::Horizontal).unwrap();
    assert!(layout.move_document(20, right, None));

    assert_eq!(
        visible_highlight_tabs(&layout, 0, false, true),
        Vec::<u64>::new()
    );
    assert_eq!(
        visible_highlight_tabs(&layout, 10, true, true),
        Vec::<u64>::new()
    );
    assert_eq!(visible_highlight_tabs(&layout, 10, false, false), vec![10]);
    assert_eq!(
        visible_highlight_tabs(&layout, 10, false, true),
        vec![10, 20]
    );

    let mut all = vec![(10, "old-10"), (20, "old-20")];
    let mut disposed = Vec::new();
    for (_, h) in all.drain(..) {
        disposed.push(h);
    }
    let visible = visible_highlight_tabs(&layout, 10, false, true);
    sync_all_highlights(
        &visible,
        &mut all,
        &mut disposed,
        |disposed, h| disposed.push(h),
        |_, id| Some(if id == 10 { "new-10" } else { "new-20" }),
    );
    assert_eq!(all, vec![(10, "new-10"), (20, "new-20")]);
    assert_eq!(disposed, vec!["old-10", "old-20"]);
}
