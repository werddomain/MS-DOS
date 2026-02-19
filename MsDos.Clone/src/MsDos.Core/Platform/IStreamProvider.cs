namespace MsDos.Core.Platform;

/// <summary>
/// Abstraction for reading and writing byte streams. Used for file I/O,
/// disk image access, and binary file import. Implementations provide
/// platform-specific stream handling (file system, IndexedDB, etc.).
/// </summary>
public interface IStreamProvider
{
    /// <summary>Open a stream for reading from the given path/identifier.</summary>
    Task<Stream> OpenReadAsync(string path);

    /// <summary>Open a stream for writing to the given path/identifier.</summary>
    Task<Stream> OpenWriteAsync(string path);

    /// <summary>Open a stream for both reading and writing.</summary>
    Task<Stream> OpenReadWriteAsync(string path);

    /// <summary>Check if a resource at the given path exists.</summary>
    Task<bool> ExistsAsync(string path);

    /// <summary>Delete the resource at the given path.</summary>
    Task DeleteAsync(string path);

    /// <summary>List entries in a directory path (files and subdirectories).</summary>
    Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath);

    /// <summary>
    /// Import a binary file (e.g., a COM or EXE) from external data.
    /// The data is provided as a byte array along with a target path/name.
    /// </summary>
    Task ImportBinaryAsync(string targetPath, byte[] data);
}
