namespace MsDos.Core.Hardware;

/// <summary>
/// MC146818 CMOS/RTC controller emulation.
/// Provides Real-Time Clock and 128 bytes of battery-backed CMOS RAM.
///
/// Port mapping:
///   0x70 : CMOS address register (write only, bit 7 = NMI disable)
///   0x71 : CMOS data register (read/write)
///
/// Register map:
///   0x00-0x09 : RTC time/date/alarm
///   0x0A-0x0D : Status registers A-D
///   0x0E      : Diagnostic status
///   0x0F      : Shutdown status
///   0x10      : Floppy drive types
///   0x12      : Hard drive types
///   0x14      : Equipment byte
///   0x15-0x16 : Base memory (low/high)
///   0x17-0x18 : Extended memory (low/high)
///   0x32      : Century (BCD)
/// </summary>
public sealed class CmosRtc
{
    private readonly byte[] _cmos = new byte[128];
    private byte _addressRegister;
    private bool _nmiDisabled;

    /// <summary>When true, time registers return BCD values (default). When false, binary.</summary>
    private bool BcdMode => (_cmos[0x0B] & 0x04) == 0;

    /// <summary>When true, 24-hour mode (default). When false, 12-hour.</summary>
    private bool Is24HourMode => (_cmos[0x0B] & 0x02) != 0;

    public CmosRtc()
    {
        InitializeDefaults();
    }

    /// <summary>
    /// Register I/O port handlers with the port bus.
    /// Replaces the existing inline CMOS stub in DosMachine.
    /// </summary>
    public void RegisterPorts(Memory.IOPortBus ports)
    {
        ports.Register(0x70, null, (port, val) =>
        {
            _nmiDisabled = (val & 0x80) != 0;
            _addressRegister = (byte)(val & 0x7F);
        });

        ports.Register(0x71,
            port => ReadRegister(_addressRegister),
            (port, val) => WriteRegister(_addressRegister, val));
    }

    /// <summary>
    /// Set floppy drive types in CMOS register 0x10.
    /// High nibble = drive 0 (A:), low nibble = drive 1 (B:).
    /// Type values: 0=none, 1=360K, 2=1.2M, 3=720K, 4=1.44M, 5=2.88M
    /// </summary>
    public void SetFloppyTypes(byte driveA, byte driveB)
    {
        _cmos[0x10] = (byte)((driveA << 4) | (driveB & 0x0F));
    }

    /// <summary>Set the number of hard drives in CMOS register 0x12.</summary>
    public void SetHardDriveType(byte drive0Type, byte drive1Type = 0)
    {
        _cmos[0x12] = (byte)((drive0Type << 4) | (drive1Type & 0x0F));
    }

    /// <summary>Set base memory size in KB (usually 640).</summary>
    public void SetBaseMemory(ushort kb)
    {
        _cmos[0x15] = (byte)(kb & 0xFF);
        _cmos[0x16] = (byte)((kb >> 8) & 0xFF);
    }

    /// <summary>Set extended memory size in KB (above 1MB).</summary>
    public void SetExtendedMemory(ushort kb)
    {
        _cmos[0x17] = (byte)(kb & 0xFF);
        _cmos[0x18] = (byte)((kb >> 8) & 0xFF);
    }

    private void InitializeDefaults()
    {
        // Status A: oscillator running, divider = 32.768 KHz, rate = 1024 Hz
        _cmos[0x0A] = 0x26;

        // Status B: 24-hour mode, BCD format, no periodic interrupt
        _cmos[0x0B] = 0x02;

        // Status C: no pending interrupts
        _cmos[0x0C] = 0x00;

        // Status D: battery OK
        _cmos[0x0D] = 0x80;

        // Floppy types: both 1.44M
        _cmos[0x10] = 0x44;

        // Hard drive type: no drives
        _cmos[0x12] = 0x00;

        // Equipment byte: math coprocessor installed, 1 floppy
        _cmos[0x14] = 0x0D;

        // Base memory: 640K
        SetBaseMemory(640);

        // Extended memory: 0K (8086 has no extended memory)
        SetExtendedMemory(0);
    }

    internal byte ReadRegister(byte register)
    {
        var now = DateTime.Now;

        return register switch
        {
            0x00 => EncodeTime(now.Second),         // Seconds
            0x01 => EncodeTime(0),                   // Seconds alarm
            0x02 => EncodeTime(now.Minute),          // Minutes
            0x03 => EncodeTime(0),                   // Minutes alarm
            0x04 => EncodeTime(now.Hour),            // Hours
            0x05 => EncodeTime(0),                   // Hours alarm
            0x06 => EncodeTime((int)now.DayOfWeek + 1), // Day of week (1-7)
            0x07 => EncodeTime(now.Day),             // Day of month
            0x08 => EncodeTime(now.Month),           // Month
            0x09 => EncodeTime(now.Year % 100),      // Year (2-digit)
            0x32 => EncodeTime(now.Year / 100),      // Century

            0x0A => (byte)(_cmos[0x0A] & 0x7F),     // Update-not-in-progress
            0x0B => _cmos[0x0B],
            0x0C =>
                // Reading status C clears all interrupt flags
                ClearAndReturn(0x0C),
            0x0D => _cmos[0x0D],                     // Battery always OK

            _ => _cmos[register]
        };
    }

    internal void WriteRegister(byte register, byte value)
    {
        switch (register)
        {
            case 0x00: case 0x01: case 0x02: case 0x03:
            case 0x04: case 0x05: case 0x06: case 0x07:
            case 0x08: case 0x09: case 0x32:
                // Time/date registers — store but don't affect host clock
                _cmos[register] = value;
                break;
            default:
                _cmos[register] = value;
                break;
        }
    }

    private byte EncodeTime(int value)
    {
        return BcdMode ? ToBcd(value) : (byte)value;
    }

    private byte ClearAndReturn(byte register)
    {
        byte val = _cmos[register];
        _cmos[register] = 0;
        return val;
    }

    private static byte ToBcd(int value) =>
        (byte)(((value / 10) << 4) | (value % 10));
}
