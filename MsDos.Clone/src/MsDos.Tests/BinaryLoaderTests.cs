using MsDos.Core.Cpu;
using MsDos.Core.Dos;
using MsDos.Core.Memory;

namespace MsDos.Tests;

public class BinaryLoaderTests
{
    private readonly MemoryBus _mem = new();
    private readonly Cpu8086 _cpu;
    private readonly BinaryLoader _loader;

    public BinaryLoaderTests()
    {
        _cpu = new Cpu8086(_mem);
        _loader = new BinaryLoader(_mem, _cpu);
    }

    [Fact]
    public void LoadCom_SetsCorrectEntryPoint()
    {
        // Minimal COM: MOV AX, 0x4C00; INT 21h
        byte[] com = { 0xB8, 0x00, 0x4C, 0xCD, 0x21 };
        bool result = _loader.Load(com);
        Assert.True(result);
        Assert.Equal((ushort)0x1000, _cpu.Regs.CS);
        Assert.Equal((ushort)0x0100, _cpu.Regs.IP);
    }

    [Fact]
    public void LoadCom_DataIsAtCorrectAddress()
    {
        byte[] com = { 0xB8, 0x00, 0x4C, 0xCD, 0x21 };
        _loader.Load(com);
        // Data should be at physical address 0x10100
        Assert.Equal(0xB8, _mem.ReadByte(0x1000, 0x0100));
        Assert.Equal(0xCD, _mem.ReadByte(0x1000, 0x0103));
    }

    [Fact]
    public void LoadCom_BuildsPSP()
    {
        byte[] com = { 0x90 }; // NOP
        _loader.Load(com);
        // PSP at segment:0000 should have INT 20h (CD 20)
        Assert.Equal(0xCD, _mem.ReadByte(0x1000, 0x0000));
        Assert.Equal(0x20, _mem.ReadByte(0x1000, 0x0001));
    }

    [Fact]
    public void LoadCom_StackIsSetup()
    {
        byte[] com = { 0x90 };
        _loader.Load(com);
        Assert.Equal((ushort)0x1000, _cpu.Regs.SS);
        // SP should be below 0xFFFE (pushed return address 0x0000)
        Assert.True(_cpu.Regs.SP < 0xFFFE);
    }

    [Fact]
    public void LoadExe_DetectsMZHeader()
    {
        // Minimal MZ header (too small to be a valid EXE, but should be detected)
        byte[] exe = new byte[28];
        exe[0] = 0x4D; // 'M'
        exe[1] = 0x5A; // 'Z'
        // Total pages = 1
        exe[4] = 1;
        // Header paragraphs = 2 (32 bytes)
        exe[8] = 2;
        // This is too small to be valid but should return false gracefully
        bool result = _loader.Load(exe);
        // With headerSize=32 and data length=28, it should fail
        Assert.False(result);
    }

    [Fact]
    public void SetCommandTail_WritesToPSP()
    {
        byte[] com = { 0x90 };
        _loader.Load(com);
        _loader.SetCommandTail(0x1000, " /test");
        // Length at PSP:0080
        Assert.Equal(6, _mem.ReadByte(0x1000, 0x0080));
        // First char at PSP:0081
        Assert.Equal((byte)' ', _mem.ReadByte(0x1000, 0x0081));
    }

    [Fact]
    public void LoadEmptyData_ReturnsFalse()
    {
        bool result = _loader.Load(Array.Empty<byte>());
        Assert.False(result);
    }

    [Fact]
    public void LoadSingleByte_Succeeds()
    {
        bool result = _loader.Load(new byte[] { 0x90 });
        // Single byte COM is valid
        Assert.True(result);
    }
}
