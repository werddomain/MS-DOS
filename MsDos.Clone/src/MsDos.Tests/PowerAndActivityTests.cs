using MsDos.Core;
using MsDos.Core.Dos;
using MsDos.Core.Platform;
using System.Collections.Concurrent;

namespace MsDos.Tests;

public class PowerAndActivityTests
{
    // ── Test doubles (matching DosMachineTests pattern) ──

    private sealed class TestRenderer : IGraphicsRenderer
    {
        public int Width => 640;
        public int Height => 400;
        public void SetMode(VideoMode mode) { }
        public void DrawCharacter(int col, int row, char character, byte foreground, byte background) { }
        public void DrawPixel(int x, int y, byte colorIndex) { }
        public void Clear(byte backgroundColorIndex) { }
        public void SetCursorPosition(int col, int row) { }
        public void SetCursorVisible(bool visible) { }
        public void ScrollUp(int lines, byte backgroundColorIndex) { }
        public void ScrollDown(int lines, byte backgroundColorIndex) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class TestEventRegistry : IEventRegistry
    {
        private readonly ConcurrentQueue<DosKeyEventArgs> _keys = new();
        private readonly SemaphoreSlim _sem = new(0);

        public event Action<DosKeyEventArgs>? KeyDown;
        public event Action<DosKeyEventArgs>? KeyUp;
        public event Action<DosMouseEventArgs>? MouseMove;
        public event Action<DosMouseEventArgs>? MouseDown;
        public event Action<DosMouseEventArgs>? MouseUp;

        public bool IsKeyAvailable => !_keys.IsEmpty;

        public async Task<DosKeyEventArgs> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            await _sem.WaitAsync(cancellationToken);
            if (_keys.TryDequeue(out var key)) return key;
            return new DosKeyEventArgs();
        }
    }

    private sealed class TestStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public Task<Stream> OpenReadAsync(string path)
        {
            if (_files.TryGetValue(path, out var data))
                return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
            throw new FileNotFoundException(path);
        }

        public Task<Stream> OpenWriteAsync(string path)
        {
            var ms = new MemoryStream();
            _files[path] = Array.Empty<byte>();
            return Task.FromResult<Stream>(ms);
        }

        public Task<Stream> OpenReadWriteAsync(string path) =>
            Task.FromResult<Stream>(new MemoryStream(_files.ContainsKey(path) ? _files[path] : Array.Empty<byte>()));

        public Task<bool> ExistsAsync(string path) => Task.FromResult(_files.ContainsKey(path));
        public Task DeleteAsync(string path) { _files.Remove(path); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.ToList());
        public Task ImportBinaryAsync(string targetPath, byte[] data) { _files[targetPath] = data; return Task.CompletedTask; }
        public Task CreateDirectoryAsync(string path) => Task.CompletedTask;
        public Task DeleteDirectoryAsync(string path) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath) { if (_files.Remove(oldPath, out var d)) _files[newPath] = d; return Task.CompletedTask; }
        public Task<long> GetFileSizeAsync(string path) =>
            Task.FromResult(_files.TryGetValue(path, out var d) ? (long)d.Length : -1L);
    }

    private static DosMachine CreateMachine() =>
        new DosMachine(new TestRenderer(), new TestEventRegistry(), new TestStreamProvider());

    // ── Power On/Off tests ──

    [Fact]
    public void PowerOff_WhenNotPowered_DoesNotCrash()
    {
        var machine = CreateMachine();
        machine.PowerOff(); // Should not throw
        Assert.False(machine.IsPoweredOn);
    }

    // ── Disk activity tests ──

    [Fact]
    public void NotifyDiskActivity_SetsDiskActive()
    {
        var machine = CreateMachine();
        bool activated = false;
        machine.OnDiskActivity += active => { if (active) activated = true; };

        machine.NotifyDiskActivity();

        Assert.True(machine.IsDiskActive);
        Assert.True(activated);
    }

    [Fact]
    public void PumpActivityLeds_ClearsDiskAfterTimeout()
    {
        var machine = CreateMachine();
        machine.NotifyDiskActivity();
        Assert.True(machine.IsDiskActive);

        Thread.Sleep(200); // Wait for timeout
        machine.PumpActivityLeds();

        Assert.False(machine.IsDiskActive);
    }

    // ── File Upload/Download tests ──

    [Fact]
    public async Task UploadFileToDrive_ConvertsToShortName()
    {
        var machine = CreateMachine();
        byte[] testData = { 0x41, 0x42, 0x43 };

        string? dosName = await machine.UploadFileToDriveAsync('C', "my_long_document.text", testData);

        Assert.Equal("MY_LONG_.TEX", dosName);
    }

    [Fact]
    public async Task UploadFileToDrive_UnmountedDrive_ReturnsNull()
    {
        var machine = CreateMachine();
        string? dosName = await machine.UploadFileToDriveAsync('D', "test.txt", new byte[] { 0x41 });
        Assert.Null(dosName);
    }

    [Fact]
    public async Task DownloadFileFromDrive_NotMounted_ReturnsNull()
    {
        var machine = CreateMachine();
        byte[]? data = await machine.DownloadFileFromDriveAsync('D', "test.txt");
        Assert.Null(data);
    }

    [Fact]
    public async Task UploadThenDownload_RoundTrips()
    {
        var machine = CreateMachine();
        byte[] testData = { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"

        string? dosName = await machine.UploadFileToDriveAsync('C', "hello.txt", testData);
        Assert.NotNull(dosName);

        byte[]? downloaded = await machine.DownloadFileFromDriveAsync('C', dosName!);
        Assert.NotNull(downloaded);
        Assert.Equal(testData, downloaded);
    }

    [Fact]
    public async Task ListDriveFiles_UnmountedDrive_ReturnsNull()
    {
        var machine = CreateMachine();
        var files = await machine.ListDriveFilesAsync('D');
        Assert.Null(files);
    }

    [Fact]
    public async Task ListDriveFiles_AfterUpload_ContainsFile()
    {
        var machine = CreateMachine();
        await machine.UploadFileToDriveAsync('C', "test.com", new byte[] { 0xC3 });

        var files = await machine.ListDriveFilesAsync('C');
        Assert.NotNull(files);
        Assert.True(files!.Count >= 1);
    }
}
