use super::{SAMPLE_BYTES, SAMPLES, drag_fraction, sample_file, sample_offset, thumb_grab_offset};
use crate::text_format::TextEncoding;

#[test]
fn samples_cover_both_ends_without_overflow() {
    assert_eq!(sample_offset(u64::MAX, 0, 160), 0);
    assert_eq!(sample_offset(u64::MAX, 159, 160), u64::MAX);
    assert_eq!(sample_offset(0, 0, 1), 0);
}

#[test]
fn log_preview_reads_bounded_windows_after_hidden_prefix() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("sample.log");
    let text = format!("hidden\n{}", "record with text\n".repeat(100_000));
    std::fs::write(&path, &text).unwrap();
    let samples = sample_file(&path, TextEncoding::Utf8, 7, text.len() as u64).unwrap();
    assert_eq!(samples.len(), SAMPLES);
    assert_eq!(samples[0], "record with text");
    assert!(
        samples
            .iter()
            .all(|sample| sample.len() <= SAMPLE_BYTES * 3)
    );
    assert!(samples.iter().all(|sample| !sample.contains("hidden")));
}

#[test]
fn preview_handles_empty_and_utf16_files() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("sample.log");
    std::fs::write(&path, []).unwrap();
    assert!(
        sample_file(&path, TextEncoding::Utf8, 0, 0)
            .unwrap()
            .is_empty()
    );
    let bytes: Vec<_> = "строка\n"
        .encode_utf16()
        .flat_map(u16::to_le_bytes)
        .collect();
    std::fs::write(&path, &bytes).unwrap();
    let samples = sample_file(&path, TextEncoding::Utf16Le, 0, bytes.len() as u64).unwrap();
    assert_eq!(samples[0], "строка");
}

#[test]
fn thumb_drag_preserves_grab_offset_and_track_click_centers_thumb() {
    let height = 400.0;
    let thumb_height = 80.0;
    let thumb_top = 80.0;

    let grab = thumb_grab_offset(140.0, thumb_top, thumb_height);
    assert_eq!(grab, 60.0);
    assert_eq!(drag_fraction(140.0, grab, height, thumb_height), 0.25);
    assert_eq!(drag_fraction(220.0, grab, height, thumb_height), 0.5);
    assert_eq!(drag_fraction(500.0, grab, height, thumb_height), 1.0);
    assert_eq!(drag_fraction(10.0, grab, height, thumb_height), 0.0);

    let track_grab = thumb_grab_offset(240.0, thumb_top, thumb_height);
    assert_eq!(track_grab, 40.0);
    assert_eq!(
        drag_fraction(240.0, track_grab, height, thumb_height),
        0.625
    );

    assert_eq!(
        drag_fraction(120.0, thumb_grab_offset(120.0, 0.0, height), height, height),
        0.0
    );
}
