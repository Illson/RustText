use gpui_kit::base::input::RangeDecorationStyle;

use super::decorations_at;

#[test]
fn find_highlight_pulses_then_keeps_only_the_frame() {
    let initial = decorations_at(4..9, 0.0);
    let pulse = decorations_at(4..9, 0.35);
    let settled = decorations_at(4..9, 1.0);

    assert_eq!(initial[1].style(), RangeDecorationStyle::Fill);
    assert!(pulse[1].color().unwrap().a > initial[1].color().unwrap().a);
    assert_eq!(settled.len(), 1);
    assert_eq!(settled[0].style(), RangeDecorationStyle::Frame);
    assert_eq!(settled[0].range(), &(4..9));
}
