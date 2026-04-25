using MsDos.Core.Memory;

namespace MsDos.Core.Cpu;

/// <summary>
/// Basic x87 FPU (8087/80287/80387) emulation.
/// Provides a floating-point register stack, basic arithmetic,
/// load/store, comparison, and control operations.
/// </summary>
public sealed class Fpu8087
{
    private readonly MemoryBus _mem;
    private readonly Cpu8086 _cpu;

    // ── Register stack (ST(0)-ST(7)) ──
    private readonly double[] _st = new double[8];
    private int _top; // Stack top pointer (0-7, wraps)

    // ── Control and status words ──
    private ushort _controlWord = 0x037F; // Default: all exceptions masked, round-to-nearest, 64-bit precision
    private ushort _statusWord;           // C0-C3 condition codes, TOP, exception flags
    private ushort _tagWord = 0xFFFF;     // All registers empty (11 binary for each)

    // Status word bit positions
    private const int StatusC0 = 8;
    private const int StatusC1 = 9;
    private const int StatusC2 = 10;
    private const int StatusC3 = 14;
    private const int StatusTop = 11; // 3-bit TOP field at bits 11-13

    public Fpu8087(Cpu8086 cpu, MemoryBus mem)
    {
        _cpu = cpu;
        _mem = mem;
        Reset();
    }

    /// <summary>Reset the FPU to its initial state (FINIT).</summary>
    public void Reset()
    {
        Array.Clear(_st);
        _top = 0;
        _controlWord = 0x037F;
        _statusWord = 0;
        _tagWord = 0xFFFF; // All empty
    }

    /// <summary>Get status word (for FSTSW/FNSTSW).</summary>
    public ushort StatusWord
    {
        get
        {
            // Encode TOP into status word
            return (ushort)((_statusWord & ~(0x07 << StatusTop)) | ((_top & 7) << StatusTop));
        }
    }

    /// <summary>Get/set control word.</summary>
    public ushort ControlWord { get => _controlWord; set => _controlWord = value; }

    /// <summary>Get tag word.</summary>
    public ushort TagWord => _tagWord;

    // ═══════════════════════════════════════════════════════════════
    //  STACK ACCESS
    // ═══════════════════════════════════════════════════════════════

    private ref double ST(int i) => ref _st[(_top + i) & 7];

    private void Push(double value)
    {
        _top = (_top - 1) & 7;
        _st[_top] = value;
        SetTag(_top, ClassifyTag(value));
    }

    private double Pop()
    {
        double val = _st[_top];
        SetTag(_top, 3); // Empty
        _top = (_top + 1) & 7;
        return val;
    }

    private void SetTag(int reg, int tag)
    {
        int shift = (reg & 7) * 2;
        _tagWord = (ushort)((_tagWord & ~(3 << shift)) | ((tag & 3) << shift));
    }

    private static int ClassifyTag(double value)
    {
        if (value == 0.0) return 1;       // Zero
        if (double.IsNaN(value) || double.IsInfinity(value)) return 2; // Special
        return 0; // Valid
    }

