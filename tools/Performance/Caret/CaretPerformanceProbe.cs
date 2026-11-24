using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using RustText.Demo;
using Xunit;

namespace RustText.Tests;

[Collection(HeadlessCollection.Name)]
public sealed class CaretPerformanceProbe
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int InputRate = 30;
    private static readonly string Initial = string.Concat(Enumerable.Range(0, 4_000)
        .Select(i => new string('я', 5 + i % 100) + "\r\n"));

    [Fact]
    public Task Measure() => HeadlessTests.RunAsync(async _ =>
    {
        var smoke = Environment.GetEnvironmentVariable("RUSTTEXT_CARET_BENCH_SMOKE") == "1";
        var count = smoke ? 30 : 500;
        var idleMs = smoke ? 1_200 : 8_000;
        var blinkInterval = GetCaretBlinkTime();
        Assert.InRange(blinkInterval, 1u, 1_000u);
        var window = new MainWindow([]) { Width = 1080, Height = 700 };
        window.Show();
        try
        {
            await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_restoreFinished"));
            Field<DispatcherTimer>(window, "_checkpoint").Stop();
            typeof(MainWindow).GetMethod("NewTab", Private)!.Invoke(window, [Initial]);
            var tab = typeof(MainWindow).GetProperty("ActiveTab", Private)!.GetValue(window)!;
            var editor = (TextEditor)tab.GetType().GetProperty("Editor")!.GetValue(tab)!;
            var area = editor.TextArea;
            var view = area.TextView;

            async Task Position()
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await HeadlessTests.WaitForAsync(() => view.VisualLinesValid && view.Bounds.Width > 0);
                window.Activate();
                area.Focus();
                editor.CaretOffset = editor.Document.TextLength;
                area.Caret.BringCaretToView();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.True(window.IsActive, "The headless window is not active.");
                Assert.True(area.IsKeyboardFocusWithin, "The editor does not have keyboard focus.");
                Assert.True(view.VisualLinesValid, "The editor has no valid visual lines.");
                Assert.Contains(view.VisualLines, line => line.FirstDocumentLine.LineNumber == area.Caret.Line);
            }

            await Position();
            for (var i = 0; i < 100; i++) window.KeyTextInput(i % 5 == 4 ? "\n" : "ж");
            editor.Text = Initial;
            await Position();
            await Task.Delay(250);
            using var process = Process.GetCurrentProcess();
            var delays = new double[count];
            var latencies = new double[count];
            var idleCpuStart = process.TotalProcessorTime.TotalMilliseconds;
            var idleAllocStart = GC.GetTotalAllocatedBytes(true);
            var idleClock = Stopwatch.StartNew();
            await Task.Delay(idleMs);
            var idleElapsed = idleClock.Elapsed.TotalMilliseconds;
            var idleCpuMs = process.TotalProcessorTime.TotalMilliseconds - idleCpuStart;
            var idleAllocated = GC.GetTotalAllocatedBytes(true) - idleAllocStart;

            var editCpuStart = process.TotalProcessorTime.TotalMilliseconds;
            var editAllocStart = GC.GetTotalAllocatedBytes(true);
            var editClock = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                var deadline = i * 1000d / InputRate;
                var delay = deadline - editClock.Elapsed.TotalMilliseconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay));
                delays[i] = editClock.Elapsed.TotalMilliseconds - deadline;
                var start = Stopwatch.GetTimestamp();
                window.KeyTextInput(i % 5 == 4 ? "\n" : "ж");
                latencies[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            var editElapsed = editClock.Elapsed.TotalMilliseconds;
            var editCpuMs = process.TotalProcessorTime.TotalMilliseconds - editCpuStart;
            var editAllocated = GC.GetTotalAllocatedBytes(true) - editAllocStart;

            var expected = Initial + string.Concat(Enumerable.Repeat("жжжж\r\n", count / 5));
            Assert.Equal(expected, editor.Text);
            Assert.Equal(4_001 + count / 5, editor.Document.LineCount);
            Assert.Equal(expected.Length, editor.CaretOffset);
            Assert.True(delays.Max() < 100, "The input stream missed its schedule by 100 ms.");
            Assert.InRange(idleElapsed, idleMs - 30, idleMs + 100);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            var layer = view.Layers.FirstOrDefault(control => control.GetType().Name == "EditorCaret");
            var motion = (bool?)layer?.GetType().GetProperty("MotionEnabled", Private)?.GetValue(layer);
            Assert.Equal(Environment.GetEnvironmentVariable("RUSTTEXT_CARET_BENCH_APP") == "candidate", motion == true);
            var result = new
            {
                initialBytes = Encoding.UTF8.GetByteCount(Initial),
                initialSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Initial))),
                inputCount = count, inputRate = InputRate, expectedLines = editor.Document.LineCount,
                motionEnabled = motion, windowsBlinkIntervalMs = blinkInterval,
                idleElapsedMs = idleElapsed, idleCpuMs, idleManagedBytes = idleAllocated,
                editElapsedMs = editElapsed, editCpuMs, editManagedBytes = editAllocated,
                maxInputDelayMs = delays.Max(), inputTurnaroundMs = latencies,
            };
            await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("RUSTTEXT_CARET_BENCH_OUT")!,
                JsonSerializer.Serialize(result));
        }
        finally
        {
            window.Close();
            await HeadlessTests.WaitForAsync(() => Field<bool>(window, "_closed"));
        }
    });

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();
}
