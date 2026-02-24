using MsDos.Core;
using MsDos.Core.Cpu;
using MsDos.Core.Interrupts;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Tests;

/// <summary>
/// Tests for BiosVideoService new features: DAC palette (AH=10h),
/// character generator (AH=11h), and CGA pixel write/read (AH=0Ch/0Dh).
/// </summary>
public class BiosVideoServiceExtendedTests
{
    private sealed class StubRenderer : IGraphicsRenderer
    {
        public int Width => 640;
        public int Height => 400;
        public VideoMode CurrentMode { get; private set; }
        public readonly List<(int x, int y, byte color)> Pixels = new();

        public void SetMode(VideoMode mode) => CurrentMode = mode;
        public void DrawCharacter(int col, int row, char character, byte foreground, byte background) { }
        public void DrawPixel(int x, int y, byte colorIndex) => Pixels.Add((x, y, colorIndex));
        public void Clear(byte backgroundColorIndex) { }
        public void SetCursorPosition(int col, int row) { }
        public void SetCursorVisible(bool visible) { }
        public void ScrollUp(int lines, byte backgroundColorIndex) { }
        public void ScrollDown(int lines, byte backgroundColorIndex) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private readonly MemoryBus _mem = new();
    private readonly Cpu8086 _cpu;
    private readonly StubRenderer _renderer;
    private readonly BiosVideoService _video;

