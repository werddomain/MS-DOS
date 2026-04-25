using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Cpu;

/// <summary>CPU model selector for instruction set compatibility.</summary>
public enum CpuModel
{
    /// <summary>Intel 8088/8086 — opcodes 0x60-0x6F are Jcc aliases; C0/C1 alias RET near; C8/C9 alias RET far.</summary>
    Intel8088,
    /// <summary>Intel 80186+ — 0x60-0x6F are PUSHA/POPA/BOUND/etc; C0/C1 shift by imm8; C8/C9 ENTER/LEAVE.</summary>
    Intel80186,
    /// <summary>Intel 80386 — adds 32-bit operand/address size prefixes (0x66/0x67).</summary>
    Intel80386
}

/// <summary>
/// Intel 8086 CPU emulator. Implements fetch-decode-execute cycle with
/// support for the core instruction set needed to run DOS COM/EXE binaries.
/// </summary>
public sealed class Cpu8086
{
    /// <summary>CPU model — controls how ambiguous opcodes (0x60-0x6F, C0/C1, C8/C9) behave.</summary>
    public CpuModel Model { get; set; } = CpuModel.Intel80386;
    public Registers Regs { get; } = new();
    private readonly MemoryBus _mem;
    private IOPortBus? _ports;
    private EmulatorLog? _log;
    private bool _halted;
    private bool _waitingForInput; // set by INT handlers when no key is available
    private int? _segmentOverride; // null = default, 0-3 = ES/CS/SS/DS, 4=FS, 5=GS
    private bool _operandSize32;   // 0x66 prefix active: use 32-bit operands
    private bool _addressSize32;   // 0x67 prefix active: use 32-bit addressing
    private bool _repPrefix;       // REP/REPNE preceding non-string instruction (8088 IDIV quirk)

    // ModR/M address cache: prevents double-decode when an instruction both reads and writes
    // the same ModR/M operand (e.g., ADD [BX+disp], reg). Without caching, the displacement
    // bytes are fetched twice from the instruction stream, corrupting IP.
    private bool _modrmCached;
    private ushort _modrmCacheSeg;
    private ushort _modrmCacheOff;

    private Fpu8087? _fpu;

    /// <summary>Raised when an INT instruction is executed.</summary>
    public event Action<byte>? InterruptTriggered;

    /// <summary>Raised after each instruction if the Trap Flag is set (INT 1 single-step).</summary>
    public event Action? TrapFired;

    /// <summary>
    /// Raise a hardware interrupt (from PIC). Pushes flags/CS/IP and dispatches
    /// just like TriggerInterrupt, but called externally instead of from an INT instruction.
    /// </summary>
    public void RaiseHardwareInterrupt(byte vector)
    {
        TriggerInterrupt(vector);
    }

    public bool IsHalted => _halted;

    /// <summary>
    /// When true, an interrupt handler needs keyboard input but none is available.
    /// The execution loop should yield and wait for a key event.
    /// </summary>
    public bool IsWaitingForInput
    {
        get => _waitingForInput;
        set => _waitingForInput = value;
    }

    public Cpu8086(MemoryBus memory)
    {
        _mem = memory;
    }

    /// <summary>Attach the I/O port bus for IN/OUT instructions.</summary>
    public void SetIOPortBus(IOPortBus ports) => _ports = ports;

    /// <summary>Attach the x87 FPU coprocessor.</summary>
    public void SetFpu(Fpu8087 fpu) => _fpu = fpu;

    public void SetLog(EmulatorLog log) => _log = log;

    public void Reset()
    {
        Regs.Reset();
        Regs.CS = 0xFFFF;
        Regs.IP = 0x0000; // 8086 starts at FFFF:0000
        Regs.Flags = CpuFlags.Interrupt;
        _halted = false;
        _segmentOverride = null;
    }

    /// <summary>Unhalt the CPU (e.g. when an IRQ arrives).</summary>
    public void Unhalt() => _halted = false;

    /// <summary>Execute a single instruction. Returns number of cycles (approximate).</summary>
    public int Step()
    {
        if (_halted) return 1;
        _segmentOverride = null;
        _modrmCached = false;
        _operandSize32 = false;
        _addressSize32 = false;
        _repPrefix = false;

        bool wasTrap = GetFlag(CpuFlags.Trap);
        int cycles = DecodeAndExecute();

        // If the Trap Flag was set before this instruction, fire INT 1 (single-step)
        if (wasTrap)
            TrapFired?.Invoke();

        return cycles;
    }

    // --- Helpers ---

    private byte FetchByte()
    {
        byte val = _mem.ReadByte(Regs.CS, Regs.IP);
        Regs.IP++;
        return val;
    }

    private ushort FetchWord()
    {
        ushort val = _mem.ReadWord(Regs.CS, Regs.IP);
        Regs.IP += 2;
        return val;
    }

    private uint FetchDword()
    {
        uint val = _mem.ReadDword(Regs.CS, Regs.IP);
        Regs.IP += 4;
        return val;
    }

    /// <summary>Fetch a 16- or 32-bit immediate based on the operand-size prefix.</summary>
    private uint FetchWordOrDword() => _operandSize32 ? FetchDword() : FetchWord();

    private ushort GetDefaultSegment(int rmField) =>
        rmField switch
        {
            // BP-based addressing defaults to SS
            2 or 3 or 6 when _segmentOverride == null => Regs.SS,
            _ when _segmentOverride != null => Regs.GetSegReg(_segmentOverride.Value),
            _ => Regs.DS,
        };

    private ushort GetBpDefaultSegment() =>
        _segmentOverride.HasValue ? Regs.GetSegReg(_segmentOverride.Value) : Regs.SS;

    private ushort GetDataSegment() =>
        _segmentOverride.HasValue ? Regs.GetSegReg(_segmentOverride.Value) : Regs.DS;

    private void Push(ushort value)
    {
        Regs.SP -= 2;
        _mem.WriteWord(Regs.SS, Regs.SP, value);
    }

    private ushort Pop()
    {
        ushort val = _mem.ReadWord(Regs.SS, Regs.SP);
        Regs.SP += 2;
        return val;
    }

    // --- ModR/M decoding ---

    /// <summary>
    /// Fetch a ModR/M byte and skip any displacement it implies, without
    /// reading or writing the operand. Used for ESC (x87) stubs so that
    /// IP advances past the full instruction even though no FPU is present.
    /// </summary>
    private void SkipModRM()
    {
        byte modrm = FetchByte();
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;

        if (mod == 3) return; // register operand – nothing more to skip
        if (mod == 0 && rm == 6) { FetchWord(); return; } // direct address (disp16)
        if (mod == 1) { FetchByte(); return; } // disp8
        if (mod == 2) { FetchWord(); } // disp16
        // mod == 0 with rm != 6: no displacement bytes
    }

    private (ushort segment, ushort offset) DecodeModRM_Address(byte modrm)
    {
        // Return cached result if already decoded for this instruction.
        // This prevents consuming displacement bytes twice when an instruction
        // both reads and writes the same ModR/M operand.
        if (_modrmCached)
            return (_modrmCacheSeg, _modrmCacheOff);

        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;

        ushort offset;
        ushort segment;

        if (mod == 0 && rm == 6)
        {
            // Direct address
            offset = FetchWord();
            segment = GetDataSegment();
            _modrmCached = true;
            _modrmCacheSeg = segment;
            _modrmCacheOff = offset;
            return (segment, offset);
        }

        // Calculate effective address
        offset = rm switch
        {
            0 => (ushort)(Regs.BX + Regs.SI),
            1 => (ushort)(Regs.BX + Regs.DI),
            2 => (ushort)(Regs.BP + Regs.SI),
            3 => (ushort)(Regs.BP + Regs.DI),
            4 => Regs.SI,
            5 => Regs.DI,
            6 => Regs.BP,
            7 => Regs.BX,
            _ => 0
        };

        // BP-based addressing uses SS by default
        bool useBp = rm == 2 || rm == 3 || rm == 6;
        segment = useBp ? GetBpDefaultSegment() : GetDataSegment();

        // Add displacement
        if (mod == 1)
            offset = (ushort)(offset + (sbyte)FetchByte());
        else if (mod == 2)
            offset = (ushort)(offset + FetchWord());

        _modrmCached = true;
        _modrmCacheSeg = segment;
        _modrmCacheOff = offset;
        return (segment, offset);
    }

    private byte ReadModRM8(byte modrm)
    {
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;
        if (mod == 3) return Regs.GetReg8(rm);
        var (seg, off) = DecodeModRM_Address(modrm);
        return _mem.ReadByte(seg, off);
    }

    private ushort ReadModRM16(byte modrm)
    {
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;
        if (mod == 3) return Regs.GetReg16(rm);
        var (seg, off) = DecodeModRM_Address(modrm);
        return _mem.ReadWord(seg, off);
    }

    private void WriteModRM8(byte modrm, byte value)
    {
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;
        if (mod == 3) { Regs.SetReg8(rm, value); return; }
        var (seg, off) = DecodeModRM_Address(modrm);
        _mem.WriteByte(seg, off, value);
    }

    private void WriteModRM16(byte modrm, ushort value)
    {
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;
        if (mod == 3) { Regs.SetReg16(rm, value); return; }
        var (seg, off) = DecodeModRM_Address(modrm);
        _mem.WriteWord(seg, off, value);
    }

    // --- Flag helpers ---

    private static bool Parity(byte val)
    {
        int bits = 0;
        for (int i = 0; i < 8; i++) bits += (val >> i) & 1;
        return (bits & 1) == 0; // even parity
    }

    public void SetFlag(CpuFlags flag, bool set)
    {
        if (set) Regs.Flags |= flag;
        else Regs.Flags &= ~flag;
    }

    public bool GetFlag(CpuFlags flag) => (Regs.Flags & flag) != 0;

    private void UpdateFlags8(byte result)
    {
        SetFlag(CpuFlags.Zero, result == 0);
        SetFlag(CpuFlags.Sign, (result & 0x80) != 0);
        SetFlag(CpuFlags.Parity, Parity(result));
    }

    private void UpdateFlags16(ushort result)
    {
        SetFlag(CpuFlags.Zero, result == 0);
        SetFlag(CpuFlags.Sign, (result & 0x8000) != 0);
        SetFlag(CpuFlags.Parity, Parity((byte)(result & 0xFF)));
    }

    // --- ALU operations ---

    private byte Add8(byte a, byte b, bool withCarry = false)
    {
        int carry = withCarry && GetFlag(CpuFlags.Carry) ? 1 : 0;
        int result = a + b + carry;
        byte r = (byte)result;
        UpdateFlags8(r);
        SetFlag(CpuFlags.Carry, result > 0xFF);
        SetFlag(CpuFlags.Overflow, ((a ^ r) & (b ^ r) & 0x80) != 0);
        SetFlag(CpuFlags.AuxCarry, ((a ^ b ^ r) & 0x10) != 0);
        return r;
    }

    private ushort Add16(ushort a, ushort b, bool withCarry = false)
    {
        int carry = withCarry && GetFlag(CpuFlags.Carry) ? 1 : 0;
        int result = a + b + carry;
        ushort r = (ushort)result;
        UpdateFlags16(r);
        SetFlag(CpuFlags.Carry, result > 0xFFFF);
        SetFlag(CpuFlags.Overflow, ((a ^ r) & (b ^ r) & 0x8000) != 0);
        SetFlag(CpuFlags.AuxCarry, ((a ^ b ^ r) & 0x10) != 0);
        return r;
    }

    private byte Sub8(byte a, byte b, bool withBorrow = false)
    {
        int borrow = withBorrow && GetFlag(CpuFlags.Carry) ? 1 : 0;
        int result = a - b - borrow;
        byte r = (byte)result;
        UpdateFlags8(r);
        SetFlag(CpuFlags.Carry, result < 0);
        SetFlag(CpuFlags.Overflow, ((a ^ b) & (a ^ r) & 0x80) != 0);
        SetFlag(CpuFlags.AuxCarry, ((a ^ b ^ r) & 0x10) != 0);
        return r;
    }

    private ushort Sub16(ushort a, ushort b, bool withBorrow = false)
    {
        int borrow = withBorrow && GetFlag(CpuFlags.Carry) ? 1 : 0;
        int result = a - b - borrow;
        ushort r = (ushort)result;
        UpdateFlags16(r);
        SetFlag(CpuFlags.Carry, result < 0);
        SetFlag(CpuFlags.Overflow, ((a ^ b) & (a ^ r) & 0x8000) != 0);
        SetFlag(CpuFlags.AuxCarry, ((a ^ b ^ r) & 0x10) != 0);
        return r;
    }

    private byte And8(byte a, byte b) { byte r = (byte)(a & b); UpdateFlags8(r); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }
    private ushort And16(ushort a, ushort b) { ushort r = (ushort)(a & b); UpdateFlags16(r); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }
    private byte Or8(byte a, byte b) { byte r = (byte)(a | b); UpdateFlags8(r); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }
    private ushort Or16(ushort a, ushort b) { ushort r = (ushort)(a | b); UpdateFlags16(r); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }
    private byte Xor8(byte a, byte b) { byte r = (byte)(a ^ b); UpdateFlags8(r); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }
    private ushort Xor16(ushort a, ushort b) { ushort r = (ushort)(a ^ b); UpdateFlags16(r); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }

