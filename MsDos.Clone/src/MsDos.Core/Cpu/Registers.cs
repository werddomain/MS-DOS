namespace MsDos.Core.Cpu;

/// <summary>
/// 8086 CPU register file. Provides 16-bit general-purpose, segment,
/// and special-purpose registers with 8-bit high/low accessors.
/// </summary>
public sealed class Registers
{
    // General-purpose registers (stored as 32-bit for 386+ support)
    private uint _eax, _ebx, _ecx, _edx;

    // Index / pointer registers (32-bit backing for 386+ support)
    private uint _esi, _edi, _esp, _ebp;

    // Instruction pointer
    public ushort IP { get; set; }

    // Segment registers
    public ushort CS { get; set; }
    public ushort DS { get; set; }
    public ushort ES { get; set; }
    public ushort SS { get; set; }
    public ushort FS { get; set; } // 386+
    public ushort GS { get; set; } // 386+

    // --- 32-bit register accessors (386+) ---
    public uint EAX { get => _eax; set => _eax = value; }
    public uint EBX { get => _ebx; set => _ebx = value; }
    public uint ECX { get => _ecx; set => _ecx = value; }
    public uint EDX { get => _edx; set => _edx = value; }
    public uint ESI { get => _esi; set => _esi = value; }
    public uint EDI { get => _edi; set => _edi = value; }
    public uint ESP { get => _esp; set => _esp = value; }
    public uint EBP { get => _ebp; set => _ebp = value; }

    // --- 16-bit register accessors (low word of 32-bit registers) ---
    public ushort AX { get => (ushort)(_eax & 0xFFFF); set => _eax = (_eax & 0xFFFF0000) | value; }
    public ushort BX { get => (ushort)(_ebx & 0xFFFF); set => _ebx = (_ebx & 0xFFFF0000) | value; }
    public ushort CX { get => (ushort)(_ecx & 0xFFFF); set => _ecx = (_ecx & 0xFFFF0000) | value; }
    public ushort DX { get => (ushort)(_edx & 0xFFFF); set => _edx = (_edx & 0xFFFF0000) | value; }
    public ushort SI { get => (ushort)(_esi & 0xFFFF); set => _esi = (_esi & 0xFFFF0000) | value; }
    public ushort DI { get => (ushort)(_edi & 0xFFFF); set => _edi = (_edi & 0xFFFF0000) | value; }
    public ushort SP { get => (ushort)(_esp & 0xFFFF); set => _esp = (_esp & 0xFFFF0000) | value; }
    public ushort BP { get => (ushort)(_ebp & 0xFFFF); set => _ebp = (_ebp & 0xFFFF0000) | value; }

    // 8-bit low byte accessors
    public byte AL { get => (byte)(_eax & 0xFF); set => _eax = (_eax & 0xFFFFFF00) | value; }
    public byte BL { get => (byte)(_ebx & 0xFF); set => _ebx = (_ebx & 0xFFFFFF00) | value; }
    public byte CL { get => (byte)(_ecx & 0xFF); set => _ecx = (_ecx & 0xFFFFFF00) | value; }
    public byte DL { get => (byte)(_edx & 0xFF); set => _edx = (_edx & 0xFFFFFF00) | value; }

    // 8-bit high byte accessors
    public byte AH { get => (byte)((_eax >> 8) & 0xFF); set => _eax = (_eax & 0xFFFF00FF) | ((uint)value << 8); }
    public byte BH { get => (byte)((_ebx >> 8) & 0xFF); set => _ebx = (_ebx & 0xFFFF00FF) | ((uint)value << 8); }
    public byte CH { get => (byte)((_ecx >> 8) & 0xFF); set => _ecx = (_ecx & 0xFFFF00FF) | ((uint)value << 8); }
    public byte DH { get => (byte)((_edx >> 8) & 0xFF); set => _edx = (_edx & 0xFFFF00FF) | ((uint)value << 8); }

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

    /// <summary>Get/set a 32-bit register by its 3-bit encoding (386+).</summary>
    public uint GetReg32(int index) => index switch
    {
        0 => EAX, 1 => ECX, 2 => EDX, 3 => EBX,
        4 => ESP, 5 => EBP, 6 => ESI, 7 => EDI,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public void SetReg32(int index, uint value)
    {
        switch (index)
        {
            case 0: EAX = value; break;
            case 1: ECX = value; break;
            case 2: EDX = value; break;
            case 3: EBX = value; break;
            case 4: ESP = value; break;
            case 5: EBP = value; break;
            case 6: ESI = value; break;
            case 7: EDI = value; break;
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

    /// <summary>Get a segment register by 2-bit encoding (0-3) or extended (4-5 for FS/GS).</summary>
    public ushort GetSegReg(int index) => index switch
    {
        0 => ES, 1 => CS, 2 => SS, 3 => DS,
        4 => FS, 5 => GS,
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
            case 4: FS = value; break;
            case 5: GS = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public void Reset()
    {
        _eax = _ebx = _ecx = _edx = 0;
        _esi = _edi = _esp = _ebp = 0;
        IP = 0;
        CS = DS = ES = SS = FS = GS = 0;
        Flags = CpuFlags.None;
    }
}
