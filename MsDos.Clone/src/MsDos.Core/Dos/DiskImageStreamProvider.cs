using MsDos.Core.Platform;

namespace MsDos.Core.Dos;

/// <summary>
/// Wraps a <see cref="DiskImageLoader"/> as an <see cref="IStreamProvider"/>,
/// making the files on a disk image accessible through the standard DOS file I/O path.
/// Read-only: supports OpenRead, Exists, and ListEntries for files found in the FAT root directory.
/// </summary>
public sealed class DiskImageStreamProvider : IStreamProvider
{
    private readonly DiskImageLoader _disk;
    private readonly EmulatorLog _log;

    public DiskImageStreamProvider(DiskImageLoader disk, EmulatorLog log)
    {
        _disk = disk;
        _log = log;
    }

    public Task<Stream> OpenReadAsync(string path)
    {
        string name = NormalizePath(path);
        var entry = FindFile(name);
        if (entry == null)
            throw new FileNotFoundException($"File not found on disk image: {path}");

        byte[]? data = _disk.ReadFile(entry.Value);
        if (data == null)
            throw new IOException($"Could not read file from disk image: {path}");

        _log.Debug("DiskImage", $"Read file '{name}' ({data.Length} bytes)");
        return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
    }

    public Task<Stream> OpenWriteAsync(string path)
    {
        _log.Warn("DiskImage", "Write operations not supported on disk images");
        throw new NotSupportedException("Disk images are read-only");
    }

    public Task<Stream> OpenReadWriteAsync(string path)
    {
        _log.Warn("DiskImage", "Write operations not supported on disk images");
        throw new NotSupportedException("Disk images are read-only");
    }

    public Task<bool> ExistsAsync(string path)
    {
        string name = NormalizePath(path);
        return Task.FromResult(FindFile(name) != null);
    }

    public Task DeleteAsync(string path)
    {
        throw new NotSupportedException("Disk images are read-only");
    }

    public Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath)
    {
        var entries = _disk.ListRootDirectory();
        var names = entries
            .Where(e => !e.IsVolumeLabel)
            .Select(e => e.Name)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public Task ImportBinaryAsync(string targetPath, byte[] data)
    {
        throw new NotSupportedException("Cannot write to disk images");
    }

    public Task CreateDirectoryAsync(string path)
    {
        throw new NotSupportedException("Cannot modify disk images");
    }

    public Task DeleteDirectoryAsync(string path)
    {
        throw new NotSupportedException("Cannot modify disk images");
    }

    public Task RenameAsync(string oldPath, string newPath)
    {
        throw new NotSupportedException("Cannot modify disk images");
    }

    public Task<long> GetFileSizeAsync(string path)
    {
        string name = NormalizePath(path);
        var entry = FindFile(name);
        if (entry != null)
            return Task.FromResult((long)entry.Value.Size);
        return Task.FromResult(-1L);
    }

    private DiskFileEntry? FindFile(string name)
    {
        var entries = _disk.ListRootDirectory();
        foreach (var entry in entries)
        {
            if (entry.IsVolumeLabel) continue;
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    private static string NormalizePath(string path)
    {
        // Strip drive letter and path separators
        path = path.Replace('/', '\\');
        if (path.Length >= 2 && path[1] == ':')
            path = path[2..];
        path = path.TrimStart('\\');

        // For root dir listing, just return the filename
        int lastSep = path.LastIndexOf('\\');
        if (lastSep >= 0)
            path = path[(lastSep + 1)..];

        return path.ToUpperInvariant();
    }
}
