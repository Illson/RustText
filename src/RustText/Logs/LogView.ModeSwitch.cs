namespace RustText.Demo.Logs;

internal sealed partial class LogView
{
    internal TextEncodingKind DetectedEncoding => _reader.Encoding;

    internal bool TryBeginModeSwitch()
    {
        if (_closed || _busy || _pageWrite is not null) return false;

        _busy = true;
        if (_pageEdit is not null && _pageEditor is not null)
        {
            _pageEditor.IsEnabled = false;
        }
        RefreshButtons();
        return true;
    }

    internal void EndModeSwitch()
    {
        if (_closed) return;

        _busy = false;
        if (_pageEdit is not null && _pageEditor is not null)
        {
            _pageEditor.IsEnabled = true;
        }
        RefreshButtons();
    }
}
