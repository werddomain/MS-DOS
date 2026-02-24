using MsDos.Core;
using MsDos.Core.Platform;
using System.Collections.Concurrent;
using System.Text;

namespace MsDos.Tests;

public class CommandShellTests
{
    private sealed class TestRenderer : IGraphicsRenderer
    {
        public int Width => 640;
        public int Height => 400;
        public VideoMode CurrentMode { get; private set; }

        private readonly List<char> _chars = new();
        public string Output => new string(_chars.ToArray());

        public void SetMode(VideoMode mode) => CurrentMode = mode;
        public void DrawCharacter(int col, int row, char character, byte foreground, byte background) => _chars.Add(character);
        public void DrawPixel(int x, int y, byte colorIndex) { }
        public void Clear(byte backgroundColorIndex) => _chars.Clear();
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
            if (_keys.TryDequeue(out var key))
                return key;
            return new DosKeyEventArgs();
        }

        public void EnqueueKey(byte ascii, byte scanCode = 0)
        {
            var key = new DosKeyEventArgs { AsciiChar = ascii, ScanCode = scanCode };
            _keys.Enqueue(key);
            _sem.Release();
        }
    }

    private sealed class RecordingWriteStream : MemoryStream
    {
        private readonly Action<byte[]> _onDispose;

        public RecordingWriteStream(Action<byte[]> onDispose)
        {
            _onDispose = onDispose;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _onDispose(ToArray());
            base.Dispose(disposing);
        }
    }

    private sealed class TestStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase) { "\\", "C:\\" };

        public Task<Stream> OpenReadAsync(string path)
        {
            if (_files.TryGetValue(path, out var data))
                return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
            throw new FileNotFoundException(path);
        }

        public Task<Stream> OpenWriteAsync(string path)
            => Task.FromResult<Stream>(new RecordingWriteStream(bytes => _files[path] = bytes));

        public Task<Stream> OpenReadWriteAsync(string path)
        {
            var data = _files.TryGetValue(path, out var existing) ? existing : Array.Empty<byte>();
            return Task.FromResult<Stream>(new MemoryStream(data));
        }

        public Task<bool> ExistsAsync(string path)
            => Task.FromResult(_files.ContainsKey(path) || Directories.Contains(path));

        public Task DeleteAsync(string path)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath)
        {
            var entries = _files.Keys.Concat(Directories).ToList();
            return Task.FromResult<IReadOnlyList<string>>(entries);
        }

        public Task ImportBinaryAsync(string targetPath, byte[] data)
        {
            _files[targetPath] = data;
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path)
        {
            Directories.Add(path);
            return Task.CompletedTask;
        }

        public Task DeleteDirectoryAsync(string path)
        {
            Directories.Remove(path);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string oldPath, string newPath)
        {
            if (_files.Remove(oldPath, out var data))
                _files[newPath] = data;
            if (Directories.Remove(oldPath))
                Directories.Add(newPath);
            return Task.CompletedTask;
        }

        public Task<long> GetFileSizeAsync(string path)
            => Task.FromResult(_files.TryGetValue(path, out var d) ? (long)d.Length : -1L);

        public void SetTextFile(string path, string content)
            => _files[path] = Encoding.ASCII.GetBytes(content);

        public bool FileExists(string path) => _files.ContainsKey(path);
        public bool DirectoryExists(string path) => Directories.Contains(path);
    }

    private sealed class ShellFixture
    {
        public readonly TestRenderer Renderer;
        public readonly TestEventRegistry Events;
        public readonly TestStreamProvider Streams;
        public readonly DosMachine Machine;

        public ShellFixture()
        {
            Renderer = new TestRenderer();
            Events = new TestEventRegistry();
            Streams = new TestStreamProvider();
            Machine = new DosMachine(Renderer, Events, Streams);
            Machine.Shell.SetCurrentDrive('C', Streams);
        }

        public Task RunAutoexecAsync(string script, CancellationToken cancellationToken = default)
        {
            Streams.SetTextFile("AUTOEXEC.BAT", script);
            return Machine.Shell.ProcessAutoexecAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task ProcessAutoexec_SetAndExpandEnvironmentVariable()
    {
        var fx = new ShellFixture();
        await fx.RunAutoexecAsync("SET MYVAR=HELLO\nECHO %MYVAR%\n");

        Assert.Contains("HELLO", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAutoexec_IfExist_ExecutesCommand()
    {
        var fx = new ShellFixture();
        fx.Streams.SetTextFile("A.TXT", "x");

        await fx.RunAutoexecAsync("IF EXIST A.TXT ECHO FOUND\n");

        Assert.Contains("FOUND", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAutoexec_IfNotExist_DoesNotExecuteCommand()
    {
        var fx = new ShellFixture();
        await fx.RunAutoexecAsync("IF EXIST NOFILE.TXT ECHO FOUND\n");

        Assert.DoesNotContain("FOUND", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAutoexec_For_ExpandsAndExecutesBody()
    {
        var fx = new ShellFixture();
        await fx.RunAutoexecAsync("FOR %I IN (ONE TWO) DO ECHO %I\n");

        Assert.Contains("ONE", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TWO", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAutoexec_Call_BatchExecutesNestedScript()
    {
        var fx = new ShellFixture();
        fx.Streams.SetTextFile("CHILD.BAT", "ECHO CHILD\n");

        await fx.RunAutoexecAsync("CALL CHILD.BAT\n");

        Assert.Contains("CHILD", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAutoexec_Pause_WaitsForKey()
    {
        var fx = new ShellFixture();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var task = fx.RunAutoexecAsync("PAUSE\n", cts.Token);
        await Task.Delay(50, cts.Token);
        fx.Events.EnqueueKey((byte)'X');
        await task;

        Assert.Contains("Press any key to continue", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAutoexec_Vol_PrintsVolumeInfo()
    {
        var fx = new ShellFixture();
        await fx.RunAutoexecAsync("VOL\n");

        Assert.Contains("Volume in drive", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Volume Serial Number", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAutoexec_Verify_PrintsVerifyOff()
    {
        var fx = new ShellFixture();
        await fx.RunAutoexecAsync("VERIFY\n");

        Assert.Contains("VERIFY is off", fx.Renderer.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAutoexec_MkdirAndRmdir_AreCalled()
    {
        var fx = new ShellFixture();
        await fx.RunAutoexecAsync("MKDIR TESTDIR\nRMDIR TESTDIR\n");

        Assert.False(fx.Streams.DirectoryExists("TESTDIR"));
    }

    [Fact]
    public async Task ProcessAutoexec_Ren_RenamesFile()
    {
        var fx = new ShellFixture();
        fx.Streams.SetTextFile("OLD.TXT", "abc");

        await fx.RunAutoexecAsync("REN OLD.TXT NEW.TXT\n");

        Assert.False(fx.Streams.FileExists("OLD.TXT"));
        Assert.True(fx.Streams.FileExists("NEW.TXT"));
    }
}
