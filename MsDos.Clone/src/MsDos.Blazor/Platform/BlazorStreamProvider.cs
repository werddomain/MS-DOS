using MsDos.Core.Platform;

namespace MsDos.Blazor.Platform;

/// <summary>
/// Blazor WebAssembly implementation of IStreamProvider using in-memory storage.
/// Files are imported via browser file upload and stored in a dictionary.
/// </summary>
public sealed class BlazorStreamProvider : IStreamProvider
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public Task<Stream> OpenReadAsync(string path)
    {
        string key = Normalize(path);
        if (_files.TryGetValue(key, out var data))
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        throw new FileNotFoundException($"File not found: {path}");
    }

    public Task<Stream> OpenWriteAsync(string path)
    {
        string key = Normalize(path);
        var ms = new CallbackMemoryStream(data => _files[key] = data);
        return Task.FromResult<Stream>(ms);
    }

    public Task<Stream> OpenReadWriteAsync(string path)
    {
        string key = Normalize(path);
        var existing = _files.TryGetValue(key, out var data) ? data : Array.Empty<byte>();
        var ms = new CallbackMemoryStream(d => _files[key] = d);
        ms.Write(existing, 0, existing.Length);
        ms.Position = 0;
        return Task.FromResult<Stream>(ms);
    }

    public Task<bool> ExistsAsync(string path)
    {
        return Task.FromResult(_files.ContainsKey(Normalize(path)));
    }

    public Task DeleteAsync(string path)
    {
        _files.Remove(Normalize(path));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath)
    {
        string prefix = Normalize(directoryPath);
        if (!prefix.EndsWith("/")) prefix += "/";
        var entries = _files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..])
            .Where(k => !k.Contains('/'))
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(entries);
    }

    public Task ImportBinaryAsync(string targetPath, byte[] data)
    {
        _files[Normalize(targetPath)] = data;
        return Task.CompletedTask;
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/').ToUpperInvariant();

    /// <summary>MemoryStream that calls back with contents on dispose.</summary>
    private sealed class CallbackMemoryStream : MemoryStream
    {
        private readonly Action<byte[]> _onClose;
        public CallbackMemoryStream(Action<byte[]> onClose) => _onClose = onClose;
        protected override void Dispose(bool disposing)
        {
            if (disposing) _onClose(ToArray());
            base.Dispose(disposing);
        }
    }
}
