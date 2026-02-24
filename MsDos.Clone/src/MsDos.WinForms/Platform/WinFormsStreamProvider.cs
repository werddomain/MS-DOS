using MsDos.Core.Platform;

namespace MsDos.WinForms.Platform;

/// <summary>
/// WinForms implementation of IStreamProvider using the local file system.
/// </summary>
public sealed class WinFormsStreamProvider : IStreamProvider
{
    private readonly string _rootDir;

    /// <summary>In-memory file storage for imported binaries.</summary>
    private readonly Dictionary<string, byte[]> _importedFiles = new(StringComparer.OrdinalIgnoreCase);

    public WinFormsStreamProvider(string rootDir)
    {
        _rootDir = rootDir;
        if (!Directory.Exists(_rootDir))
            Directory.CreateDirectory(_rootDir);
    }

    private string ResolvePath(string path)
    {
        // Normalize DOS-style paths
        path = path.Replace('\\', '/').TrimStart('/');
        return Path.Combine(_rootDir, path);
    }

    public Task<Stream> OpenReadAsync(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/').ToUpperInvariant();
        if (_importedFiles.TryGetValue(normalized, out var data))
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));

        string fullPath = ResolvePath(path);
        return Task.FromResult<Stream>(File.OpenRead(fullPath));
    }

    public Task<Stream> OpenWriteAsync(string path)
    {
        string fullPath = ResolvePath(path);
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return Task.FromResult<Stream>(File.Create(fullPath));
    }

    public Task<Stream> OpenReadWriteAsync(string path)
    {
        string fullPath = ResolvePath(path);
        return Task.FromResult<Stream>(File.Open(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite));
    }

    public Task<bool> ExistsAsync(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/').ToUpperInvariant();
        if (_importedFiles.ContainsKey(normalized))
            return Task.FromResult(true);
        return Task.FromResult(File.Exists(ResolvePath(path)));
    }

    public Task DeleteAsync(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/').ToUpperInvariant();
        _importedFiles.Remove(normalized);
        string fullPath = ResolvePath(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath)
    {
        string fullPath = ResolvePath(directoryPath);
        if (!Directory.Exists(fullPath))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var entries = Directory.GetFileSystemEntries(fullPath)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Select(n => n!)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(entries);
    }

    public Task ImportBinaryAsync(string targetPath, byte[] data)
    {
        string normalized = targetPath.Replace('\\', '/').TrimStart('/').ToUpperInvariant();
        _importedFiles[normalized] = data;
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path)
    {
        string fullPath = ResolvePath(path);
        Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path)
    {
        string fullPath = ResolvePath(path);
        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: false);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string oldPath, string newPath)
    {
        string fullOld = ResolvePath(oldPath);
        string fullNew = ResolvePath(newPath);
        if (File.Exists(fullOld))
            File.Move(fullOld, fullNew);
        else if (Directory.Exists(fullOld))
            Directory.Move(fullOld, fullNew);
        return Task.CompletedTask;
    }

    public Task<long> GetFileSizeAsync(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/').ToUpperInvariant();
        if (_importedFiles.TryGetValue(normalized, out var data))
            return Task.FromResult((long)data.Length);

        string fullPath = ResolvePath(path);
        if (File.Exists(fullPath))
            return Task.FromResult(new FileInfo(fullPath).Length);
        return Task.FromResult(-1L);
    }
}
