using MsDos.Core.Cpu;
using MsDos.Core.Interrupts;
using MsDos.Core.Memory;

namespace MsDos.Tests;

/// <summary>
/// Tests for BiosMiscService: INT 11h, INT 12h, INT 1Ah, INT 14h, INT 17h.
/// </summary>
public class BiosMiscServiceTests
{
    private readonly MemoryBus _mem = new();
    private readonly Cpu8086 _cpu;
    private readonly BiosMiscService _svc;

    public BiosMiscServiceTests()
    {
        _cpu = new Cpu8086(_mem);
        _svc = new BiosMiscService(_cpu, _mem);
    }

    // ─── INT 11h — Equipment list ───────────────────────────────────

    [Fact]
    public void HandleInt11_ReturnsBdaEquipmentWord()
    {
        _mem.WriteWord(0x0040, 0x0010, 0x4321);
        _svc.HandleInt11();
        Assert.Equal((ushort)0x4321, _cpu.Regs.AX);
    }

    [Fact]
    public void HandleInt11_DefaultWhenBdaIsZero()
    {
        _mem.WriteWord(0x0040, 0x0010, 0);
        _svc.HandleInt11();
        Assert.Equal((ushort)0x0021, _cpu.Regs.AX);
    }

    // ─── INT 12h — Memory size ──────────────────────────────────────

    [Fact]
    public void HandleInt12_ReturnsBdaMemorySize()
    {
        _mem.WriteWord(0x0040, 0x0013, 512);
        _svc.HandleInt12();
        Assert.Equal((ushort)512, _cpu.Regs.AX);
    }

    [Fact]
    public void HandleInt12_DefaultWhenBdaIsZero()
    {
        _mem.WriteWord(0x0040, 0x0013, 0);
        _svc.HandleInt12();
        Assert.Equal((ushort)640, _cpu.Regs.AX);
    }

    // ─── INT 1Ah AH=01h — Set system timer ─────────────────────────

    [Fact]
    public void HandleInt1A_SetTimer_WritesToBda()
    {
        _cpu.Regs.AH = 0x01;
        _cpu.Regs.CX = 0x0012;  // High word of tick count
        _cpu.Regs.DX = 0x3456;  // Low word of tick count
        _svc.HandleInt1A();

        Assert.Equal((ushort)0x3456, _mem.ReadWord(0x0040, 0x006C));
        Assert.Equal((ushort)0x0012, _mem.ReadWord(0x0040, 0x006E));
    }

    [Fact]
    public void HandleInt1A_GetTimer_ReadsFromBda()
    {
        // First set via BDA
        _mem.WriteWord(0x0040, 0x006C, 0xAAAA);
        _mem.WriteWord(0x0040, 0x006E, 0xBBBB);

        _cpu.Regs.AH = 0x00;
        _svc.HandleInt1A();

        Assert.Equal((ushort)0xBBBB, _cpu.Regs.CX); // High word
        Assert.Equal((ushort)0xAAAA, _cpu.Regs.DX); // Low word
        Assert.Equal(0, _cpu.Regs.AL); // Midnight flag = 0
    }

    // ─── INT 1Ah AH=02h — Get RTC time ─────────────────────────────

    [Fact]
    public void HandleInt1A_GetRtcTime_ClearsCarry()
    {
        _cpu.Regs.Flags |= CpuFlags.Carry;
        _cpu.Regs.AH = 0x02;
        _svc.HandleInt1A();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    // ─── INT 1Ah AH=03h — Set RTC time (stub) ─────────────────────

    [Fact]
    public void HandleInt1A_SetRtcTime_ClearsCarry()
    {
        _cpu.Regs.Flags |= CpuFlags.Carry;
        _cpu.Regs.AH = 0x03;
        _svc.HandleInt1A();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    // ─── INT 1Ah AH=04h — Get RTC date ─────────────────────────────

    [Fact]
    public void HandleInt1A_GetRtcDate_ClearsCarry()
    {
        _cpu.Regs.Flags |= CpuFlags.Carry;
        _cpu.Regs.AH = 0x04;
        _svc.HandleInt1A();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    // ─── INT 1Ah AH=05h — Set RTC date (stub) ─────────────────────

    [Fact]
    public void HandleInt1A_SetRtcDate_ClearsCarry()
    {
        _cpu.Regs.Flags |= CpuFlags.Carry;
        _cpu.Regs.AH = 0x05;
        _svc.HandleInt1A();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    // ─── INT 1Ah AH=06h — Set RTC alarm (stub) ────────────────────

    [Fact]
    public void HandleInt1A_SetRtcAlarm_ClearsCarry()
    {
        _cpu.Regs.Flags |= CpuFlags.Carry;
        _cpu.Regs.AH = 0x06;
        _svc.HandleInt1A();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    // ─── INT 1Ah AH=07h — Reset RTC alarm (stub) ──────────────────

    [Fact]
    public void HandleInt1A_ResetRtcAlarm_ClearsCarry()
    {
        _cpu.Regs.Flags |= CpuFlags.Carry;
        _cpu.Regs.AH = 0x07;
        _svc.HandleInt1A();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    // ─── INT 14h — Serial port services ─────────────────────────────

    [Fact]
    public void HandleInt14_AH00_InitSerialPort()
    {
        _cpu.Regs.AH = 0x00;
        _svc.HandleInt14();
        Assert.Equal((ushort)0x6030, _cpu.Regs.AX); // AH=60h (THRE+TEMT), AL=30h (DSR+CTS)
    }

    [Fact]
    public void HandleInt14_AH01_WriteCharacter()
    {
        _cpu.Regs.AH = 0x01;
        _svc.HandleInt14();
        Assert.Equal(0x60, _cpu.Regs.AH); // Success (bit 7 clear)
    }

    [Fact]
    public void HandleInt14_AH02_ReadCharacter_Timeout()
    {
        _cpu.Regs.AH = 0x02;
        _svc.HandleInt14();
        Assert.Equal(0x80, _cpu.Regs.AH); // Timeout (bit 7 set)
        Assert.Equal(0x00, _cpu.Regs.AL);
    }

    [Fact]
    public void HandleInt14_AH03_GetPortStatus()
    {
        _cpu.Regs.AH = 0x03;
        _svc.HandleInt14();
        Assert.Equal((ushort)0x6030, _cpu.Regs.AX); // AH=60h (THRE+TEMT), AL=30h (DSR+CTS)
    }

    // ─── INT 17h — Printer services ─────────────────────────────────

    [Fact]
    public void HandleInt17_AH00_PrintCharacter()
    {
        _cpu.Regs.AH = 0x00;
        _svc.HandleInt17();
        Assert.Equal(0x90, _cpu.Regs.AH); // Selected and not busy
    }

    [Fact]
    public void HandleInt17_AH01_InitializePrinter()
    {
        _cpu.Regs.AH = 0x01;
        _svc.HandleInt17();
        Assert.Equal(0x90, _cpu.Regs.AH);
    }

    [Fact]
    public void HandleInt17_AH02_GetPrinterStatus()
    {
        _cpu.Regs.AH = 0x02;
        _svc.HandleInt17();
        Assert.Equal(0x90, _cpu.Regs.AH);
    }
}
