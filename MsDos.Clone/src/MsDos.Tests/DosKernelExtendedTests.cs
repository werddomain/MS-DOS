using MsDos.Core;
using MsDos.Core.Cpu;
using MsDos.Core.Dos;
using MsDos.Core.Interrupts;
using MsDos.Core.Memory;
using MsDos.Core.Platform;
using System.Collections.Concurrent;

namespace MsDos.Tests;

/// <summary>
/// Tests for DosKernel new INT 21h functions: DPB, FCB stubs,
/// List of Lists, Translate BPB, network stubs, AH=03h-05h.
/// </summary>
public class DosKernelExtendedTests
{
    private sealed class StubRenderer : IGraphicsRenderer
    {
        public int Width => 640;
        public int Height => 400;
        public VideoMode CurrentMode { get; private set; }
        public void SetMode(VideoMode mode) => CurrentMode = mode;
        public void DrawCharacter(int col, int row, char character, byte foreground, byte background) { }
        public void DrawPixel(int x, int y, byte colorIndex) { }
        public void Clear(byte backgroundColorIndex) { }
        public void SetCursorPosition(int col, int row) { }
        public void SetCursorVisible(bool visible) { }
        public void ScrollUp(int lines, byte backgroundColorIndex) { }
        public void ScrollDown(int lines, byte backgroundColorIndex) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class StubEventRegistry : IEventRegistry
    {
        public event Action<DosKeyEventArgs>? KeyDown;
        public event Action<DosKeyEventArgs>? KeyUp;
        public event Action<DosMouseEventArgs>? MouseMove;
        public event Action<DosMouseEventArgs>? MouseDown;
        public event Action<DosMouseEventArgs>? MouseUp;
        public bool IsKeyAvailable => false;
        public Task<DosKeyEventArgs> ReadKeyAsync(CancellationToken ct = default)
            => Task.FromResult(new DosKeyEventArgs());
        public void RaiseKeyDown(DosKeyEventArgs args) => KeyDown?.Invoke(args);
        public void RaiseKeyUp(DosKeyEventArgs args) => KeyUp?.Invoke(args);
    }

