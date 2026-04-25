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

    // --- Serial port state (COM1-COM4 loopback) ---
    private const int MaxPorts = 4;
    private readonly Queue<byte>[] _serialBuf = { new(), new(), new(), new() };
    private readonly ushort[] _serialBaud = new ushort[MaxPorts];  // divisor
    private readonly byte[] _serialLcr = new byte[MaxPorts];       // line control register

    /// <summary>INT 14h — Serial port services. DX = port number (0-3).</summary>
    public void HandleInt14()
    {
        int port = _cpu.Regs.DX;
        if (port >= MaxPorts) { _cpu.Regs.AH = 0x80; return; } // timeout / invalid

        switch (_cpu.Regs.AH)
        {
            case 0x00: // Initialize serial port — AL = parameters
            {
                byte param = _cpu.Regs.AL;
                // Bits 7-5: baud rate, 4-3: parity, 2: stop bits, 1-0: data length
                _serialLcr[port] = param;
                _serialBaud[port] = (ushort)((param >> 5) & 7); // store baud index
                _serialBuf[port].Clear();
                // Return line status (AH) + modem status (AL)
                // AH: bit6=THRE, bit5=TEMT (transmitter empty), rest clear
                // AL: bit5=DSR, bit4=CTS
                _cpu.Regs.AH = 0x60; // THRE + TEMT
                _cpu.Regs.AL = 0x30; // DSR + CTS
                break;
            }
            case 0x01: // Send character — AL = char
            {
                // Loopback: queue the byte for reading back
                if (_serialBuf[port].Count < 4096)
                    _serialBuf[port].Enqueue(_cpu.Regs.AL);
                // AH bit 7 clear = success, bits 6-0 = line status
                _cpu.Regs.AH = 0x60; // THRE + TEMT, no error
                break;
            }
            case 0x02: // Read character
            {
                if (_serialBuf[port].Count > 0)
                {
                    _cpu.Regs.AL = _serialBuf[port].Dequeue();
                    _cpu.Regs.AH = 0x60; // bit 7 clear = success
                }
                else
                {
                    _cpu.Regs.AH = 0x80; // bit 7 set = timeout
                    _cpu.Regs.AL = 0x00;
                }
                break;
            }
            case 0x03: // Get port status
            {
                // AH = line status: bit6 THRE, bit5 TEMT, bit0 = data ready
                byte lsr = 0x60; // THRE + TEMT always set
                if (_serialBuf[port].Count > 0) lsr |= 0x01; // data ready
                _cpu.Regs.AH = lsr;
                _cpu.Regs.AL = 0x30; // modem status: DSR + CTS
                break;
            }
            case 0x04: // Extended init (PS/2+) — BX = baud, CL = data/stop/parity
            {
                _serialBuf[port].Clear();
                _cpu.Regs.AH = 0x60;
                _cpu.Regs.AL = 0x30;
                break;
            }
            case 0x05: // Extended port control (PS/2+)
            {
                if (_cpu.Regs.AL == 0x00) // Read modem control register
                    _cpu.Regs.BL = 0x0B; // DTR + RTS + OUT2
                // AL=0x01: Write modem control register — silently accept
                _cpu.Regs.AH = 0x60;
                _cpu.Regs.AL = 0x30;
                break;
            }
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
