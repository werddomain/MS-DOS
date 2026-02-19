namespace MsDos.Core.Cpu;

/// <summary>
/// 8086 CPU register file. Provides 16-bit general-purpose, segment,
/// and special-purpose registers with 8-bit high/low accessors.
/// </summary>
public sealed class Registers
{
    // General-purpose registers (stored as 16-bit)
    private ushort _ax, _bx, _cx, _dx;

    // Index registers
    public ushort SI { get; set; }
    public ushort DI { get; set; }

    // Pointer registers
    public ushort SP { get; set; }
    public ushort BP { get; set; }

    // Instruction pointer
    public ushort IP { get; set; }

    // Segment registers
    public ushort CS { get; set; }
    public ushort DS { get; set; }
    public ushort ES { get; set; }
    public ushort SS { get; set; }

    // 16-bit register accessors
    public ushort AX { get => _ax; set => _ax = value; }
    public ushort BX { get => _bx; set => _bx = value; }
    public ushort CX { get => _cx; set => _cx = value; }
    public ushort DX { get => _dx; set => _dx = value; }

    // 8-bit low byte accessors
    public byte AL { get => (byte)(_ax & 0xFF); set => _ax = (ushort)((_ax & 0xFF00) | value); }
    public byte BL { get => (byte)(_bx & 0xFF); set => _bx = (ushort)((_bx & 0xFF00) | value); }
    public byte CL { get => (byte)(_cx & 0xFF); set => _cx = (ushort)((_cx & 0xFF00) | value); }
    public byte DL { get => (byte)(_dx & 0xFF); set => _dx = (ushort)((_dx & 0xFF00) | value); }

    // 8-bit high byte accessors
    public byte AH { get => (byte)((_ax >> 8) & 0xFF); set => _ax = (ushort)((_ax & 0x00FF) | (value << 8)); }
    public byte BH { get => (byte)((_bx >> 8) & 0xFF); set => _bx = (ushort)((_bx & 0x00FF) | (value << 8)); }
    public byte CH { get => (byte)((_cx >> 8) & 0xFF); set => _cx = (ushort)((_cx & 0x00FF) | (value << 8)); }
    public byte DH { get => (byte)((_dx >> 8) & 0xFF); set => _dx = (ushort)((_dx & 0x00FF) | (value << 8)); }

    // Flags register
    public CpuFlags Flags { get; set; }

    /// <summary>Compute physical address from segment:offset.</summary>
    public static uint PhysicalAddress(ushort segment, ushort offset) =>
        (uint)(segment << 4) + offset;

    /// <summary>Get/set a 16-bit register by its 3-bit encoding (used in ModR/M).</summary>
    public ushort GetReg16(int index) => index switch
    {
        0 => AX, 1 => CX, 2 => DX, 3 => BX,
        4 => SP, 5 => BP, 6 => SI, 7 => DI,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetReg16(int index, ushort value)
    {
        switch (index)
        {
            case 0: AX = value; break;
            case 1: CX = value; break;
            case 2: DX = value; break;
            case 3: BX = value; break;
            case 4: SP = value; break;
            case 5: BP = value; break;
            case 6: SI = value; break;
            case 7: DI = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    /// <summary>Get/set an 8-bit register by its 3-bit encoding.</summary>
    public byte GetReg8(int index) => index switch
    {
        0 => AL, 1 => CL, 2 => DL, 3 => BL,
        4 => AH, 5 => CH, 6 => DH, 7 => BH,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetReg8(int index, byte value)
    {
        switch (index)
        {
            case 0: AL = value; break;
            case 1: CL = value; break;
            case 2: DL = value; break;
            case 3: BL = value; break;
            case 4: AH = value; break;
            case 5: CH = value; break;
            case 6: DH = value; break;
            case 7: BH = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    /// <summary>Get a segment register by 2-bit encoding.</summary>
    public ushort GetSegReg(int index) => index switch
    {
        0 => ES, 1 => CS, 2 => SS, 3 => DS,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetSegReg(int index, ushort value)
    {
        switch (index)
        {
            case 0: ES = value; break;
            case 1: CS = value; break;
            case 2: SS = value; break;
            case 3: DS = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public void Reset()
    {
        _ax = _bx = _cx = _dx = 0;
        SI = DI = SP = BP = IP = 0;
        CS = DS = ES = SS = 0;
        Flags = CpuFlags.None;
    }
}
