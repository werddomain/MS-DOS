using MsDos.Core;
using MsDos.Core.Platform;
using System.Collections.Concurrent;

namespace MsDos.Tests;

/// <summary>
/// Integration tests for the DosMachine class, testing the full emulation stack.
/// </summary>
public class DosMachineTests
{
    /// <summary>Simple test renderer for capturing output.</summary>
    private sealed class TestRenderer : IGraphicsRenderer
    {
        public int Width => 640;
        public int Height => 400;
        private readonly List<(int col, int row, char ch, byte fg, byte bg)> _chars = new();
        public IReadOnlyList<(int col, int row, char ch, byte fg, byte bg)> DrawnChars => _chars;

        public bool CursorVisible { get; private set; } = true;
        public int CursorCol { get; private set; }
        public int CursorRow { get; private set; }
        public VideoMode CurrentMode { get; private set; }

        public void SetMode(VideoMode mode) => CurrentMode = mode;
        public void DrawCharacter(int col, int row, char character, byte foreground, byte background)
            => _chars.Add((col, row, character, foreground, background));
        public void DrawPixel(int x, int y, byte colorIndex) { }
        public void Clear(byte backgroundColorIndex) => _chars.Clear();
        public void SetCursorPosition(int col, int row) { CursorCol = col; CursorRow = row; }
        public void SetCursorVisible(bool visible) => CursorVisible = visible;
        public void ScrollUp(int lines, byte backgroundColorIndex) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>Simple test event registry.</summary>
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
            _keys.TryDequeue(out var key);
            return key!;
        }

        public void EnqueueKey(byte ascii, byte scanCode = 0)
        {
            var key = new DosKeyEventArgs { AsciiChar = ascii, ScanCode = scanCode };
            _keys.Enqueue(key);
            _sem.Release();
        }
    }

    /// <summary>Simple in-memory stream provider.</summary>
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

        public Task<Stream> OpenReadWriteAsync(string path)
        {
            var data = _files.ContainsKey(path) ? _files[path] : Array.Empty<byte>();
            return Task.FromResult<Stream>(new MemoryStream(data));
        }

        public Task<bool> ExistsAsync(string path) => Task.FromResult(_files.ContainsKey(path));

        public Task DeleteAsync(string path)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath)
            => Task.FromResult<IReadOnlyList<string>>(_files.Keys.ToList());

        public Task ImportBinaryAsync(string targetPath, byte[] data)
        {
            _files[targetPath] = data;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void LoadBinary_ComFile_LoadsAndConfigures()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Simple COM: MOV AX, 4C00h; INT 21h (terminate with code 0)
        byte[] com = { 0xB8, 0x00, 0x4C, 0xCD, 0x21 };
        bool loaded = machine.LoadBinary(com);

        Assert.True(loaded);
        Assert.Equal((ushort)0x1000, machine.Cpu.Regs.CS);
        Assert.Equal((ushort)0x0100, machine.Cpu.Regs.IP);
        Assert.False(machine.IsTerminated);
    }

    [Fact]
    public async Task RunAsync_SimpleProgram_TerminatesCleanly()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // COM program: MOV AH, 4Ch; MOV AL, 42; INT 21h (exit with code 42)
        byte[] com = { 0xB4, 0x4C, 0xB0, 0x2A, 0xCD, 0x21 };
        machine.LoadBinary(com);

        byte exitCode = 0;
        machine.OnProcessExit += code => exitCode = code;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await machine.RunAsync(cts.Token);

        Assert.True(machine.IsTerminated);
        Assert.Equal(42, exitCode);
    }

    [Fact]
    public async Task RunAsync_HelloWorld_ProducesOutput()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // COM program that prints "Hi" using INT 21h AH=02h then exits
        byte[] com = {
            0xB4, 0x02,       // MOV AH, 02h (display character)
            0xB2, 0x48,       // MOV DL, 'H'
            0xCD, 0x21,       // INT 21h
            0xB2, 0x69,       // MOV DL, 'i'
            0xCD, 0x21,       // INT 21h
            0xB4, 0x4C,       // MOV AH, 4Ch (terminate)
            0xB0, 0x00,       // MOV AL, 0
            0xCD, 0x21,       // INT 21h
        };
        machine.LoadBinary(com);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await machine.RunAsync(cts.Token);

        Assert.True(machine.IsTerminated);
        // Verify 'H' and 'i' were drawn to the renderer
        Assert.Contains(renderer.DrawnChars, c => c.ch == 'H');
        Assert.Contains(renderer.DrawnChars, c => c.ch == 'i');
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        byte[] com = { 0xB8, 0x00, 0x4C, 0xCD, 0x21 };
        machine.LoadBinary(com);
        machine.Reset();

        Assert.False(machine.IsTerminated);
        Assert.False(machine.IsRunning);
        Assert.Equal(VideoMode.Text80x25, renderer.CurrentMode);
    }

    [Fact]
    public void StepInstruction_ExecutesSingleInstruction()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // NOP NOP HLT
        byte[] com = { 0x90, 0x90, 0xF4 };
        machine.LoadBinary(com);

        machine.StepInstruction(); // NOP
        Assert.Equal((ushort)0x0101, machine.Cpu.Regs.IP);
        Assert.False(machine.Cpu.IsHalted);

        machine.StepInstruction(); // NOP
        Assert.Equal((ushort)0x0102, machine.Cpu.Regs.IP);

        machine.StepInstruction(); // HLT
        Assert.True(machine.Cpu.IsHalted);
    }
}
