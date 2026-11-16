use serde::de::IgnoredAny;

pub const MAX_INDENT_DEPTH: usize = 1024;

pub fn format(source: &str, newline: &str) -> Result<String, String> {
    serde_json::from_str::<IgnoredAny>(source).map_err(|error| {
        format!(
            "Ошибка JSON, строка {}, столбец {}: {error}",
            error.line().max(1),
            error.column().max(1),
        )
    })?;

    let mut output = Vec::with_capacity(source.len());
    let mut bytes = source.bytes().peekable();
    let mut depth = 0;
    let mut previous = b' ';
    while let Some(byte) = bytes.next() {
        match byte {
            b' ' | b'\t' | b'\r' | b'\n' => continue,
            b'"' => {
                output.push(byte);
                while let Some(part) = bytes.next() {
                    output.push(part);
                    match part {
                        b'\\' => output.extend(bytes.next()),
                        b'"' => break,
                        _ => {}
                    }
                }
            }
            b'{' | b'[' => {
                output.push(byte);
                depth += 1;
                if depth > MAX_INDENT_DEPTH {
                    return Err(format!(
                        "Глубина вложенности JSON превышает {MAX_INDENT_DEPTH}"
                    ));
                }

                while bytes.next_if(|byte| byte.is_ascii_whitespace()).is_some() {}
                if !matches!(bytes.peek(), Some(b'}' | b']')) {
                    indent(&mut output, newline, depth);
                }
            }
            b'}' | b']' => {
                depth -= 1;
                if !matches!(previous, b'{' | b'[') {
                    indent(&mut output, newline, depth);
                }
                output.push(byte);
            }
            b',' => {
                output.push(byte);
                indent(&mut output, newline, depth);
            }
            b':' => output.extend_from_slice(b": "),
            _ => output.push(byte),
        }
        previous = byte;
    }

    if source.ends_with(['\r', '\n']) {
        output.extend_from_slice(newline.as_bytes());
    }
    Ok(String::from_utf8(output).expect("formatting only changes ASCII whitespace"))
}

fn indent(output: &mut Vec<u8>, newline: &str, depth: usize) {
    output.extend_from_slice(newline.as_bytes());
    output.resize(output.len() + depth * 2, b' ');
}

#[cfg(test)]
mod tests {
    use super::format;

    #[test]
    fn formats_without_rewriting_lexemes_or_reordering_keys() {
        let source = r#"{"z":123456789012345678901234567890,"a":1e9000,"z":"\"/ёж\u2603","arr":[{},[],-0.00]}"#;
        assert_eq!(
            format(source, "\n").unwrap(),
            concat!(
                "{\n",
                "  \"z\": 123456789012345678901234567890,\n",
                "  \"a\": 1e9000,\n",
                "  \"z\": \"\\\"/ёж\\u2603\",\n",
                "  \"arr\": [\n    {},\n    [],\n    -0.00\n  ]\n}"
            )
        );
    }

    #[test]
    fn reports_invalid_json_with_a_location() {
        let error = format("{\n \"a\": [1,]\n}", "\n").unwrap_err();
        assert!(error.contains("строка 2, столбец 10"), "{error}");
        assert!(format(r#"{"a":"\x00"}"#, "\n").is_err());
        assert!(format("01", "\n").is_err());
        assert!(format("{} false", "\n").is_err());
    }
}
