extern alias Baseline;
extern alias Candidate;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Base = Baseline::RustText.Performance.CoreAdapter;
using Test = Candidate::RustText.Performance.CoreAdapter;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args is not [var count, var size, var logPath] ||
    !int.TryParse(count, out int iterations) || iterations is < 2 or > 100 || iterations % 2 != 0 ||
    !int.TryParse(size, out int targetBytes) || targetBytes is < 2 * 1024 * 1024 or > 64 * 1024 * 1024)
{
    throw new ArgumentException("Usage: CoreBench <even iterations 2–100> <fixture bytes 2–64 MiB> <new log path>");
}

var fixture = CreateLog(logPath, targetBytes);
using (var file = File.OpenRead(logPath))
{
    Console.WriteLine($"fixture\tlog\t{file.Length}\t{Convert.ToHexString(SHA256.HashData(file))}");
}

Measure("log-index", () => Base.Index(logPath), () => Test.Index(logPath),
    result => Require(result == (fixture.Bytes, fixture.Lines), "Invalid final offset or line count."));

LogCase[] searches =
[
    new("literal-miss", "MARKER_ABSENT", false, [], fixture.Bytes, true),
    new("literal-late-hit", "ОШИБКА_КОНЦА", false, [fixture.Last], fixture.Bytes, true),
    new("literal-dense-first32", "INFO", false, fixture.Dense, fixture.DenseEnd, false),
    new("regex-literal-miss", @"^\d+.*MARKER_ABSENT", true, [], fixture.Bytes, true),
    new("regex-no-literal-miss", @"\d{12}$", true, [], fixture.Bytes, true),
    new("regex-no-literal-late-hit", @"\d{10}$", true, [fixture.Last], fixture.Bytes, true),
];
foreach (var scenario in searches)
{
    Measure(scenario.Name, () => Base.Search(logPath, scenario.Pattern, scenario.Regex),
        () => Test.Search(logPath, scenario.Pattern, scenario.Regex),
        result => Require(result.Complete == scenario.Complete && result.End == scenario.End &&
            result.Offsets.SequenceEqual(scenario.Offsets),
            $"{scenario.Name}: wrong match offsets, stop position or completion state " +
            $"(hits={result.Offsets.Length}, end={result.End}, complete={result.Complete})."));
}

int records = targetBytes / 4096;
const string record = """{"level":"Внимание","count":3,"ok":true}""";
string json = "[" + string.Join(',', Enumerable.Repeat(record, records)) + "]";
string nl = Environment.NewLine;
string formatted = $"  {{{nl}    \"level\": \"Внимание\",{nl}    \"count\": 3,{nl}    \"ok\": true{nl}  }}";
string expectedJson = $"[{nl}{string.Join($",{nl}", Enumerable.Repeat(formatted, records))}{nl}]";
Measure("json-format-string", () => Base.FormatJson(json), () => Test.FormatJson(json),
    result => Require(string.Equals(result, expectedJson, StringComparison.Ordinal), "Invalid formatted JSON."));

const string editorLine = "id=12345 status=WARNING abcdefghijklmnopqrstuvwxyz 1234567890\n";
const string query = "WARNING";
const string replacement = "OK";
string editorText = string.Concat(Enumerable.Repeat(editorLine, records));
int column = editorLine.IndexOf(query, StringComparison.Ordinal);
var ranges = Enumerable.Range(0, records).Select(i => (i * editorLine.Length + column, query.Length)).ToArray();
Measure("editor-find-all", () => Base.Find(editorText, query, records), () => Test.Find(editorText, query, records),
    result => Require(!result.TimedOut && result.Count == records && result.Ranges.AsSpan().SequenceEqual(ranges),
        "Invalid or incomplete editor search ranges."));

string expectedReplacement = editorText.Replace(query, replacement, StringComparison.Ordinal);
Measure("editor-replace-all-string", () => Base.Replace(editorText, query, replacement),
    () => Test.Replace(editorText, query, replacement),
    result => Require(result == (expectedReplacement, records), "Invalid replaced text or count."));

PrintStringIdentity("json", json);
PrintStringIdentity("editor", editorText);

void Measure<T>(string name, Func<T> baseline, Func<T> candidate, Action<T> check)
{
    check(baseline());
    check(candidate());
    for (int i = 1; i <= iterations; i++)
    {
        if (i % 2 == 1)
        {
            Sample("baseline", baseline, 1);
            Sample("candidate", candidate, 2);
        }
        else
        {
            Sample("candidate", candidate, 1);
            Sample("baseline", baseline, 2);
        }

        void Sample(string app, Func<T> operation, int order)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            T result = operation();
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            check(result);
            Console.WriteLine($"sample\t{name}\t{app}\t{i}\t{order}\t{ms:F6}\t{allocated}");
        }
    }
}

static LogFixture CreateLog(string path, int size)
{
    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    var dense = new long[32];
    long denseEnd = 0;
    long line = 0;
    var longPayload = new string('x', 4096);
    while (output.Position < size)
    {
        if (line < dense.Length) dense[line] = output.Position;
        string body = line % 67 == 0 ? longPayload : "payload=operation_ready";
        output.Write(Encoding.UTF8.GetBytes($"{line:D8} INFO worker=Север {body} flag=ok\r\n"));
        if (++line == dense.Length) denseEnd = output.Position;
    }
    long last = output.Position;
    output.Write(Encoding.UTF8.GetBytes("99999999 ERROR ОШИБКА_КОНЦА 1234567890\r\n"));
    return new(output.Position, line + 1, last, dense, denseEnd);
}

static void PrintStringIdentity(string name, string input)
{
    var bytes = Encoding.UTF8.GetBytes(input);
    Console.WriteLine($"fixture\t{name}\t{bytes.Length}\t{Convert.ToHexString(SHA256.HashData(bytes))}");
}

static void Require(bool valid, string reason)
{
    if (!valid) throw new InvalidDataException(reason);
}

record LogFixture(long Bytes, long Lines, long Last, long[] Dense, long DenseEnd);
record LogCase(string Name, string Pattern, bool Regex, long[] Offsets, long End, bool Complete);
