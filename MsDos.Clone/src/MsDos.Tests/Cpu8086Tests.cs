using MsDos.Core.Cpu;
using MsDos.Core.Memory;

namespace MsDos.Tests;

public class Cpu8086Tests
{
    private readonly MemoryBus _mem = new();
    private readonly Cpu8086 _cpu;

    public Cpu8086Tests()
    {
        _cpu = new Cpu8086(_mem);
        // Set up for COM file execution at segment 0x1000
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

    [Fact]
    public void Nop_DoesNotChangeState()
    {
        LoadCode(0x90); // NOP
        ushort origIP = _cpu.Regs.IP;
        _cpu.Step();
        Assert.Equal((ushort)(origIP + 1), _cpu.Regs.IP);
    }

    [Fact]
    public void MovRegImm16_LoadsValue()
    {
        LoadCode(0xB8, 0x34, 0x12); // MOV AX, 0x1234
        _cpu.Step();
        Assert.Equal((ushort)0x1234, _cpu.Regs.AX);
    }

    [Fact]
    public void MovRegImm8_LoadsValue()
    {
        LoadCode(0xB0, 0x42); // MOV AL, 0x42
        _cpu.Step();
        Assert.Equal(0x42, _cpu.Regs.AL);
    }

    [Fact]
    public void AddRegImm_ComputesSum()
    {
        _cpu.Regs.AX = 0x0010;
        LoadCode(0x05, 0x20, 0x00); // ADD AX, 0x0020
        _cpu.Step();
        Assert.Equal((ushort)0x0030, _cpu.Regs.AX);
    }

    [Fact]
    public void SubRegImm_ComputesDifference()
    {
        _cpu.Regs.AX = 0x0050;
        LoadCode(0x2D, 0x20, 0x00); // SUB AX, 0x0020
        _cpu.Step();
        Assert.Equal((ushort)0x0030, _cpu.Regs.AX);
    }

    [Fact]
    public void Cmp_SetsZeroFlag()
    {
        _cpu.Regs.AX = 0x0042;
        LoadCode(0x3D, 0x42, 0x00); // CMP AX, 0x0042
        _cpu.Step();
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void Cmp_ClearsZeroFlagWhenNotEqual()
    {
        _cpu.Regs.AX = 0x0042;
        LoadCode(0x3D, 0x43, 0x00); // CMP AX, 0x0043
        _cpu.Step();
        Assert.False((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void JmpShort_ChangesIP()
    {
        LoadCode(0xEB, 0x05); // JMP short +5
        ushort expectedIP = (ushort)(_cpu.Regs.IP + 2 + 5);
        _cpu.Step();
        Assert.Equal(expectedIP, _cpu.Regs.IP);
    }

    [Fact]
    public void Jz_JumpsWhenZero()
    {
        _cpu.Regs.Flags |= CpuFlags.Zero;
        LoadCode(0x74, 0x04); // JZ +4
        ushort expectedIP = (ushort)(_cpu.Regs.IP + 2 + 4);
        _cpu.Step();
        Assert.Equal(expectedIP, _cpu.Regs.IP);
    }

    [Fact]
    public void Jz_DoesNotJumpWhenNotZero()
    {
        _cpu.Regs.Flags &= ~CpuFlags.Zero;
        LoadCode(0x74, 0x04); // JZ +4
        ushort expectedIP = (ushort)(_cpu.Regs.IP + 2);
        _cpu.Step();
        Assert.Equal(expectedIP, _cpu.Regs.IP);
    }

    [Fact]
    public void PushPop_PreservesValue()
    {
        _cpu.Regs.AX = 0xBEEF;
        LoadCode(
            0x50,       // PUSH AX
            0x31, 0xC0, // XOR AX, AX
            0x58        // POP AX
        );
        _cpu.Step(); // PUSH
        _cpu.Step(); // XOR AX, AX
        Assert.Equal((ushort)0, _cpu.Regs.AX);
        _cpu.Step(); // POP AX
        Assert.Equal((ushort)0xBEEF, _cpu.Regs.AX);
    }

    [Fact]
    public void XorRegReg_ZerosRegister()
    {
        _cpu.Regs.AX = 0x1234;
        LoadCode(0x31, 0xC0); // XOR AX, AX
        _cpu.Step();
        Assert.Equal((ushort)0, _cpu.Regs.AX);
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void IncDec_ModifiesValue()
    {
        _cpu.Regs.AX = 0x0005;
        LoadCode(0x40, 0x48); // INC AX, DEC AX
        _cpu.Step();
        Assert.Equal((ushort)0x0006, _cpu.Regs.AX);
        _cpu.Step();
        Assert.Equal((ushort)0x0005, _cpu.Regs.AX);
    }

    [Fact]
    public void CallRet_ReturnsToCorrectAddress()
    {
        // CALL +3, which skips to NOP, NOP, NOP, then RET
        LoadCode(
            0xE8, 0x03, 0x00, // CALL +3 (jumps to offset 0x0106)
            0x90,              // NOP (return point = 0x0103)
            0x90,              // NOP
            0x90,              // NOP
            0xC3               // RET (at 0x0106)
        );
        ushort returnAddr = (ushort)(_cpu.Regs.IP + 3); // After CALL instruction
        _cpu.Step(); // CALL
        Assert.Equal((ushort)0x0106, _cpu.Regs.IP);
        _cpu.Step(); // RET
        Assert.Equal(returnAddr, _cpu.Regs.IP);
    }

    [Fact]
    public void Loop_DecrementsAndJumps()
    {
        _cpu.Regs.CX = 3;
        _cpu.Regs.AX = 0;
        LoadCode(
            0x40,       // INC AX (at 0x0100)
            0xE2, 0xFD  // LOOP -3 (back to 0x0100)
        );
        for (int i = 0; i < 6; i++) _cpu.Step(); // 3 iterations of INC + LOOP
        Assert.Equal((ushort)3, _cpu.Regs.AX);
        Assert.Equal((ushort)0, _cpu.Regs.CX);
    }

    [Fact]
    public void Hlt_StopsCpu()
    {
        LoadCode(0xF4); // HLT
        _cpu.Step();
        Assert.True(_cpu.IsHalted);
    }

    [Fact]
    public void MovMemory_WritesAndReads()
    {
        // MOV [0x0200], AX  then  MOV BX, [0x0200]
        _cpu.Regs.AX = 0xCAFE;
        LoadCode(
            0xA3, 0x00, 0x02, // MOV [0x0200], AX
            0x8B, 0x1E, 0x00, 0x02 // MOV BX, [0x0200]
        );
        _cpu.Step(); // MOV [0x0200], AX
        _cpu.Step(); // MOV BX, [0x0200]
        Assert.Equal((ushort)0xCAFE, _cpu.Regs.BX);
    }

    [Fact]
    public void Lea_LoadsEffectiveAddress()
    {
        _cpu.Regs.BX = 0x0010;
        _cpu.Regs.SI = 0x0005;
        LoadCode(0x8D, 0x00); // LEA AX, [BX+SI]
        _cpu.Step();
        Assert.Equal((ushort)0x0015, _cpu.Regs.AX);
    }

    [Fact]
    public void ShlShr_ShiftsCorrectly()
    {
        _cpu.Regs.AX = 0x0001;
        LoadCode(
            0xD1, 0xE0, // SHL AX, 1
            0xD1, 0xE0, // SHL AX, 1
            0xD1, 0xE8  // SHR AX, 1
        );
        _cpu.Step(); // AX = 2
        Assert.Equal((ushort)2, _cpu.Regs.AX);
        _cpu.Step(); // AX = 4
        Assert.Equal((ushort)4, _cpu.Regs.AX);
        _cpu.Step(); // AX = 2
        Assert.Equal((ushort)2, _cpu.Regs.AX);
    }

    [Fact]
    public void Int_TriggersInterruptHandler()
    {
        byte triggeredVector = 0;
        _cpu.InterruptTriggered += v => triggeredVector = v;
        LoadCode(0xCD, 0x21); // INT 21h
        _cpu.Step();
        Assert.Equal(0x21, triggeredVector);
    }

    [Fact]
    public void SAHF_StoresAHIntoFlags()
    {
        var mem = new MsDos.Core.Memory.MemoryBus();
        var cpu = new MsDos.Core.Cpu.Cpu8086(mem);
        cpu.Regs.CS = 0x1000;
        cpu.Regs.IP = 0x0100;
        cpu.Regs.AH = 0xD5; // SF=1, ZF=1, AF=1, PF=1, CF=1

        mem.WriteByte(0x1000, 0x0100, 0x9E); // SAHF
        cpu.Step();

        // Check that the low byte of flags reflects AH
        Assert.True((cpu.Regs.Flags & MsDos.Core.Cpu.CpuFlags.Carry) != 0);
        Assert.True((cpu.Regs.Flags & MsDos.Core.Cpu.CpuFlags.Parity) != 0);
    }

    [Fact]
    public void LAHF_LoadsFlagsIntoAH()
    {
        var mem = new MsDos.Core.Memory.MemoryBus();
        var cpu = new MsDos.Core.Cpu.Cpu8086(mem);
        cpu.Regs.CS = 0x1000;
        cpu.Regs.IP = 0x0100;
        cpu.Regs.Flags = MsDos.Core.Cpu.CpuFlags.Carry | MsDos.Core.Cpu.CpuFlags.Zero;

        mem.WriteByte(0x1000, 0x0100, 0x9F); // LAHF
        cpu.Step();

        // AH should contain flags low byte
        byte ah = cpu.Regs.AH;
        Assert.True((ah & 0x01) != 0); // CF
        Assert.True((ah & 0x40) != 0); // ZF
    }

    [Fact]
    public void XLAT_TranslatesAL()
    {
        var mem = new MsDos.Core.Memory.MemoryBus();
        var cpu = new MsDos.Core.Cpu.Cpu8086(mem);
        cpu.Regs.CS = 0x1000;
        cpu.Regs.IP = 0x0100;
        cpu.Regs.DS = 0x2000;
        cpu.Regs.BX = 0x0050;
        cpu.Regs.AL = 5;

        // Put translation table value at DS:BX+AL = 2000:0055
        mem.WriteByte(0x2000, 0x0055, 0x42); // 'B'
        mem.WriteByte(0x1000, 0x0100, 0xD7); // XLAT
        cpu.Step();

        Assert.Equal(0x42, cpu.Regs.AL);
    }

    [Fact]
    public void AAM_DividesALBy10()
    {
        var mem = new MsDos.Core.Memory.MemoryBus();
        var cpu = new MsDos.Core.Cpu.Cpu8086(mem);
        cpu.Regs.CS = 0x1000;
        cpu.Regs.IP = 0x0100;
        cpu.Regs.AL = 35; // 35 / 10 = 3 remainder 5

        mem.WriteByte(0x1000, 0x0100, 0xD4); // AAM
        mem.WriteByte(0x1000, 0x0101, 0x0A); // base 10
        cpu.Step();

        Assert.Equal(3, cpu.Regs.AH);
        Assert.Equal(5, cpu.Regs.AL);
    }

    [Fact]
    public void AAD_CombinesAHAL()
    {
        var mem = new MsDos.Core.Memory.MemoryBus();
        var cpu = new MsDos.Core.Cpu.Cpu8086(mem);
        cpu.Regs.CS = 0x1000;
        cpu.Regs.IP = 0x0100;
        cpu.Regs.AH = 3;
        cpu.Regs.AL = 5; // 3 * 10 + 5 = 35

        mem.WriteByte(0x1000, 0x0100, 0xD5); // AAD
        mem.WriteByte(0x1000, 0x0101, 0x0A); // base 10
        cpu.Step();

        Assert.Equal(35, cpu.Regs.AL);
        Assert.Equal(0, cpu.Regs.AH);
    }

    [Fact]
    public void DAA_AdjustsAfterBCDAdd()
    {
        var mem = new MsDos.Core.Memory.MemoryBus();
        var cpu = new MsDos.Core.Cpu.Cpu8086(mem);
        cpu.Regs.CS = 0x1000;
        cpu.Regs.IP = 0x0100;
        // Set AL to result of 0x19 + 0x19 = 0x32 (invalid BCD)
        cpu.Regs.AL = 0x19 + 0x19; // = 0x32
        cpu.Regs.Flags |= MsDos.Core.Cpu.CpuFlags.AuxCarry; // Half-carry set from add

        mem.WriteByte(0x1000, 0x0100, 0x27); // DAA
        cpu.Step();

        // After DAA, should be adjusted: 0x32 + 6 = 0x38
        // (low nibble > 9 or AF set, so add 6)
        Assert.Equal(0x38, cpu.Regs.AL);
    }
}
