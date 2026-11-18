using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace RustText.Demo;

public partial class MainWindow : Window
{
    private static readonly string[] SampleLines =
    [
        "09:41:00  INFO   Запуск службы импорта",
        "09:41:01  INFO   Найдено файлов: 12",
        "09:41:02  DEBUG  Очередь импорта создана",
        "09:41:04  INFO   Обработан файл input-01.csv",
        "09:41:05  WARN   Пропущена пустая строка в input-02.csv",
        "09:41:07  INFO   Обработан файл input-02.csv",
        "09:41:09  ERROR  Не удалось открыть input-03.csv",
        "09:41:10  DEBUG  Запланирована повторная попытка",
        "09:41:12  INFO   Обработан файл input-03.csv",
        "09:41:13  WARN   Ответ хранилища занял 820 мс",
        "09:41:15  INFO   Индекс обновлён",
        "09:41:16  INFO   Импорт завершён"
    ];

    public MainWindow()
    {
        InitializeComponent();
        var executionMode = RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT";
        RuntimeLabel.Text = $".NET {Environment.Version.ToString(2)} · {executionMode}";
        SearchBox.TextChanged += (_, _) => RefreshLog();
        RefreshLog();
    }

    private void RefreshLog()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var matches = Array.FindAll(SampleLines, line => line.Contains(query, StringComparison.OrdinalIgnoreCase));

        LogBox.Text = string.Join(Environment.NewLine, matches);
        CountLabel.Text = $"Строки · {matches.Length} из {SampleLines.Length}";
    }
}
