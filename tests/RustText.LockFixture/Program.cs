namespace RustText.LockFixture;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not [var path, var ready, var stop, var shareDelete] || !bool.TryParse(shareDelete, out var permitDeletion))
            return 2;
        var share = permitDeletion ? FileShare.ReadWrite | FileShare.Delete : FileShare.ReadWrite;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, share);
        await File.WriteAllTextAsync(ready, "ready");
        for (var i = 0; i < 2400 && !File.Exists(stop); i++) await Task.Delay(50);
        return 0;
    }
}
