using MsDos.Core.Cpu;
using MsDos.Core.Memory;

namespace MsDos.Core.Interrupts;

/// <summary>
/// BIOS INT 12h - Get conventional memory size.
/// BIOS INT 11h - Equipment list.
/// BIOS INT 1Ah - Time of day.
/// </summary>
public sealed class BiosMiscService
{
    private readonly Cpu8086 _cpu;
    private readonly DateTime _startTime = DateTime.UtcNow;

    public BiosMiscService(Cpu8086 cpu)
    {
        _cpu = cpu;
    }

    public void HandleInt11() // Equipment list
    {
        // Report: 80-column CGA, 1 floppy, serial & parallel
        _cpu.Regs.AX = 0x0021;
    }

    public void HandleInt12() // Memory size
    {
        // 640 KB conventional memory
        _cpu.Regs.AX = 640;
    }

    public void HandleInt1A() // Time of day
    {
        switch (_cpu.Regs.AH)
        {
            case 0x00: // Get system timer
            {
                var elapsed = DateTime.UtcNow - _startTime;
                uint ticks = (uint)(elapsed.TotalSeconds * 18.2065);
                _cpu.Regs.CX = (ushort)(ticks >> 16);
                _cpu.Regs.DX = (ushort)(ticks & 0xFFFF);
                _cpu.Regs.AL = 0; // midnight flag
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
        }
    }

    private static byte ToBcd(byte val) => (byte)(((val / 10) << 4) | (val % 10));
}
