use super::{
    MAX_EDITOR_DRAFT_BYTES, RecoverySearchAction, SearchRecoveryState, Stage,
    invalidate_document_highlights, recovery_search_action, search_recovery_state,
    sync_all_highlights, visible_highlight_tabs,
};
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

#[test]
fn all_tabs_search_waits_for_an_active_restore_before_starting_another() {
    assert_eq!(
        recovery_search_action([SearchRecoveryState::Queued(2), SearchRecoveryState::Loading,]),
        RecoverySearchAction::Wait,
    );
    assert_eq!(
        recovery_search_action([
            SearchRecoveryState::Queued(2),
            SearchRecoveryState::Queued(3),
        ]),
        RecoverySearchAction::Start(2),
    );
}

#[test]
fn all_tabs_search_reports_failed_restores_but_treats_log_previews_as_logs() {
    let failed = search_recovery_state(2, &Stage::Failed("Read error".into()), None);
    let preview = search_recovery_state(3, &Stage::Preview, None);
    assert_eq!(
        recovery_search_action([failed, preview].into_iter().flatten()),
        RecoverySearchAction::Ready { incomplete_tabs: 1 },
    );
    assert_eq!(
        recovery_search_action(preview),
        RecoverySearchAction::Ready { incomplete_tabs: 0 },
    );
}

#[test]
fn all_tabs_search_skips_oversized_drafts_before_loading_them() {
    let oversized = search_recovery_state(2, &Stage::Queued, Some(512 * 1024 * 1024));
    let eligible = search_recovery_state(3, &Stage::Queued, Some(MAX_EDITOR_DRAFT_BYTES));

    assert_eq!(
        recovery_search_action([oversized, eligible].into_iter().flatten()),
        RecoverySearchAction::Start(3),
    );
    assert_eq!(
        recovery_search_action(oversized),
        RecoverySearchAction::Ready { incomplete_tabs: 0 },
    );
}
