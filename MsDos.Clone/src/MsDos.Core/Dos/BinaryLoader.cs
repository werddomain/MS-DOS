using MsDos.Core.Cpu;
using MsDos.Core.Memory;

namespace MsDos.Core.Dos;

/// <summary>
/// Loads DOS binary executables (COM and EXE format) into memory
/// and prepares the CPU state for execution.
/// </summary>
public sealed class BinaryLoader
{
    private readonly MemoryBus _mem;
    private readonly Cpu8086 _cpu;

    /// <summary>Default load segment for COM files.</summary>
    private const ushort DefaultLoadSegment = 0x1000;

    public BinaryLoader(MemoryBus mem, Cpu8086 cpu)
    {
        _mem = mem;
        _cpu = cpu;
    }

    /// <summary>
    /// Load and prepare a binary file for execution.
    /// Detects format from header (EXE has 'MZ' signature) or defaults to COM.
    /// </summary>
    /// <param name="data">Raw binary file data.</param>
    /// <param name="loadSegment">Segment to load at (default 0x1000).</param>
    /// <returns>True if loaded successfully.</returns>
    public bool Load(ReadOnlySpan<byte> data, ushort loadSegment = DefaultLoadSegment)
    {
        if (data.Length < 2)
            return false;

        // Check for MZ (EXE) header
        if (data[0] == 0x4D && data[1] == 0x5A)
            return LoadExe(data, loadSegment);

        return LoadCom(data, loadSegment);
    }

    /// <summary>
    /// Load a COM file. COM files are loaded at segment:0100h and
    /// execution begins at CS:IP = segment:0100h.
    /// </summary>
    private bool LoadCom(ReadOnlySpan<byte> data, ushort loadSegment)
    {
        if (data.Length > 0xFF00) // COM files max ~64KB
            return false;

        // Build PSP at segment:0000h
        BuildPSP(loadSegment);

        // Load program at segment:0100h
        _mem.LoadData(loadSegment, 0x0100, data);

        // Set up CPU state
        _cpu.Regs.CS = loadSegment;
        _cpu.Regs.DS = loadSegment;
        _cpu.Regs.ES = loadSegment;
        _cpu.Regs.SS = loadSegment;
        _cpu.Regs.SP = 0xFFFE; // Stack at top of segment
        _cpu.Regs.IP = 0x0100; // COM entry point

        // Push 0x0000 onto stack (return address for INT 20h termination)
        _cpu.Regs.SP -= 2;
        _mem.WriteWord(loadSegment, _cpu.Regs.SP, 0x0000);

        return true;
    }

    /// <summary>
    /// Load an EXE (MZ) file with relocation support.
    /// </summary>
    private bool LoadExe(ReadOnlySpan<byte> data, ushort loadSegment)
    {
        if (data.Length < 28) return false;

        // Parse MZ header
        ushort lastPageSize = (ushort)(data[2] | (data[3] << 8));
        ushort totalPages = (ushort)(data[4] | (data[5] << 8));
        ushort relocCount = (ushort)(data[6] | (data[7] << 8));
        ushort headerParagraphs = (ushort)(data[8] | (data[9] << 8));
        // ushort minAlloc = (ushort)(data[10] | (data[11] << 8));
        // ushort maxAlloc = (ushort)(data[12] | (data[13] << 8));
        ushort initSS = (ushort)(data[14] | (data[15] << 8));
        ushort initSP = (ushort)(data[16] | (data[17] << 8));
        // ushort checksum = (ushort)(data[18] | (data[19] << 8));
        ushort initIP = (ushort)(data[20] | (data[21] << 8));
        ushort initCS = (ushort)(data[22] | (data[23] << 8));
        ushort relocOffset = (ushort)(data[24] | (data[25] << 8));

        int headerSize = headerParagraphs * 16;
        int imageSize = totalPages * 512;
        if (lastPageSize > 0) imageSize -= (512 - lastPageSize);
        imageSize -= headerSize;

        if (headerSize >= data.Length) return false;

        // Build PSP
        BuildPSP(loadSegment);
        ushort codeSegment = (ushort)(loadSegment + 0x10); // Skip PSP (256 bytes = 16 paragraphs)

        // Load program image after PSP
        var imageData = data.Slice(headerSize, Math.Min(imageSize, data.Length - headerSize));
        _mem.LoadData(codeSegment, 0x0000, imageData);

        // Apply relocations
        for (int i = 0; i < relocCount; i++)
        {
            int relocAddr = relocOffset + i * 4;
            if (relocAddr + 4 > data.Length) break;

            ushort relOff = (ushort)(data[relocAddr] | (data[relocAddr + 1] << 8));
            ushort relSeg = (ushort)(data[relocAddr + 2] | (data[relocAddr + 3] << 8));

            uint physAddr = Registers.PhysicalAddress((ushort)(codeSegment + relSeg), relOff);
            ushort origVal = _mem.ReadWord(physAddr);
            _mem.WriteWord(physAddr, (ushort)(origVal + codeSegment));
        }

        // Set up CPU state
        _cpu.Regs.CS = (ushort)(codeSegment + initCS);
        _cpu.Regs.IP = initIP;
        _cpu.Regs.SS = (ushort)(codeSegment + initSS);
        _cpu.Regs.SP = initSP;
        _cpu.Regs.DS = loadSegment;
        _cpu.Regs.ES = loadSegment;

        return true;
    }

    /// <summary>
    /// Build a minimal Program Segment Prefix (PSP) at the given segment.
    /// </summary>
    private void BuildPSP(ushort segment)
    {
        // Clear PSP area (256 bytes)
        for (int i = 0; i < 256; i++)
            _mem.WriteByte(segment, (ushort)i, 0);

        // INT 20h at PSP:0000 (program termination)
        _mem.WriteByte(segment, 0x0000, 0xCD); // INT
        _mem.WriteByte(segment, 0x0001, 0x20); // 20h

        // Memory size (top of memory in paragraphs)
        _mem.WriteWord(segment, 0x0002, 0xA000); // 640KB

        // Far call to DOS dispatcher at PSP:0005
        _mem.WriteByte(segment, 0x0005, 0xCD); // INT
        _mem.WriteByte(segment, 0x0006, 0x21); // 21h
        _mem.WriteByte(segment, 0x0007, 0xCB); // RETF

        // Default DTA at PSP:0080
        // Command tail length at PSP:0080 = 0 (empty)
        _mem.WriteByte(segment, 0x0080, 0x00);
        _mem.WriteByte(segment, 0x0081, 0x0D); // CR terminator

        // Environment segment (point to a small env block)
        ushort envSegment = (ushort)(segment - 0x10);
        _mem.WriteWord(segment, 0x002C, envSegment);

        // Set up minimal environment block
        // PATH=C:\
        byte[] env = System.Text.Encoding.ASCII.GetBytes("PATH=C:\\\0\0");
        _mem.LoadData(envSegment, 0x0000, env);
    }

    /// <summary>
    /// Set a command line tail in the PSP for the loaded program.
    /// </summary>
    public void SetCommandTail(ushort pspSegment, string args)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(args);
        int len = Math.Min(bytes.Length, 126);
        _mem.WriteByte(pspSegment, 0x0080, (byte)len);
        for (int i = 0; i < len; i++)
            _mem.WriteByte(pspSegment, (ushort)(0x0081 + i), bytes[i]);
        _mem.WriteByte(pspSegment, (ushort)(0x0081 + len), 0x0D);
    }
}
