using MsDos.Core.Memory;

namespace MsDos.Tests;

public class MemoryBusTests
{
    [Fact]
    public void ReadWriteByte_RoundTrips()
    {
        var mem = new MemoryBus();
        mem.WriteByte(0x10000, 0xAB);
        Assert.Equal(0xAB, mem.ReadByte(0x10000));
    }

    [Fact]
    public void ReadWriteWord_LittleEndian()
    {
        var mem = new MemoryBus();
        mem.WriteWord(0x20000, 0x1234);
        Assert.Equal((ushort)0x1234, mem.ReadWord(0x20000));
        Assert.Equal(0x34, mem.ReadByte(0x20000)); // Low byte
        Assert.Equal(0x12, mem.ReadByte(0x20001)); // High byte
    }

    [Fact]
    public void SegmentOffset_CalculatesPhysicalAddress()
    {
        var mem = new MemoryBus();
        // Segment 0x1000, Offset 0x0100 = 0x10100
        mem.WriteByte(0x1000, 0x0100, 0xFF);
        Assert.Equal(0xFF, mem.ReadByte(0x10100));
    }

    [Fact]
    public void LoadData_CopiesBlock()
    {
        var mem = new MemoryBus();
        byte[] data = { 0x01, 0x02, 0x03, 0x04 };
        mem.LoadData(0x5000, data);
        Assert.Equal(0x01, mem.ReadByte(0x5000));
        Assert.Equal(0x04, mem.ReadByte(0x5003));
    }

    [Fact]
    public void AddressWrapsAt1MB()
    {
        var mem = new MemoryBus();
        // Address 0xFFFFF + 1 should wrap to 0x00000
        mem.WriteByte(0xFFFFF, 0x42);
        Assert.Equal(0x42, mem.ReadByte(0xFFFFF));
    }

    [Fact]
    public void Clear_ZerosAllMemory()
    {
        var mem = new MemoryBus();
        mem.WriteByte(0x0000, 0xFF);
        mem.WriteByte(0x80000, 0xFF);
        mem.Clear();
        Assert.Equal(0, mem.ReadByte(0x0000));
        Assert.Equal(0, mem.ReadByte(0x80000));
    }

    [Fact]
    public void ReadBlock_ReturnsCorrectData()
    {
        var mem = new MemoryBus();
        byte[] data = { 0xDE, 0xAD, 0xBE, 0xEF };
        mem.LoadData(0x1000, data);
        var result = mem.ReadBlock(0x1000, 4);
        Assert.Equal(data, result);
    }
}
