use serde::de::IgnoredAny;

pub const MAX_INDENT_DEPTH: usize = 1024;
pub const MAX_JSON_BYTES: usize = 64 * 1024 * 1024;

pub fn format(source: &str, newline: &str) -> Result<String, String> {
    format_with_limit(source, newline, MAX_JSON_BYTES)
}

fn format_with_limit(source: &str, newline: &str, limit: usize) -> Result<String, String> {
    serde_json::from_str::<IgnoredAny>(source).map_err(|error| {
        format!(
            "Ошибка JSON, строка {}, столбец {}: {error}",
            error.line().max(1),
            error.column().max(1),
        )
    })?;

    let mut output = Vec::with_capacity(source.len().min(limit));
    let mut bytes = source.bytes().enumerate().peekable();
    let mut depth = 0;
    let mut previous = b' ';
    while let Some((index, byte)) = bytes.next() {
        if byte.is_ascii_whitespace() {
            continue;
        }
        check_size(&output, 1, limit)?;
        match byte {
            b'"' => {
                while let Some((end, part)) = bytes.next() {
                    match part {
                        b'"' => {
                            let literal = &source.as_bytes()[index..=end];
                            check_size(&output, literal.len(), limit)?;
                            output.extend_from_slice(literal);
                            break;
                        }
                        b'\\' => {
                            bytes.next();
                        }
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

                while bytes
                    .next_if(|(_, byte)| byte.is_ascii_whitespace())
                    .is_some()
                {}
                if !matches!(bytes.peek(), Some((_, b'}' | b']'))) {
                    indent(&mut output, newline, depth, limit)?;
                }
            }
            b'}' | b']' => {
                depth -= 1;
                if !matches!(previous, b'{' | b'[') {
                    indent(&mut output, newline, depth, limit)?;
                }
                check_size(&output, 1, limit)?;
                output.push(byte);
            }
            b',' => {
                output.push(byte);
                indent(&mut output, newline, depth, limit)?;
            }
            b':' => {
                check_size(&output, 2, limit)?;
                output.extend_from_slice(b": ");
            }
            _ => output.push(byte),
        }
        previous = byte;
    }

    if source.ends_with(['\r', '\n']) {
        check_size(&output, newline.len(), limit)?;
        output.extend_from_slice(newline.as_bytes());
    }
    Ok(String::from_utf8(output).expect("formatting only changes ASCII whitespace"))
}

fn indent(output: &mut Vec<u8>, newline: &str, depth: usize, limit: usize) -> Result<(), String> {
    check_size(output, newline.len().saturating_add(depth * 2), limit)?;
    output.extend_from_slice(newline.as_bytes());
    output.resize(output.len() + depth * 2, b' ');
    Ok(())
}

fn check_size(output: &[u8], additional: usize, limit: usize) -> Result<(), String> {
    if additional > limit - output.len() {
        Err(format!("Форматированный JSON превышает лимит {limit} байт"))
    } else {
        Ok(())
    }
}

#[cfg(test)]
#[path = "../tests/unit/json_format.rs"]
mod tests;
