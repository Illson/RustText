# RustText

Текстовый редактор для Windows x64 на Rust и GPUI Kit. Текущая сборка поддерживает обычное редактирование файлов до 50 МиБ.

## Запуск

Нужны Rust с целью `x86_64-pc-windows-msvc`, инструменты C++ и Windows SDK из Visual Studio, а также CMake в `PATH`. Локальная сборка проверена с Rust 1.100.0 и CMake 4.5.0.

```powershell
cargo run --locked
cargo run --locked -- "C:\путь\к\файлу.txt"
```

- `Ctrl+N` создаёт вкладку, `Ctrl+O` открывает файлы. Файлы также можно передать аргументами запуска или перетащить в окно.
- `Ctrl+S` сохраняет документ, `Ctrl+Shift+S` открывает диалог «Сохранить как».
- `Ctrl+W` закрывает вкладку, `Ctrl+Shift+T` повторно открывает последний закрытый файл в текущем запуске.
- Редактор открывает UTF-8 и UTF-16 с BOM автоматически. Кнопка «1251» открывает файлы в Windows-1251.

## Проверка

```powershell
cargo fmt --all -- --check
cargo test --locked
cargo clippy --locked --all-targets -- -D warnings
```
