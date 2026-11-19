namespace RustText.Demo.Editor;

internal sealed class ReplacingTextReader(TextReader source, int start, int length, string replacement) : TextReader
{
    private enum ReadPhase { Prefix, Skip, Insert, Suffix, Done }

    private readonly char[] _discard = new char[4096];
    private int _prefixRemaining = Math.Max(0, start);
    private int _skipRemaining = Math.Max(0, length);
    private int _replacementOffset;
    private ReadPhase _phase;

    public override int Read(Span<char> buffer)
    {
        var written = 0;
        while (!buffer.IsEmpty && _phase != ReadPhase.Done)
        {
            switch (_phase)
            {
                case ReadPhase.Prefix:
                {
                    var count = source.Read(buffer[..Math.Min(buffer.Length, _prefixRemaining)]);
                    if (count == 0)
                    {
                        _prefixRemaining = 0;
                        _phase = ReadPhase.Skip;
                        break;
                    }
                    _prefixRemaining -= count;
                    written += count;
                    buffer = buffer[count..];
                    if (_prefixRemaining == 0) _phase = ReadPhase.Skip;
                    break;
                }
                case ReadPhase.Skip:
                {
                    if (_skipRemaining == 0)
                    {
                        _phase = ReadPhase.Insert;
                        break;
                    }
                    var count = source.Read(_discard.AsSpan(0, Math.Min(_discard.Length, _skipRemaining)));
                    if (count == 0)
                    {
                        _skipRemaining = 0;
                        _phase = ReadPhase.Insert;
                    }
                    else
                    {
                        _skipRemaining -= count;
                    }
                    break;
                }
                case ReadPhase.Insert:
                {
                    var count = Math.Min(buffer.Length, replacement.Length - _replacementOffset);
                    if (count == 0)
                    {
                        _phase = ReadPhase.Suffix;
                        break;
                    }
                    replacement.AsSpan(_replacementOffset, count).CopyTo(buffer);
                    _replacementOffset += count;
                    written += count;
                    buffer = buffer[count..];
                    if (_replacementOffset == replacement.Length) _phase = ReadPhase.Suffix;
                    break;
                }
                case ReadPhase.Suffix:
                {
                    var count = source.Read(buffer);
                    written += count;
                    if (count == 0) _phase = ReadPhase.Done;
                    return written;
                }
            }
        }
        return written;
    }

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }
}
