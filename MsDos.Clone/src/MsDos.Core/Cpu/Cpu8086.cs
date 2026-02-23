using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Cpu;

/// <summary>
/// Intel 8086 CPU emulator. Implements fetch-decode-execute cycle with
/// support for the core instruction set needed to run DOS COM/EXE binaries.
/// </summary>
public sealed class Cpu8086
{
    public Registers Regs { get; } = new();
    private readonly MemoryBus _mem;
    private EmulatorLog? _log;
    private bool _halted;
    private int? _segmentOverride; // null = default, 0-3 = ES/CS/SS/DS

    /// <summary>Raised when an INT instruction is executed.</summary>
    public event Action<byte>? InterruptTriggered;

    public bool IsHalted => _halted;

    public Cpu8086(MemoryBus memory)
    {
        _mem = memory;
    }

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

    /// <summary>Execute a single instruction. Returns number of cycles (approximate).</summary>
    public int Step()
    {
        if (_halted) return 1;
        _segmentOverride = null;
        return DecodeAndExecute();
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

    private (ushort segment, ushort offset) DecodeModRM_Address(byte modrm)
    {
        int mod = (modrm >> 6) & 3;
        int rm = modrm & 7;

        ushort offset;
        ushort segment;

        if (mod == 0 && rm == 6)
        {
            // Direct address
            offset = FetchWord();
            segment = GetDataSegment();
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

    private void SetFlag(CpuFlags flag, bool set)
    {
        if (set) Regs.Flags |= flag;
        else Regs.Flags &= ~flag;
    }

    private bool GetFlag(CpuFlags flag) => (Regs.Flags & flag) != 0;

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
            case 0x01: modrm = FetchByte(); WriteModRM16(modrm, Add16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); return 3;
            case 0x02: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Add8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x03: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, Add16(Regs.GetReg16(reg), ReadModRM16(modrm))); return 3;
            case 0x04: Regs.AL = Add8(Regs.AL, FetchByte()); return 4;
            case 0x05: Regs.AX = Add16(Regs.AX, FetchWord()); return 4;

            // --- PUSH/POP segment ---
            case 0x06: Push(Regs.ES); return 10;
            case 0x07: Regs.ES = Pop(); return 8;
            case 0x0E: Push(Regs.CS); return 10;
            case 0x0F: Regs.CS = Pop(); return 8;
            case 0x16: Push(Regs.SS); return 10;
            case 0x17: Regs.SS = Pop(); return 8;
            case 0x1E: Push(Regs.DS); return 10;
            case 0x1F: Regs.DS = Pop(); return 8;

            // --- OR ---
            case 0x08: modrm = FetchByte(); WriteModRM8(modrm, Or8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7))); return 3;
            case 0x09: modrm = FetchByte(); WriteModRM16(modrm, Or16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); return 3;
            case 0x0A: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Or8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x0B: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, Or16(Regs.GetReg16(reg), ReadModRM16(modrm))); return 3;
            case 0x0C: Regs.AL = Or8(Regs.AL, FetchByte()); return 4;
            case 0x0D: Regs.AX = Or16(Regs.AX, FetchWord()); return 4;

            // --- ADC ---
            case 0x10: modrm = FetchByte(); WriteModRM8(modrm, Add8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7), true)); return 3;
            case 0x11: modrm = FetchByte(); WriteModRM16(modrm, Add16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7), true)); return 3;
            case 0x12: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Add8(Regs.GetReg8(reg), ReadModRM8(modrm), true)); return 3;
            case 0x13: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, Add16(Regs.GetReg16(reg), ReadModRM16(modrm), true)); return 3;
            case 0x14: Regs.AL = Add8(Regs.AL, FetchByte(), true); return 4;
            case 0x15: Regs.AX = Add16(Regs.AX, FetchWord(), true); return 4;

            // --- SBB ---
            case 0x18: modrm = FetchByte(); WriteModRM8(modrm, Sub8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7), true)); return 3;
            case 0x19: modrm = FetchByte(); WriteModRM16(modrm, Sub16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7), true)); return 3;
            case 0x1A: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Sub8(Regs.GetReg8(reg), ReadModRM8(modrm), true)); return 3;
            case 0x1B: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, Sub16(Regs.GetReg16(reg), ReadModRM16(modrm), true)); return 3;
            case 0x1C: Regs.AL = Sub8(Regs.AL, FetchByte(), true); return 4;
            case 0x1D: Regs.AX = Sub16(Regs.AX, FetchWord(), true); return 4;

            // --- AND ---
            case 0x20: modrm = FetchByte(); WriteModRM8(modrm, And8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7))); return 3;
            case 0x21: modrm = FetchByte(); WriteModRM16(modrm, And16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); return 3;
            case 0x22: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, And8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x23: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, And16(Regs.GetReg16(reg), ReadModRM16(modrm))); return 3;
            case 0x24: Regs.AL = And8(Regs.AL, FetchByte()); return 4;
            case 0x25: Regs.AX = And16(Regs.AX, FetchWord()); return 4;

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
                if (oldAL > 0x99 || oldCF)
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
            case 0x29: modrm = FetchByte(); WriteModRM16(modrm, Sub16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); return 3;
            case 0x2A: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Sub8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x2B: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, Sub16(Regs.GetReg16(reg), ReadModRM16(modrm))); return 3;
            case 0x2C: Regs.AL = Sub8(Regs.AL, FetchByte()); return 4;
            case 0x2D: Regs.AX = Sub16(Regs.AX, FetchWord()); return 4;

            // --- DAS ---
            case 0x2F:
            {
                byte oldAL = Regs.AL;
                bool oldCF = GetFlag(CpuFlags.Carry);
                SetFlag(CpuFlags.Carry, false);
                if ((Regs.AL & 0x0F) > 9 || GetFlag(CpuFlags.AuxCarry))
                {
                    Regs.AL -= 6;
                    SetFlag(CpuFlags.Carry, oldCF || (oldAL < 6));
                    SetFlag(CpuFlags.AuxCarry, true);
                }
                else
                    SetFlag(CpuFlags.AuxCarry, false);
                if (oldAL > 0x99 || oldCF)
                {
                    Regs.AL -= 0x60;
                    SetFlag(CpuFlags.Carry, true);
                }
                UpdateFlags8(Regs.AL);
                return 4;
            }

            // --- XOR ---
            case 0x30: modrm = FetchByte(); WriteModRM8(modrm, Xor8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7))); return 3;
            case 0x31: modrm = FetchByte(); WriteModRM16(modrm, Xor16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7))); return 3;
            case 0x32: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg8(reg, Xor8(Regs.GetReg8(reg), ReadModRM8(modrm))); return 3;
            case 0x33: modrm = FetchByte(); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, Xor16(Regs.GetReg16(reg), ReadModRM16(modrm))); return 3;
            case 0x34: Regs.AL = Xor8(Regs.AL, FetchByte()); return 4;
            case 0x35: Regs.AX = Xor16(Regs.AX, FetchWord()); return 4;

            // --- AAA ---
            case 0x37:
            {
                if ((Regs.AL & 0x0F) > 9 || GetFlag(CpuFlags.AuxCarry))
                {
                    Regs.AX += 0x106;
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
            case 0x39: modrm = FetchByte(); Sub16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7)); return 3;
            case 0x3A: modrm = FetchByte(); reg = (modrm >> 3) & 7; Sub8(Regs.GetReg8(reg), ReadModRM8(modrm)); return 3;
            case 0x3B: modrm = FetchByte(); reg = (modrm >> 3) & 7; Sub16(Regs.GetReg16(reg), ReadModRM16(modrm)); return 3;
            case 0x3C: Sub8(Regs.AL, FetchByte()); return 4;
            case 0x3D: Sub16(Regs.AX, FetchWord()); return 4;

            // --- AAS ---
            case 0x3F:
            {
                if ((Regs.AL & 0x0F) > 9 || GetFlag(CpuFlags.AuxCarry))
                {
                    Regs.AX -= 6;
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
                { bool cf = GetFlag(CpuFlags.Carry); Regs.SetReg16(reg, Add16(Regs.GetReg16(reg), 1)); SetFlag(CpuFlags.Carry, cf); }
                return 2;

            // --- DEC reg16 (0x48-0x4F) ---
            case >= 0x48 and <= 0x4F:
                reg = opcode - 0x48;
                { bool cf = GetFlag(CpuFlags.Carry); Regs.SetReg16(reg, Sub16(Regs.GetReg16(reg), 1)); SetFlag(CpuFlags.Carry, cf); }
                return 2;

            // --- PUSH reg16 (0x50-0x57) ---
            case >= 0x50 and <= 0x57: Push(Regs.GetReg16(opcode - 0x50)); return 11;

            // --- POP reg16 (0x58-0x5F) ---
            case >= 0x58 and <= 0x5F: Regs.SetReg16(opcode - 0x58, Pop()); return 8;

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
            case 0x81: return ExecuteGroup1_16(false);
            case 0x82: return ExecuteGroup1_8(false); // Same as 0x80
            case 0x83: return ExecuteGroup1_16(true); // sign-extended byte

            // --- TEST ---
            case 0x84: modrm = FetchByte(); And8(ReadModRM8(modrm), Regs.GetReg8((modrm >> 3) & 7)); return 3;
            case 0x85: modrm = FetchByte(); And16(ReadModRM16(modrm), Regs.GetReg16((modrm >> 3) & 7)); return 3;

            // --- XCHG ---
            case 0x86: modrm = FetchByte(); { reg = (modrm >> 3) & 7; byte a = Regs.GetReg8(reg); byte b = ReadModRM8(modrm); Regs.SetReg8(reg, b); WriteModRM8(modrm, a); } return 4;
            case 0x87: modrm = FetchByte(); { reg = (modrm >> 3) & 7; ushort a = Regs.GetReg16(reg); ushort b = ReadModRM16(modrm); Regs.SetReg16(reg, b); WriteModRM16(modrm, a); } return 4;

            // --- MOV r/m, reg ---
            case 0x88: modrm = FetchByte(); WriteModRM8(modrm, Regs.GetReg8((modrm >> 3) & 7)); return 2;
            case 0x89: modrm = FetchByte(); WriteModRM16(modrm, Regs.GetReg16((modrm >> 3) & 7)); return 2;
            // --- MOV reg, r/m ---
            case 0x8A: modrm = FetchByte(); Regs.SetReg8((modrm >> 3) & 7, ReadModRM8(modrm)); return 2;
            case 0x8B: modrm = FetchByte(); Regs.SetReg16((modrm >> 3) & 7, ReadModRM16(modrm)); return 2;

            // --- MOV r/m, sreg ---
            case 0x8C: modrm = FetchByte(); WriteModRM16(modrm, Regs.GetSegReg((modrm >> 3) & 3)); return 2;
            // --- LEA ---
            case 0x8D: modrm = FetchByte(); { var (_, off) = DecodeModRM_Address(modrm); Regs.SetReg16((modrm >> 3) & 7, off); } return 2;
            // --- MOV sreg, r/m ---
            case 0x8E: modrm = FetchByte(); Regs.SetSegReg((modrm >> 3) & 3, ReadModRM16(modrm)); return 2;
            // --- POP r/m ---
            case 0x8F: modrm = FetchByte(); WriteModRM16(modrm, Pop()); return 8;

            // --- NOP / XCHG AX, reg ---
            case 0x90: return 3; // NOP
            case >= 0x91 and <= 0x97:
                { reg = opcode - 0x90; ushort tmp = Regs.AX; Regs.AX = Regs.GetReg16(reg); Regs.SetReg16(reg, tmp); }
                return 3;

            // --- CBW / CWD ---
            case 0x98: Regs.AX = (ushort)(sbyte)Regs.AL; return 2; // CBW
            case 0x99: Regs.DX = (ushort)((Regs.AX & 0x8000) != 0 ? 0xFFFF : 0); return 5; // CWD

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
            case 0x9C: Push((ushort)Regs.Flags); return 10;
            case 0x9D: Regs.Flags = (CpuFlags)(Pop() | 0x0002); return 8; // bit 1 always set

            // --- MOV AL/AX, [addr] ---
            case 0xA0: { ushort addr = FetchWord(); Regs.AL = _mem.ReadByte(GetDataSegment(), addr); } return 10;
            case 0xA1: { ushort addr = FetchWord(); Regs.AX = _mem.ReadWord(GetDataSegment(), addr); } return 10;
            // --- MOV [addr], AL/AX ---
            case 0xA2: { ushort addr = FetchWord(); _mem.WriteByte(GetDataSegment(), addr, Regs.AL); } return 10;
            case 0xA3: { ushort addr = FetchWord(); _mem.WriteWord(GetDataSegment(), addr, Regs.AX); } return 10;

            // --- MOVSB/MOVSW ---
            case 0xA4: ExecuteMovs(false); return 18;
            case 0xA5: ExecuteMovs(true); return 18;

            // --- CMPSB/CMPSW ---
            case 0xA6: ExecuteCmps(false); return 22;
            case 0xA7: ExecuteCmps(true); return 22;

            // --- TEST AL/AX, imm ---
            case 0xA8: And8(Regs.AL, FetchByte()); return 4;
            case 0xA9: And16(Regs.AX, FetchWord()); return 4;

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
            case >= 0xB8 and <= 0xBF: Regs.SetReg16(opcode - 0xB8, FetchWord()); return 4;

            // --- Shift/rotate group (0xC0, 0xC1) ---
            case 0xC0: return ExecuteShiftGroup_8(FetchByte(), FetchByte());
            case 0xC1: return ExecuteShiftGroup_16(FetchByte(), FetchByte());

            // --- RET near (with/without pop) ---
            case 0xC2: { ushort pop = FetchWord(); Regs.IP = Pop(); Regs.SP += pop; } return 20;
            case 0xC3: Regs.IP = Pop(); return 8;

            // --- LES/LDS ---
            case 0xC4: modrm = FetchByte(); { var (seg, off) = DecodeModRM_Address(modrm); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, _mem.ReadWord(seg, off)); Regs.ES = _mem.ReadWord(seg, (ushort)(off + 2)); } return 16;
            case 0xC5: modrm = FetchByte(); { var (seg, off) = DecodeModRM_Address(modrm); reg = (modrm >> 3) & 7; Regs.SetReg16(reg, _mem.ReadWord(seg, off)); Regs.DS = _mem.ReadWord(seg, (ushort)(off + 2)); } return 16;

            // --- MOV r/m, imm ---
            case 0xC6: modrm = FetchByte(); WriteModRM8(modrm, FetchByte()); return 10;
            case 0xC7: modrm = FetchByte(); WriteModRM16(modrm, FetchWord()); return 10;

            // --- RET far (with/without pop) ---
            case 0xCA: { ushort pop = FetchWord(); Regs.IP = Pop(); Regs.CS = Pop(); Regs.SP += pop; } return 25;
            case 0xCB: Regs.IP = Pop(); Regs.CS = Pop(); return 18;

            // --- INT ---
            case 0xCC: TriggerInterrupt(3); return 52; // INT 3
            case 0xCD: TriggerInterrupt(FetchByte()); return 51; // INT n
            case 0xCE: if (GetFlag(CpuFlags.Overflow)) TriggerInterrupt(4); return 4; // INTO

            // --- IRET ---
            case 0xCF: Regs.IP = Pop(); Regs.CS = Pop(); Regs.Flags = (CpuFlags)Pop(); return 24;

            // --- Shift/rotate group by 1 / CL ---
            case 0xD0: return ExecuteShiftGroup_8(FetchByte(), 1);
            case 0xD1: return ExecuteShiftGroup_16(FetchByte(), 1);
            case 0xD2: return ExecuteShiftGroup_8(FetchByte(), Regs.CL);
            case 0xD3: return ExecuteShiftGroup_16(FetchByte(), Regs.CL);

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
            // --- XLAT ---
            case 0xD7:
                Regs.AL = _mem.ReadByte(GetDataSegment(), (ushort)(Regs.BX + Regs.AL));
                return 11;

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

            // --- IN/OUT (simplified - do nothing meaningful) ---
            case 0xE4: FetchByte(); return 10; // IN AL, imm8
            case 0xE5: FetchByte(); return 10; // IN AX, imm8
            case 0xE6: FetchByte(); return 10; // OUT imm8, AL
            case 0xE7: FetchByte(); return 10; // OUT imm8, AX
            case 0xEC: return 8; // IN AL, DX
            case 0xED: return 8; // IN AX, DX
            case 0xEE: return 8; // OUT DX, AL
            case 0xEF: return 8; // OUT DX, AX

            // --- REP/REPZ/REPNZ prefixes ---
            case 0xF2: return ExecuteRep(false); // REPNZ
            case 0xF3: return ExecuteRep(true);  // REP/REPZ

            // --- HLT ---
            case 0xF4: _halted = true; return 2;

            // --- CMC ---
            case 0xF5: SetFlag(CpuFlags.Carry, !GetFlag(CpuFlags.Carry)); return 2;

            // --- Group 3: unary (0xF6, 0xF7) ---
            case 0xF6: return ExecuteGroup3_8();
            case 0xF7: return ExecuteGroup3_16();

            // --- CLC/STC/CLI/STI/CLD/STD ---
            case 0xF8: SetFlag(CpuFlags.Carry, false); return 2;
            case 0xF9: SetFlag(CpuFlags.Carry, true); return 2;
            case 0xFA: SetFlag(CpuFlags.Interrupt, false); return 2;
            case 0xFB: SetFlag(CpuFlags.Interrupt, true); return 2;
            case 0xFC: SetFlag(CpuFlags.Direction, false); return 2;
            case 0xFD: SetFlag(CpuFlags.Direction, true); return 2;

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
        Push((ushort)Regs.Flags);
        Push(Regs.CS);
        Push(Regs.IP);
        SetFlag(CpuFlags.Interrupt, false);
        SetFlag(CpuFlags.Trap, false);

        // Notify registered handlers (DOS kernel, BIOS, etc.)
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
        int cycles = 0;
        bool isCompare = nextOp is 0xA6 or 0xA7 or 0xAE or 0xAF;

        while (Regs.CX != 0)
        {
            Regs.CX--;
            switch (nextOp)
            {
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
                { ushort dividend = Regs.AX; Regs.AL = (byte)(dividend / val); Regs.AH = (byte)(dividend % val); }
                break;
            case 7: // IDIV
                if (val == 0) { TriggerInterrupt(0); break; }
                { short dividend = (short)Regs.AX; Regs.AL = (byte)((sbyte)(dividend / (sbyte)val)); Regs.AH = (byte)((sbyte)(dividend % (sbyte)val)); }
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
                { uint dividend = (uint)(Regs.DX << 16 | Regs.AX); Regs.AX = (ushort)(dividend / val); Regs.DX = (ushort)(dividend % val); }
                break;
            case 7: // IDIV
                if (val == 0) { TriggerInterrupt(0); break; }
                { int dividend = (Regs.DX << 16) | Regs.AX; Regs.AX = (ushort)((short)(dividend / (short)val)); Regs.DX = (ushort)((short)(dividend % (short)val)); }
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
                Push(ReadModRM16(modrm));
                return 16;
        }
        return 1;
    }

    private int ExecuteShiftGroup_8(byte modrm, byte count)
    {
        int op = (modrm >> 3) & 7;
        byte val = ReadModRM8(modrm);
        count &= 0x1F;

        for (int i = 0; i < count; i++)
        {
            switch (op)
            {
                case 0: // ROL
                {
                    bool msb = (val & 0x80) != 0;
                    val = (byte)((val << 1) | (msb ? 1 : 0));
                    SetFlag(CpuFlags.Carry, msb);
                    break;
                }
                case 1: // ROR
                {
                    bool lsb = (val & 1) != 0;
                    val = (byte)((val >> 1) | (lsb ? 0x80 : 0));
                    SetFlag(CpuFlags.Carry, lsb);
                    break;
                }
                case 2: // RCL
                {
                    bool cf = GetFlag(CpuFlags.Carry);
                    SetFlag(CpuFlags.Carry, (val & 0x80) != 0);
                    val = (byte)((val << 1) | (cf ? 1 : 0));
                    break;
                }
                case 3: // RCR
                {
                    bool cf = GetFlag(CpuFlags.Carry);
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val = (byte)((val >> 1) | (cf ? 0x80 : 0));
                    break;
                }
                case 4: // SHL
                case 6:
                    SetFlag(CpuFlags.Carry, (val & 0x80) != 0);
                    val <<= 1;
                    break;
                case 5: // SHR
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val >>= 1;
                    break;
                case 7: // SAR
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val = (byte)((sbyte)val >> 1);
                    break;
            }
        }
        if (count > 0) UpdateFlags8(val);
        WriteModRM8(modrm, val);
        return 2 + count * 4;
    }

    private int ExecuteShiftGroup_16(byte modrm, byte count)
    {
        int op = (modrm >> 3) & 7;
        ushort val = ReadModRM16(modrm);
        count &= 0x1F;

        for (int i = 0; i < count; i++)
        {
            switch (op)
            {
                case 0: // ROL
                {
                    bool msb = (val & 0x8000) != 0;
                    val = (ushort)((val << 1) | (msb ? 1 : 0));
                    SetFlag(CpuFlags.Carry, msb);
                    break;
                }
                case 1: // ROR
                {
                    bool lsb = (val & 1) != 0;
                    val = (ushort)((val >> 1) | (lsb ? 0x8000 : 0));
                    SetFlag(CpuFlags.Carry, lsb);
                    break;
                }
                case 2: // RCL
                {
                    bool cf = GetFlag(CpuFlags.Carry);
                    SetFlag(CpuFlags.Carry, (val & 0x8000) != 0);
                    val = (ushort)((val << 1) | (cf ? 1 : 0));
                    break;
                }
                case 3: // RCR
                {
                    bool cf = GetFlag(CpuFlags.Carry);
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val = (ushort)((val >> 1) | (cf ? 0x8000 : 0));
                    break;
                }
                case 4:
                case 6: // SHL
                    SetFlag(CpuFlags.Carry, (val & 0x8000) != 0);
                    val <<= 1;
                    break;
                case 5: // SHR
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val >>= 1;
                    break;
                case 7: // SAR
                    SetFlag(CpuFlags.Carry, (val & 1) != 0);
                    val = (ushort)((short)val >> 1);
                    break;
            }
        }
        if (count > 0) UpdateFlags16(val);
        WriteModRM16(modrm, val);
        return 2 + count * 4;
    }
}