    private void SetConditionCodes(bool c0, bool c1, bool c2, bool c3)
    {
        _statusWord = (ushort)(_statusWord & ~((1 << StatusC0) | (1 << StatusC1) | (1 << StatusC2) | (1 << StatusC3)));
        if (c0) _statusWord |= (ushort)(1 << StatusC0);
        if (c1) _statusWord |= (ushort)(1 << StatusC1);
        if (c2) _statusWord |= (ushort)(1 << StatusC2);
        if (c3) _statusWord |= (ushort)(1 << StatusC3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  MEMORY HELPERS
    // ═══════════════════════════════════════════════════════════════

    private float ReadFloat(ushort seg, ushort off)
    {
        uint bits = _mem.ReadDword(seg, off);
        return BitConverter.Int32BitsToSingle((int)bits);
    }

    private double ReadDouble(ushort seg, ushort off)
    {
        ulong lo = _mem.ReadDword(seg, off);
        ulong hi = _mem.ReadDword(seg, (ushort)(off + 4));
        return BitConverter.Int64BitsToDouble((long)(lo | (hi << 32)));
    }

    private void WriteFloat(ushort seg, ushort off, float value)
    {
        _mem.WriteDword(seg, off, (uint)BitConverter.SingleToInt32Bits(value));
    }

    private void WriteDouble(ushort seg, ushort off, double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        _mem.WriteDword(seg, off, (uint)(bits & 0xFFFFFFFF));
        _mem.WriteDword(seg, (ushort)(off + 4), (uint)((ulong)bits >> 32));
    }

    // ═══════════════════════════════════════════════════════════════
    //  ESC INSTRUCTION DISPATCHER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Handle an x87 ESC instruction. Called from the CPU when it encounters
    /// opcodes 0xD8-0xDF. The CPU passes the opcode and pre-fetched ModR/M byte.
    /// </summary>
    /// <param name="opcode">The ESC opcode (0xD8-0xDF).</param>
    /// <param name="modrm">The ModR/M byte following the opcode.</param>
    /// <param name="seg">Segment for memory operand (if mod != 3).</param>
    /// <param name="off">Offset for memory operand (if mod != 3).</param>
    public void Execute(byte opcode, byte modrm, ushort seg, ushort off)
    {
        int mod = (modrm >> 6) & 3;
        int reg = (modrm >> 3) & 7; // opcode extension
        int rm = modrm & 7;

        switch (opcode)
        {
            case 0xD8: ExecuteD8(mod, reg, rm, seg, off); break;
            case 0xD9: ExecuteD9(mod, reg, rm, seg, off); break;
            case 0xDA: ExecuteDA(mod, reg, rm, seg, off); break;
            case 0xDB: ExecuteDB(mod, reg, rm, seg, off); break;
            case 0xDC: ExecuteDC(mod, reg, rm, seg, off); break;
            case 0xDD: ExecuteDD(mod, reg, rm, seg, off); break;
            case 0xDE: ExecuteDE(mod, reg, rm, seg, off); break;
            case 0xDF: ExecuteDF(mod, reg, rm, seg, off); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  0xD8 — float32 arithmetic / register-register
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteD8(int mod, int reg, int rm, ushort seg, ushort off)
    {
        double operand;
        if (mod == 3)
            operand = ST(rm);
        else
            operand = ReadFloat(seg, off);

        switch (reg)
        {
            case 0: ST(0) += operand; break;           // FADD
            case 1: ST(0) *= operand; break;           // FMUL
            case 2: CompareAndSetFlags(ST(0), operand); break; // FCOM
            case 3: CompareAndSetFlags(ST(0), operand); Pop(); break; // FCOMP
            case 4: ST(0) -= operand; break;           // FSUB
            case 5: ST(0) = operand - ST(0); break;   // FSUBR
            case 6: ST(0) /= operand; break;           // FDIV
            case 7: ST(0) = operand / ST(0); break;   // FDIVR
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  0xD9 — load/store float32, transcendentals, control
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteD9(int mod, int reg, int rm, ushort seg, ushort off)
    {
        if (mod != 3)
        {
            switch (reg)
            {
                case 0: Push(ReadFloat(seg, off)); break;         // FLD float32
                case 2: WriteFloat(seg, off, (float)ST(0)); break; // FST float32
                case 3: WriteFloat(seg, off, (float)Pop()); break; // FSTP float32
                case 4: break; // FLDENV (stub)
                case 5: // FLDCW
                    _controlWord = _mem.ReadWord(seg, off);
                    break;
                case 6: break; // FNSTENV (stub)
                case 7: // FNSTCW
                    _mem.WriteWord(seg, off, _controlWord);
                    break;
            }
        }
        else
        {
            int combined = (reg << 3) | rm;
            switch (combined)
            {
                case 0x00: case 0x01: case 0x02: case 0x03: // FLD ST(i)
                case 0x04: case 0x05: case 0x06: case 0x07:
                    Push(ST(rm));
                    break;
                case 0x08: // FXCH ST(0), ST(0) — NOP
                case 0x09: case 0x0A: case 0x0B: case 0x0C: case 0x0D: case 0x0E: case 0x0F:
                    (ST(0), ST(rm)) = (ST(rm), ST(0)); // FXCH ST(i)
                    break;
                case 0x10: break; // FNOP
                case 0x20: break; // FCHS handled below
                case 0x21: break; // FABS handled below
                default:
                    // Encoded specials
                    if (reg == 4)
                    {
                        switch (rm)
                        {
                            case 0: ST(0) = -ST(0); break;          // FCHS
                            case 1: ST(0) = Math.Abs(ST(0)); break; // FABS
                            case 4: // FTST
                                CompareAndSetFlags(ST(0), 0.0);
                                break;
                            case 5: // FXAM (simplified)
                                SetConditionCodes(false, false, false, false);
                                break;
                        }
                    }
                    else if (reg == 5)
                    {
                        switch (rm)
                        {
                            case 0: Push(1.0); break;               // FLD1
                            case 1: Push(Math.Log2(10)); break;     // FLDL2T
                            case 2: Push(Math.Log2(Math.E)); break; // FLDL2E
                            case 3: Push(Math.PI); break;           // FLDPI
                            case 4: Push(Math.Log10(2)); break;     // FLDLG2
                            case 5: Push(Math.Log(2)); break;       // FLDLN2
                            case 6: Push(0.0); break;               // FLDZ
                        }
                    }
                    else if (reg == 6)
                    {
                        switch (rm)
                        {
                            case 0: // F2XM1: ST(0) = 2^ST(0) - 1
                                ST(0) = Math.Pow(2, ST(0)) - 1;
                                break;
                            case 1: // FYL2X: ST(1) = ST(1) * log2(ST(0)), pop
                            {
                                double val = ST(0);
                                Pop();
                                ST(0) *= Math.Log2(val);
                                break;
                            }
                            case 2: // FPTAN: ST(0) = tan(ST(0)), push 1.0
                                ST(0) = Math.Tan(ST(0));
                                Push(1.0);
                                break;
                            case 3: // FPATAN: ST(1) = atan2(ST(1), ST(0)), pop
                            {
                                double x = ST(0);
                                Pop();
                                ST(0) = Math.Atan2(ST(0), x);
                                break;
                            }
                            case 5: // FPREM1 (387)
                                ST(0) = Math.IEEERemainder(ST(0), ST(1));
                                break;
                        }
                    }
                    else if (reg == 7)
                    {
                        switch (rm)
                        {
                            case 0: // FPREM: ST(0) = ST(0) % ST(1)
                                ST(0) %= ST(1);
                                break;
                            case 2: // FSQRT
                                ST(0) = Math.Sqrt(ST(0));
                                break;
                            case 3: // FSINCOS (387): push cos, ST(1) = sin
                            {
                                double val = ST(0);
                                ST(0) = Math.Sin(val);
                                Push(Math.Cos(val));
                                break;
                            }
                            case 4: // FRNDINT
                                ST(0) = Math.Round(ST(0), MidpointRounding.ToEven);
                                break;
                            case 5: // FSCALE: ST(0) = ST(0) * 2^trunc(ST(1))
                                ST(0) *= Math.Pow(2, Math.Truncate(ST(1)));
                                break;
                            case 6: // FSIN (387)
                                ST(0) = Math.Sin(ST(0));
                                break;
                            case 7: // FCOS (387)
                                ST(0) = Math.Cos(ST(0));
                                break;
                        }
                    }
                    break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  0xDA — int32 arithmetic
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteDA(int mod, int reg, int rm, ushort seg, ushort off)
    {
        if (mod == 3)
        {
            // FCMOV* (387+) — conditional moves
            bool cond = reg switch
            {
                0 => _cpu.GetFlag(CpuFlags.Carry),   // FCMOVB
                1 => _cpu.GetFlag(CpuFlags.Zero),    // FCMOVE
                2 => _cpu.GetFlag(CpuFlags.Carry) || _cpu.GetFlag(CpuFlags.Zero), // FCMOVBE
                3 => _cpu.GetFlag(CpuFlags.Parity),  // FCMOVU
                _ => false
            };
            if (cond) ST(0) = ST(rm);
            return;
        }

        int val = (int)_mem.ReadDword(seg, off);
        double operand = val;

        switch (reg)
        {
            case 0: ST(0) += operand; break;
            case 1: ST(0) *= operand; break;
            case 2: CompareAndSetFlags(ST(0), operand); break;
            case 3: CompareAndSetFlags(ST(0), operand); Pop(); break;
            case 4: ST(0) -= operand; break;
            case 5: ST(0) = operand - ST(0); break;
            case 6: ST(0) /= operand; break;
            case 7: ST(0) = operand / ST(0); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  0xDB — int32 load/store, misc
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteDB(int mod, int reg, int rm, ushort seg, ushort off)
    {
        if (mod == 3)
        {
            if (reg == 4 && rm == 3) // FINIT (FNINIT via WAIT prefix)
            {
                Reset();
                return;
            }
            if (reg == 4 && rm == 2) // FCLEX (FNCLEX)
            {
                _statusWord &= 0x7F00; // Clear exception flags
                return;
            }
            // FCMOVNB/FCMOVNE/FCMOVNBE/FCMOVNU (reg 0-3)
            if (reg <= 3)
            {
                bool cond = reg switch
                {
                    0 => !_cpu.GetFlag(CpuFlags.Carry),
                    1 => !_cpu.GetFlag(CpuFlags.Zero),
                    2 => !_cpu.GetFlag(CpuFlags.Carry) && !_cpu.GetFlag(CpuFlags.Zero),
                    3 => !_cpu.GetFlag(CpuFlags.Parity),
                    _ => false
                };
                if (cond) ST(0) = ST(rm);
            }
            // FUCOMI (reg == 5), FCOMI (reg == 6)
            if (reg == 5 || reg == 6)
            {
                double a = ST(0), b = ST(rm);
                _cpu.SetFlag(CpuFlags.Zero, a == b);
                _cpu.SetFlag(CpuFlags.Carry, a < b);
                _cpu.SetFlag(CpuFlags.Parity, double.IsNaN(a) || double.IsNaN(b));
            }
            return;
        }

        switch (reg)
        {
            case 0: // FILD int32
                Push((int)_mem.ReadDword(seg, off));
                break;
            case 2: // FIST int32
                _mem.WriteDword(seg, off, (uint)(int)Math.Round(ST(0)));
                break;
            case 3: // FISTP int32
                _mem.WriteDword(seg, off, (uint)(int)Math.Round(Pop()));
                break;
            case 5: // FLD extended real (80-bit) — approximate with double
                Push(ReadDouble(seg, off)); // Best we can do without 80-bit type
                break;
            case 7: // FSTP extended real (80-bit) — approximate
                WriteDouble(seg, off, Pop());
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  0xDC — float64 arithmetic / register-register reversed
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteDC(int mod, int reg, int rm, ushort seg, ushort off)
    {
        if (mod == 3)
        {
            // Register-register: destination is ST(i)
            switch (reg)
            {
                case 0: ST(rm) += ST(0); break;
                case 1: ST(rm) *= ST(0); break;
                case 4: ST(rm) -= ST(0); break;
                case 5: ST(rm) = ST(0) - ST(rm); break;
                case 6: ST(rm) /= ST(0); break;
                case 7: ST(rm) = ST(0) / ST(rm); break;
            }
            return;
        }

        double operand = ReadDouble(seg, off);
        switch (reg)
        {
            case 0: ST(0) += operand; break;
            case 1: ST(0) *= operand; break;
            case 2: CompareAndSetFlags(ST(0), operand); break;
            case 3: CompareAndSetFlags(ST(0), operand); Pop(); break;
            case 4: ST(0) -= operand; break;
            case 5: ST(0) = operand - ST(0); break;
            case 6: ST(0) /= operand; break;
            case 7: ST(0) = operand / ST(0); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  0xDD — float64 load/store, status
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteDD(int mod, int reg, int rm, ushort seg, ushort off)
    {
        if (mod == 3)
        {
            switch (reg)
            {
                case 0: // FFREE ST(i)
                    SetTag((_top + rm) & 7, 3);
                    break;
                case 2: // FST ST(i)
                    ST(rm) = ST(0);
                    break;
                case 3: // FSTP ST(i)
                    ST(rm) = Pop();
                    break;
                case 4: // FUCOM ST(i) (387)
                    CompareAndSetFlags(ST(0), ST(rm));
                    break;
                case 5: // FUCOMP ST(i)
                    CompareAndSetFlags(ST(0), ST(rm));
                    Pop();
                    break;
            }
            return;
        }

        switch (reg)
        {
            case 0: Push(ReadDouble(seg, off)); break;          // FLD float64
            case 2: WriteDouble(seg, off, ST(0)); break;        // FST float64
            case 3: WriteDouble(seg, off, Pop()); break;        // FSTP float64
            case 4: break; // FRSTOR (stub)
            case 6: break; // FNSAVE (stub)
            case 7: // FNSTSW
                _mem.WriteWord(seg, off, StatusWord);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  0xDE — int16 arithmetic / register pop
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteDE(int mod, int reg, int rm, ushort seg, ushort off)
    {
        if (mod == 3)
        {
            // Register operations with pop
            switch (reg)
            {
                case 0: ST(rm) += ST(0); Pop(); break; // FADDP ST(i), ST(0)
                case 1: ST(rm) *= ST(0); Pop(); break; // FMULP
                case 3: // FCOMPP
                    CompareAndSetFlags(ST(0), ST(1));
                    Pop(); Pop();
                    break;
                case 4: ST(rm) -= ST(0); Pop(); break; // FSUBRP (note: reversed naming)
                case 5: ST(rm) = ST(0) - ST(rm); Pop(); break; // FSUBP
                case 6: ST(rm) /= ST(0); Pop(); break; // FDIVRP
                case 7: ST(rm) = ST(0) / ST(rm); Pop(); break; // FDIVP
            }
            return;
        }

        short val = (short)_mem.ReadWord(seg, off);
        double operand = val;

        switch (reg)
        {
            case 0: ST(0) += operand; break;
            case 1: ST(0) *= operand; break;
            case 2: CompareAndSetFlags(ST(0), operand); break;
            case 3: CompareAndSetFlags(ST(0), operand); Pop(); break;
            case 4: ST(0) -= operand; break;
            case 5: ST(0) = operand - ST(0); break;
            case 6: ST(0) /= operand; break;
            case 7: ST(0) = operand / ST(0); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  0xDF — int16 load/store, BCD, FNSTSW AX
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteDF(int mod, int reg, int rm, ushort seg, ushort off)
    {
        if (mod == 3)
        {
            if (reg == 4 && rm == 0)
            {
                // FNSTSW AX
                _cpu.Regs.AX = StatusWord;
                return;
            }
            // FUCOMIP (reg == 5), FCOMIP (reg == 6) — set EFLAGS
            if (reg == 5 || reg == 6)
            {
                double a = ST(0), b = ST(rm);
                _cpu.SetFlag(CpuFlags.Zero, a == b);
                _cpu.SetFlag(CpuFlags.Carry, a < b);
                _cpu.SetFlag(CpuFlags.Parity, double.IsNaN(a) || double.IsNaN(b));
                Pop();
            }
            return;
        }

        switch (reg)
        {
            case 0: // FILD int16
                Push((short)_mem.ReadWord(seg, off));
                break;
            case 2: // FIST int16
                _mem.WriteWord(seg, off, (ushort)(short)Math.Round(ST(0)));
                break;
            case 3: // FISTP int16
                _mem.WriteWord(seg, off, (ushort)(short)Math.Round(Pop()));
                break;
            case 4: // FBLD (BCD load) — simplified
                Push(0); // BCD stub
                break;
            case 5: // FILD int64
            {
                ulong lo = _mem.ReadDword(seg, off);
                ulong hi = _mem.ReadDword(seg, (ushort)(off + 4));
                Push((long)(lo | (hi << 32)));
                break;
            }
            case 6: // FBSTP (BCD store) — simplified
                Pop();
                break;
            case 7: // FISTP int64
            {
                long val = (long)Math.Round(Pop());
                _mem.WriteDword(seg, off, (uint)(val & 0xFFFFFFFF));
                _mem.WriteDword(seg, (ushort)(off + 4), (uint)((ulong)val >> 32));
                break;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  COMPARISON HELPER
    // ═══════════════════════════════════════════════════════════════

    private void CompareAndSetFlags(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
            SetConditionCodes(true, false, true, true); // Unordered
        else if (a > b)
            SetConditionCodes(false, false, false, false); // GT
        else if (a < b)
            SetConditionCodes(true, false, false, false);  // LT
        else
            SetConditionCodes(false, false, false, true);  // EQ
    }
}
