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
        public void ScrollDown(int lines, byte backgroundColorIndex) { }
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
        public void RaiseKeyDown(DosKeyEventArgs args) => KeyDown?.Invoke(args);
        public void RaiseKeyUp(DosKeyEventArgs args) => KeyUp?.Invoke(args);
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

        public Task CreateDirectoryAsync(string path) => Task.CompletedTask;
        public Task DeleteDirectoryAsync(string path) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath)
        {
            if (_files.Remove(oldPath, out var d)) _files[newPath] = d;
            return Task.CompletedTask;
        }
        public Task<long> GetFileSizeAsync(string path) =>
            Task.FromResult(_files.TryGetValue(path, out var d) ? (long)d.Length : -1L);
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

    [Fact]
    public void MountDiskImage_ValidFat12_MountsSuccessfully()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Create a minimal 360K floppy (size-based geometry guess)
        var image = new byte[368640];

        bool mounted = machine.MountDiskImage('A', image);

        Assert.True(mounted);
        Assert.NotNull(machine.GetDriveProvider('A'));
        Assert.Contains('A', machine.GetMountedDrives());
        Assert.Contains('C', machine.GetMountedDrives());
    }

    [Fact]
    public void MountDiskImage_TooSmall_ReturnsFalse()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        bool mounted = machine.MountDiskImage('A', new byte[100]);

        Assert.False(mounted);
        Assert.Null(machine.GetDriveProvider('A'));
    }

    [Fact]
    public void LoadBinary_WithProgramPath_EnvironmentContainsPath()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        byte[] com = { 0xB4, 0x4C, 0xB0, 0x00, 0xCD, 0x21 };
        bool loaded = machine.LoadBinary(com, "", "A:\\TEST.COM");

        Assert.True(loaded);

        // Read environment segment from PSP at offset 0x002C
        ushort envSeg = machine.Memory.ReadWord(0x1000, 0x002C);

        // Scan past environment variables
        int offset = 0;
        while (offset < 256)
        {
            byte b = machine.Memory.ReadByte(envSeg, (ushort)offset);
            if (b == 0)
            {
                offset++;
                byte next = machine.Memory.ReadByte(envSeg, (ushort)offset);
                if (next == 0) { offset++; break; }
            }
            else offset++;
        }

        // After double-null: WORD count should be 1
        ushort count = machine.Memory.ReadWord(envSeg, (ushort)offset);
        Assert.Equal(1, count);
        offset += 2;

        // Read program path
        var pathBytes = new List<byte>();
        for (int i = 0; i < 50; i++)
        {
            byte b = machine.Memory.ReadByte(envSeg, (ushort)(offset + i));
            if (b == 0) break;
            pathBytes.Add(b);
        }
        Assert.Equal("A:\\TEST.COM", System.Text.Encoding.ASCII.GetString(pathBytes.ToArray()));
    }

    [Fact]
    public void InsertBlankFloppyDisk_CreatesAndMountsDisk()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        bool inserted = machine.InsertBlankFloppyDisk('A', MsDos.Core.Dos.FloppyDiskType.Floppy1440K, "TEST");

        Assert.True(inserted);
        Assert.NotNull(machine.GetDriveProvider('A'));
        Assert.Contains('A', machine.GetMountedDrives());
        Assert.True(machine.Floppy.SlotA.HasDisk);
    }

    [Fact]
    public void InsertBlankFloppyDisk_InvalidDrive_ReturnsFalse()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        bool inserted = machine.InsertBlankFloppyDisk('C');

        Assert.False(inserted);
    }

    [Fact]
    public void DosKernel_DriveProvider_ResolvesFilesOnMountedDrive()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Mount a blank floppy on A:
        machine.InsertBlankFloppyDisk('A');

        // Verify the DOS kernel has the provider registered
        // by checking that the drive shows in mounted drives
        Assert.Contains('A', machine.GetMountedDrives());

        // Eject should remove it
        machine.EjectFloppyDisk('A');
        Assert.DoesNotContain('A', machine.GetMountedDrives());
    }

    [Fact]
    public void Reset_PreservesFloppyDiskImages()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Mount a floppy disk on A:
        var image = new byte[368640]; // 360K
        bool mounted = machine.MountDiskImage('A', image);
        Assert.True(mounted);
        Assert.True(machine.Floppy.SlotA.HasDisk);

        // Reset the machine
        machine.Reset();

        // Verify disk is still mounted after reset
        Assert.Contains('A', machine.GetMountedDrives());
        Assert.NotNull(machine.GetDriveProvider('A'));
        Assert.True(machine.Floppy.SlotA.HasDisk);
    }

    [Fact]
    public void Reset_PreservesHardDiskImages()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Mount a small HDD image on C: (overriding default stream provider)
        var image = new byte[368640]; // 360K — small image for test
        bool mounted = machine.MountDiskImage('C', image);
        Assert.True(mounted);

        // Reset the machine
        machine.Reset();

        // Verify C: is still mounted
        Assert.Contains('C', machine.GetMountedDrives());
        Assert.NotNull(machine.GetDriveProvider('C'));
    }

    [Fact]
    public void Reset_PreservesMultipleDrives()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Mount A: and B:
        var imageA = new byte[368640];
        var imageB = new byte[368640];
        machine.MountDiskImage('A', imageA);
        machine.MountDiskImage('B', imageB);

        // Also verify C: is default-mounted
        Assert.Contains('C', machine.GetMountedDrives());

        // Reset
        machine.Reset();

        // All three drives should survive
        Assert.Contains('A', machine.GetMountedDrives());
        Assert.Contains('B', machine.GetMountedDrives());
        Assert.Contains('C', machine.GetMountedDrives());
        Assert.True(machine.Floppy.SlotA.HasDisk);
        Assert.True(machine.Floppy.SlotB.HasDisk);
    }

    [Fact]
    public void LoadBinary_PreservesDiskMountsAcrossReset()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Mount floppy first
        var image = new byte[368640];
        machine.MountDiskImage('A', image);

        // Load a binary (which calls Reset internally)
        byte[] com = { 0xB4, 0x4C, 0xB0, 0x00, 0xCD, 0x21 };
        bool loaded = machine.LoadBinary(com);
        Assert.True(loaded);

        // Verify floppy is still mounted after LoadBinary's internal reset
        Assert.Contains('A', machine.GetMountedDrives());
        Assert.True(machine.Floppy.SlotA.HasDisk);
    }

    [Fact]
    public void MountDiskThenBootSequence_FindsFloppyDisk()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Mount a floppy on A: before powering on
        var image = new byte[368640];
        machine.MountDiskImage('A', image);

        // Reset simulates power-on preparation
        machine.Reset();

        // Boot sequence should find A: drive
        var bootDrive = machine.CheckBootSequence();
        Assert.Equal('A', bootDrive);
    }

    [Fact]
    public void Int15Wait_PausesManualExecution_AndResumesViaUiSignal()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams)
        {
            ManualClockMode = true
        };

        // Program:
        // MOV AH,86h
        // MOV CX,004Ch
        // MOV DX,4B40h   ; 5,000,000 us (~5s)
        // INT 15h
        // MOV AH,4Ch
        // MOV AL,07h
        // INT 21h
        byte[] com =
        {
            0xB4, 0x86,
            0xB9, 0x4C, 0x00,
            0xBA, 0x40, 0x4B,
            0xCD, 0x15,
            0xB4, 0x4C,
            0xB0, 0x07,
            0xCD, 0x21,
        };

        Assert.True(machine.LoadBinary(com));

        machine.StepInstruction(); // MOV AH,86
        machine.StepInstruction(); // MOV CX,004C
        machine.StepInstruction(); // MOV DX,4B40
        machine.StepInstruction(); // INT 15h -> starts wait gate

        Assert.True(machine.IsBiosWaitPending);

        ushort ipBefore = machine.Cpu.Regs.IP;
        int blockedCycles = machine.StepInstruction(); // must not advance while waiting
        Assert.Equal(0, blockedCycles);
        Assert.Equal(ipBefore, machine.Cpu.Regs.IP);

        machine.ResumeBiosWait();
        Assert.False(machine.IsBiosWaitPending);

        int resumedCycles = machine.StepInstruction(); // MOV AH,4C should execute
        Assert.True(resumedCycles > 0);
        Assert.NotEqual(ipBefore, machine.Cpu.Regs.IP);
    }

    [Fact]
    public async Task Int15Wait_AutoResumesByTimeout_DuringRunAsync()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);

        // Program with short BIOS wait then terminate with code 9
        // MOV AH,86h
        // MOV CX,0000h
        // MOV DX,03E8h ; 1000 us = 1ms
        // INT 15h
        // MOV AH,4Ch
        // MOV AL,09h
        // INT 21h
        byte[] com =
        {
            0xB4, 0x86,
            0xB9, 0x00, 0x00,
            0xBA, 0xE8, 0x03,
            0xCD, 0x15,
            0xB4, 0x4C,
            0xB0, 0x09,
            0xCD, 0x21,
        };

        Assert.True(machine.LoadBinary(com));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await machine.RunAsync(cts.Token);

        Assert.True(machine.IsTerminated);
        Assert.Equal((byte)9, machine.ExitCode);
        Assert.False(machine.IsBiosWaitPending);
    }
}