    private sealed class StubStreamProvider : IStreamProvider
    {
        public Task<Stream> OpenReadAsync(string path) => throw new FileNotFoundException();
        public Task<Stream> OpenWriteAsync(string path) => throw new FileNotFoundException();
        public Task<Stream> OpenReadWriteAsync(string path) => throw new FileNotFoundException();
        public Task<bool> ExistsAsync(string path) => Task.FromResult(false);
        public Task DeleteAsync(string path) => throw new FileNotFoundException();
        public Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task ImportBinaryAsync(string targetPath, byte[] data) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path) => Task.CompletedTask;
        public Task DeleteDirectoryAsync(string path) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath) => throw new FileNotFoundException();
        public Task<long> GetFileSizeAsync(string path) => throw new FileNotFoundException();
    }

    private readonly MemoryBus _mem = new();
    private readonly Cpu8086 _cpu;
    private readonly DosKernel _dos;

    public DosKernelExtendedTests()
    {
        _cpu = new Cpu8086(_mem);
        var renderer = new StubRenderer();
        var events = new StubEventRegistry();
        var streams = new StubStreamProvider();
        var video = new BiosVideoService(_cpu, _mem, renderer);
        var log = new EmulatorLog();
        _dos = new DosKernel(_cpu, _mem, streams, events, renderer, video, log);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=1Fh — Get Default DPB
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH1F_GetDefaultDPB_ReturnsDsBx()
    {
        _cpu.Regs.AH = 0x1F;
        _dos.Handle();

        Assert.Equal((ushort)0x0080, _cpu.Regs.DS);
        Assert.Equal((ushort)0x0000, _cpu.Regs.BX);
        Assert.Equal(0, _cpu.Regs.AL);
    }

    [Fact]
    public void Handle_AH1F_GetDefaultDPB_WritesDpbStructure()
    {
        _cpu.Regs.AH = 0x1F;
        _dos.Handle();

        // Verify DPB structure at 0080:0000
        Assert.Equal((ushort)512, _mem.ReadWord(0x0080, 0x0002)); // Bytes per sector
        Assert.Equal(63, _mem.ReadByte(0x0080, 0x0004));           // Sectors per cluster - 1
        Assert.Equal(6, _mem.ReadByte(0x0080, 0x0005));            // Cluster shift
        Assert.Equal((ushort)1, _mem.ReadWord(0x0080, 0x0006));   // Reserved sectors
        Assert.Equal(2, _mem.ReadByte(0x0080, 0x0008));            // Number of FATs
        Assert.Equal((ushort)512, _mem.ReadWord(0x0080, 0x0009)); // Max root dir entries
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=32h — Get DPB for specific drive (same as 1Fh)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH32_GetDPBForDrive_ReturnsDsBx()
    {
        _cpu.Regs.AH = 0x32;
        _cpu.Regs.DL = 3; // Drive C:
        _dos.Handle();

        Assert.Equal((ushort)0x0080, _cpu.Regs.DS);
        Assert.Equal((ushort)0x0000, _cpu.Regs.BX);
        Assert.Equal(0, _cpu.Regs.AL);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=52h — Get List of Lists
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH52_GetListOfLists_ReturnsEsBx()
    {
        _cpu.Regs.AH = 0x52;
        _dos.Handle();

        Assert.Equal((ushort)0x0080, _cpu.Regs.ES);
        Assert.Equal((ushort)0x0020, _cpu.Regs.BX);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=53h — Translate BPB (stub)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH53_TranslateBPB_ClearsCarry()
    {
        _cpu.Regs.Flags |= CpuFlags.Carry;
        _cpu.Regs.AH = 0x53;
        _dos.Handle();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=5Dh — Server function call (network stub)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH5D_NetworkServerCall_SetsCarry()
    {
        _cpu.Regs.AH = 0x5D;
        _dos.Handle();
        Assert.True((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
        Assert.Equal((ushort)1, _cpu.Regs.AX);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=5Eh — Network operations (stub)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH5E_NetworkOperations_SetsCarry()
    {
        _cpu.Regs.AH = 0x5E;
        _dos.Handle();
        Assert.True((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
        Assert.Equal((ushort)0x01, _cpu.Regs.AX);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=5Fh — Network redirector (stub)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH5F_NetworkRedirector_SetsCarry()
    {
        _cpu.Regs.AH = 0x5F;
        _dos.Handle();
        Assert.True((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
        Assert.Equal((ushort)0x01, _cpu.Regs.AX);
    }

    // ═══════════════════════════════════════════════════════════════
    // FCB operations — all return AL=0xFF
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0x0F)] // Open file FCB
    [InlineData(0x10)] // Close FCB
    [InlineData(0x11)] // Find first FCB
    [InlineData(0x12)] // Find next FCB
    [InlineData(0x13)] // Delete FCB
    [InlineData(0x14)] // Sequential read FCB
    [InlineData(0x15)] // Sequential write FCB
    [InlineData(0x16)] // Create FCB
    [InlineData(0x17)] // Rename FCB
    [InlineData(0x21)] // Random read FCB
    [InlineData(0x22)] // Random write FCB
    [InlineData(0x23)] // Get file size FCB
    [InlineData(0x27)] // Random block read FCB
    [InlineData(0x28)] // Random block write FCB
    public void Handle_FcbStubs_ReturnAL_FF(byte func)
    {
        _cpu.Regs.AH = func;
        _cpu.Regs.AL = 0x00; // Clear before call
        _dos.Handle();
        Assert.Equal(0xFF, _cpu.Regs.AL);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=30h — Get DOS version
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH30_GetDosVersion()
    {
        _cpu.Regs.AH = 0x30;
        _dos.Handle();
        Assert.Equal(4, _cpu.Regs.AL);  // Major version 4
        Assert.Equal(0, _cpu.Regs.AH);  // Minor version 0
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=19h — Get current default drive
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH19_GetCurrentDrive()
    {
        _cpu.Regs.AH = 0x19;
        _dos.Handle();
        Assert.Equal(2, _cpu.Regs.AL); // C: = 2
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=2Ah — Get date / AH=2Ch — Get time
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH2A_GetDate_ReturnsValidDate()
    {
        _cpu.Regs.AH = 0x2A;
        _dos.Handle();
        // CX = year, DH = month, DL = day
        Assert.InRange(_cpu.Regs.CX, (ushort)2024, (ushort)2100);
        Assert.InRange(_cpu.Regs.DH, (byte)1, (byte)12);
        Assert.InRange(_cpu.Regs.DL, (byte)1, (byte)31);
    }

    [Fact]
    public void Handle_AH2C_GetTime_ReturnsValidTime()
    {
        _cpu.Regs.AH = 0x2C;
        _dos.Handle();
        Assert.InRange(_cpu.Regs.CH, (byte)0, (byte)23);
        Assert.InRange(_cpu.Regs.CL, (byte)0, (byte)59);
        Assert.InRange(_cpu.Regs.DH, (byte)0, (byte)59);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=25h / AH=35h — Set/Get interrupt vector
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH25_AH35_SetGet_InterruptVector()
    {
        // Set interrupt vector for INT 80h
        _cpu.Regs.AH = 0x25;
        _cpu.Regs.AL = 0x80;  // INT number
        _cpu.Regs.DS = 0x1234;
        _cpu.Regs.DX = 0x5678;
        _dos.Handle();

        // Get it back
        _cpu.Regs.AH = 0x35;
        _cpu.Regs.AL = 0x80;
        _dos.Handle();

        Assert.Equal((ushort)0x1234, _cpu.Regs.ES);
        Assert.Equal((ushort)0x5678, _cpu.Regs.BX);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=4Ch — Terminate with return code
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Handle_AH4C_TerminateRaisesEvent()
    {
        byte exitCode = 0;
        _dos.ProcessTerminated += code => exitCode = code;

        _cpu.Regs.AH = 0x4C;
        _cpu.Regs.AL = 42;
        _dos.Handle();

        Assert.Equal(42, exitCode);
    }
}
