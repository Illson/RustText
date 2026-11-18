use super::{scroll_geometry, seek_target};

#[test]
fn estimates_visible_file_bytes_and_local_scroll_position() {
    let (visible_bytes, scroll_fraction) = scroll_geometry(100.0, 500.0, -200.0, 1_000);

    assert_eq!(visible_bytes, 200);
    assert_eq!(scroll_fraction, 0.5);
}

#[test]
fn treats_a_fully_visible_page_as_the_full_thumb() {
    let (visible_bytes, scroll_fraction) = scroll_geometry(100.0, 80.0, 0.0, 1_000);

    assert_eq!(visible_bytes, 1_000);
    assert_eq!(scroll_fraction, 0.0);
}

#[test]
fn maps_track_fraction_over_the_navigable_file_after_hidden_prefix() {
    assert_eq!(seek_target(200, 600, 0.0), 200);
    assert_eq!(seek_target(200, 600, 0.5), 500);
    assert_eq!(seek_target(200, 600, 1.0), 800);
}