    public BiosVideoServiceExtendedTests()
    {
        _cpu = new Cpu8086(_mem);
        _renderer = new StubRenderer();
        _video = new BiosVideoService(_cpu, _mem, _renderer);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=10h — DAC Palette operations
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Palette_SetAndReadIndividualDac()
    {
        // Set DAC register 5: R=10, G=20, B=30
        _cpu.Regs.AH = 0x10;
        _cpu.Regs.AL = 0x10; // Set individual DAC
        _cpu.Regs.BX = 5;    // Register number
        _cpu.Regs.DH = 10;   // Red
        _cpu.Regs.CH = 20;   // Green
        _cpu.Regs.CL = 30;   // Blue
        _video.Handle();

        // Now read it back
        _cpu.Regs.AH = 0x10;
        _cpu.Regs.AL = 0x15; // Read individual DAC
        _cpu.Regs.BX = 5;
        _video.Handle();

        Assert.Equal(10, _cpu.Regs.DH);
        Assert.Equal(20, _cpu.Regs.CH);
        Assert.Equal(30, _cpu.Regs.CL);
    }

    [Fact]
    public void Palette_SetBlockOfDac()
    {
        // Write 3 RGB triplets starting at register 10
        // ES:DX points to memory with triplets
        _cpu.Regs.ES = 0x2000;
        _cpu.Regs.DX = 0x0000;
        // Triplet 0: R=1, G=2, B=3
        _mem.WriteByte(0x2000, 0x0000, 1);
        _mem.WriteByte(0x2000, 0x0001, 2);
        _mem.WriteByte(0x2000, 0x0002, 3);
        // Triplet 1: R=4, G=5, B=6
        _mem.WriteByte(0x2000, 0x0003, 4);
        _mem.WriteByte(0x2000, 0x0004, 5);
        _mem.WriteByte(0x2000, 0x0005, 6);
        // Triplet 2: R=7, G=8, B=9
        _mem.WriteByte(0x2000, 0x0006, 7);
        _mem.WriteByte(0x2000, 0x0007, 8);
        _mem.WriteByte(0x2000, 0x0008, 9);

        _cpu.Regs.AH = 0x10;
        _cpu.Regs.AL = 0x12; // Set block of DAC
        _cpu.Regs.BX = 10;   // First register
        _cpu.Regs.CX = 3;    // Count
        _video.Handle();

        // Read back register 12 (third one)
        _cpu.Regs.AH = 0x10;
        _cpu.Regs.AL = 0x15;
        _cpu.Regs.BX = 12;
        _video.Handle();

        Assert.Equal(7, _cpu.Regs.DH);
        Assert.Equal(8, _cpu.Regs.CH);
        Assert.Equal(9, _cpu.Regs.CL);
    }

    [Fact]
    public void Palette_ReadBlockOfDac()
    {
        // First set some values
        for (int i = 0; i < 3; i++)
        {
            _cpu.Regs.AH = 0x10;
            _cpu.Regs.AL = 0x10;
            _cpu.Regs.BX = (ushort)(20 + i);
            _cpu.Regs.DH = (byte)(10 + i);
            _cpu.Regs.CH = (byte)(20 + i);
            _cpu.Regs.CL = (byte)(30 + i);
            _video.Handle();
        }

        // Read block
        _cpu.Regs.AH = 0x10;
        _cpu.Regs.AL = 0x17; // Read block of DAC
        _cpu.Regs.BX = 20;   // First register
        _cpu.Regs.CX = 3;    // Count
        _cpu.Regs.ES = 0x3000;
        _cpu.Regs.DX = 0x0000;
        _video.Handle();

        // Verify memory at ES:DX
        Assert.Equal(10, _mem.ReadByte(0x3000, 0x0000)); // R of reg 20
        Assert.Equal(20, _mem.ReadByte(0x3000, 0x0001)); // G of reg 20
        Assert.Equal(30, _mem.ReadByte(0x3000, 0x0002)); // B of reg 20
        Assert.Equal(11, _mem.ReadByte(0x3000, 0x0003)); // R of reg 21
        Assert.Equal(21, _mem.ReadByte(0x3000, 0x0004)); // G of reg 21
        Assert.Equal(31, _mem.ReadByte(0x3000, 0x0005)); // B of reg 21
    }

    [Fact]
    public void Palette_ReadIndividualPalette_ReturnsBH0()
    {
        _cpu.Regs.AH = 0x10;
        _cpu.Regs.AL = 0x07; // Read individual palette
        _cpu.Regs.BH = 0xFF; // Should be overwritten
        _video.Handle();
        Assert.Equal(0, _cpu.Regs.BH);
    }

    [Fact]
    public void Palette_ReadOverscan_ReturnsBH0()
    {
        _cpu.Regs.AH = 0x10;
        _cpu.Regs.AL = 0x08; // Read overscan
        _cpu.Regs.BH = 0xFF;
        _video.Handle();
        Assert.Equal(0, _cpu.Regs.BH);
    }

    [Fact]
    public void Palette_DacPageState_Returns00()
    {
        _cpu.Regs.AH = 0x10;
        _cpu.Regs.AL = 0x1A; // Get/Set DAC page state
        _cpu.Regs.BL = 0xFF;
        _cpu.Regs.BH = 0xFF;
        _video.Handle();
        Assert.Equal(0, _cpu.Regs.BL);
        Assert.Equal(0, _cpu.Regs.BH);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=11h — Character generator
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CharGen_GetFontInfo_Returns8BytesPerChar()
    {
        _cpu.Regs.AH = 0x11;
        _cpu.Regs.AL = 0x30; // Get font info
        _video.Handle();

        Assert.Equal((ushort)8, _cpu.Regs.CX); // 8 bytes per character (8x8 CGA font)
        Assert.Equal(24, _cpu.Regs.DL);          // 25 rows - 1
    }

    [Fact]
    public void CharGen_LoadFontStub_DoesNotCrash()
    {
        // Various load font sub-functions should be accepted silently
        foreach (byte al in new byte[] { 0x00, 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x14 })
        {
            _cpu.Regs.AH = 0x11;
            _cpu.Regs.AL = al;
            _video.Handle(); // Should not throw
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=0Ch/0Dh — Pixel write/read (Mode 13h)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WritePixel_Mode13h_WritesToVram()
    {
        // Set mode 13h
        _cpu.Regs.AH = 0x00;
        _cpu.Regs.AL = 0x13;
        _video.Handle();

        // Write pixel at (10, 5) with color 42
        _cpu.Regs.AH = 0x0C;
        _cpu.Regs.AL = 42;
        _cpu.Regs.CX = 10; // X
        _cpu.Regs.DX = 5;  // Y
        _video.Handle();

        // Check VRAM: A000:0000 + y*320 + x = 5*320+10 = 1610
        uint addr = 0xA0000 + 5 * 320 + 10;
        Assert.Equal(42, _mem.ReadByte(addr));
    }

    [Fact]
    public void ReadPixel_Mode13h_ReadsFromVram()
    {
        // Set mode 13h
        _cpu.Regs.AH = 0x00;
        _cpu.Regs.AL = 0x13;
        _video.Handle();

        // Manually write to VRAM
        uint addr = 0xA0000 + 100 * 320 + 200; // (200, 100)
        _mem.WriteByte(addr, 99);

        // Read pixel
        _cpu.Regs.AH = 0x0D;
        _cpu.Regs.CX = 200; // X
        _cpu.Regs.DX = 100; // Y
        _video.Handle();

        Assert.Equal(99, _cpu.Regs.AL);
    }

    [Fact]
    public void WriteReadPixel_Mode13h_RoundTrip()
    {
        _cpu.Regs.AH = 0x00;
        _cpu.Regs.AL = 0x13;
        _video.Handle();

        // Write
        _cpu.Regs.AH = 0x0C;
        _cpu.Regs.AL = 255;
        _cpu.Regs.CX = 0; // X=0
        _cpu.Regs.DX = 0; // Y=0
        _video.Handle();

        // Read
        _cpu.Regs.AH = 0x0D;
        _cpu.Regs.CX = 0;
        _cpu.Regs.DX = 0;
        _video.Handle();

        Assert.Equal(255, _cpu.Regs.AL);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=0Ch/0Dh — CGA Mode 04h (320x200 4-color)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WriteReadPixel_Mode04_EvenRow()
    {
        _cpu.Regs.AH = 0x00;
        _cpu.Regs.AL = 0x04;
        _video.Handle();

        // Write pixel at (4, 0) color=3 (even row, at B800:0000)
        _cpu.Regs.AH = 0x0C;
        _cpu.Regs.AL = 3;
        _cpu.Regs.CX = 4; // X
        _cpu.Regs.DX = 0; // Y (even row)
        _video.Handle();

        // Read back
        _cpu.Regs.AH = 0x0D;
        _cpu.Regs.CX = 4;
        _cpu.Regs.DX = 0;
        _video.Handle();
        Assert.Equal(3, _cpu.Regs.AL);
    }

    [Fact]
    public void WriteReadPixel_Mode04_OddRow()
    {
        _cpu.Regs.AH = 0x00;
        _cpu.Regs.AL = 0x04;
        _video.Handle();

        // Write pixel at (0, 1) color=2 (odd row, at BA00:0000)
        _cpu.Regs.AH = 0x0C;
        _cpu.Regs.AL = 2;
        _cpu.Regs.CX = 0;
        _cpu.Regs.DX = 1; // Odd row
        _video.Handle();

        // Read back
        _cpu.Regs.AH = 0x0D;
        _cpu.Regs.CX = 0;
        _cpu.Regs.DX = 1;
        _video.Handle();
        Assert.Equal(2, _cpu.Regs.AL);
    }

    // ═══════════════════════════════════════════════════════════════
    // AH=0Ch/0Dh — CGA Mode 06h (640x200 2-color)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WriteReadPixel_Mode06_EvenRow()
    {
        _cpu.Regs.AH = 0x00;
        _cpu.Regs.AL = 0x06;
        _video.Handle();

        _cpu.Regs.AH = 0x0C;
        _cpu.Regs.AL = 1;
        _cpu.Regs.CX = 7; // X
        _cpu.Regs.DX = 0; // Y (even)
        _video.Handle();

        _cpu.Regs.AH = 0x0D;
        _cpu.Regs.CX = 7;
        _cpu.Regs.DX = 0;
        _video.Handle();
        Assert.Equal(1, _cpu.Regs.AL);
    }

    [Fact]
    public void WriteReadPixel_Mode06_OddRow()
    {
        _cpu.Regs.AH = 0x00;
        _cpu.Regs.AL = 0x06;
        _video.Handle();

        _cpu.Regs.AH = 0x0C;
        _cpu.Regs.AL = 1;
        _cpu.Regs.CX = 0;
        _cpu.Regs.DX = 3; // Odd row
        _video.Handle();

        _cpu.Regs.AH = 0x0D;
        _cpu.Regs.CX = 0;
        _cpu.Regs.DX = 3;
        _video.Handle();
        Assert.Equal(1, _cpu.Regs.AL);
    }

    // ═══════════════════════════════════════════════════════════════
    // Write pixel notifies renderer
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WritePixel_Mode13h_CallsRendererDrawPixel()
    {
        _cpu.Regs.AH = 0x00;
        _cpu.Regs.AL = 0x13;
        _video.Handle();
        _renderer.Pixels.Clear();

        _cpu.Regs.AH = 0x0C;
        _cpu.Regs.AL = 7;
        _cpu.Regs.CX = 50;
        _cpu.Regs.DX = 100;
        _video.Handle();

        Assert.Contains(_renderer.Pixels, p => p.x == 50 && p.y == 100 && p.color == 7);
    }
}
