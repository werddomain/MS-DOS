using MsDos.Core.Cpu;
using MsDos.Core.Memory;

namespace MsDos.Core.Interrupts;

/// <summary>
/// BIOS INT 12h - Get conventional memory size.
/// BIOS INT 11h - Equipment list.
/// BIOS INT 1Ah - Time of day.
/// BIOS INT 14h - Serial port services.
/// BIOS INT 17h - Printer services.
/// </summary>
public sealed class BiosMiscService
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _mem;
    private readonly DateTime _startTime = DateTime.UtcNow;

    public BiosMiscService(Cpu8086 cpu, MemoryBus mem)
    {
        _cpu = cpu;
        _mem = mem;
    }

    public void HandleInt11() // Equipment list
    {
        // Read equipment word from BDA 0040:0010
        ushort equip = _mem.ReadWord(0x0040, 0x0010);
        _cpu.Regs.AX = equip != 0 ? equip : (ushort)0x0021;
    }

    public void HandleInt12() // Memory size
    {
        // Read from BDA 0040:0013
        ushort memKb = _mem.ReadWord(0x0040, 0x0013);
        _cpu.Regs.AX = memKb != 0 ? memKb : (ushort)640;
    }

    public void HandleInt1A() // Time of day
    {
        switch (_cpu.Regs.AH)
        {
            case 0x00: // Get system timer
            {
                // Read from BDA timer tick count 0040:006C
                uint ticks = (uint)(_mem.ReadWord(0x0040, 0x006E) << 16 | _mem.ReadWord(0x0040, 0x006C));
                if (ticks == 0)
                {
                    var elapsed = DateTime.UtcNow - _startTime;
                    ticks = (uint)(elapsed.TotalSeconds * 18.2065);
                }
                _cpu.Regs.CX = (ushort)(ticks >> 16);
                _cpu.Regs.DX = (ushort)(ticks & 0xFFFF);
                _cpu.Regs.AL = 0; // midnight flag
                break;
            }
            case 0x01: // Set system timer
            {
                uint ticks = (uint)(_cpu.Regs.CX << 16) | _cpu.Regs.DX;
                _mem.WriteWord(0x0040, 0x006C, (ushort)(ticks & 0xFFFF));
                _mem.WriteWord(0x0040, 0x006E, (ushort)(ticks >> 16));
                break;
            }
            case 0x02: // Get RTC time
            {
                var now = DateTime.Now;
                _cpu.Regs.CH = ToBcd((byte)now.Hour);
                _cpu.Regs.CL = ToBcd((byte)now.Minute);
                _cpu.Regs.DH = ToBcd((byte)now.Second);
                _cpu.Regs.DL = 0; // no daylight saving
                _cpu.Regs.Flags &= ~CpuFlags.Carry; // success
                break;
            }
            case 0x03: // Set RTC time
            {
                // Accept but ignore (we use real system time)
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            }
            case 0x04: // Get RTC date
            {
                var now = DateTime.Now;
                _cpu.Regs.CH = ToBcd((byte)(now.Year / 100));
                _cpu.Regs.CL = ToBcd((byte)(now.Year % 100));
                _cpu.Regs.DH = ToBcd((byte)now.Month);
                _cpu.Regs.DL = ToBcd((byte)now.Day);
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            }
            case 0x05: // Set RTC date
            {
                // Accept but ignore
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            }
            case 0x06: // Set RTC alarm
            {
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            }
            case 0x07: // Reset RTC alarm
            {
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            }
        }
    }

    /// <summary>INT 14h — Serial port services.</summary>
    public void HandleInt14()
    {
        switch (_cpu.Regs.AH)
        {
            case 0x00: // Initialize serial port
                _cpu.Regs.AX = 0x6000; // DSR + CTS set, THRE + TEMT
                break;
            case 0x01: // Write character
                _cpu.Regs.AH = 0x60; // success (bit 7 clear)
                break;
            case 0x02: // Read character
                _cpu.Regs.AH = 0x80; // timeout (bit 7 set)
                _cpu.Regs.AL = 0x00;
                break;
            case 0x03: // Get port status
                _cpu.Regs.AX = 0x6000; // DSR + CTS, THRE + TEMT
                break;
        }
    }

    /// <summary>INT 17h — Printer services.</summary>
    public void HandleInt17()
    {
        switch (_cpu.Regs.AH)
        {
            case 0x00: // Print character
                _cpu.Regs.AH = 0x90; // Printer selected and not busy
                break;
            case 0x01: // Initialize printer port
                _cpu.Regs.AH = 0x90;
                break;
            case 0x02: // Get printer status
                _cpu.Regs.AH = 0x90; // Selected, not busy
                break;
        }
    }

    private static byte ToBcd(byte val) => (byte)(((val / 10) << 4) | (val % 10));
}
