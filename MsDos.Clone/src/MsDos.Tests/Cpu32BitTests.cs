using MsDos.Core.Cpu;
using MsDos.Core.Memory;

namespace MsDos.Tests;

/// <summary>
/// Tests for 32-bit CPU instruction support (0x66 prefix, CMOVcc, CPUID, LSS/LFS/LGS,
/// 32-bit ALU operations, Push32/Pop32, Group1/3/Shift 32-bit).
/// </summary>
public class Cpu32BitTests
{
    private readonly MemoryBus _mem = new();
    private readonly Cpu8086 _cpu;

    public Cpu32BitTests()
    {
        _cpu = new Cpu8086(_mem);
        _cpu.Regs.CS = 0x1000;
        _cpu.Regs.DS = 0x1000;
        _cpu.Regs.ES = 0x1000;
        _cpu.Regs.SS = 0x1000;
        _cpu.Regs.SP = 0xFFFE;
        _cpu.Regs.IP = 0x0100;
        _cpu.Regs.Flags = CpuFlags.Interrupt;
    }

    private void LoadCode(params byte[] code)
    {
        _mem.LoadData(_cpu.Regs.CS, _cpu.Regs.IP, code);
    }

    // ═══════════════════════════════════════════════════════════════
    // 0x66 PREFIX — MOV reg32, imm32
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_MovEaxImm32()
    {
        // 0x66 0xB8 imm32 — MOV EAX, 0x12345678
        LoadCode(0x66, 0xB8, 0x78, 0x56, 0x34, 0x12);
        _cpu.Step();
        Assert.Equal(0x12345678u, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_MovEcxImm32()
    {
        // 0x66 0xB9 imm32 — MOV ECX, 0xAABBCCDD
        LoadCode(0x66, 0xB9, 0xDD, 0xCC, 0xBB, 0xAA);
        _cpu.Step();
        Assert.Equal(0xAABBCCDDu, _cpu.Regs.ECX);
    }

    [Fact]
    public void Prefix66_MovEdxImm32()
    {
        // 0x66 0xBA imm32 — MOV EDX, 0xDEADBEEF
        LoadCode(0x66, 0xBA, 0xEF, 0xBE, 0xAD, 0xDE);
        _cpu.Step();
        Assert.Equal(0xDEADBEEFu, _cpu.Regs.EDX);
    }

    [Fact]
    public void Prefix66_MovEbxImm32()
    {
        // 0x66 0xBB imm32 — MOV EBX, 0xCAFEBABE
        LoadCode(0x66, 0xBB, 0xBE, 0xBA, 0xFE, 0xCA);
        _cpu.Step();
        Assert.Equal(0xCAFEBABEu, _cpu.Regs.EBX);
    }

    // ═══════════════════════════════════════════════════════════════
    // 0x66 PREFIX — ADD/SUB/XOR/AND/OR/CMP with 32-bit operands
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_AddEaxImm32()
    {
        // 0x66 0x05 imm32 — ADD EAX, imm32
        _cpu.Regs.EAX = 0x10000000;
        LoadCode(0x66, 0x05, 0x01, 0x00, 0x00, 0x10); // ADD EAX, 0x10000001
        _cpu.Step();
        Assert.Equal(0x20000001u, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_SubEaxImm32()
    {
        // 0x66 0x2D imm32 — SUB EAX, imm32
        _cpu.Regs.EAX = 0x80000000;
        LoadCode(0x66, 0x2D, 0x01, 0x00, 0x00, 0x00); // SUB EAX, 1
        _cpu.Step();
        Assert.Equal(0x7FFFFFFFu, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_XorEaxEax_ZerosAndSetsZeroFlag()
    {
        // 0x66 0x31 0xC0 — XOR EAX, EAX (ModR/M = C0: reg=0, rm=0, mod=3)
        _cpu.Regs.EAX = 0xFFFFFFFF;
        LoadCode(0x66, 0x31, 0xC0);
        _cpu.Step();
        Assert.Equal(0u, _cpu.Regs.EAX);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void Prefix66_AndEaxImm32()
    {
        // 0x66 0x25 imm32 — AND EAX, imm32
        _cpu.Regs.EAX = 0xFF00FF00;
        LoadCode(0x66, 0x25, 0xFF, 0x00, 0x00, 0xFF); // AND EAX, 0xFF0000FF
        _cpu.Step();
        Assert.Equal(0xFF000000u, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_OrEaxImm32()
    {
        // 0x66 0x0D imm32 — OR EAX, imm32
        _cpu.Regs.EAX = 0x00FF0000;
        LoadCode(0x66, 0x0D, 0x00, 0x00, 0x00, 0xFF); // OR EAX, 0xFF000000
        _cpu.Step();
        Assert.Equal(0xFFFF0000u, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_CmpEaxImm32_SetsCarry()
    {
        // 0x66 0x3D imm32 — CMP EAX, imm32
        _cpu.Regs.EAX = 0x00000001;
        LoadCode(0x66, 0x3D, 0x02, 0x00, 0x00, 0x00); // CMP EAX, 2
        _cpu.Step();
        Assert.True((_cpu.Regs.Flags & CpuFlags.Carry) != 0); // 1 < 2 → carry
    }

    [Fact]
    public void Prefix66_CmpEaxImm32_SetsZero()
    {
        _cpu.Regs.EAX = 0x12345678;
        LoadCode(0x66, 0x3D, 0x78, 0x56, 0x34, 0x12); // CMP EAX, 0x12345678
        _cpu.Step();
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0); // Equal
    }

    // ═══════════════════════════════════════════════════════════════
    // 0x66 PREFIX — MOV r/m32, reg32 and MOV reg32, r/m32
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_MovEaxEbx_RegToReg()
    {
        // 0x66 0x89 0xD8 — MOV EAX, EBX (89 /r with ModR/M = D8: mod=3 reg=3(BX) rm=0(AX))
        _cpu.Regs.EBX = 0xDEADCAFE;
        LoadCode(0x66, 0x89, 0xD8);
        _cpu.Step();
        Assert.Equal(0xDEADCAFEu, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_MovEcx_FromMemory()
    {
        // 0x66 0x8B 0x0E disp16 — MOV ECX, [DS:disp16] (8B /r with ModR/M = 0E: mod=0, reg=1(CX), rm=6(disp16))
        ushort addr = 0x0200;
        _mem.WriteDword(0x1000, addr, 0xF0F0F0F0);
        LoadCode(0x66, 0x8B, 0x0E, (byte)(addr & 0xFF), (byte)(addr >> 8));
        _cpu.Step();
        Assert.Equal(0xF0F0F0F0u, _cpu.Regs.ECX);
    }

    [Fact]
    public void Prefix66_MovMemory_FromEdx()
    {
        // 0x66 0x89 0x16 disp16 — MOV [DS:disp16], EDX (89 /r with ModR/M = 16: mod=0, reg=2(DX), rm=6(disp16))
        ushort addr = 0x0300;
        _cpu.Regs.EDX = 0x11223344;
        LoadCode(0x66, 0x89, 0x16, (byte)(addr & 0xFF), (byte)(addr >> 8));
        _cpu.Step();
        Assert.Equal(0x11223344u, _mem.ReadDword(0x1000, addr));
    }

    // ═══════════════════════════════════════════════════════════════
    // 0x66 PREFIX — PUSH/POP 32-bit
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_PushEax_PopEbx()
    {
        // PUSH EAX: 0x66 0x50 — POP EBX: 0x66 0x5B
        _cpu.Regs.EAX = 0xCAFEBABE;
        ushort origSP = _cpu.Regs.SP;
        LoadCode(0x66, 0x50, 0x66, 0x5B);

        _cpu.Step(); // PUSH EAX
        Assert.Equal((ushort)(origSP - 4), _cpu.Regs.SP);

        _cpu.Step(); // POP EBX
        Assert.Equal(0xCAFEBABEu, _cpu.Regs.EBX);
        Assert.Equal(origSP, _cpu.Regs.SP);
    }

    [Fact]
    public void Prefix66_PushImm32()
    {
        // 0x66 0x68 imm32 — PUSH imm32
        ushort origSP = _cpu.Regs.SP;
        LoadCode(0x66, 0x68, 0x78, 0x56, 0x34, 0x12); // PUSH 0x12345678
        _cpu.Step();
        Assert.Equal((ushort)(origSP - 4), _cpu.Regs.SP);
        Assert.Equal(0x12345678u, _mem.ReadDword(_cpu.Regs.SS, _cpu.Regs.SP));
    }

    // ═══════════════════════════════════════════════════════════════
    // 0x66 PREFIX — INC/DEC 32-bit
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_IncEax()
    {
        // 0x66 0x40 — INC EAX
        _cpu.Regs.EAX = 0xFFFFFFFE;
        LoadCode(0x66, 0x40);
        _cpu.Step();
        Assert.Equal(0xFFFFFFFFu, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_DecEcx()
    {
        // 0x66 0x49 — DEC ECX
        _cpu.Regs.ECX = 0x00000001;
        LoadCode(0x66, 0x49);
        _cpu.Step();
        Assert.Equal(0u, _cpu.Regs.ECX);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void Prefix66_IncEax_Overflow()
    {
        _cpu.Regs.EAX = 0x7FFFFFFF;
        LoadCode(0x66, 0x40); // INC EAX
        _cpu.Step();
        Assert.Equal(0x80000000u, _cpu.Regs.EAX);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Overflow) != 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // 0x66 PREFIX — MOV r/m32, imm32 (opcode C7)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_MovRM32Imm32()
    {
        // 0x66 0xC7 0xC0 imm32 — MOV EAX, 0xDEADBEEF (ModR/M C0: mod=3, reg=0, rm=0)
        LoadCode(0x66, 0xC7, 0xC0, 0xEF, 0xBE, 0xAD, 0xDE);
        _cpu.Step();
        Assert.Equal(0xDEADBEEFu, _cpu.Regs.EAX);
    }

    // ═══════════════════════════════════════════════════════════════
    // 0x67 PREFIX — Transparent pass-through
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix67_TransparentPassThrough()
    {
        // 0x67 NOP — should just execute NOP normally
        LoadCode(0x67, 0x90);
        ushort origIP = _cpu.Regs.IP;
        _cpu.Step();
        Assert.Equal((ushort)(origIP + 2), _cpu.Regs.IP);
    }

    // ═══════════════════════════════════════════════════════════════
    // GROUP 1 — 32-bit (0x66 0x81 / 0x66 0x83)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_Group1_AddRM32Imm32()
    {
        // 0x66 0x81 0xC0 imm32 — ADD EAX, imm32 (Group1: op=0, mod=3, rm=0)
        _cpu.Regs.EAX = 0x10000000;
        LoadCode(0x66, 0x81, 0xC0, 0xFF, 0xFF, 0xFF, 0x0F); // ADD EAX, 0x0FFFFFFF
        _cpu.Step();
        Assert.Equal(0x1FFFFFFFu, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_Group1_SubRM32SignExtImm8()
    {
        // 0x66 0x83 0xE8 0x05 — SUB EAX, 5 (sign-extended byte)
        _cpu.Regs.EAX = 0x10000000;
        LoadCode(0x66, 0x83, 0xE8, 0x05); // Group1 op=5(SUB), mod=3, rm=0
        _cpu.Step();
        Assert.Equal(0x0FFFFFFBu, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_Group1_CmpRM32Imm32_NoWriteback()
    {
        // 0x66 0x81 0xF8 imm32 — CMP EAX, imm32 (Group1: op=7)
        _cpu.Regs.EAX = 0x00000005;
        LoadCode(0x66, 0x81, 0xF8, 0x05, 0x00, 0x00, 0x00); // CMP EAX, 5
        _cpu.Step();
        Assert.Equal(0x00000005u, _cpu.Regs.EAX); // Not modified
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void Prefix66_Group1_XorRM32Imm32()
    {
        // 0x66 0x81 0xF0 imm32 — XOR EAX, imm32 (Group1: op=6, rm=0)
        _cpu.Regs.EAX = 0xFF00FF00;
        LoadCode(0x66, 0x81, 0xF0, 0x00, 0xFF, 0x00, 0xFF); // XOR EAX, 0xFF00FF00
        _cpu.Step();
        Assert.Equal(0u, _cpu.Regs.EAX);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // GROUP 3 — 32-bit (0x66 0xF7)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_Group3_TestEaxImm32()
    {
        // 0x66 0xF7 0xC0 imm32 — TEST EAX, imm32 (op=0, mod=3, rm=0)
        _cpu.Regs.EAX = 0x80000000;
        LoadCode(0x66, 0xF7, 0xC0, 0x00, 0x00, 0x00, 0x80); // TEST EAX, 0x80000000
        _cpu.Step();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Zero) != 0); // Result non-zero
        Assert.True((_cpu.Regs.Flags & CpuFlags.Sign) != 0);  // High bit set
    }

    [Fact]
    public void Prefix66_Group3_NotEax()
    {
        // 0x66 0xF7 0xD0 — NOT EAX (op=2, mod=3, rm=0)
        _cpu.Regs.EAX = 0x00FF00FF;
        LoadCode(0x66, 0xF7, 0xD0);
        _cpu.Step();
        Assert.Equal(0xFF00FF00u, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_Group3_NegEax()
    {
        // 0x66 0xF7 0xD8 — NEG EAX (op=3, mod=3, rm=0)
        _cpu.Regs.EAX = 1;
        LoadCode(0x66, 0xF7, 0xD8);
        _cpu.Step();
        Assert.Equal(0xFFFFFFFFu, _cpu.Regs.EAX);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Carry) != 0); // NEG sets carry when val != 0
    }

    [Fact]
    public void Prefix66_Group3_MulEax()
    {
        // 0x66 0xF7 0xE1 — MUL ECX (op=4, mod=3, rm=1)
        _cpu.Regs.EAX = 0x10000;
        _cpu.Regs.ECX = 0x10000;
        LoadCode(0x66, 0xF7, 0xE1);
        _cpu.Step();
        // 0x10000 * 0x10000 = 0x100000000 → EDX:EAX = 1:0
        Assert.Equal(0u, _cpu.Regs.EAX);
        Assert.Equal(1u, _cpu.Regs.EDX);
    }

    [Fact]
    public void Prefix66_Group3_ImulEax()
    {
        // 0x66 0xF7 0xE9 — IMUL ECX (op=5, mod=3, rm=1)
        _cpu.Regs.EAX = unchecked((uint)-2); // 0xFFFFFFFE
        _cpu.Regs.ECX = 3;
        LoadCode(0x66, 0xF7, 0xE9);
        _cpu.Step();
        // -2 * 3 = -6 → 0xFFFFFFFF_FFFFFFFA
        Assert.Equal(0xFFFFFFFAu, _cpu.Regs.EAX);
        Assert.Equal(0xFFFFFFFFu, _cpu.Regs.EDX);
    }

    [Fact]
    public void Prefix66_Group3_DivEax()
    {
        // 0x66 0xF7 0xF1 — DIV ECX (op=6, mod=3, rm=1)
        _cpu.Regs.EAX = 100;
        _cpu.Regs.EDX = 0;
        _cpu.Regs.ECX = 7;
        LoadCode(0x66, 0xF7, 0xF1);
        _cpu.Step();
        Assert.Equal(14u, _cpu.Regs.EAX);   // 100 / 7 = 14
        Assert.Equal(2u, _cpu.Regs.EDX);    // 100 % 7 = 2
    }

    // ═══════════════════════════════════════════════════════════════
    // SHIFT GROUP — 32-bit (0x66 0xC1 / 0x66 0xD1)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_ShlEax_By1()
    {
        // 0x66 0xD1 0xE0 — SHL EAX, 1 (Shift group op=4, count=1)
        _cpu.Regs.EAX = 0x40000000;
        LoadCode(0x66, 0xD1, 0xE0);
        _cpu.Step();
        Assert.Equal(0x80000000u, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_ShrEax_ByImm8()
    {
        // 0x66 0xC1 0xE8 0x10 — SHR EAX, 16 (Shift group op=5)
        _cpu.Regs.EAX = 0xFFFF0000;
        LoadCode(0x66, 0xC1, 0xE8, 0x10);
        _cpu.Step();
        Assert.Equal(0x0000FFFFu, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_SarEax_SignPreserved()
    {
        // 0x66 0xC1 0xF8 0x04 — SAR EAX, 4 (op=7)
        _cpu.Regs.EAX = 0x80000000;
        LoadCode(0x66, 0xC1, 0xF8, 0x04);
        _cpu.Step();
        Assert.Equal(0xF8000000u, _cpu.Regs.EAX); // Sign bit extended
    }

    [Fact]
    public void Prefix66_RolEax()
    {
        // 0x66 0xC1 0xC0 0x04 — ROL EAX, 4 (op=0)
        _cpu.Regs.EAX = 0xF0000001;
        LoadCode(0x66, 0xC1, 0xC0, 0x04);
        _cpu.Step();
        Assert.Equal(0x0000001Fu, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_RorEax()
    {
        // 0x66 0xC1 0xC8 0x04 — ROR EAX, 4 (op=1)
        _cpu.Regs.EAX = 0x0000001F;
        LoadCode(0x66, 0xC1, 0xC8, 0x04);
        _cpu.Step();
        Assert.Equal(0xF0000001u, _cpu.Regs.EAX);
    }

    // ═══════════════════════════════════════════════════════════════
    // CMOVCC — Conditional moves (0x0F 0x40-0x4F)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void CmovZ_ConditionTrue_Moves()
    {
        // CMOVZ AX, CX (0x0F 0x44 0xC1) — ModR/M C1: mod=3, reg=0(AX), rm=1(CX)
        _cpu.Regs.AX = 0x0000;
        _cpu.Regs.CX = 0x1234;
        _cpu.Regs.Flags |= CpuFlags.Zero;
        LoadCode(0x0F, 0x44, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x1234, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovZ_ConditionFalse_NoMove()
    {
        _cpu.Regs.AX = 0x5555;
        _cpu.Regs.CX = 0x1234;
        _cpu.Regs.Flags &= ~CpuFlags.Zero;
        LoadCode(0x0F, 0x44, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x5555, _cpu.Regs.AX); // Unchanged
    }

    [Fact]
    public void CmovNZ_ConditionTrue_Moves()
    {
        // CMOVNZ AX, BX (0x0F 0x45 0xC3)
        _cpu.Regs.AX = 0;
        _cpu.Regs.BX = 0xAAAA;
        _cpu.Regs.Flags &= ~CpuFlags.Zero; // ZF=0 → NZ is true
        LoadCode(0x0F, 0x45, 0xC3);
        _cpu.Step();
        Assert.Equal((ushort)0xAAAA, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovB_CarrySet_Moves()
    {
        // CMOVB AX, DX (0x0F 0x42 0xC2)
        _cpu.Regs.AX = 0;
        _cpu.Regs.DX = 0xBBBB;
        _cpu.Regs.Flags |= CpuFlags.Carry;
        LoadCode(0x0F, 0x42, 0xC2);
        _cpu.Step();
        Assert.Equal((ushort)0xBBBB, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovNB_CarryClear_Moves()
    {
        // CMOVNB AX, DX (0x0F 0x43 0xC2)
        _cpu.Regs.AX = 0;
        _cpu.Regs.DX = 0xCCCC;
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
        LoadCode(0x0F, 0x43, 0xC2);
        _cpu.Step();
        Assert.Equal((ushort)0xCCCC, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovS_SignSet_Moves()
    {
        // CMOVS CX, AX (0x0F 0x48 0xC8 — reg=1(CX), rm=0(AX))
        _cpu.Regs.CX = 0;
        _cpu.Regs.AX = 0x1111;
        _cpu.Regs.Flags |= CpuFlags.Sign;
        LoadCode(0x0F, 0x48, 0xC8);
        _cpu.Step();
        Assert.Equal((ushort)0x1111, _cpu.Regs.CX);
    }

    [Fact]
    public void CmovO_OverflowSet_Moves()
    {
        // CMOVO AX, CX (0x0F 0x40 0xC1)
        _cpu.Regs.AX = 0;
        _cpu.Regs.CX = 0x9999;
        _cpu.Regs.Flags |= CpuFlags.Overflow;
        LoadCode(0x0F, 0x40, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x9999, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovO_OverflowClear_NoMove()
    {
        _cpu.Regs.AX = 0x7777;
        _cpu.Regs.CX = 0x9999;
        _cpu.Regs.Flags &= ~CpuFlags.Overflow;
        LoadCode(0x0F, 0x40, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x7777, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovL_SignNeqOverflow_Moves()
    {
        // CMOVL AX, CX (0x0F 0x4C 0xC1) — Sign != Overflow
        _cpu.Regs.AX = 0;
        _cpu.Regs.CX = 0x2222;
        _cpu.Regs.Flags = CpuFlags.Sign; // SF=1, OF=0 → L is true
        LoadCode(0x0F, 0x4C, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x2222, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovGE_SignEqOverflow_Moves()
    {
        // CMOVGE AX, CX (0x0F 0x4D 0xC1) — Sign == Overflow
        _cpu.Regs.AX = 0;
        _cpu.Regs.CX = 0x3333;
        _cpu.Regs.Flags = CpuFlags.None; // SF=0, OF=0 → GE is true
        LoadCode(0x0F, 0x4D, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x3333, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovLE_ZeroOrSignNeqOverflow()
    {
        // CMOVLE AX, BX (0x0F 0x4E 0xC3)
        _cpu.Regs.AX = 0;
        _cpu.Regs.BX = 0x4444;
        _cpu.Regs.Flags = CpuFlags.Zero; // ZF=1 → LE is true
        LoadCode(0x0F, 0x4E, 0xC3);
        _cpu.Step();
        Assert.Equal((ushort)0x4444, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovG_NotZeroAndSignEqOverflow()
    {
        // CMOVG AX, BX (0x0F 0x4F 0xC3)
        _cpu.Regs.AX = 0;
        _cpu.Regs.BX = 0x5555;
        _cpu.Regs.Flags = CpuFlags.None; // ZF=0, SF==OF → G is true
        LoadCode(0x0F, 0x4F, 0xC3);
        _cpu.Step();
        Assert.Equal((ushort)0x5555, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovBE_CarryOrZero()
    {
        // CMOVBE AX, CX (0x0F 0x46 0xC1)
        _cpu.Regs.AX = 0;
        _cpu.Regs.CX = 0x6666;
        _cpu.Regs.Flags = CpuFlags.Carry; // CF=1 → BE is true
        LoadCode(0x0F, 0x46, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x6666, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovA_NotCarryAndNotZero()
    {
        // CMOVA AX, CX (0x0F 0x47 0xC1)
        _cpu.Regs.AX = 0;
        _cpu.Regs.CX = 0x7777;
        _cpu.Regs.Flags = CpuFlags.None; // CF=0 and ZF=0 → A is true
        LoadCode(0x0F, 0x47, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x7777, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovP_ParitySet()
    {
        // CMOVP AX, CX (0x0F 0x4A 0xC1)
        _cpu.Regs.AX = 0;
        _cpu.Regs.CX = 0x8888;
        _cpu.Regs.Flags = CpuFlags.Parity;
        LoadCode(0x0F, 0x4A, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x8888, _cpu.Regs.AX);
    }

    [Fact]
    public void CmovNP_ParityClear()
    {
        // CMOVNP AX, CX (0x0F 0x4B 0xC1)
        _cpu.Regs.AX = 0;
        _cpu.Regs.CX = 0x9999;
        _cpu.Regs.Flags = CpuFlags.None; // PF=0 → NP is true
        LoadCode(0x0F, 0x4B, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x9999, _cpu.Regs.AX);
    }

    // ─── CMOVcc with 32-bit prefix ─────────────────────────────────

    [Fact]
    public void Prefix66_CmovZ_32Bit()
    {
        // 0x66 CMOVZ EAX, ECX (0x66 0x0F 0x44 0xC1)
        _cpu.Regs.EAX = 0;
        _cpu.Regs.ECX = 0xDEADBEEF;
        _cpu.Regs.Flags |= CpuFlags.Zero;
        LoadCode(0x66, 0x0F, 0x44, 0xC1);
        _cpu.Step();
        Assert.Equal(0xDEADBEEFu, _cpu.Regs.EAX);
    }

    [Fact]
    public void Prefix66_CmovZ_32Bit_ConditionFalse()
    {
        _cpu.Regs.EAX = 0x11111111;
        _cpu.Regs.ECX = 0xDEADBEEF;
        _cpu.Regs.Flags &= ~CpuFlags.Zero;
        LoadCode(0x66, 0x0F, 0x44, 0xC1);
        _cpu.Step();
        Assert.Equal(0x11111111u, _cpu.Regs.EAX);
    }

    // ═══════════════════════════════════════════════════════════════
    // CPUID — 0x0F 0xA2
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Cpuid_Leaf0_VendorString()
    {
        _cpu.Regs.EAX = 0; // Leaf 0
        LoadCode(0x0F, 0xA2);
        _cpu.Step();

        Assert.Equal(1u, _cpu.Regs.EAX); // Max supported leaf
        Assert.Equal(0x756E6547u, _cpu.Regs.EBX); // "Genu"
        Assert.Equal(0x49656E69u, _cpu.Regs.EDX); // "ineI"
        Assert.Equal(0x6C65746Eu, _cpu.Regs.ECX); // "ntel"
    }

    [Fact]
    public void Cpuid_Leaf1_FamilyAndFeatures()
    {
        _cpu.Regs.EAX = 1; // Leaf 1
        LoadCode(0x0F, 0xA2);
        _cpu.Step();

        Assert.Equal(0x00000300u, _cpu.Regs.EAX); // 386 family
        Assert.Equal(0u, _cpu.Regs.EBX);
        Assert.Equal(0u, _cpu.Regs.ECX);
        Assert.Equal(0x00000001u, _cpu.Regs.EDX); // FPU present
    }

    [Fact]
    public void Cpuid_UnsupportedLeaf_ReturnsZeros()
    {
        _cpu.Regs.EAX = 99; // Unsupported leaf
        LoadCode(0x0F, 0xA2);
        _cpu.Step();

        Assert.Equal(0u, _cpu.Regs.EAX);
        Assert.Equal(0u, _cpu.Regs.EBX);
        Assert.Equal(0u, _cpu.Regs.ECX);
        Assert.Equal(0u, _cpu.Regs.EDX);
    }

    // ═══════════════════════════════════════════════════════════════
    // LSS / LFS / LGS — Load far pointer
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Lss_LoadsFarPointer()
    {
        // LSS SP, [DS:0x0200] — 0x0F 0xB2 0x26 0x00 0x02
        // ModR/M 0x26 = mod=0, reg=4(SP), rm=6(disp16)
        ushort addr = 0x0200;
        _mem.WriteWord(0x1000, addr, 0x1234);         // Offset → SP
        _mem.WriteWord(0x1000, (ushort)(addr + 2), 0x5678); // Segment → SS
        LoadCode(0x0F, 0xB2, 0x26, 0x00, 0x02);
        _cpu.Step();

        Assert.Equal((ushort)0x1234, _cpu.Regs.SP);
        Assert.Equal((ushort)0x5678, _cpu.Regs.SS);
    }

    [Fact]
    public void Lfs_LoadsFarPointer()
    {
        // LFS BX, [DS:0x0300] — 0x0F 0xB4 0x1E 0x00 0x03
        // ModR/M 0x1E = mod=0, reg=3(BX), rm=6(disp16)
        ushort addr = 0x0300;
        _mem.WriteWord(0x1000, addr, 0xAAAA);
        _mem.WriteWord(0x1000, (ushort)(addr + 2), 0xBBBB);
        LoadCode(0x0F, 0xB4, 0x1E, 0x00, 0x03);
        _cpu.Step();

        Assert.Equal((ushort)0xAAAA, _cpu.Regs.BX);
        Assert.Equal((ushort)0xBBBB, _cpu.Regs.FS);
    }

    [Fact]
    public void Lgs_LoadsFarPointer()
    {
        // LGS DX, [DS:0x0400] — 0x0F 0xB5 0x16 0x00 0x04
        // ModR/M 0x16 = mod=0, reg=2(DX), rm=6(disp16)
        ushort addr = 0x0400;
        _mem.WriteWord(0x1000, addr, 0xCCCC);
        _mem.WriteWord(0x1000, (ushort)(addr + 2), 0xDDDD);
        LoadCode(0x0F, 0xB5, 0x16, 0x00, 0x04);
        _cpu.Step();

        Assert.Equal((ushort)0xCCCC, _cpu.Regs.DX);
        Assert.Equal((ushort)0xDDDD, _cpu.Regs.GS);
    }

    // ═══════════════════════════════════════════════════════════════
    // MOVZX / MOVSX with 32-bit prefix
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Movzx_Reg16_RM8()
    {
        // MOVZX AX, CL — 0x0F 0xB6 0xC1 (reg=0(AX), rm=1(CL))
        _cpu.Regs.CL = 0x80;
        LoadCode(0x0F, 0xB6, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0x0080, _cpu.Regs.AX);
    }

    [Fact]
    public void Movsx_Reg16_RM8()
    {
        // MOVSX AX, CL — 0x0F 0xBE 0xC1
        _cpu.Regs.CL = 0x80; // -128 as signed byte
        LoadCode(0x0F, 0xBE, 0xC1);
        _cpu.Step();
        Assert.Equal((ushort)0xFF80, _cpu.Regs.AX); // Sign-extended
    }

    [Fact]
    public void Movzx_Reg16_RM8_Positive()
    {
        _cpu.Regs.DL = 0x42;
        LoadCode(0x0F, 0xB6, 0xC2); // MOVZX AX, DL
        _cpu.Step();
        Assert.Equal((ushort)0x0042, _cpu.Regs.AX);
    }

    // ═══════════════════════════════════════════════════════════════
    // 32-bit ADD with carry/overflow flag tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_Add_Carry32()
    {
        // ADD EAX, EBX where result overflows 32 bits
        _cpu.Regs.EAX = 0xFFFFFFFF;
        _cpu.Regs.EBX = 1;
        // 0x66 0x01 0xD8 — ADD EAX, EBX (01 /r ModR/M = D8: reg=3(BX), rm=0(AX))
        LoadCode(0x66, 0x01, 0xD8);
        _cpu.Step();
        Assert.Equal(0u, _cpu.Regs.EAX);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void Prefix66_Sub_SignedOverflow32()
    {
        // SUB EAX, EBX
        _cpu.Regs.EAX = 0x80000000; // Min int32
        _cpu.Regs.EBX = 1;
        // 0x66 0x29 0xD8 — SUB EAX, EBX (29 /r ModR/M = D8: reg=3(BX), rm=0(AX))
        LoadCode(0x66, 0x29, 0xD8);
        _cpu.Step();
        Assert.Equal(0x7FFFFFFFu, _cpu.Regs.EAX);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Overflow) != 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // PUSHA/POPA 32-bit (0x66 0x60 / 0x66 0x61)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Prefix66_Pushad_Popad()
    {
        _cpu.Regs.EAX = 0x11111111;
        _cpu.Regs.ECX = 0x22222222;
        _cpu.Regs.EDX = 0x33333333;
        _cpu.Regs.EBX = 0x44444444;
        _cpu.Regs.EBP = 0x66666666;
        _cpu.Regs.ESI = 0x77777777;
        _cpu.Regs.EDI = 0x88888888;
        ushort origSP = _cpu.Regs.SP;

        // PUSHAD then zero all, then POPAD
        LoadCode(0x66, 0x60, // PUSHAD
                 0x66, 0x61); // POPAD

        _cpu.Step(); // PUSHAD
        Assert.Equal((ushort)(origSP - 32), _cpu.Regs.SP); // 8 × 4 bytes

        // Clear regs
        _cpu.Regs.EAX = 0; _cpu.Regs.ECX = 0; _cpu.Regs.EDX = 0; _cpu.Regs.EBX = 0;
        _cpu.Regs.EBP = 0; _cpu.Regs.ESI = 0; _cpu.Regs.EDI = 0;

        _cpu.Step(); // POPAD
        Assert.Equal(0x11111111u, _cpu.Regs.EAX);
        Assert.Equal(0x22222222u, _cpu.Regs.ECX);
        Assert.Equal(0x33333333u, _cpu.Regs.EDX);
        Assert.Equal(0x44444444u, _cpu.Regs.EBX);
        Assert.Equal(0x66666666u, _cpu.Regs.EBP);
        Assert.Equal(0x77777777u, _cpu.Regs.ESI);
        Assert.Equal(0x88888888u, _cpu.Regs.EDI);
        Assert.Equal(origSP, _cpu.Regs.SP);
    }
}
