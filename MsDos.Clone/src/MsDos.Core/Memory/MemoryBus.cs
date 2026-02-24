namespace MsDos.Core.Memory;

/// <summary>
/// Emulates the 8086 1 MB address space (0x00000 – 0xFFFFF).
/// Provides byte and word read/write with segment:offset addressing.
/// </summary>
public sealed class MemoryBus
{
    /// <summary>Total addressable memory: 1 MB.</summary>
    public const int TotalSize = 1024 * 1024; // 1 MB

    private readonly byte[] _data = new byte[TotalSize];

    /// <summary>Direct access to raw memory (use with caution).</summary>
    public byte[] Raw => _data;

    // --- Physical-address operations ---

    public byte ReadByte(uint address) =>
        _data[address & 0xFFFFF];

    public void WriteByte(uint address, byte value) =>
        _data[address & 0xFFFFF] = value;

    public ushort ReadWord(uint address)
    {
        address &= 0xFFFFF;
        return (ushort)(_data[address] | (_data[(address + 1) & 0xFFFFF] << 8));
    }

    public void WriteWord(uint address, ushort value)
    {
        address &= 0xFFFFF;
        _data[address] = (byte)(value & 0xFF);
        _data[(address + 1) & 0xFFFFF] = (byte)((value >> 8) & 0xFF);
    }

    public uint ReadDword(uint address)
    {
        address &= 0xFFFFF;
        return (uint)(_data[address]
            | (_data[(address + 1) & 0xFFFFF] << 8)
            | (_data[(address + 2) & 0xFFFFF] << 16)
            | (_data[(address + 3) & 0xFFFFF] << 24));
    }

    public void WriteDword(uint address, uint value)
    {
        address &= 0xFFFFF;
        _data[address] = (byte)(value & 0xFF);
        _data[(address + 1) & 0xFFFFF] = (byte)((value >> 8) & 0xFF);
        _data[(address + 2) & 0xFFFFF] = (byte)((value >> 16) & 0xFF);
        _data[(address + 3) & 0xFFFFF] = (byte)((value >> 24) & 0xFF);
    }

    // --- Segment:offset convenience ---

    public byte ReadByte(ushort segment, ushort offset) =>
        ReadByte(Cpu.Registers.PhysicalAddress(segment, offset));

    public void WriteByte(ushort segment, ushort offset, byte value) =>
        WriteByte(Cpu.Registers.PhysicalAddress(segment, offset), value);

    public ushort ReadWord(ushort segment, ushort offset) =>
        ReadWord(Cpu.Registers.PhysicalAddress(segment, offset));

    public void WriteWord(ushort segment, ushort offset, ushort value) =>
        WriteWord(Cpu.Registers.PhysicalAddress(segment, offset), value);

    public uint ReadDword(ushort segment, ushort offset) =>
        ReadDword(Cpu.Registers.PhysicalAddress(segment, offset));

    public void WriteDword(ushort segment, ushort offset, uint value) =>
        WriteDword(Cpu.Registers.PhysicalAddress(segment, offset), value);

    /// <summary>Load a block of data at a physical address.</summary>
    public void LoadData(uint address, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
            _data[(address + (uint)i) & 0xFFFFF] = data[i];
    }

    /// <summary>Load a block of data at a segment:offset address.</summary>
    public void LoadData(ushort segment, ushort offset, ReadOnlySpan<byte> data) =>
        LoadData(Cpu.Registers.PhysicalAddress(segment, offset), data);

    /// <summary>Read a block of data from memory.</summary>
    public byte[] ReadBlock(uint address, int length)
    {
        var result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = _data[(address + (uint)i) & 0xFFFFF];
        return result;
    }

    /// <summary>Clear all memory to zero.</summary>
    public void Clear() => Array.Clear(_data, 0, _data.Length);
}
