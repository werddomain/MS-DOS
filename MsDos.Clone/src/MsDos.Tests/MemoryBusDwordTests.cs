using MsDos.Core.Memory;

namespace MsDos.Tests;

/// <summary>
/// Tests for 32-bit (DWORD) read/write support in MemoryBus.
/// </summary>
public class MemoryBusDwordTests
{
    private readonly MemoryBus _mem = new();

    // ─── Physical address DWORD read/write ──────────────────────────

    [Fact]
    public void WriteDword_ReadDword_RoundTrip()
    {
        _mem.WriteDword(0x1000, 0xDEADBEEF);
        Assert.Equal(0xDEADBEEFu, _mem.ReadDword(0x1000));
    }

    [Fact]
    public void WriteDword_LittleEndian_ByteOrder()
    {
        _mem.WriteDword(0x2000, 0x44332211);
        Assert.Equal(0x11, _mem.ReadByte(0x2000));   // Lowest byte first
        Assert.Equal(0x22, _mem.ReadByte(0x2001));
        Assert.Equal(0x33, _mem.ReadByte(0x2002));
        Assert.Equal(0x44, _mem.ReadByte(0x2003));   // Highest byte last
    }

    [Fact]
    public void ReadDword_FromIndividualBytes()
    {
        _mem.WriteByte(0x3000, 0xAA);
        _mem.WriteByte(0x3001, 0xBB);
        _mem.WriteByte(0x3002, 0xCC);
        _mem.WriteByte(0x3003, 0xDD);
        Assert.Equal(0xDDCCBBAAu, _mem.ReadDword(0x3000));
    }

    [Fact]
    public void WriteDword_Zero()
    {
        _mem.WriteDword(0x4000, 0xFFFFFFFF);
        _mem.WriteDword(0x4000, 0x00000000);
        Assert.Equal(0u, _mem.ReadDword(0x4000));
    }

    [Fact]
    public void WriteDword_MaxValue()
    {
        _mem.WriteDword(0x5000, 0xFFFFFFFF);
        Assert.Equal(0xFFFFFFFFu, _mem.ReadDword(0x5000));
    }

    [Fact]
    public void WriteDword_DoesNotClobberAdjacentMemory()
    {
        _mem.WriteByte(0x5FFC, 0xAA);
        _mem.WriteByte(0x5FFD, 0xBB);
        _mem.WriteByte(0x6004, 0xCC);
        _mem.WriteByte(0x6005, 0xDD);

        _mem.WriteDword(0x6000, 0x12345678); // Write at 0x6000-0x6003

        // Adjacent bytes should be untouched
        Assert.Equal(0xAA, _mem.ReadByte(0x5FFC));
        Assert.Equal(0xBB, _mem.ReadByte(0x5FFD));
        Assert.Equal(0xCC, _mem.ReadByte(0x6004));
        Assert.Equal(0xDD, _mem.ReadByte(0x6005));
    }

    // ─── Segment:offset DWORD read/write ────────────────────────────

    [Fact]
    public void WriteDword_SegmentOffset_RoundTrip()
    {
        _mem.WriteDword(0x1000, 0x0100, 0xCAFEBABE);
        Assert.Equal(0xCAFEBABEu, _mem.ReadDword(0x1000, 0x0100));
    }

    [Fact]
    public void WriteDword_SegmentOffset_MatchesPhysical()
    {
        // Segment 0x1000, offset 0x0200 → physical 0x10200
        _mem.WriteDword(0x1000, 0x0200, 0x11223344);
        Assert.Equal(0x11223344u, _mem.ReadDword(0x10200));
    }

    // ─── 1MB wrap-around ────────────────────────────────────────────

    [Fact]
    public void WriteDword_AtTopOfMemory_Wraps()
    {
        // Address 0xFFFFE — last 2 bytes will wrap to 0x00000 and 0x00001
        _mem.WriteDword(0xFFFFE, 0xAABBCCDD);

        Assert.Equal(0xDD, _mem.ReadByte(0xFFFFE));
        Assert.Equal(0xCC, _mem.ReadByte(0xFFFFF));
        Assert.Equal(0xBB, _mem.ReadByte(0x00000)); // Wrapped
        Assert.Equal(0xAA, _mem.ReadByte(0x00001)); // Wrapped
    }

    [Fact]
    public void ReadDword_AtTopOfMemory_Wraps()
    {
        _mem.WriteByte(0xFFFFE, 0x11);
        _mem.WriteByte(0xFFFFF, 0x22);
        _mem.WriteByte(0x00000, 0x33);
        _mem.WriteByte(0x00001, 0x44);

        Assert.Equal(0x44332211u, _mem.ReadDword(0xFFFFE));
    }

    // ─── Dword overlaps with Word ───────────────────────────────────

    [Fact]
    public void DwordContainsTwoWords()
    {
        _mem.WriteDword(0x7000, 0xAABB1234);
        Assert.Equal((ushort)0x1234, _mem.ReadWord(0x7000));
        Assert.Equal((ushort)0xAABB, _mem.ReadWord(0x7002));
    }

    [Fact]
    public void TwoWordsFormDword()
    {
        _mem.WriteWord(0x8000, 0x5678);
        _mem.WriteWord(0x8002, 0x1234);
        Assert.Equal(0x12345678u, _mem.ReadDword(0x8000));
    }
}
