using MsDos.Core.Cpu;
using MsDos.Core.Interrupts;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Tests;

public class BiosDiskServiceTests
{
    private readonly MemoryBus _mem = new();
    private readonly Cpu8086 _cpu;
    private readonly EmulatorLog _log = new();
    private readonly BiosDiskService _disk;

    public BiosDiskServiceTests()
    {
        _cpu = new Cpu8086(_mem);
        _cpu.Regs.CS = 0x1000;
        _cpu.Regs.IP = 0x0100;
        _cpu.Regs.SS = 0x1000;
        _cpu.Regs.SP = 0xFFFE;
        _disk = new BiosDiskService(_cpu, _mem, _log);
    }

    [Fact]
    public void Reset_Succeeds()
    {
        _cpu.Regs.AH = 0x00; // Reset
        _cpu.Regs.DL = 0x00; // Drive A:
        _disk.Handle();
        Assert.Equal((byte)0, _cpu.Regs.AH);
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    [Fact]
    public void ReadSector_ReadsData()
    {
        // Create a 1.44M floppy image with known data in sector 1
        var image = new byte[1474560];
        for (int i = 0; i < 512; i++)
            image[i] = (byte)(i & 0xFF); // First sector has sequential bytes

        _disk.RegisterDisk(0x00, image, 2, 18, 80);

        // Read 1 sector from C=0, H=0, S=1 (first sector) into ES:BX
        _cpu.Regs.AH = 0x02; // Read sectors
        _cpu.Regs.AL = 1;    // 1 sector
        _cpu.Regs.CH = 0;    // Cylinder 0
        _cpu.Regs.CL = 1;    // Sector 1
        _cpu.Regs.DH = 0;    // Head 0
        _cpu.Regs.DL = 0x00; // Drive A:
        _cpu.Regs.ES = 0x2000;
        _cpu.Regs.BX = 0x0000;

        _disk.Handle();

        Assert.Equal((byte)0, _cpu.Regs.AH); // Success
        Assert.Equal((byte)1, _cpu.Regs.AL); // 1 sector read
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);

        // Verify data was copied to memory
        for (int i = 0; i < 512; i++)
            Assert.Equal((byte)(i & 0xFF), _mem.ReadByte(0x2000, (ushort)i));
    }

    [Fact]
    public void WriteSector_WritesData()
    {
        var image = new byte[1474560];
        _disk.RegisterDisk(0x00, image, 2, 18, 80);

        // Write data to memory first
        for (int i = 0; i < 512; i++)
            _mem.WriteByte(0x2000, (ushort)i, (byte)(255 - (i & 0xFF)));

        // Write 1 sector to C=0, H=0, S=2 (second sector)
        _cpu.Regs.AH = 0x03;
        _cpu.Regs.AL = 1;
        _cpu.Regs.CH = 0;
        _cpu.Regs.CL = 2; // Second sector
        _cpu.Regs.DH = 0;
        _cpu.Regs.DL = 0x00;
        _cpu.Regs.ES = 0x2000;
        _cpu.Regs.BX = 0x0000;

        _disk.Handle();

        Assert.Equal((byte)0, _cpu.Regs.AH);
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);

        // Verify data in image (sector 2 starts at byte 512)
        for (int i = 0; i < 512; i++)
            Assert.Equal((byte)(255 - (i & 0xFF)), image[512 + i]);
    }

    [Fact]
    public void GetDriveParams_ReturnGeometry()
    {
        var image = new byte[1474560];
        _disk.RegisterDisk(0x00, image, 2, 18, 80);

        _cpu.Regs.AH = 0x08;
        _cpu.Regs.DL = 0x00;
        _disk.Handle();

        Assert.Equal((byte)0, _cpu.Regs.AH);
        Assert.Equal((byte)1, _cpu.Regs.DH); // heads - 1 = 1
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }

    [Fact]
    public void ReadFromUnregisteredDrive_Fails()
    {
        _cpu.Regs.AH = 0x02;
        _cpu.Regs.AL = 1;
        _cpu.Regs.DL = 0x05; // Unregistered drive
        _disk.Handle();

        Assert.True((_cpu.Regs.Flags & CpuFlags.Carry) != 0); // Error
        Assert.Equal((byte)0x80, _cpu.Regs.AH); // Timeout/not ready
    }

    [Fact]
    public void AutoRegister_GuessesFloppyGeometry()
    {
        var image = new byte[1474560]; // 1.44M
        _disk.RegisterDiskAuto(0x00, image);

        _cpu.Regs.AH = 0x08;
        _cpu.Regs.DL = 0x00;
        _disk.Handle();

        Assert.Equal((byte)0, _cpu.Regs.AH); // Success
        Assert.False((_cpu.Regs.Flags & CpuFlags.Carry) != 0);
    }
}
