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

    [Fact]
    public void PUSHA_POPA_PreservesRegisters()
    {
        _cpu.Regs.AX = 0x1111;
        _cpu.Regs.CX = 0x2222;
        _cpu.Regs.DX = 0x3333;
        _cpu.Regs.BX = 0x4444;
        _cpu.Regs.BP = 0x5555;
        _cpu.Regs.SI = 0x6666;
        _cpu.Regs.DI = 0x7777;
        ushort origSP = _cpu.Regs.SP;
        LoadCode(0x60, 0x61); // PUSHA, POPA
        _cpu.Step(); // PUSHA
        Assert.Equal((ushort)(origSP - 16), _cpu.Regs.SP); // 8 words pushed
        _cpu.Step(); // POPA
        Assert.Equal(origSP, _cpu.Regs.SP);
        Assert.Equal((ushort)0x1111, _cpu.Regs.AX);
        Assert.Equal((ushort)0x2222, _cpu.Regs.CX);
        Assert.Equal((ushort)0x3333, _cpu.Regs.DX);
        Assert.Equal((ushort)0x4444, _cpu.Regs.BX);
        Assert.Equal((ushort)0x5555, _cpu.Regs.BP);
        Assert.Equal((ushort)0x6666, _cpu.Regs.SI);
        Assert.Equal((ushort)0x7777, _cpu.Regs.DI);
    }

    [Fact]
    public void PUSH_Imm16_PushesValue()
    {
        ushort origSP = _cpu.Regs.SP;
        LoadCode(0x68, 0xAD, 0xDE); // PUSH 0xDEAD
        _cpu.Step();
        Assert.Equal((ushort)(origSP - 2), _cpu.Regs.SP);
        Assert.Equal((ushort)0xDEAD, _mem.ReadWord(_cpu.Regs.SS, _cpu.Regs.SP));
    }

    [Fact]
    public void PUSH_Imm8_SignExtends()
    {
        ushort origSP = _cpu.Regs.SP;
        LoadCode(0x6A, 0xFE); // PUSH -2 (sign-extended to 0xFFFE)
        _cpu.Step();
        Assert.Equal((ushort)(origSP - 2), _cpu.Regs.SP);
        Assert.Equal((ushort)0xFFFE, _mem.ReadWord(_cpu.Regs.SS, _cpu.Regs.SP));
    }

    [Fact]
    public void ENTER_LEAVE_StackFrame()
    {
        _cpu.Regs.BP = 0x1234;
        LoadCode(
            0xC8, 0x10, 0x00, 0x00, // ENTER 16, 0
            0xC9                      // LEAVE
        );
        ushort origSP = _cpu.Regs.SP;
        _cpu.Step(); // ENTER
        // BP should be set to the frame pointer
        ushort frameBP = _cpu.Regs.BP;
        Assert.Equal((ushort)(origSP - 2), frameBP); // BP = old SP - 2
        Assert.Equal((ushort)(frameBP - 16), _cpu.Regs.SP); // SP = BP - 16

        _cpu.Step(); // LEAVE
        Assert.Equal(origSP, _cpu.Regs.SP);
        Assert.Equal((ushort)0x1234, _cpu.Regs.BP);
    }

    [Fact]
    public void IMUL_Imm16_ThreeOperand()
    {
        _cpu.Regs.BX = 100;
        // IMUL AX, BX, 7  → opcode 0x6B, modrm = 0xC3 (reg=AX, rm=BX), imm8 = 7
        LoadCode(0x6B, 0xC3, 0x07);
        _cpu.Step();
        Assert.Equal((ushort)700, _cpu.Regs.AX);
    }

    [Fact]
    public void MOVZX_ZeroExtendsR8ToR16()
    {
        _cpu.Regs.BX = 0xFFFF; // Will be overwritten
        _cpu.Regs.CL = 0x80;   // 128 in unsigned
        // MOVZX BX, CL → 0x0F B6 modrm=0xD9 (reg=BX(3), rm=CL(1))
        LoadCode(0x0F, 0xB6, 0xD9);
        _cpu.Step();
        Assert.Equal((ushort)0x0080, _cpu.Regs.BX);
    }

    [Fact]
    public void MOVSX_SignExtendsR8ToR16()
    {
        _cpu.Regs.BX = 0x0000;
        _cpu.Regs.CL = 0x80; // -128 in signed
        // MOVSX BX, CL → 0x0F BE modrm=0xD9
        LoadCode(0x0F, 0xBE, 0xD9);
        _cpu.Step();
        Assert.Equal((ushort)0xFF80, _cpu.Regs.BX);
    }

    [Fact]
    public void Jcc_Near_JumpsWithRel16()
    {
        // Set Zero flag, then JZ near (+5 from end of instruction)
        _cpu.Regs.Flags |= CpuFlags.Zero;
        LoadCode(0x0F, 0x84, 0x05, 0x00); // JZ near +5
        ushort ipAfterInstruction = (ushort)(_cpu.Regs.IP + 4); // 4-byte instruction
        _cpu.Step();
        Assert.Equal((ushort)(ipAfterInstruction + 5), _cpu.Regs.IP);
    }

    [Fact]
    public void Jcc_Near_NoJumpWhenConditionFalse()
    {
        // Clear Zero flag, JZ near should NOT jump
        _cpu.Regs.Flags &= ~CpuFlags.Zero;
        LoadCode(0x0F, 0x84, 0x05, 0x00); // JZ near +5
        ushort ipAfterInstruction = (ushort)(_cpu.Regs.IP + 4);
        _cpu.Step();
        Assert.Equal(ipAfterInstruction, _cpu.Regs.IP); // Did not jump
    }

    [Fact]
    public void IMUL_r16_rm16_TwoOperand()
    {
        _cpu.Regs.AX = 25;
        _cpu.Regs.BX = 4;
        // IMUL AX, BX → 0x0F AF modrm=0xC3 (reg=AX(0), rm=BX(3))
        LoadCode(0x0F, 0xAF, 0xC3);
        _cpu.Step();
        Assert.Equal((ushort)100, _cpu.Regs.AX);
    }

    [Fact]
    public void BSF_FindsLowestSetBit()
    {
        _cpu.Regs.BX = 0x0040; // Bit 6 is lowest set bit
        // BSF AX, BX → 0x0F BC modrm=0xC3
        LoadCode(0x0F, 0xBC, 0xC3);
        _cpu.Step();
        Assert.Equal((ushort)6, _cpu.Regs.AX);
        Assert.False((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void BSR_FindsHighestSetBit()
    {
        _cpu.Regs.BX = 0x0040; // Bit 6 is highest set bit
        // BSR AX, BX → 0x0F BD modrm=0xC3
        LoadCode(0x0F, 0xBD, 0xC3);
        _cpu.Step();
        Assert.Equal((ushort)6, _cpu.Regs.AX);
    }

    [Fact]
    public void BT_TestsBit()
    {
        _cpu.Regs.BX = 0x0004; // Bit 2 set
        _cpu.Regs.CX = 2;       // Test bit 2
        // BT BX, CX → 0x0F A3 modrm=0xCB (reg=CX(1), rm=BX(3))
        LoadCode(0x0F, 0xA3, 0xCB);
        _cpu.Step();
        Assert.True((_cpu.Regs.Flags & CpuFlags.Carry) != 0); // Bit was set
    }

    [Fact]
    public void SETcc_SetsOnCondition()
    {
        _cpu.Regs.Flags |= CpuFlags.Zero;
        _cpu.Regs.CL = 0xFF; // Will be overwritten
        // SETZ CL → 0x0F 0x94 modrm=0xC1
        LoadCode(0x0F, 0x94, 0xC1);
        _cpu.Step();
        Assert.Equal((byte)1, _cpu.Regs.CL);
    }

    [Fact]
    public void Group1_CMP_MemDisp16_DoesNotConsumeExtraBytes()
    {
        // CMP byte [BX+1234h], 0x56
        // Encoding: 80 BF 34 12 56  (opcode=80, modrm=BF (mod=10,reg=7,rm=7), disp16=1234, imm8=56)
        // This tests that DecodeModRM_Address displacement is not double-consumed.
        _cpu.Regs.BX = 0x0000;
        // Write value at DS:1234h for comparison
        _mem.WriteByte(_cpu.Regs.DS, 0x1234, 0x56);
        LoadCode(0x80, 0xBF, 0x34, 0x12, 0x56, // CMP byte [BX+1234h], 56h
                 0x90);                           // NOP (marker for correct IP)
        ushort startIP = _cpu.Regs.IP;
        _cpu.Step();
        // CMP should be 5 bytes: 80 + modrm + disp16(2) + imm8 = 5
        Assert.Equal((ushort)(startIP + 5), _cpu.Regs.IP);
        // ZF should be set since mem value (0x56) == immediate (0x56)
        Assert.True((_cpu.Regs.Flags & CpuFlags.Zero) != 0);
    }

    [Fact]
    public void ADD_MemDisp16_Reg_DoesNotConsumeExtraBytes()
    {
        // ADD byte [BX+100h], AL
        // Encoding: 00 87 00 01  (opcode=00, modrm=87 (mod=10,reg=0,rm=7), disp16=0100)
        _cpu.Regs.BX = 0x0050;
        _cpu.Regs.AL = 0x05;
        _mem.WriteByte(_cpu.Regs.DS, 0x0150, 0x10); // [BX+100h] = DS:0150 = 0x10
        LoadCode(0x00, 0x87, 0x00, 0x01,  // ADD byte [BX+100h], AL
                 0x90);                     // NOP (marker)
        ushort startIP = _cpu.Regs.IP;
        _cpu.Step();
        // ADD r/m8, r8 with disp16 is 4 bytes: 00 + modrm + disp16(2)
        Assert.Equal((ushort)(startIP + 4), _cpu.Regs.IP);
        // Memory should contain 0x10 + 0x05 = 0x15
        Assert.Equal(0x15, _mem.ReadByte(_cpu.Regs.DS, 0x0150));
    }

    [Fact]
    public void NOT_MemDisp8_DoesNotConsumeExtraBytes()
    {
        // NOT byte [BX+10h]
        // Encoding: F6 57 10  (opcode=F6, modrm=57 (mod=01,reg=2,rm=7), disp8=10h)
        _cpu.Regs.BX = 0x0030;
        _mem.WriteByte(_cpu.Regs.DS, 0x0040, 0x0F); // [BX+10h] = DS:0040 = 0x0F
        LoadCode(0xF6, 0x57, 0x10,   // NOT byte [BX+10h]
                 0x90);                // NOP (marker)
        ushort startIP = _cpu.Regs.IP;
        _cpu.Step();
        // F6 /2 with disp8 = 3 bytes: F6 + modrm + disp8
        Assert.Equal((ushort)(startIP + 3), _cpu.Regs.IP);
        // NOT 0x0F = 0xF0
        Assert.Equal(0xF0, _mem.ReadByte(_cpu.Regs.DS, 0x0040));
    }

    [Fact]
    public void INC_MemDirect_DoesNotConsumeExtraBytes()
    {
        // INC word [0x200]
        // Encoding: FF 06 00 02  (opcode=FF, modrm=06 (mod=00,reg=0,rm=6=direct), disp16=0200)
        _mem.WriteWord(_cpu.Regs.DS, 0x0200, 0x00FF);
        LoadCode(0xFF, 0x06, 0x00, 0x02,  // INC word [0200h]
                 0x90);                     // NOP
        ushort startIP = _cpu.Regs.IP;
        _cpu.Step();
        // FF /0 with direct addressing = 4 bytes: FF + modrm + addr16(2)
        Assert.Equal((ushort)(startIP + 4), _cpu.Regs.IP);
        Assert.Equal((ushort)0x0100, _mem.ReadWord(_cpu.Regs.DS, 0x0200));
    }

    [Fact]
    public void SHL_MemDisp16_DoesNotConsumeExtraBytes()
    {
        // SHL byte [BX+300h], 1
        // Encoding: D0 A7 00 03  (opcode=D0, modrm=A7 (mod=10,reg=4,rm=7), disp16=0300)
        _cpu.Regs.BX = 0x0000;
        _mem.WriteByte(_cpu.Regs.DS, 0x0300, 0x01);
        LoadCode(0xD0, 0xA7, 0x00, 0x03,  // SHL byte [BX+300h], 1
                 0x90);                     // NOP
        ushort startIP = _cpu.Regs.IP;
        _cpu.Step();
        // D0 /4 with disp16 = 4 bytes
        Assert.Equal((ushort)(startIP + 4), _cpu.Regs.IP);
        Assert.Equal(0x02, _mem.ReadByte(_cpu.Regs.DS, 0x0300)); // 1 << 1 = 2
    }
}
