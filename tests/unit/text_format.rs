use super::*;

#[test]
fn saves_cyrillic_without_changing_original_format() {
    let utf16 = [0xff, 0xfe, 0x2f, 0x04, 0x0d, 0x00, 0x0a, 0x00];
    let (text, format) = TextFormat::decode(&utf16, false).unwrap();
    assert_eq!(text, "Я\r\n");
    assert_eq!(format.encode(&text).unwrap(), utf16);
    assert_eq!(
        format.encode("Я\r\nБ\n").unwrap(),
        [
            0xff, 0xfe, 0x2f, 0x04, 0x0d, 0x00, 0x0a, 0x00, 0x11, 0x04, 0x0d, 0x00, 0x0a, 0x00
        ]
    );

    let (text, format) = TextFormat::decode(b"\xcf\xf0\xe8\xe2\xe5\xf2", true).unwrap();
    assert_eq!(text, "Привет");
    assert_eq!(format.encode(&text).unwrap(), b"\xcf\xf0\xe8\xe2\xe5\xf2");
    assert!(format.encode("Привет 🦀").is_err());
}

#[test]
fn keeps_utf8_and_big_endian_utf16_boms_and_rejects_invalid_sequences() {
    let utf8 = b"\xef\xbb\xbf\xd1\x91\xd0\xb6";
    let (text, format) = TextFormat::decode(utf8, false).unwrap();
    assert_eq!(text, "ёж");
    assert_eq!(format.encoding.label(), "UTF-8 BOM");
    assert_eq!(format.encode(&text).unwrap(), utf8);

    let big_endian = [0xfe, 0xff, 0x04, 0x2f, 0x00, 0x0a];
    let (text, format) = TextFormat::decode(&big_endian, false).unwrap();
    assert_eq!(text, "Я\n");
    assert_eq!(format.encoding.label(), "UTF-16 BE");
    assert_eq!(format.encode(&text).unwrap(), big_endian);
    assert!(TextFormat::decode(&big_endian[..5], false).is_err());
    assert!(TextFormat::decode(&[0xff], false).is_err());
    assert!(TextFormat::decode(b"a\0b", false).is_err());
}

#[test]
fn preserves_mixed_endings_and_roundtrips_old_mac_endings() {
    let mixed = "один\r\nдва\nтри\rчетыре";
    let (decoded, format) = TextFormat::decode(mixed.as_bytes(), false).unwrap();
    assert_eq!(format.line_endings_label(), "Смешанные");
    assert_eq!(decoded, mixed);
    assert_eq!(format.encode(&decoded).unwrap(), mixed.as_bytes());

    let (decoded, format) = TextFormat::decode(b"a\rb\r", false).unwrap();
    assert_eq!(format.line_endings_label(), "CR");
    assert_eq!(decoded, "a\nb\n");
    assert_eq!(format.encode(&decoded).unwrap(), b"a\rb\r");
}