    // --- Main decode/execute ---

    private int DecodeAndExecute()
    {
        byte opcode = FetchByte();
        byte modrm;
        int reg;

        switch (opcode)
        {
            // --- ADD ---
            case 0x00: modrm = FetchByte(); WriteModRM8(modrm, Add8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7))); return 3;
            case 0x01: modrm = FetchByte(); if (_operandSize32) { WriteModRM32(modrm, Add32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7))); } else { WriteModRM16(modrm, Add16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); } return 3;
            case 0x02: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Add8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x03: modrm = FetchByte(); reg = (modrm >> 3) & 7; if (_operandSize32) { Regs.SetReg32(reg, Add32(Regs.GetReg32(reg), ReadModRM32(modrm))); } else { Regs.SetReg16(reg, Add16(Regs.GetReg16(reg), ReadModRM16(modrm))); } return 3;
            case 0x04: Regs.AL = Add8(Regs.AL, FetchByte()); return 4;
            case 0x05: if (_operandSize32) { Regs.EAX = Add32(Regs.EAX, FetchDword()); } else { Regs.AX = Add16(Regs.AX, FetchWord()); } return 4;

            // --- PUSH/POP segment ---
            case 0x06: Push(Regs.ES); return 10;
            case 0x07: Regs.ES = Pop(); return 8;
            case 0x0E: Push(Regs.CS); return 10;
            // 0x0F is the two-byte opcode prefix on 80186+, handled below
            case 0x16: Push(Regs.SS); return 10;
            case 0x17: Regs.SS = Pop(); return 8;
            case 0x1E: Push(Regs.DS); return 10;
            case 0x1F: Regs.DS = Pop(); return 8;

            // --- OR ---
            case 0x08: modrm = FetchByte(); WriteModRM8(modrm, Or8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7))); return 3;
            case 0x09: modrm = FetchByte(); if (_operandSize32) { WriteModRM32(modrm, Or32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7))); } else { WriteModRM16(modrm, Or16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); } return 3;
            case 0x0A: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Or8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x0B: modrm = FetchByte(); reg = (modrm >> 3) & 7; if (_operandSize32) { Regs.SetReg32(reg, Or32(Regs.GetReg32(reg), ReadModRM32(modrm))); } else { Regs.SetReg16(reg, Or16(Regs.GetReg16(reg), ReadModRM16(modrm))); } return 3;
            case 0x0C: Regs.AL = Or8(Regs.AL, FetchByte()); return 4;
            case 0x0D: if (_operandSize32) { Regs.EAX = Or32(Regs.EAX, FetchDword()); } else { Regs.AX = Or16(Regs.AX, FetchWord()); } return 4;

            // --- ADC ---
            case 0x10: modrm = FetchByte(); WriteModRM8(modrm, Add8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7), true)); return 3;
            case 0x11: modrm = FetchByte(); if (_operandSize32) { WriteModRM32(modrm, Add32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7), true)); } else { WriteModRM16(modrm, Add16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7), true)); } return 3;
            case 0x12: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Add8(Regs.GetReg8(reg), ReadModRM8(modrm), true)); return 3;
            case 0x13: modrm = FetchByte(); reg = (modrm >> 3) & 7; if (_operandSize32) { Regs.SetReg32(reg, Add32(Regs.GetReg32(reg), ReadModRM32(modrm), true)); } else { Regs.SetReg16(reg, Add16(Regs.GetReg16(reg), ReadModRM16(modrm), true)); } return 3;
            case 0x14: Regs.AL = Add8(Regs.AL, FetchByte(), true); return 4;
            case 0x15: if (_operandSize32) { Regs.EAX = Add32(Regs.EAX, FetchDword(), true); } else { Regs.AX = Add16(Regs.AX, FetchWord(), true); } return 4;

            // --- SBB ---
            case 0x18: modrm = FetchByte(); WriteModRM8(modrm, Sub8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7), true)); return 3;
            case 0x19: modrm = FetchByte(); if (_operandSize32) { WriteModRM32(modrm, Sub32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7), true)); } else { WriteModRM16(modrm, Sub16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7), true)); } return 3;
            case 0x1A: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Sub8(Regs.GetReg8(reg), ReadModRM8(modrm), true)); return 3;
            case 0x1B: modrm = FetchByte(); reg = (modrm >> 3) & 7; if (_operandSize32) { Regs.SetReg32(reg, Sub32(Regs.GetReg32(reg), ReadModRM32(modrm), true)); } else { Regs.SetReg16(reg, Sub16(Regs.GetReg16(reg), ReadModRM16(modrm), true)); } return 3;
            case 0x1C: Regs.AL = Sub8(Regs.AL, FetchByte(), true); return 4;
            case 0x1D: if (_operandSize32) { Regs.EAX = Sub32(Regs.EAX, FetchDword(), true); } else { Regs.AX = Sub16(Regs.AX, FetchWord(), true); } return 4;

            // --- AND ---
            case 0x20: modrm = FetchByte(); WriteModRM8(modrm, And8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7))); return 3;
            case 0x21: modrm = FetchByte(); if (_operandSize32) { WriteModRM32(modrm, And32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7))); } else { WriteModRM16(modrm, And16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); } return 3;
            case 0x22: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, And8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x23: modrm = FetchByte(); reg = (modrm >> 3) & 7; if (_operandSize32) { Regs.SetReg32(reg, And32(Regs.GetReg32(reg), ReadModRM32(modrm))); } else { Regs.SetReg16(reg, And16(Regs.GetReg16(reg), ReadModRM16(modrm))); } return 3;
            case 0x24: Regs.AL = And8(Regs.AL, FetchByte()); return 4;
            case 0x25: if (_operandSize32) { Regs.EAX = And32(Regs.EAX, FetchDword()); } else { Regs.AX = And16(Regs.AX, FetchWord()); } return 4;

            // --- DAA ---
            case 0x27:
            {
                byte oldAL = Regs.AL;
                bool oldCF = GetFlag(CpuFlags.Carry);
                SetFlag(CpuFlags.Carry, false);
                if ((Regs.AL & 0x0F) > 9 || GetFlag(CpuFlags.AuxCarry))
                {
                    Regs.AL += 6;
                    SetFlag(CpuFlags.Carry, oldCF || (Regs.AL < oldAL));
                    SetFlag(CpuFlags.AuxCarry, true);
                }
                else
                    SetFlag(CpuFlags.AuxCarry, false);
                if (oldAL >= 0xA0 || oldCF)
                {
                    Regs.AL += 0x60;
                    SetFlag(CpuFlags.Carry, true);
                }
                UpdateFlags8(Regs.AL);
                return 4;
            }

            // --- Segment overrides ---
            case 0x26: _segmentOverride = 0; return DecodeAndExecute(); // ES:
            case 0x2E: _segmentOverride = 1; return DecodeAndExecute(); // CS:
            case 0x36: _segmentOverride = 2; return DecodeAndExecute(); // SS:
            case 0x3E: _segmentOverride = 3; return DecodeAndExecute(); // DS:

            // --- SUB ---
            case 0x28: modrm = FetchByte(); WriteModRM8(modrm, Sub8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7))); return 3;
            case 0x29: modrm = FetchByte(); if (_operandSize32) { WriteModRM32(modrm, Sub32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7))); } else { WriteModRM16(modrm, Sub16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); } return 3;
            case 0x2A: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Sub8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x2B: modrm = FetchByte(); reg = (modrm >> 3) & 7; if (_operandSize32) { Regs.SetReg32(reg, Sub32(Regs.GetReg32(reg), ReadModRM32(modrm))); } else { Regs.SetReg16(reg, Sub16(Regs.GetReg16(reg), ReadModRM16(modrm))); } return 3;
            case 0x2C: Regs.AL = Sub8(Regs.AL, FetchByte()); return 4;
            case 0x2D: if (_operandSize32) { Regs.EAX = Sub32(Regs.EAX, FetchDword()); } else { Regs.AX = Sub16(Regs.AX, FetchWord()); } return 4;

            // --- DAS ---
            case 0x2F:
            {
                byte oldAL = Regs.AL;
                bool oldCF = GetFlag(CpuFlags.Carry);
                SetFlag(CpuFlags.Carry, false);
                if ((Regs.AL & 0x0F) > 9 || GetFlag(CpuFlags.AuxCarry))
                {
                    Regs.AL -= 6;
                    SetFlag(CpuFlags.Carry, oldCF);
                    SetFlag(CpuFlags.AuxCarry, true);
                }
                else
                    SetFlag(CpuFlags.AuxCarry, false);
                if (oldAL >= 0xA0 || oldCF)
                {
                    Regs.AL -= 0x60;
                    SetFlag(CpuFlags.Carry, true);
                }
                UpdateFlags8(Regs.AL);
                return 4;
            }

            // --- XOR ---
            case 0x30: modrm = FetchByte(); WriteModRM8(modrm, Xor8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7))); return 3;
            case 0x31: modrm = FetchByte(); if (_operandSize32) { WriteModRM32(modrm, Xor32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7))); } else { WriteModRM16(modrm, Xor16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); } return 3;
            case 0x32: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Xor8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x33: modrm = FetchByte(); reg = (modrm >> 3) & 7; if (_operandSize32) { Regs.SetReg32(reg, Xor32(Regs.GetReg32(reg), ReadModRM32(modrm))); } else { Regs.SetReg16(reg, Xor16(Regs.GetReg16(reg), ReadModRM16(modrm))); } return 3;
            case 0x34: Regs.AL = Xor8(Regs.AL, FetchByte()); return 4;
            case 0x35: if (_operandSize32) { Regs.EAX = Xor32(Regs.EAX, FetchDword()); } else { Regs.AX = Xor16(Regs.AX, FetchWord()); } return 4;

            // --- AAA ---
            case 0x37:
            {
                if ((Regs.AL & 0x0F) > 9 || GetFlag(CpuFlags.AuxCarry))
                {
                    // Add 6 to AL and 1 to AH separately to avoid carry propagation
                    Regs.AL = (byte)(Regs.AL + 6);
                    Regs.AH = (byte)(Regs.AH + 1);
                    SetFlag(CpuFlags.AuxCarry, true);
                    SetFlag(CpuFlags.Carry, true);
                }
                else
                {
                    SetFlag(CpuFlags.AuxCarry, false);
                    SetFlag(CpuFlags.Carry, false);
                }
                Regs.AL &= 0x0F;
                return 4;
            }

            // --- CMP ---
            case 0x38: modrm = FetchByte(); Sub8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7)); return 3;
            case 0x39: modrm = FetchByte(); if (_operandSize32) { Sub32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7)); } else { Sub16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7)); } return 3;
            case 0x3A: modrm = FetchByte(); reg = (modrm >> 3) & 7; Sub8(Regs.GetReg8(reg), ReadModRM8(modrm)); return 3;
            case 0x3B: modrm = FetchByte(); reg = (modrm >> 3) & 7; if (_operandSize32) { Sub32(Regs.GetReg32(reg), ReadModRM32(modrm)); } else { Sub16(Regs.GetReg16(reg), ReadModRM16(modrm)); } return 3;
            case 0x3C: Sub8(Regs.AL, FetchByte()); return 4;
            case 0x3D: if (_operandSize32) { Sub32(Regs.EAX, FetchDword()); } else { Sub16(Regs.AX, FetchWord()); } return 4;

            // --- AAS ---
            case 0x3F:
            {
                if ((Regs.AL & 0x0F) > 9 || GetFlag(CpuFlags.AuxCarry))
                {
                    Regs.AL = (byte)(Regs.AL - 6);
                    Regs.AH -= 1;
                    SetFlag(CpuFlags.AuxCarry, true);
                    SetFlag(CpuFlags.Carry, true);
                }
                else
                {
                    SetFlag(CpuFlags.AuxCarry, false);
                    SetFlag(CpuFlags.Carry, false);
                }
                Regs.AL &= 0x0F;
                return 4;
            }

            // --- INC reg16 (0x40-0x47) ---
            case >= 0x40 and <= 0x47:
                reg = opcode - 0x40;
                if (_operandSize32) { bool cf = GetFlag(CpuFlags.Carry); Regs.SetReg32(reg, Add32(Regs.GetReg32(reg), 1)); SetFlag(CpuFlags.Carry, cf); }
                else { bool cf = GetFlag(CpuFlags.Carry); Regs.SetReg16(reg, Add16(Regs.GetReg16(reg), 1)); SetFlag(CpuFlags.Carry, cf); }
                return 2;

            // --- DEC reg16 (0x48-0x4F) ---
            case >= 0x48 and <= 0x4F:
                reg = opcode - 0x48;
                if (_operandSize32) { bool cf = GetFlag(CpuFlags.Carry); Regs.SetReg32(reg, Sub32(Regs.GetReg32(reg), 1)); SetFlag(CpuFlags.Carry, cf); }
                else { bool cf = GetFlag(CpuFlags.Carry); Regs.SetReg16(reg, Sub16(Regs.GetReg16(reg), 1)); SetFlag(CpuFlags.Carry, cf); }
                return 2;

            // --- PUSH reg16 (0x50-0x57) ---
            case >= 0x50 and <= 0x57:
                if (_operandSize32) Push32(Regs.GetReg32(opcode - 0x50));
                else if (opcode == 0x54)
                {
                    // 8086/8088 quirk: PUSH SP pushes the DECREMENTED value of SP
                    Regs.SP -= 2;
                    _mem.WriteWord(Regs.SS, Regs.SP, Regs.SP);
                }
                else Push(Regs.GetReg16(opcode - 0x50));
                return 11;

            // --- POP reg16 (0x58-0x5F) ---
            case >= 0x58 and <= 0x5F:
                if (_operandSize32) Regs.SetReg32(opcode - 0x58, Pop32());
                else Regs.SetReg16(opcode - 0x58, Pop());
                return 8;

            // --- Jcc short (conditional jumps) ---
            case 0x70: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JO
            case 0x71: { sbyte off = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JNO
            case 0x72: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Carry)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JB/JC
            case 0x73: { sbyte off = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Carry)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JNB/JNC
            case 0x74: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JZ/JE
            case 0x75: { sbyte off = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JNZ/JNE
            case 0x76: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Carry) || GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JBE
            case 0x77: { sbyte off = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Carry) && !GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JA
            case 0x78: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Sign)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JS
            case 0x79: { sbyte off = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Sign)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JNS
            case 0x7A: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Parity)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JP
            case 0x7B: { sbyte off = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Parity)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JNP
            case 0x7C: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JL
            case 0x7D: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JGE
            case 0x7E: { sbyte off = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Zero) || (GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow))) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JLE
            case 0x7F: { sbyte off = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Zero) && (GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow))) Regs.IP = (ushort)(Regs.IP + off); return 4; } // JG

            // --- Group 1: immediate to r/m (0x80-0x83) ---
            case 0x80: return ExecuteGroup1_8(false);
            case 0x81: return _operandSize32 ? ExecuteGroup1_32(false) : ExecuteGroup1_16(false);
            case 0x82: return ExecuteGroup1_8(false); // Same as 0x80
            case 0x83: return _operandSize32 ? ExecuteGroup1_32(true) : ExecuteGroup1_16(true); // sign-extended byte

            // --- TEST ---
            case 0x84: modrm = FetchByte(); And8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7)); return 3;
            case 0x85: modrm = FetchByte(); if (_operandSize32) { And32(ReadModRM32(modrm), Regs.GetReg32((modrm >> 3) & 7)); } else { And16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7)); } return 3;

            // --- XCHG ---
            case 0x86: modrm = FetchByte(); { reg = (modrm >> 3) & 7; byte a = Regs.GetReg8(reg); byte b = ReadModRM8(modrm); Regs.SetReg8(reg, b); WriteModRM8(modrm, a); } return 4;
            case 0x87: modrm = FetchByte(); if (_operandSize32) { reg = (modrm >> 3) & 7; uint a = Regs.GetReg32(reg); uint b = ReadModRM32(modrm); Regs.SetReg32(reg, b); WriteModRM32(modrm, a); } else { reg = (modrm >> 3) & 7; ushort a = Regs.GetReg16(reg); ushort b = ReadModRM16(modrm); Regs.SetReg16(reg, b); WriteModRM16(modrm, a); } return 4;

            // --- MOV r/m, reg ---
            case 0x88: modrm = FetchByte(); WriteModRM8(modrm, Regs.GetReg8((modrm >> 3) & 7)); return 2;
            case 0x89: modrm = FetchByte(); if (_operandSize32) { WriteModRM32(modrm, Regs.GetReg32((modrm >> 3) & 7)); } else { WriteModRM16(modrm, Regs.GetReg16((modrm >> 3) & 7)); } return 2;
            // --- MOV reg, r/m ---
            case 0x8A: modrm = FetchByte(); Regs.SetReg8((modrm >> 3) & 7, ReadModRM8(modrm)); return 2;
            case 0x8B: modrm = FetchByte(); if (_operandSize32) { Regs.SetReg32((modrm >> 3) & 7, ReadModRM32(modrm)); } else { Regs.SetReg16((modrm >> 3) & 7, ReadModRM16(modrm)); } return 2;

            // --- MOV r/m, sreg ---
            case 0x8C: modrm = FetchByte(); WriteModRM16(modrm, Regs.GetSegReg((modrm >> 3) & 3)); return 2;
            // --- LEA ---
            case 0x8D: modrm = FetchByte(); { var (_, off) = DecodeModRM_Address(modrm); if (_operandSize32) Regs.SetReg32((modrm >> 3) & 7, off); else Regs.SetReg16((modrm >> 3) & 7, off); } return 2;
            // --- MOV sreg, r/m ---
            case 0x8E: modrm = FetchByte(); Regs.SetSegReg((modrm >> 3) & 3, ReadModRM16(modrm)); return 2;
            // --- POP r/m ---
            case 0x8F: modrm = FetchByte(); if (_operandSize32) WriteModRM32(modrm, Pop32()); else WriteModRM16(modrm, Pop()); return 8;

            // --- NOP / XCHG AX, reg ---
            case 0x90: return 3; // NOP
            case >= 0x91 and <= 0x97:
                if (_operandSize32)
                { reg = opcode - 0x90; uint tmp = Regs.EAX; Regs.EAX = Regs.GetReg32(reg); Regs.SetReg32(reg, tmp); }
                else
                { reg = opcode - 0x90; ushort tmp = Regs.AX; Regs.AX = Regs.GetReg16(reg); Regs.SetReg16(reg, tmp); }
                return 3;

            // --- CBW / CWD ---
            case 0x98:
                if (_operandSize32) Regs.EAX = (uint)(int)(short)Regs.AX; // CWDE
                else Regs.AX = (ushort)(sbyte)Regs.AL; // CBW
                return 2;
            case 0x99:
                if (_operandSize32) Regs.EDX = (Regs.EAX & 0x80000000) != 0 ? 0xFFFFFFFF : 0; // CDQ
                else Regs.DX = (ushort)((Regs.AX & 0x8000) != 0 ? 0xFFFF : 0); // CWD
                return 5;

            // --- WAIT/FWAIT ---
            case 0x9B: return 4; // WAIT (no FPU - NOP)

            // --- SAHF/LAHF ---
            case 0x9E: // SAHF - Store AH into flags (low byte)
            {
                ushort flags = (ushort)Regs.Flags;
                flags = (ushort)((flags & 0xFF00) | (Regs.AH & 0xD5) | 0x02);
                Regs.Flags = (CpuFlags)flags;
                return 4;
            }
            case 0x9F: // LAHF - Load flags into AH
                Regs.AH = (byte)((ushort)Regs.Flags & 0xFF);
                return 4;

            // --- CALL far ---
            case 0x9A:
            {
                ushort newIp = FetchWord();
                ushort newCs = FetchWord();
                Push(Regs.CS);
                Push(Regs.IP);
                Regs.CS = newCs;
                Regs.IP = newIp;
                return 28;
            }

            // --- PUSHF / POPF ---
            case 0x9C: if (_operandSize32) Push32((uint)Regs.Flags & 0xFFFF); else Push((ushort)((ushort)Regs.Flags | 0xF002)); return 10; // 8086: bits 12-15 always 1
            case 0x9D: if (_operandSize32) { Regs.Flags = (CpuFlags)((ushort)Pop32() | 0x0002); } else { Regs.Flags = (CpuFlags)((Pop() | 0xF002) & 0xFFD7); } return 8; // 8086: bits 12-15,1=1; bits 3,5=0

            // --- MOV AL/AX, [addr] ---
            case 0xA0: { ushort addr = FetchWord(); Regs.AL = _mem.ReadByte(GetDataSegment(), addr); } return 10;
            case 0xA1: { ushort addr = FetchWord(); if (_operandSize32) Regs.EAX = _mem.ReadDword(GetDataSegment(), addr); else Regs.AX = _mem.ReadWord(GetDataSegment(), addr); } return 10;
            // --- MOV [addr], AL/AX ---
            case 0xA2: { ushort addr = FetchWord(); _mem.WriteByte(GetDataSegment(), addr, Regs.AL); } return 10;
            case 0xA3: { ushort addr = FetchWord(); if (_operandSize32) _mem.WriteDword(GetDataSegment(), addr, Regs.EAX); else _mem.WriteWord(GetDataSegment(), addr, Regs.AX); } return 10;

            // --- MOVSB/MOVSW ---
            case 0xA4: ExecuteMovs(false); return 18;
            case 0xA5: ExecuteMovs(true); return 18;

            // --- CMPSB/CMPSW ---
            case 0xA6: ExecuteCmps(false); return 22;
            case 0xA7: ExecuteCmps(true); return 22;

            // --- TEST AL/AX, imm ---
            case 0xA8: And8(Regs.AL, FetchByte()); return 4;
            case 0xA9: if (_operandSize32) And32(Regs.EAX, FetchDword()); else And16(Regs.AX, FetchWord()); return 4;

            // --- STOSB/STOSW ---
            case 0xAA: ExecuteStos(false); return 11;
            case 0xAB: ExecuteStos(true); return 11;

            // --- LODSB/LODSW ---
            case 0xAC: ExecuteLods(false); return 12;
            case 0xAD: ExecuteLods(true); return 12;

            // --- SCASB/SCASW ---
            case 0xAE: ExecuteScas(false); return 15;
            case 0xAF: ExecuteScas(true); return 15;

            // --- MOV reg8, imm8 (0xB0-0xB7) ---
            case >= 0xB0 and <= 0xB7: Regs.SetReg8(opcode - 0xB0, FetchByte()); return 4;

            // --- MOV reg16, imm16 (0xB8-0xBF) ---
            case >= 0xB8 and <= 0xBF:
                if (_operandSize32) Regs.SetReg32(opcode - 0xB8, FetchDword());
                else Regs.SetReg16(opcode - 0xB8, FetchWord());
                return 4;

            // --- RET near (with/without pop) ---
            // On 8088: C0=alias C2, C1=alias C3. On 186+: C0=shift/rotate rm8,imm8; C1=shift/rotate rm16,imm8.
            case 0xC0:
                if (Model == CpuModel.Intel8088) { ushort popC0 = FetchWord(); Regs.IP = Pop(); Regs.SP += popC0; return 20; }
                { byte mC0 = FetchByte(); byte cntC0 = FetchByte(); return ExecuteShiftGroup_8(mC0, cntC0); } // shift/rotate r/m8, imm8 (186+)
            case 0xC1:
                if (Model == CpuModel.Intel8088) { Regs.IP = Pop(); return 8; }
                { byte mC1 = FetchByte(); byte cntC1 = FetchByte(); return _operandSize32 ? ExecuteShiftGroup_32(mC1, cntC1) : ExecuteShiftGroup_16(mC1, cntC1); } // shift/rotate r/m16, imm8 (186+)
            case 0xC2: { ushort pop = FetchWord(); Regs.IP = Pop(); Regs.SP += pop; } return 20;
            case 0xC3: Regs.IP = Pop(); return 8;

            // --- LES/LDS ---
            case 0xC4: modrm = FetchByte(); { var (seg, off) = DecodeModRM_Address(modrm); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, _mem.ReadWord(seg, off)); Regs.ES = _mem.ReadWord(seg, (ushort)(off + 2)); } return 16;
            case 0xC5: modrm = FetchByte(); { var (seg, off) = DecodeModRM_Address(modrm); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, _mem.ReadWord(seg, off)); Regs.DS = _mem.ReadWord(seg, (ushort)(off + 2)); } return 16;

            // --- MOV r/m, imm ---
            // Must decode ModR/M (consuming displacement) BEFORE fetching immediate
            case 0xC6:
            {
                modrm = FetchByte();
                if (((modrm >> 6) & 3) == 3)
                    Regs.SetReg8(modrm & 7, FetchByte());
                else
                {
                    var (seg, off) = DecodeModRM_Address(modrm);
                    _mem.WriteByte(seg, off, FetchByte());
                }
                return 10;
            }
            case 0xC7:
            {
                modrm = FetchByte();
                if (_operandSize32)
                {
                    if (((modrm >> 6) & 3) == 3)
                        Regs.SetReg32(modrm & 7, FetchDword());
                    else
                    {
                        var (seg, off) = DecodeModRM_Address(modrm);
                        _mem.WriteDword(seg, off, FetchDword());
                    }
                }
                else
                {
                    if (((modrm >> 6) & 3) == 3)
                        Regs.SetReg16(modrm & 7, FetchWord());
                    else
                    {
                        var (seg, off) = DecodeModRM_Address(modrm);
                        _mem.WriteWord(seg, off, FetchWord());
                    }
                }
                return 10;
            }

            // --- RET far / ENTER / LEAVE ---
            // On 8088: C8=alias CA (RETF+pop), C9=alias CB (RETF). On 186+: C8=ENTER, C9=LEAVE.
            case 0xC8:
                if (Model == CpuModel.Intel8088) { ushort popC8 = FetchWord(); Regs.IP = Pop(); Regs.CS = Pop(); Regs.SP += popC8; return 25; }
            {
                // ENTER imm16, imm8 (186+)
                ushort allocSize = FetchWord();
                byte nestingLevel = (byte)(FetchByte() & 0x1F);
                Push(Regs.BP);
                ushort framePtr = Regs.SP;
                if (nestingLevel > 0)
                {
                    for (int i = 1; i < nestingLevel; i++)
                    {
                        Regs.BP -= 2;
                        Push(_mem.ReadWord(Regs.SS, Regs.BP));
                    }
                    Push(framePtr);
                }
                Regs.BP = framePtr;
                Regs.SP -= allocSize;
                return 15;
            }
            case 0xC9:
                if (Model == CpuModel.Intel8088) { Regs.IP = Pop(); Regs.CS = Pop(); return 18; }
                // LEAVE (186+)
                Regs.SP = Regs.BP;
                Regs.BP = Pop();
                return 4;
            case 0xCA: { ushort pop = FetchWord(); Regs.IP = Pop(); Regs.CS = Pop(); Regs.SP += pop; } return 25;
            case 0xCB: Regs.IP = Pop(); Regs.CS = Pop(); return 18;

            // --- INT ---
            case 0xCC: TriggerInterrupt(3); return 52; // INT 3
            case 0xCD: TriggerInterrupt(FetchByte()); return 51; // INT n
            case 0xCE: if (GetFlag(CpuFlags.Overflow)) TriggerInterrupt(4); return 4; // INTO

            // --- IRET ---
            case 0xCF: Regs.IP = Pop(); Regs.CS = Pop(); Regs.Flags = (CpuFlags)((Pop() | 0xF002) & 0xFFD7); return 24; // 8086: bits 12-15,1=1; bits 3,5=0

            // --- Shift/rotate group by 1 / CL ---
            case 0xD0: return ExecuteShiftGroup_8(FetchByte(), 1);
            case 0xD1: { byte m = FetchByte(); return _operandSize32 ? ExecuteShiftGroup_32(m, 1) : ExecuteShiftGroup_16(m, 1); }
            case 0xD2: return ExecuteShiftGroup_8(FetchByte(), Regs.CL);
            case 0xD3: { byte m = FetchByte(); return _operandSize32 ? ExecuteShiftGroup_32(m, Regs.CL) : ExecuteShiftGroup_16(m, Regs.CL); }

            // --- AAM ---
            case 0xD4:
            {
                byte imm = FetchByte(); // Usually 0x0A
                if (imm == 0) { TriggerInterrupt(0); return 4; }
                Regs.AH = (byte)(Regs.AL / imm);
                Regs.AL = (byte)(Regs.AL % imm);
                UpdateFlags8(Regs.AL);
                return 83;
            }
            // --- AAD ---
            case 0xD5:
            {
                byte imm = FetchByte(); // Usually 0x0A
                Regs.AL = (byte)(Regs.AH * imm + Regs.AL);
                Regs.AH = 0;
                UpdateFlags8(Regs.AL);
                return 60;
            }
            // --- SALC (undocumented: Set AL if Carry) ---
            case 0xD6:
                Regs.AL = GetFlag(CpuFlags.Carry) ? (byte)0xFF : (byte)0x00;
                return 3;
            // --- XLAT ---
            case 0xD7:
                Regs.AL = _mem.ReadByte(GetDataSegment(), (ushort)(Regs.BX + Regs.AL));
                return 11;

            // --- ESC (x87 FPU coprocessor escape) ---
            case 0xD8: case 0xD9: case 0xDA: case 0xDB:
            case 0xDC: case 0xDD: case 0xDE: case 0xDF:
            {
                byte fpOpcode = opcode;
                modrm = FetchByte();
                int fpMod = (modrm >> 6) & 3;
                ushort fpSeg = 0, fpOff = 0;
                if (fpMod != 3)
                {
                    (fpSeg, fpOff) = DecodeModRM_Address(modrm);
                }
                _fpu?.Execute(fpOpcode, modrm, fpSeg, fpOff);
                return 2;
            }

            // --- LOOP / LOOPcc ---
            case 0xE0: { sbyte off = (sbyte)FetchByte(); Regs.CX--; if (Regs.CX != 0 && !GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); } return 5; // LOOPNZ
            case 0xE1: { sbyte off = (sbyte)FetchByte(); Regs.CX--; if (Regs.CX != 0 && GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); } return 5; // LOOPZ
            case 0xE2: { sbyte off = (sbyte)FetchByte(); Regs.CX--; if (Regs.CX != 0) Regs.IP = (ushort)(Regs.IP + off); } return 5; // LOOP
            case 0xE3: { sbyte off = (sbyte)FetchByte(); if (Regs.CX == 0) Regs.IP = (ushort)(Regs.IP + off); } return 5; // JCXZ

            // --- CALL near ---
            case 0xE8: { short off = (short)FetchWord(); Push(Regs.IP); Regs.IP = (ushort)(Regs.IP + off); } return 19;

            // --- JMP near (rel16) ---
            case 0xE9: { short off = (short)FetchWord(); Regs.IP = (ushort)(Regs.IP + off); } return 15;
            // --- JMP far ---
            case 0xEA: { ushort newIp = FetchWord(); ushort newCs = FetchWord(); Regs.CS = newCs; Regs.IP = newIp; } return 15;
            // --- JMP short (rel8) ---
            case 0xEB: { sbyte off = (sbyte)FetchByte(); Regs.IP = (ushort)(Regs.IP + off); } return 15;

            // --- LOCK prefix (treat as NOP) ---
            case 0xF0: return DecodeAndExecute(); // LOCK prefix - execute next instruction without lock semantics

            // --- IN/OUT with I/O port bus ---
            case 0xE4: { byte port = FetchByte(); Regs.AL = _ports?.ReadByte(port) ?? 0xFF; } return 10; // IN AL, imm8
            case 0xE5: { byte port = FetchByte(); Regs.AX = _ports?.ReadWord(port) ?? 0xFFFF; } return 10; // IN AX, imm8
            case 0xE6: { byte port = FetchByte(); _ports?.WriteByte(port, Regs.AL); } return 10; // OUT imm8, AL
            case 0xE7: { byte port = FetchByte(); _ports?.WriteWord(port, Regs.AX); } return 10; // OUT imm8, AX
            case 0xEC: Regs.AL = _ports?.ReadByte(Regs.DX) ?? 0xFF; return 8; // IN AL, DX
            case 0xED: Regs.AX = _ports?.ReadWord(Regs.DX) ?? 0xFFFF; return 8; // IN AX, DX
            case 0xEE: _ports?.WriteByte(Regs.DX, Regs.AL); return 8; // OUT DX, AL
            case 0xEF: _ports?.WriteWord(Regs.DX, Regs.AX); return 8; // OUT DX, AX

            // --- REP/REPZ/REPNZ prefixes ---
            case 0xF2: return ExecuteRep(false); // REPNZ
            case 0xF3: return ExecuteRep(true);  // REP/REPZ

            // --- HLT ---
            case 0xF4: _halted = true; return 2;

            // --- CMC ---
            case 0xF5: SetFlag(CpuFlags.Carry, !GetFlag(CpuFlags.Carry)); return 2;

            // --- Group 3: unary (0xF6, 0xF7) ---
            case 0xF6: return ExecuteGroup3_8();
            case 0xF7: return _operandSize32 ? ExecuteGroup3_32() : ExecuteGroup3_16();

            // --- CLC/STC/CLI/STI/CLD/STD ---
            case 0xF8: SetFlag(CpuFlags.Carry, false); return 2;
            case 0xF9: SetFlag(CpuFlags.Carry, true); return 2;
            case 0xFA: SetFlag(CpuFlags.Interrupt, false); return 2;
            case 0xFB: SetFlag(CpuFlags.Interrupt, true); return 2;
            case 0xFC: SetFlag(CpuFlags.Direction, false); return 2;
            case 0xFD: SetFlag(CpuFlags.Direction, true); return 2;

            // --- 0x60-0x6F: On 8088 these are Jcc aliases; on 186+ they are new instructions ---
            case 0x60:
                if (Model == CpuModel.Intel8088) { sbyte off60 = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off60); return 4; } // JO alias
                if (_operandSize32) { Push32(Regs.EAX); Push32(Regs.ECX); Push32(Regs.EDX); Push32(Regs.EBX); uint tmpESP = Regs.ESP; Push32(tmpESP); Push32(Regs.EBP); Push32(Regs.ESI); Push32(Regs.EDI); } // PUSHAD
                else { Push(Regs.AX); Push(Regs.CX); Push(Regs.DX); Push(Regs.BX); ushort tmpSP60 = Regs.SP; Push(tmpSP60); Push(Regs.BP); Push(Regs.SI); Push(Regs.DI); } // PUSHA
                return 19;
            case 0x61:
                if (Model == CpuModel.Intel8088) { sbyte off61 = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off61); return 4; } // JNO alias
                if (_operandSize32) { Regs.EDI = Pop32(); Regs.ESI = Pop32(); Regs.EBP = Pop32(); Pop32(); Regs.EBX = Pop32(); Regs.EDX = Pop32(); Regs.ECX = Pop32(); Regs.EAX = Pop32(); } // POPAD
                else { Regs.DI = Pop(); Regs.SI = Pop(); Regs.BP = Pop(); Pop(); Regs.BX = Pop(); Regs.DX = Pop(); Regs.CX = Pop(); Regs.AX = Pop(); } // POPA
                return 19;
            case 0x62:
                if (Model == CpuModel.Intel8088) { sbyte off62 = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Carry)) Regs.IP = (ushort)(Regs.IP + off62); return 4; } // JB alias
                SkipModRM(); return 10; // BOUND (186+)
            case 0x63:
                if (Model == CpuModel.Intel8088) { sbyte off63 = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Carry)) Regs.IP = (ushort)(Regs.IP + off63); return 4; } // JNB alias
                modrm = FetchByte(); return 10; // ARPL (286+)
            case 0x64:
                if (Model == CpuModel.Intel8088) { sbyte off64 = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off64); return 4; } // JZ alias
                _segmentOverride = 4; return DecodeAndExecute(); // FS: (386+)
            case 0x65:
                if (Model == CpuModel.Intel8088) { sbyte off65 = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off65); return 4; } // JNZ alias
                _segmentOverride = 5; return DecodeAndExecute(); // GS: (386+)
            case 0x66:
                if (Model == CpuModel.Intel8088) { sbyte off66 = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Carry) || GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off66); return 4; } // JBE alias
                _operandSize32 = !_operandSize32; return DecodeAndExecute(); // Operand-size (386+)
            case 0x67:
                if (Model == CpuModel.Intel8088) { sbyte off67 = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Carry) && !GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off67); return 4; } // JNBE alias
                _addressSize32 = !_addressSize32; return DecodeAndExecute(); // Address-size (386+)
            case 0x68:
                if (Model == CpuModel.Intel8088) { sbyte off68 = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Sign)) Regs.IP = (ushort)(Regs.IP + off68); return 4; } // JS alias
                if (_operandSize32) Push32(FetchDword()); else Push(FetchWord()); return 3; // PUSH imm (186+)
            case 0x69: // IMUL r16/32, r/m16/32, imm16/32 (186+)
                if (Model == CpuModel.Intel8088) { sbyte off69 = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Sign)) Regs.IP = (ushort)(Regs.IP + off69); return 4; } // JNS alias
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                int val69 = (short)ReadModRM16(modrm);
                int imm69 = (short)FetchWord();
                int result69 = val69 * imm69;
                Regs.SetReg16(reg, (ushort)(result69 & 0xFFFF));
                SetFlag(CpuFlags.Carry, result69 != (short)result69);
                SetFlag(CpuFlags.Overflow, result69 != (short)result69);
                return 21;
            }
            case 0x6A:
                if (Model == CpuModel.Intel8088) { sbyte off6a = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Parity)) Regs.IP = (ushort)(Regs.IP + off6a); return 4; } // JP alias
                Push((ushort)(short)(sbyte)FetchByte()); return 3; // PUSH imm8 (186+)
            case 0x6B: // IMUL r16, r/m16, imm8 (186+)
                if (Model == CpuModel.Intel8088) { sbyte off6b = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Parity)) Regs.IP = (ushort)(Regs.IP + off6b); return 4; } // JNP alias
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                int val6b = (short)ReadModRM16(modrm);
                int imm6b = (sbyte)FetchByte();
                int result6b = val6b * imm6b;
                Regs.SetReg16(reg, (ushort)(result6b & 0xFFFF));
                SetFlag(CpuFlags.Carry, result6b != (short)result6b);
                SetFlag(CpuFlags.Overflow, result6b != (short)result6b);
                return 21;
            }
            case 0x6C:
                if (Model == CpuModel.Intel8088) { sbyte off6c = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off6c); return 4; } // JL alias
                if (_ports != null) _mem.WriteByte(Regs.ES, Regs.DI, _ports.ReadByte(Regs.DX));
                Regs.DI = (ushort)(Regs.DI + (GetFlag(CpuFlags.Direction) ? -1 : 1)); return 14; // INSB (186+)
            case 0x6D:
                if (Model == CpuModel.Intel8088) { sbyte off6d = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off6d); return 4; } // JNL alias
                if (_ports != null) { ushort pw = _ports.ReadWord(Regs.DX); _mem.WriteWord(Regs.ES, Regs.DI, pw); }
                Regs.DI = (ushort)(Regs.DI + (GetFlag(CpuFlags.Direction) ? -2 : 2)); return 14; // INSW (186+)
            case 0x6E:
                if (Model == CpuModel.Intel8088) { sbyte off6e = (sbyte)FetchByte(); if (GetFlag(CpuFlags.Zero) || (GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow))) Regs.IP = (ushort)(Regs.IP + off6e); return 4; } // JLE alias
                _ports?.WriteByte(Regs.DX, _mem.ReadByte(GetDataSegment(), Regs.SI));
                Regs.SI = (ushort)(Regs.SI + (GetFlag(CpuFlags.Direction) ? -1 : 1)); return 14; // OUTSB (186+)
            case 0x6F:
                if (Model == CpuModel.Intel8088) { sbyte off6f = (sbyte)FetchByte(); if (!GetFlag(CpuFlags.Zero) && (GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow))) Regs.IP = (ushort)(Regs.IP + off6f); return 4; } // JNLE alias
                _ports?.WriteWord(Regs.DX, _mem.ReadWord(GetDataSegment(), Regs.SI));
                Regs.SI = (ushort)(Regs.SI + (GetFlag(CpuFlags.Direction) ? -2 : 2)); return 14; // OUTSW (186+)

            // --- Two-byte opcodes (0x0F prefix) ---
            case 0x0F: return DecodeAndExecute0F();

            // --- Group 4/5 (INC/DEC/CALL/JMP/PUSH) ---
            case 0xFE: return ExecuteGroup4();
            case 0xFF: return ExecuteGroup5();

            default:
                // Unimplemented opcode - treat as NOP with warning
                _log?.Warn("CPU", $"Unimplemented opcode: 0x{opcode:X2} at {Regs.CS:X4}:{(ushort)(Regs.IP - 1):X4}");
                return 1;
        }
    }

    private void TriggerInterrupt(byte vector)
    {
        // Push flags, CS, IP (same as hardware interrupt)
        // 8086: bits 12-15 always 1, bit 1 always 1 (same mask as PUSHF)
        Push((ushort)((ushort)Regs.Flags | 0xF002));
        Push(Regs.CS);
        Push(Regs.IP);
        SetFlag(CpuFlags.Interrupt, false);
        SetFlag(CpuFlags.Trap, false);

        // Load new CS:IP from Interrupt Vector Table (real 8088 behavior)
        uint ivtAddr = (uint)(vector * 4);
        Regs.IP = _mem.ReadWord(ivtAddr);
        Regs.CS = _mem.ReadWord(ivtAddr + 2);

        // Notify registered handlers (DOS kernel, BIOS, etc.)
        // Handlers can override CS:IP if they intercept the interrupt.
        InterruptTriggered?.Invoke(vector);
    }

    // --- String operations ---

    private void ExecuteMovs(bool isWord)
    {
        if (isWord)
        {
            _mem.WriteWord(Regs.ES, Regs.DI, _mem.ReadWord(GetDataSegment(), Regs.SI));
            int delta = GetFlag(CpuFlags.Direction) ? -2 : 2;
            Regs.SI = (ushort)(Regs.SI + delta);
            Regs.DI = (ushort)(Regs.DI + delta);
        }
        else
        {
            _mem.WriteByte(Regs.ES, Regs.DI, _mem.ReadByte(GetDataSegment(), Regs.SI));
            int delta = GetFlag(CpuFlags.Direction) ? -1 : 1;
            Regs.SI = (ushort)(Regs.SI + delta);
            Regs.DI = (ushort)(Regs.DI + delta);
        }
    }

    private void ExecuteCmps(bool isWord)
    {
        if (isWord)
        {
            ushort src = _mem.ReadWord(GetDataSegment(), Regs.SI);
            ushort dst = _mem.ReadWord(Regs.ES, Regs.DI);
            Sub16(src, dst);
            int delta = GetFlag(CpuFlags.Direction) ? -2 : 2;
            Regs.SI = (ushort)(Regs.SI + delta);
            Regs.DI = (ushort)(Regs.DI + delta);
        }
        else
        {
            byte src = _mem.ReadByte(GetDataSegment(), Regs.SI);
            byte dst = _mem.ReadByte(Regs.ES, Regs.DI);
            Sub8(src, dst);
            int delta = GetFlag(CpuFlags.Direction) ? -1 : 1;
            Regs.SI = (ushort)(Regs.SI + delta);
            Regs.DI = (ushort)(Regs.DI + delta);
        }
    }

    private void ExecuteStos(bool isWord)
    {
        if (isWord)
        {
            _mem.WriteWord(Regs.ES, Regs.DI, Regs.AX);
            Regs.DI = (ushort)(Regs.DI + (GetFlag(CpuFlags.Direction) ? -2 : 2));
        }
        else
        {
            _mem.WriteByte(Regs.ES, Regs.DI, Regs.AL);
            Regs.DI = (ushort)(Regs.DI + (GetFlag(CpuFlags.Direction) ? -1 : 1));
        }
    }

    private void ExecuteLods(bool isWord)
    {
        if (isWord)
        {
            Regs.AX = _mem.ReadWord(GetDataSegment(), Regs.SI);
            Regs.SI = (ushort)(Regs.SI + (GetFlag(CpuFlags.Direction) ? -2 : 2));
        }
        else
        {
            Regs.AL = _mem.ReadByte(GetDataSegment(), Regs.SI);
            Regs.SI = (ushort)(Regs.SI + (GetFlag(CpuFlags.Direction) ? -1 : 1));
        }
    }

    private void ExecuteScas(bool isWord)
    {
        if (isWord)
        {
            ushort val = _mem.ReadWord(Regs.ES, Regs.DI);
            Sub16(Regs.AX, val);
            Regs.DI = (ushort)(Regs.DI + (GetFlag(CpuFlags.Direction) ? -2 : 2));
        }
        else
        {
            byte val = _mem.ReadByte(Regs.ES, Regs.DI);
            Sub8(Regs.AL, val);
            Regs.DI = (ushort)(Regs.DI + (GetFlag(CpuFlags.Direction) ? -1 : 1));
        }
    }

    private int ExecuteRep(bool isRepz)
    {
        byte nextOp = FetchByte();

        // On the 8088, REP prefix only applies to string operations.
        // For non-string opcodes, REP is ignored and the instruction executes normally.
        bool isStringOp = nextOp is 0xA4 or 0xA5 or 0xA6 or 0xA7 or 0xAA or 0xAB or 0xAC or 0xAD or 0xAE or 0xAF;
        if (!isStringOp)
        {
            _repPrefix = true; // 8088 quirk: affects IDIV sign correction
            Regs.IP--; // un-read the byte
            return DecodeAndExecute(); // execute normally, REP ignored
        }

        int cycles = 0;
        bool isCompare = nextOp is 0xA6 or 0xA7 or 0xAE or 0xAF;

        while (Regs.CX != 0)
        {
            Regs.CX--;
            switch (nextOp)
            {
                // String ops (INS/OUTS 0x6C-0x6F don't exist on 8088 — those opcodes are Jcc aliases)
                case 0xA4: ExecuteMovs(false); break;
                case 0xA5: ExecuteMovs(true); break;
                case 0xA6: ExecuteCmps(false); break;
                case 0xA7: ExecuteCmps(true); break;
                case 0xAA: ExecuteStos(false); break;
                case 0xAB: ExecuteStos(true); break;
                case 0xAC: ExecuteLods(false); break;
                case 0xAD: ExecuteLods(true); break;
                case 0xAE: ExecuteScas(false); break;
                case 0xAF: ExecuteScas(true); break;
                default: return cycles; // Unknown string op
            }
            cycles += 2;

            if (isCompare)
            {
                if (isRepz && !GetFlag(CpuFlags.Zero)) break;  // REPZ: stop if not equal
                if (!isRepz && GetFlag(CpuFlags.Zero)) break;  // REPNZ: stop if equal
            }
        }
        return cycles;
    }

    // --- Group instruction helpers ---

    private int ExecuteGroup1_8(bool signExtend)
    {
        byte modrm = FetchByte();
        int op = (modrm >> 3) & 7;
        byte val = ReadModRM8(modrm);
        byte imm = FetchByte();

        byte result = op switch
        {
            0 => Add8(val, imm),           // ADD
            1 => Or8(val, imm),            // OR
            2 => Add8(val, imm, true),     // ADC
            3 => Sub8(val, imm, true),     // SBB
            4 => And8(val, imm),           // AND
            5 => Sub8(val, imm),           // SUB
            6 => Xor8(val, imm),           // XOR
            7 => Sub8(val, imm),           // CMP (discard result)
            _ => val
        };

        if (op != 7) WriteModRM8(modrm, result);
        return 4;
    }

    private int ExecuteGroup1_16(bool signExtendByte)
    {
        byte modrm = FetchByte();
        int op = (modrm >> 3) & 7;
        ushort val = ReadModRM16(modrm);
        ushort imm = signExtendByte ? (ushort)(short)(sbyte)FetchByte() : FetchWord();

        ushort result = op switch
        {
            0 => Add16(val, imm),
            1 => Or16(val, imm),
            2 => Add16(val, imm, true),
            3 => Sub16(val, imm, true),
            4 => And16(val, imm),
            5 => Sub16(val, imm),
            6 => Xor16(val, imm),
            7 => Sub16(val, imm),
            _ => val
        };

        if (op != 7) WriteModRM16(modrm, result);
        return 4;
    }

    private int ExecuteGroup3_8()
    {
        byte modrm = FetchByte();
        int op = (modrm >> 3) & 7;
        byte val = ReadModRM8(modrm);

        switch (op)
        {
            case 0: // TEST r/m8, imm8
            case 1:
                And8(val, FetchByte());
                break;
            case 2: // NOT
                WriteModRM8(modrm, (byte)~val);
                break;
            case 3: // NEG
                WriteModRM8(modrm, Sub8(0, val));
                SetFlag(CpuFlags.Carry, val != 0);
                break;
            case 4: // MUL
            {
                ushort result = (ushort)(Regs.AL * val);
                Regs.AX = result;
                bool highSet = Regs.AH != 0;
                SetFlag(CpuFlags.Carry, highSet);
                SetFlag(CpuFlags.Overflow, highSet);
                break;
            }
            case 5: // IMUL
            {
                short result = (short)((sbyte)Regs.AL * (sbyte)val);
                Regs.AX = (ushort)result;
                bool highSet = Regs.AH != (byte)((result < 0) ? 0xFF : 0x00);
                SetFlag(CpuFlags.Carry, highSet);
                SetFlag(CpuFlags.Overflow, highSet);
                break;
            }
            case 6: // DIV
                if (val == 0) { TriggerInterrupt(0); break; }
                {
                    ushort dividend = Regs.AX;
                    ushort q = (ushort)(dividend / val);
                    if (q > 0xFF)
                    {
                        // 8088: flags from internal overflow comparison (AH - divisor)
                        Sub8(Regs.AH, val);
                        TriggerInterrupt(0);
                        break;
                    }
                    Regs.AL = (byte)q;
                    Regs.AH = (byte)(dividend % val);
                }
                break;
            case 7: // IDIV
                if (val == 0) { TriggerInterrupt(0); break; }
                {
                    int dividend = (short)Regs.AX;
                    int divisor = (sbyte)val;
                    int q = dividend / divisor;
                    // 8088 quirk: REP/REPNE prefix negates the quotient sign
                    if (_repPrefix) q = -q;
                    if (q > 127 || q < -128)
                    {
                        // Compute flags from the 8088's internal division state
                        ushort absDvd = (ushort)Math.Abs(dividend);
                        byte absDvs = (byte)Math.Abs(divisor);
                        byte absHigh = (byte)(absDvd >> 8);
                        if (absHigh >= absDvs)
                        {
                            // Unsigned overflow: flags from initial comparison
                            Sub8(absHigh, absDvs);
                        }
                        else
                        {
                            // Signed-only overflow: flags from last division step
                            ushort unsignedQ = (ushort)(absDvd / absDvs);
                            byte unsignedR = (byte)(absDvd % absDvs);
                            if ((unsignedQ & 1) != 0)
                                Sub8((byte)(unsignedR + absDvs), absDvs);
                            else
                                Sub8(unsignedR, absDvs);
                            SetFlag(CpuFlags.Carry, false);
                        }
                        TriggerInterrupt(0);
                        break;
                    }
                    Regs.AL = (byte)q;
                    Regs.AH = (byte)(sbyte)(dividend % divisor);
                }
                break;
        }
        return 4;
    }

    private int ExecuteGroup3_16()
    {
        byte modrm = FetchByte();
        int op = (modrm >> 3) & 7;
        ushort val = ReadModRM16(modrm);

        switch (op)
        {
            case 0: // TEST
            case 1:
                And16(val, FetchWord());
                break;
            case 2: // NOT
                WriteModRM16(modrm, (ushort)~val);
                break;
            case 3: // NEG
                WriteModRM16(modrm, Sub16(0, val));
                SetFlag(CpuFlags.Carry, val != 0);
                break;
            case 4: // MUL
            {
                uint result = (uint)Regs.AX * val;
                Regs.AX = (ushort)result;
                Regs.DX = (ushort)(result >> 16);
                bool highSet = Regs.DX != 0;
                SetFlag(CpuFlags.Carry, highSet);
                SetFlag(CpuFlags.Overflow, highSet);
                break;
            }
            case 5: // IMUL
            {
                int result = (short)Regs.AX * (short)val;
                Regs.AX = (ushort)result;
                Regs.DX = (ushort)(result >> 16);
                bool highSet = Regs.DX != (ushort)((result < 0) ? 0xFFFF : 0);
                SetFlag(CpuFlags.Carry, highSet);
                SetFlag(CpuFlags.Overflow, highSet);
                break;
            }
            case 6: // DIV
                if (val == 0) { TriggerInterrupt(0); break; }
                {
                    uint dividend = (uint)((Regs.DX << 16) | Regs.AX);
                    uint q = dividend / val;
                    if (q > 0xFFFF)
                    {
                        // 8088: flags from internal overflow comparison (DX - divisor)
                        Sub16(Regs.DX, val);
                        TriggerInterrupt(0);
                        break;
                    }
                    Regs.AX = (ushort)q;
                    Regs.DX = (ushort)(dividend % val);
                }
                break;
            case 7: // IDIV
                if (val == 0) { TriggerInterrupt(0); break; }
                {
                    long dividend = (int)((Regs.DX << 16) | Regs.AX);
                    long divisor = (short)val;
                    long q = dividend / divisor;
                    // 8088 quirk: REP/REPNE prefix negates the quotient sign
                    if (_repPrefix) q = -q;
                    if (q > 32767 || q < -32768)
                    {
                        // Compute flags from the 8088's internal division state
                        uint absDvd = (uint)Math.Abs(dividend);
                        ushort absDvs = (ushort)Math.Abs(divisor);
                        ushort absHigh = (ushort)(absDvd >> 16);
                        if (absHigh >= absDvs)
                        {
                            // Unsigned overflow: flags from initial comparison
                            Sub16(absHigh, absDvs);
                        }
                        else
                        {
                            // Signed-only overflow: flags from last division step
                            uint unsignedQ = absDvd / absDvs;
                            ushort unsignedR = (ushort)(absDvd % absDvs);
                            if ((unsignedQ & 1) != 0)
                                Sub16((ushort)(unsignedR + absDvs), absDvs);
                            else
                                Sub16(unsignedR, absDvs);
                            SetFlag(CpuFlags.Carry, false);
                        }
                        TriggerInterrupt(0);
                        break;
                    }
                    Regs.AX = (ushort)q;
                    Regs.DX = (ushort)(short)(dividend % divisor);
                }
                break;
        }
        return 4;
    }

    private int ExecuteGroup4() // 0xFE: INC/DEC r/m8
    {
        byte modrm = FetchByte();
        int op = (modrm >> 3) & 7;
        byte val = ReadModRM8(modrm);
        bool cf = GetFlag(CpuFlags.Carry);

        switch (op)
        {
            case 0: WriteModRM8(modrm, Add8(val, 1)); break; // INC
            case 1: WriteModRM8(modrm, Sub8(val, 1)); break; // DEC
        }
        SetFlag(CpuFlags.Carry, cf); // INC/DEC don't affect CF
        return 3;
    }

    private int ExecuteGroup5() // 0xFF: INC/DEC/CALL/JMP/PUSH r/m16
    {
        byte modrm = FetchByte();
        int op = (modrm >> 3) & 7;

        switch (op)
        {
            case 0: // INC r/m16
            {
                ushort val = ReadModRM16(modrm);
                bool cf = GetFlag(CpuFlags.Carry);
                WriteModRM16(modrm, Add16(val, 1));
                SetFlag(CpuFlags.Carry, cf);
                return 3;
            }
            case 1: // DEC r/m16
            {
                ushort val = ReadModRM16(modrm);
                bool cf = GetFlag(CpuFlags.Carry);
                WriteModRM16(modrm, Sub16(val, 1));
                SetFlag(CpuFlags.Carry, cf);
                return 3;
            }
            case 2: // CALL r/m16 (near indirect)
            {
                ushort target = ReadModRM16(modrm);
                Push(Regs.IP);
                Regs.IP = target;
                return 16;
            }
            case 3: // CALL far indirect
            {
                var (seg, off) = DecodeModRM_Address(modrm);
                ushort newIp = _mem.ReadWord(seg, off);
                ushort newCs = _mem.ReadWord(seg, (ushort)(off + 2));
                Push(Regs.CS);
                Push(Regs.IP);
                Regs.CS = newCs;
                Regs.IP = newIp;
                return 37;
            }
            case 4: // JMP r/m16 (near indirect)
                Regs.IP = ReadModRM16(modrm);
                return 11;
            case 5: // JMP far indirect
            {
                var (seg, off) = DecodeModRM_Address(modrm);
                Regs.IP = _mem.ReadWord(seg, off);
                Regs.CS = _mem.ReadWord(seg, (ushort)(off + 2));
                return 18;
            }
            case 6: // PUSH r/m16
            case 7: // Undocumented 8088 alias for PUSH r/m16
            {
                // 8086/8088 quirk: PUSH SP pushes the DECREMENTED value
                int rm = modrm & 7;
                int mod = (modrm >> 6) & 3;
                if (mod == 3 && rm == 4) // register-direct SP
                {
                    Regs.SP -= 2;
                    _mem.WriteWord(Regs.SS, Regs.SP, Regs.SP);
                }
                else
                {
                    Push(ReadModRM16(modrm));
                }
                return 16;
            }
        }
        return 1;
    }

    private int ExecuteShiftGroup_8(byte modrm, byte count)
    {
        int op = (modrm >> 3) & 7;
        byte val = ReadModRM8(modrm);
        // Note: 8088 does NOT mask count by 0x1F (that was added in 80186).

        for (int i = 0; i < count; i++)
        {
            switch (op)
            {
                case 0: // ROL
                {
                    bool msb = (val & 0x80) != 0;
                    val = (byte)((val << 1) | (msb ? 1 : 0));
                    SetFlag(CpuFlags.Carry, msb);
                    // OF = MSB(result) XOR CF
                    SetFlag(CpuFlags.Overflow, ((val >> 7) ^ (val & 1)) != 0);
                    break;
                }
                case 1: // ROR
                {
                    bool lsb = (val & 1) != 0;
                    val = (byte)((val >> 1) | (lsb ? 0x80 : 0));
                    SetFlag(CpuFlags.Carry, lsb);
                    // OF = bit7 XOR bit6 of result
                    SetFlag(CpuFlags.Overflow, (((val >> 7) ^ (val >> 6)) & 1) != 0);
                    break;
                }
                case 2: // RCL
                {
                    bool cf = GetFlag(CpuFlags.Carry);
                    SetFlag(CpuFlags.Carry, (val & 0x80) != 0);
                    val = (byte)((val << 1) | (cf ? 1 : 0));
                    // OF = MSB(result) XOR CF
                    SetFlag(CpuFlags.Overflow, (((val >> 7) & 1) != 0) != GetFlag(CpuFlags.Carry));
                    break;
                }
                case 3: // RCR
                {
                    bool cf = GetFlag(CpuFlags.Carry);
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val = (byte)((val >> 1) | (cf ? 0x80 : 0));
                    // OF = bit7 XOR bit6 of result
                    SetFlag(CpuFlags.Overflow, (((val >> 7) ^ (val >> 6)) & 1) != 0);
                    break;
                }
                case 4: // SHL
                    SetFlag(CpuFlags.Carry, (val & 0x80) != 0);
                    val <<= 1;
                    // OF = MSB(result) XOR CF
                    SetFlag(CpuFlags.Overflow, (((val >> 7) & 1) != 0) != GetFlag(CpuFlags.Carry));
                    break;
                case 6: // SETMO (undocumented 8088: set to all 1s)
                    SetFlag(CpuFlags.Carry, (val & 0x80) != 0);
                    val = 0xFF;
                    SetFlag(CpuFlags.Overflow, (((val >> 7) & 1) != 0) != GetFlag(CpuFlags.Carry));
                    break;
                case 5: // SHR
                {
                    bool oldMsb = (val & 0x80) != 0;
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val >>= 1;
                    // OF = MSB of original value (before shift)
                    SetFlag(CpuFlags.Overflow, oldMsb);
                    break;
                }
                case 7: // SAR
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val = (byte)((sbyte)val >> 1);
                    // OF = 0 for SAR
                    SetFlag(CpuFlags.Overflow, false);
                    break;
            }
        }
        if (count > 0 && op >= 4) UpdateFlags8(val); // SZP flags only for shifts, not rotates
        WriteModRM8(modrm, val);
        return 2 + count * 4;
    }

    private int ExecuteShiftGroup_16(byte modrm, byte count)
    {
        int op = (modrm >> 3) & 7;
        ushort val = ReadModRM16(modrm);
        // Note: 8088 does NOT mask count by 0x1F (that was added in 80186).

        for (int i = 0; i < count; i++)
        {
            switch (op)
            {
                case 0: // ROL
                {
                    bool msb = (val & 0x8000) != 0;
                    val = (ushort)((val << 1) | (msb ? 1 : 0));
                    SetFlag(CpuFlags.Carry, msb);
                    // OF = MSB(result) XOR CF
                    SetFlag(CpuFlags.Overflow, ((val >> 15) ^ (val & 1)) != 0);
                    break;
                }
                case 1: // ROR
                {
                    bool lsb = (val & 1) != 0;
                    val = (ushort)((val >> 1) | (lsb ? 0x8000 : 0));
                    SetFlag(CpuFlags.Carry, lsb);
                    // OF = bit15 XOR bit14 of result
                    SetFlag(CpuFlags.Overflow, (((val >> 15) ^ (val >> 14)) & 1) != 0);
                    break;
                }
                case 2: // RCL
                {
                    bool cf = GetFlag(CpuFlags.Carry);
                    SetFlag(CpuFlags.Carry, (val & 0x8000) != 0);
                    val = (ushort)((val << 1) | (cf ? 1 : 0));
                    // OF = MSB(result) XOR CF
                    SetFlag(CpuFlags.Overflow, (((val >> 15) & 1) != 0) != GetFlag(CpuFlags.Carry));
                    break;
                }
                case 3: // RCR
                {
                    bool cf = GetFlag(CpuFlags.Carry);
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val = (ushort)((val >> 1) | (cf ? 0x8000 : 0));
                    // OF = bit15 XOR bit14 of result
                    SetFlag(CpuFlags.Overflow, (((val >> 15) ^ (val >> 14)) & 1) != 0);
                    break;
                }
                case 4: // SHL
                    SetFlag(CpuFlags.Carry, (val & 0x8000) != 0);
                    val <<= 1;
                    // OF = MSB(result) XOR CF
                    SetFlag(CpuFlags.Overflow, (((val >> 15) & 1) != 0) != GetFlag(CpuFlags.Carry));
                    break;
                case 6: // SETMO (undocumented 8088: set to all 1s)
                    SetFlag(CpuFlags.Carry, (val & 0x8000) != 0);
                    val = 0xFFFF;
                    SetFlag(CpuFlags.Overflow, (((val >> 15) & 1) != 0) != GetFlag(CpuFlags.Carry));
                    break;
                case 5: // SHR
                {
                    bool oldMsb = (val & 0x8000) != 0;
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val >>= 1;
                    // OF = MSB of original value (before shift)
                    SetFlag(CpuFlags.Overflow, oldMsb);
                    break;
                }
                case 7: // SAR
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val = (ushort)((short)val >> 1);
                    // OF = 0 for SAR
                    SetFlag(CpuFlags.Overflow, false);
                    break;
            }
        }
        if (count > 0 && op >= 4) UpdateFlags16(val); // SZP flags only for shifts, not rotates
        WriteModRM16(modrm, val);
        return 2 + count * 4;
    }

    // --- Two-byte opcodes (0x0F prefix) ---

    // --- 32-bit ModR/M accessors (for 0x66 prefix) ---

    private uint ReadModRM32(byte modrm)
    {
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;
        if (mod == 3) return Regs.GetReg32(rm);
        var (seg, off) = DecodeModRM_Address(modrm);
        return _mem.ReadDword(seg, off);
    }

    private void WriteModRM32(byte modrm, uint value)
    {
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;
        if (mod == 3) { Regs.SetReg32(rm, value); return; }
        var (seg, off) = DecodeModRM_Address(modrm);
        _mem.WriteDword(seg, off, value);
    }

    // --- 32-bit ALU operations ---

    private uint Add32(uint a, uint b, bool withCarry = false)
    {
        long carry = withCarry && GetFlag(CpuFlags.Carry) ? 1 : 0;
        long result = (long)a + b + carry;
        uint r = (uint)result;
        SetFlag(CpuFlags.Zero, r == 0);
        SetFlag(CpuFlags.Sign, (r & 0x80000000) != 0);
        SetFlag(CpuFlags.Parity, Parity((byte)(r & 0xFF)));
        SetFlag(CpuFlags.Carry, result > 0xFFFFFFFFL);
        SetFlag(CpuFlags.Overflow, ((a ^ r) & (b ^ r) & 0x80000000) != 0);
        SetFlag(CpuFlags.AuxCarry, ((a ^ b ^ r) & 0x10) != 0);
        return r;
    }

    private uint Sub32(uint a, uint b, bool withBorrow = false)
    {
        long borrow = withBorrow && GetFlag(CpuFlags.Carry) ? 1 : 0;
        long result = (long)a - b - borrow;
        uint r = (uint)result;
        SetFlag(CpuFlags.Zero, r == 0);
        SetFlag(CpuFlags.Sign, (r & 0x80000000) != 0);
        SetFlag(CpuFlags.Parity, Parity((byte)(r & 0xFF)));
        SetFlag(CpuFlags.Carry, result < 0);
        SetFlag(CpuFlags.Overflow, ((a ^ b) & (a ^ r) & 0x80000000) != 0);
        SetFlag(CpuFlags.AuxCarry, ((a ^ b ^ r) & 0x10) != 0);
        return r;
    }

    private uint And32(uint a, uint b) { uint r = a & b; SetFlag(CpuFlags.Zero, r == 0); SetFlag(CpuFlags.Sign, (r & 0x80000000) != 0); SetFlag(CpuFlags.Parity, Parity((byte)(r & 0xFF))); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }
    private uint Or32(uint a, uint b) { uint r = a | b; SetFlag(CpuFlags.Zero, r == 0); SetFlag(CpuFlags.Sign, (r & 0x80000000) != 0); SetFlag(CpuFlags.Parity, Parity((byte)(r & 0xFF))); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }
    private uint Xor32(uint a, uint b) { uint r = a ^ b; SetFlag(CpuFlags.Zero, r == 0); SetFlag(CpuFlags.Sign, (r & 0x80000000) != 0); SetFlag(CpuFlags.Parity, Parity((byte)(r & 0xFF))); SetFlag(CpuFlags.Carry, false); SetFlag(CpuFlags.Overflow, false); return r; }

    private void UpdateFlags32(uint result)
    {
        SetFlag(CpuFlags.Zero, result == 0);
        SetFlag(CpuFlags.Sign, (result & 0x80000000) != 0);
        SetFlag(CpuFlags.Parity, Parity((byte)(result & 0xFF)));
    }

    // --- 32-bit push/pop ---

    private void Push32(uint value)
    {
        Regs.SP -= 4;
        _mem.WriteDword(Regs.SS, Regs.SP, value);
    }

    private uint Pop32()
    {
        uint val = _mem.ReadDword(Regs.SS, Regs.SP);
        Regs.SP += 4;
        return val;
    }

    // --- 32-bit Group instructions ---

    private int ExecuteGroup1_32(bool signExtendByte)
    {
        byte modrm = FetchByte();
        int op = (modrm >> 3) & 7;
        uint val = ReadModRM32(modrm);
        uint imm = signExtendByte ? (uint)(int)(sbyte)FetchByte() : FetchDword();

        uint result = op switch
        {
            0 => Add32(val, imm),
            1 => Or32(val, imm),
            2 => Add32(val, imm, true),
            3 => Sub32(val, imm, true),
            4 => And32(val, imm),
            5 => Sub32(val, imm),
            6 => Xor32(val, imm),
            7 => Sub32(val, imm),
            _ => val
        };

        if (op != 7) WriteModRM32(modrm, result);
        return 4;
    }

    private int ExecuteGroup3_32()
    {
        byte modrm = FetchByte();
        int op = (modrm >> 3) & 7;
        uint val = ReadModRM32(modrm);

        switch (op)
        {
            case 0: case 1: And32(val, FetchDword()); break; // TEST
            case 2: WriteModRM32(modrm, ~val); break; // NOT
            case 3: WriteModRM32(modrm, Sub32(0, val)); SetFlag(CpuFlags.Carry, val != 0); break; // NEG
            case 4: // MUL
            {
                ulong result = (ulong)Regs.EAX * val;
                Regs.EAX = (uint)result;
                Regs.EDX = (uint)(result >> 32);
                bool highSet = Regs.EDX != 0;
                SetFlag(CpuFlags.Carry, highSet);
                SetFlag(CpuFlags.Overflow, highSet);
                break;
            }
            case 5: // IMUL
            {
                long result = (long)(int)Regs.EAX * (int)val;
                Regs.EAX = (uint)result;
                Regs.EDX = (uint)(result >> 32);
                bool highSet = Regs.EDX != (uint)((result < 0) ? 0xFFFFFFFF : 0);
                SetFlag(CpuFlags.Carry, highSet);
                SetFlag(CpuFlags.Overflow, highSet);
                break;
            }
            case 6: // DIV
                if (val == 0) { TriggerInterrupt(0); break; }
                { ulong dividend = ((ulong)Regs.EDX << 32) | Regs.EAX; ulong q = dividend / val; if (q > 0xFFFFFFFF) { TriggerInterrupt(0); break; } Regs.EAX = (uint)q; Regs.EDX = (uint)(dividend % val); }
                break;
            case 7: // IDIV
                if (val == 0) { TriggerInterrupt(0); break; }
                { long dividend = ((long)Regs.EDX << 32) | Regs.EAX; long q = dividend / (int)val; if (q > int.MaxValue || q < int.MinValue) { TriggerInterrupt(0); break; } Regs.EAX = (uint)(int)q; Regs.EDX = (uint)(int)(dividend % (int)val); }
                break;
        }
        return 4;
    }

    private int ExecuteShiftGroup_32(byte modrm, byte count)
    {
        int op = (modrm >> 3) & 7;
        uint val = ReadModRM32(modrm);
        count &= 0x1F;

        for (int i = 0; i < count; i++)
        {
            switch (op)
            {
                case 0: { bool msb = (val & 0x80000000) != 0; val = (val << 1) | (msb ? 1u : 0u); SetFlag(CpuFlags.Carry, msb); break; } // ROL
                case 1: { bool lsb = (val & 1) != 0; val = (val >> 1) | (lsb ? 0x80000000u : 0u); SetFlag(CpuFlags.Carry, lsb); break; } // ROR
                case 2: { bool cf = GetFlag(CpuFlags.Carry); SetFlag(CpuFlags.Carry, (val & 0x80000000) != 0); val = (val << 1) | (cf ? 1u : 0u); break; } // RCL
                case 3: { bool cf = GetFlag(CpuFlags.Carry); SetFlag(CpuFlags.Carry, (val & 1) != 0); val = (val >> 1) | (cf ? 0x80000000u : 0u); break; } // RCR
                case 4: case 6: SetFlag(CpuFlags.Carry, (val & 0x80000000) != 0); val <<= 1; break; // SHL
                case 5: SetFlag(CpuFlags.Carry, (val & 1) != 0); val >>= 1; break; // SHR
                case 7: SetFlag(CpuFlags.Carry, (val & 1) != 0); val = (uint)((int)val >> 1); break; // SAR
            }
        }
        if (count > 0) UpdateFlags32(val);
        WriteModRM32(modrm, val);
        return 2 + count * 4;
    }

    private int DecodeAndExecute0F()
    {
        byte op2 = FetchByte();
        byte modrm;
        int reg;

        switch (op2)
        {
            // --- CMOVcc (0x0F 0x40-0x4F, 386+/Pentium Pro) ---
            case >= 0x40 and <= 0x4F:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                bool cond = (op2 & 0x0F) switch
                {
                    0x0 => GetFlag(CpuFlags.Overflow),
                    0x1 => !GetFlag(CpuFlags.Overflow),
                    0x2 => GetFlag(CpuFlags.Carry),
                    0x3 => !GetFlag(CpuFlags.Carry),
                    0x4 => GetFlag(CpuFlags.Zero),
                    0x5 => !GetFlag(CpuFlags.Zero),
                    0x6 => GetFlag(CpuFlags.Carry) || GetFlag(CpuFlags.Zero),
                    0x7 => !GetFlag(CpuFlags.Carry) && !GetFlag(CpuFlags.Zero),
                    0x8 => GetFlag(CpuFlags.Sign),
                    0x9 => !GetFlag(CpuFlags.Sign),
                    0xA => GetFlag(CpuFlags.Parity),
                    0xB => !GetFlag(CpuFlags.Parity),
                    0xC => GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow),
                    0xD => GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow),
                    0xE => GetFlag(CpuFlags.Zero) || (GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow)),
                    0xF => !GetFlag(CpuFlags.Zero) && (GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow)),
                    _ => false
                };
                if (_operandSize32)
                {
                    uint src = ReadModRM32(modrm);
                    if (cond) Regs.SetReg32(reg, src);
                }
                else
                {
                    ushort src = ReadModRM16(modrm);
                    if (cond) Regs.SetReg16(reg, src);
                }
                return 4;
            }

            // --- Jcc near rel16 (0x0F 0x80 - 0x0F 0x8F) ---
            case 0x80: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JO
            case 0x81: { short off = (short)FetchWord(); if (!GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JNO
            case 0x82: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Carry)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JB
            case 0x83: { short off = (short)FetchWord(); if (!GetFlag(CpuFlags.Carry)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JNB
            case 0x84: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JZ
            case 0x85: { short off = (short)FetchWord(); if (!GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JNZ
            case 0x86: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Carry) || GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JBE
            case 0x87: { short off = (short)FetchWord(); if (!GetFlag(CpuFlags.Carry) && !GetFlag(CpuFlags.Zero)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JA
            case 0x88: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Sign)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JS
            case 0x89: { short off = (short)FetchWord(); if (!GetFlag(CpuFlags.Sign)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JNS
            case 0x8A: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Parity)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JP
            case 0x8B: { short off = (short)FetchWord(); if (!GetFlag(CpuFlags.Parity)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JNP
            case 0x8C: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JL
            case 0x8D: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow)) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JGE
            case 0x8E: { short off = (short)FetchWord(); if (GetFlag(CpuFlags.Zero) || (GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow))) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JLE
            case 0x8F: { short off = (short)FetchWord(); if (!GetFlag(CpuFlags.Zero) && (GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow))) Regs.IP = (ushort)(Regs.IP + off); return 7; } // JG

            // --- SETcc (0x0F 0x90-0x9F) ---
            case >= 0x90 and <= 0x9F:
            {
                modrm = FetchByte();
                bool cond = (op2 & 0x0F) switch
                {
                    0x0 => GetFlag(CpuFlags.Overflow),
                    0x1 => !GetFlag(CpuFlags.Overflow),
                    0x2 => GetFlag(CpuFlags.Carry),
                    0x3 => !GetFlag(CpuFlags.Carry),
                    0x4 => GetFlag(CpuFlags.Zero),
                    0x5 => !GetFlag(CpuFlags.Zero),
                    0x6 => GetFlag(CpuFlags.Carry) || GetFlag(CpuFlags.Zero),
                    0x7 => !GetFlag(CpuFlags.Carry) && !GetFlag(CpuFlags.Zero),
                    0x8 => GetFlag(CpuFlags.Sign),
                    0x9 => !GetFlag(CpuFlags.Sign),
                    0xA => GetFlag(CpuFlags.Parity),
                    0xB => !GetFlag(CpuFlags.Parity),
                    0xC => GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow),
                    0xD => GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow),
                    0xE => GetFlag(CpuFlags.Zero) || (GetFlag(CpuFlags.Sign) != GetFlag(CpuFlags.Overflow)),
                    0xF => !GetFlag(CpuFlags.Zero) && (GetFlag(CpuFlags.Sign) == GetFlag(CpuFlags.Overflow)),
                    _ => false
                };
                WriteModRM8(modrm, cond ? (byte)1 : (byte)0);
                return 4;
            }

            // --- MOVZX r16/32, r/m8 (0x0F 0xB6) ---
            case 0xB6:
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                if (_operandSize32) Regs.SetReg32(reg, ReadModRM8(modrm));
                else Regs.SetReg16(reg, ReadModRM8(modrm));
                return 3;

            // --- MOVZX r32, r/m16 (0x0F 0xB7) ---
            case 0xB7:
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                if (_operandSize32) Regs.SetReg32(reg, ReadModRM16(modrm));
                else Regs.SetReg16(reg, ReadModRM16(modrm));
                return 3;

            // --- MOVSX r16/32, r/m8 (0x0F 0xBE) ---
            case 0xBE:
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                if (_operandSize32) Regs.SetReg32(reg, (uint)(int)(sbyte)ReadModRM8(modrm));
                else Regs.SetReg16(reg, (ushort)(short)(sbyte)ReadModRM8(modrm));
                return 3;

            // --- MOVSX r32, r/m16 (0x0F 0xBF) ---
            case 0xBF:
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                if (_operandSize32) Regs.SetReg32(reg, (uint)(int)(short)ReadModRM16(modrm));
                else Regs.SetReg16(reg, ReadModRM16(modrm));
                return 3;

            // --- IMUL r16/32, r/m16/32 (0x0F 0xAF) ---
            case 0xAF:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                if (_operandSize32)
                {
                    long result = (long)(int)Regs.GetReg32(reg) * (int)ReadModRM32(modrm);
                    Regs.SetReg32(reg, (uint)result);
                    bool highSet = result != (int)result;
                    SetFlag(CpuFlags.Carry, highSet);
                    SetFlag(CpuFlags.Overflow, highSet);
                }
                else
                {
                    int result = (short)Regs.GetReg16(reg) * (short)ReadModRM16(modrm);
                    Regs.SetReg16(reg, (ushort)result);
                    bool highSet = result != (short)result;
                    SetFlag(CpuFlags.Carry, highSet);
                    SetFlag(CpuFlags.Overflow, highSet);
                }
                return 21;
            }

            // --- SHLD r/m16, reg, imm8 (0x0F 0xA4) ---
            case 0xA4:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort dst = ReadModRM16(modrm);
                ushort src = Regs.GetReg16(reg);
                byte cnt = (byte)(FetchByte() & 0x1F);
                if (cnt > 0)
                {
                    uint combined = (uint)(dst << 16) | src;
                    combined <<= cnt;
                    ushort result = (ushort)(combined >> 16);
                    SetFlag(CpuFlags.Carry, ((dst << (cnt - 1)) & 0x8000) != 0);
                    WriteModRM16(modrm, result);
                    UpdateFlags16(result);
                }
                return 3;
            }
            // --- SHLD r/m16, reg, CL (0x0F 0xA5) ---
            case 0xA5:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort dst = ReadModRM16(modrm);
                ushort src = Regs.GetReg16(reg);
                byte cnt = (byte)(Regs.CL & 0x1F);
                if (cnt > 0)
                {
                    uint combined = (uint)(dst << 16) | src;
                    combined <<= cnt;
                    ushort result = (ushort)(combined >> 16);
                    SetFlag(CpuFlags.Carry, ((dst << (cnt - 1)) & 0x8000) != 0);
                    WriteModRM16(modrm, result);
                    UpdateFlags16(result);
                }
                return 3;
            }

            // --- SHRD r/m16, reg, imm8 (0x0F 0xAC) ---
            case 0xAC:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort dst = ReadModRM16(modrm);
                ushort src = Regs.GetReg16(reg);
                byte cnt = (byte)(FetchByte() & 0x1F);
                if (cnt > 0)
                {
                    uint combined = (uint)(src << 16) | dst;
                    combined >>= cnt;
                    ushort result = (ushort)combined;
                    SetFlag(CpuFlags.Carry, ((dst >> (cnt - 1)) & 1) != 0);
                    WriteModRM16(modrm, result);
                    UpdateFlags16(result);
                }
                return 3;
            }
            // --- SHRD r/m16, reg, CL (0x0F 0xAD) ---
            case 0xAD:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort dst = ReadModRM16(modrm);
                ushort src = Regs.GetReg16(reg);
                byte cnt = (byte)(Regs.CL & 0x1F);
                if (cnt > 0)
                {
                    uint combined = (uint)(src << 16) | dst;
                    combined >>= cnt;
                    ushort result = (ushort)combined;
                    SetFlag(CpuFlags.Carry, ((dst >> (cnt - 1)) & 1) != 0);
                    WriteModRM16(modrm, result);
                    UpdateFlags16(result);
                }
                return 3;
            }

            // --- BT r/m16, reg (0x0F 0xA3) ---
            case 0xA3:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort val = ReadModRM16(modrm);
                int bit = Regs.GetReg16(reg) & 0x0F;
                SetFlag(CpuFlags.Carry, (val & (1 << bit)) != 0);
                return 3;
            }

            // --- BTS r/m16, reg (0x0F 0xAB) ---
            case 0xAB:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort val = ReadModRM16(modrm);
                int bit = Regs.GetReg16(reg) & 0x0F;
                SetFlag(CpuFlags.Carry, (val & (1 << bit)) != 0);
                WriteModRM16(modrm, (ushort)(val | (1 << bit)));
                return 6;
            }

            // --- BTR r/m16, reg (0x0F 0xB3) ---
            case 0xB3:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort val = ReadModRM16(modrm);
                int bit = Regs.GetReg16(reg) & 0x0F;
                SetFlag(CpuFlags.Carry, (val & (1 << bit)) != 0);
                WriteModRM16(modrm, (ushort)(val & ~(1 << bit)));
                return 6;
            }

            // --- BTC r/m16, reg (0x0F 0xBB) ---
            case 0xBB:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort val = ReadModRM16(modrm);
                int bit = Regs.GetReg16(reg) & 0x0F;
                SetFlag(CpuFlags.Carry, (val & (1 << bit)) != 0);
                WriteModRM16(modrm, (ushort)(val ^ (1 << bit)));
                return 6;
            }

            // --- BSF r16, r/m16 (0x0F 0xBC) ---
            case 0xBC:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort val = ReadModRM16(modrm);
                if (val == 0)
                {
                    SetFlag(CpuFlags.Zero, true);
                }
                else
                {
                    SetFlag(CpuFlags.Zero, false);
                    int pos = 0;
                    while ((val & (1 << pos)) == 0) pos++;
                    Regs.SetReg16(reg, (ushort)pos);
                }
                return 10;
            }

            // --- BSR r16, r/m16 (0x0F 0xBD) ---
            case 0xBD:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort val = ReadModRM16(modrm);
                if (val == 0)
                {
                    SetFlag(CpuFlags.Zero, true);
                }
                else
                {
                    SetFlag(CpuFlags.Zero, false);
                    int pos = 15;
                    while ((val & (1 << pos)) == 0) pos--;
                    Regs.SetReg16(reg, (ushort)pos);
                }
                return 10;
            }

            // --- Group BT imm (0x0F 0xBA) ---
            case 0xBA:
            {
                modrm = FetchByte();
                int op = (modrm >> 3) & 7;
                ushort val = ReadModRM16(modrm);
                int bit = FetchByte() & 0x0F;
                SetFlag(CpuFlags.Carry, (val & (1 << bit)) != 0);
                switch (op)
                {
                    case 4: break; // BT
                    case 5: WriteModRM16(modrm, (ushort)(val | (1 << bit))); break; // BTS
                    case 6: WriteModRM16(modrm, (ushort)(val & ~(1 << bit))); break; // BTR
                    case 7: WriteModRM16(modrm, (ushort)(val ^ (1 << bit))); break; // BTC
                }
                return 6;
            }

            // --- PUSH/POP FS/GS (386+) ---
            case 0xA0: Push(Regs.FS); return 3; // PUSH FS
            case 0xA1: Regs.FS = Pop(); return 3; // POP FS
            case 0xA8: Push(Regs.GS); return 3; // PUSH GS
            case 0xA9: Regs.GS = Pop(); return 3; // POP GS

            // --- CPUID (0x0F 0xA2) ---
            case 0xA2:
            {
                // Return a minimal 386-compatible CPUID
                switch (Regs.EAX)
                {
                    case 0: // Max leaf + vendor string
                        Regs.EAX = 1;
                        Regs.EBX = 0x756E6547; // "Genu"
                        Regs.EDX = 0x49656E69; // "ineI"
                        Regs.ECX = 0x6C65746E; // "ntel"
                        break;
                    case 1: // Family/Model/Stepping + Features
                        Regs.EAX = 0x00000300; // 386
                        Regs.EBX = 0;
                        Regs.ECX = 0;
                        Regs.EDX = 0x00000001; // FPU present (lie)
                        break;
                    default:
                        Regs.EAX = 0; Regs.EBX = 0; Regs.ECX = 0; Regs.EDX = 0;
                        break;
                }
                return 14;
            }

            // --- LSS r16, m16:16 (0x0F 0xB2) ---
            case 0xB2:
                modrm = FetchByte();
                { var (seg, off) = DecodeModRM_Address(modrm); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, _mem.ReadWord(seg, off)); Regs.SS = _mem.ReadWord(seg, (ushort)(off + 2)); }
                return 16;

            // --- LFS r16, m16:16 (0x0F 0xB4) ---
            case 0xB4:
                modrm = FetchByte();
                { var (seg, off) = DecodeModRM_Address(modrm); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, _mem.ReadWord(seg, off)); Regs.FS = _mem.ReadWord(seg, (ushort)(off + 2)); }
                return 16;

            // --- LGS r16, m16:16 (0x0F 0xB5) ---
            case 0xB5:
                modrm = FetchByte();
                { var (seg, off) = DecodeModRM_Address(modrm); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, _mem.ReadWord(seg, off)); Regs.GS = _mem.ReadWord(seg, (ushort)(off + 2)); }
                return 16;

            // --- CMPXCHG r/m8, reg8 (0x0F 0xB0) ---
            case 0xB0:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                byte rm8 = ReadModRM8(modrm);
                if (Regs.AL == rm8)
                {
                    SetFlag(CpuFlags.Zero, true);
                    WriteModRM8(modrm, Regs.GetReg8(reg));
                }
                else
                {
                    SetFlag(CpuFlags.Zero, false);
                    Regs.AL = rm8;
                }
                return 6;
            }

            // --- CMPXCHG r/m16, reg16 (0x0F 0xB1) ---
            case 0xB1:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort rm16 = ReadModRM16(modrm);
                if (Regs.AX == rm16)
                {
                    SetFlag(CpuFlags.Zero, true);
                    WriteModRM16(modrm, Regs.GetReg16(reg));
                }
                else
                {
                    SetFlag(CpuFlags.Zero, false);
                    Regs.AX = rm16;
                }
                return 6;
            }

            // --- XADD r/m8, reg8 (0x0F 0xC0) ---
            case 0xC0:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                byte a8 = ReadModRM8(modrm);
                byte b8 = Regs.GetReg8(reg);
                Regs.SetReg8(reg, a8);
                WriteModRM8(modrm, Add8(a8, b8));
                return 3;
            }

            // --- XADD r/m16, reg16 (0x0F 0xC1) ---
            case 0xC1:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                ushort a16 = ReadModRM16(modrm);
                ushort b16 = Regs.GetReg16(reg);
                Regs.SetReg16(reg, a16);
                WriteModRM16(modrm, Add16(a16, b16));
                return 3;
            }

            // --- 0x0F 0x01: SGDT/SIDT/LGDT/LIDT/SMSW/LMSW (Group 7) ---
            case 0x01:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7;
                switch (reg)
                {
                    case 0: // SGDT m — store GDTR (6 bytes: limit then base)
                    {
                        var (sg, of) = DecodeModRM_Address(modrm);
                        _mem.WriteWord(sg, of, Regs.GdtLimit);
                        _mem.WriteWord(sg, (ushort)(of + 2), (ushort)(Regs.GdtBase & 0xFFFF));
                        _mem.WriteByte(sg, (ushort)(of + 4), (byte)((Regs.GdtBase >> 16) & 0xFF));
                        _mem.WriteByte(sg, (ushort)(of + 5), (byte)((Regs.GdtBase >> 24) & 0xFF));
                        break;
                    }
                    case 1: // SIDT m — store IDTR (6 bytes)
                    {
                        var (sg, of) = DecodeModRM_Address(modrm);
                        _mem.WriteWord(sg, of, Regs.IdtLimit);
                        _mem.WriteWord(sg, (ushort)(of + 2), (ushort)(Regs.IdtBase & 0xFFFF));
                        _mem.WriteByte(sg, (ushort)(of + 4), (byte)((Regs.IdtBase >> 16) & 0xFF));
                        _mem.WriteByte(sg, (ushort)(of + 5), (byte)((Regs.IdtBase >> 24) & 0xFF));
                        break;
                    }
                    case 2: // LGDT m — load GDTR from memory (6 bytes)
                    {
                        var (sg, of) = DecodeModRM_Address(modrm);
                        Regs.GdtLimit = _mem.ReadWord(sg, of);
                        Regs.GdtBase = (uint)(_mem.ReadWord(sg, (ushort)(of + 2))
                            | (_mem.ReadByte(sg, (ushort)(of + 4)) << 16)
                            | (_mem.ReadByte(sg, (ushort)(of + 5)) << 24));
                        break;
                    }
                    case 3: // LIDT m — load IDTR from memory (6 bytes)
                    {
                        var (sg, of) = DecodeModRM_Address(modrm);
                        Regs.IdtLimit = _mem.ReadWord(sg, of);
                        Regs.IdtBase = (uint)(_mem.ReadWord(sg, (ushort)(of + 2))
                            | (_mem.ReadByte(sg, (ushort)(of + 4)) << 16)
                            | (_mem.ReadByte(sg, (ushort)(of + 5)) << 24));
                        break;
                    }
                    case 4: // SMSW r/m16 — store machine status word (low 16 of CR0)
                        WriteModRM16(modrm, (ushort)(Regs.CR0 & 0xFFFF));
                        break;
                    case 6: // LMSW r/m16 — load machine status word (sets low 16 of CR0, cannot clear PE)
                    {
                        ushort val = ReadModRM16(modrm);
                        // LMSW can set PE but cannot clear it
                        Regs.CR0 = (Regs.CR0 & 0xFFFF0001u) | val;
                        break;
                    }
                    default:
                        _log?.Warn("CPU", $"Unknown 0F 01 /r={reg}");
                        break;
                }
                return 11;
            }

            // --- MOV r32, CRn (0x0F 0x20) ---
            case 0x20:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7; // CRn
                int rm = modrm & 7;
                uint crVal = reg switch
                {
                    0 => Regs.CR0,
                    2 => Regs.CR2,
                    3 => Regs.CR3,
                    _ => 0
                };
                Regs.SetReg32(rm, crVal);
                return 6;
            }

            // --- MOV CRn, r32 (0x0F 0x22) ---
            case 0x22:
            {
                modrm = FetchByte();
                reg = (modrm >> 3) & 7; // CRn
                int rm = modrm & 7;
                uint val = Regs.GetReg32(rm);
                switch (reg)
                {
                    case 0: Regs.CR0 = val; break;
                    case 2: Regs.CR2 = val; break;
                    case 3: Regs.CR3 = val; break;
                }
                return 10;
            }

            default:
                _log?.Warn("CPU", $"Unimplemented 0x0F opcode: 0x0F 0x{op2:X2} at {Regs.CS:X4}:{(ushort)(Regs.IP - 2):X4}");
                return 1;
        }
    }
}
