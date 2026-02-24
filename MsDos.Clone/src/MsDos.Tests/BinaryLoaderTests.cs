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

    [Fact]
    public void BuildPSP_EnvironmentContainsProgramPath()
    {
        byte[] com = { 0x90 };
        _loader.Load(com, programPath: "A:\\RUNME.EXE");

        // Read environment segment from PSP at offset 0x002C
        ushort envSeg = _mem.ReadWord(0x1000, 0x002C);

        // The environment should contain "COMSPEC=C:\COMMAND.COM" + \0 + "PATH=C:\" + \0 + \0 + WORD(1) + "A:\RUNME.EXE" + \0
        // Find the double-null terminator
        int offset = 0;
        while (offset < 512)
        {
            byte b = _mem.ReadByte(envSeg, (ushort)offset);
            if (b == 0)
            {
                offset++;
                byte next = _mem.ReadByte(envSeg, (ushort)offset);
                if (next == 0)
                {
                    offset++; // past double-null
                    break;
                }
            }
            else
            {
                offset++;
            }
        }

        // After double-null: WORD count should be 1
        ushort count = _mem.ReadWord(envSeg, (ushort)offset);
        Assert.Equal(1, count);
        offset += 2;

        // Then the program path string
        var pathBytes = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < 50; i++)
        {
            byte b = _mem.ReadByte(envSeg, (ushort)(offset + i));
            if (b == 0) break;
            pathBytes.Add(b);
        }
        string programPath = System.Text.Encoding.ASCII.GetString(pathBytes.ToArray());
        Assert.Equal("A:\\RUNME.EXE", programPath);
    }

    [Fact]
    public void BuildPSP_EnvironmentContainsComspec()
    {
        byte[] com = { 0x90 };
        _loader.Load(com);

        ushort envSeg = _mem.ReadWord(0x1000, 0x002C);

        // First env variable should be COMSPEC=C:\COMMAND.COM
        var envBytes = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < 50; i++)
        {
            byte b = _mem.ReadByte(envSeg, (ushort)i);
            if (b == 0) break;
            envBytes.Add(b);
        }
        string firstEnv = System.Text.Encoding.ASCII.GetString(envBytes.ToArray());
        Assert.Equal("COMSPEC=C:\\COMMAND.COM", firstEnv);
    }

    [Fact]
    public void BuildPSP_DefaultProgramPathWhenNoneProvided()
    {
        byte[] com = { 0x90 };
        _loader.Load(com); // no programPath

        ushort envSeg = _mem.ReadWord(0x1000, 0x002C);

        // Scan past environment strings to find program path
        int offset = 0;
        while (offset < 512)
        {
            byte b = _mem.ReadByte(envSeg, (ushort)offset);
            if (b == 0)
            {
                offset++;
                byte next = _mem.ReadByte(envSeg, (ushort)offset);
                if (next == 0) { offset++; break; }
            }
            else offset++;
        }

        // Should have default path
        ushort count = _mem.ReadWord(envSeg, (ushort)offset);
        Assert.Equal(1, count);
        offset += 2;

        var pathBytes = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < 50; i++)
        {
            byte b = _mem.ReadByte(envSeg, (ushort)(offset + i));
            if (b == 0) break;
            pathBytes.Add(b);
        }
        string path = System.Text.Encoding.ASCII.GetString(pathBytes.ToArray());
        Assert.Equal("C:\\PROGRAM.EXE", path);
    }
}
