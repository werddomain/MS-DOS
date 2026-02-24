using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Interrupts;

/// <summary>
/// BIOS INT 13h disk services. Provides sector-level read/write access
/// to mounted disk images.
/// </summary>
public sealed class BiosDiskService
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _mem;
    private readonly EmulatorLog _log;

    /// <summary>
    /// Registered disk images by drive number (0x00=A:, 0x01=B:, 0x80=C: first hard disk).
    /// </summary>
    private readonly Dictionary<byte, byte[]> _diskImages = new();

    /// <summary>
    /// Disk geometry per drive: (heads, sectors per track, cylinders, bytes per sector).
    /// </summary>
    private readonly Dictionary<byte, (byte heads, byte spt, ushort cylinders, ushort bps)> _geometry = new();

    private byte _lastStatus;

    public BiosDiskService(Cpu8086 cpu, MemoryBus mem, EmulatorLog log)
    {
        _cpu = cpu;
        _mem = mem;
        _log = log;
    }

    /// <summary>
    /// Register a disk image for a given BIOS drive number.
    /// Drive 0x00 = first floppy (A:), 0x01 = second floppy (B:), 0x80 = first hard disk.
    /// </summary>
    public void RegisterDisk(byte driveNumber, byte[] imageData, byte heads, byte sectorsPerTrack, ushort cylinders, ushort bytesPerSector = 512)
    {
        _diskImages[driveNumber] = imageData;
        _geometry[driveNumber] = (heads, sectorsPerTrack, cylinders, bytesPerSector);
        _log.Info("INT13", $"Registered drive 0x{driveNumber:X2}: {imageData.Length} bytes, C={cylinders} H={heads} S={sectorsPerTrack}");
    }

    /// <summary>Auto-register a disk image with geometry guessed from size.</summary>
    public void RegisterDiskAuto(byte driveNumber, byte[] imageData)
    {
        var (heads, spt, cyls) = GuessGeometry(imageData.Length);
        RegisterDisk(driveNumber, imageData, heads, spt, cyls);
    }

    private static (byte heads, byte spt, ushort cylinders) GuessGeometry(int size)
    {
        return size switch
        {
            163840 => (1, 8, 40),    // 160K
            184320 => (1, 9, 40),    // 180K
            327680 => (2, 8, 40),    // 320K
            368640 => (2, 9, 40),    // 360K
            737280 => (2, 9, 80),    // 720K
            1228800 => (2, 15, 80),  // 1.2M
            1474560 => (2, 18, 80),  // 1.44M
            2949120 => (2, 36, 80),  // 2.88M
            _ => (2, 18, (ushort)(size / (2 * 18 * 512)))
        };
    }

    public void Handle()
    {
        byte func = _cpu.Regs.AH;
        byte drive = _cpu.Regs.DL;

        switch (func)
        {
            case 0x00: // Reset disk system
                _lastStatus = 0;
                _cpu.Regs.AH = 0;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x01: // Get disk status
                _cpu.Regs.AH = _lastStatus;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x02: // Read sectors
                ReadSectors(drive);
                break;

            case 0x03: // Write sectors
                WriteSectors(drive);
                break;

            case 0x04: // Verify sectors
                if (_diskImages.ContainsKey(drive))
                {
                    _cpu.Regs.AH = 0;
                    _cpu.Regs.Flags &= ~CpuFlags.Carry;
                }
                else
                {
                    _cpu.Regs.AH = 0x0C; // Unsupported track
                    _cpu.Regs.Flags |= CpuFlags.Carry;
                }
                break;

            case 0x08: // Get drive parameters
                GetDriveParameters(drive);
                break;

            case 0x15: // Get disk type
                if (_diskImages.ContainsKey(drive))
                {
                    _cpu.Regs.AH = (byte)(drive >= 0x80 ? 0x03 : 0x02); // Fixed / changeable disk
                    if (drive >= 0x80)
                    {
                        // Total sectors in DX:CX
                        int totalSectors = _diskImages[drive].Length / 512;
                        _cpu.Regs.CX = (ushort)(totalSectors >> 16);
                        _cpu.Regs.DX = (ushort)(totalSectors & 0xFFFF);
                    }
                    _cpu.Regs.Flags &= ~CpuFlags.Carry;
                }
                else
                {
                    _cpu.Regs.AH = 0x00; // No disk
                    _cpu.Regs.Flags &= ~CpuFlags.Carry;
                }
                break;

            case 0x16: // Detect disk change
                _cpu.Regs.AH = 0x00; // No change
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            default:
                _log.Warn("INT13", $"Unhandled INT 13h AH={func:X2}h drive={drive:X2}h");
                _cpu.Regs.AH = 0x01; // Invalid command
                _cpu.Regs.Flags |= CpuFlags.Carry;
                break;
        }
    }

    private void ReadSectors(byte drive)
    {
        if (!_diskImages.TryGetValue(drive, out var image) || !_geometry.TryGetValue(drive, out var geo))
        {
            _log.Warn("INT13", $"Read: drive 0x{drive:X2} not registered");
            _cpu.Regs.AH = 0x80; // Timeout / not ready
            _lastStatus = 0x80;
            _cpu.Regs.Flags |= CpuFlags.Carry;
            return;
        }

        byte sectors = _cpu.Regs.AL;
        ushort cx = _cpu.Regs.CX;
        int cylinder = ((cx >> 8) & 0xFF) | ((cx & 0xC0) << 2);
        int sector = cx & 0x3F;
        int head = _cpu.Regs.DH;
        ushort bufSeg = _cpu.Regs.ES;
        ushort bufOff = _cpu.Regs.BX;

        // CHS to LBA: LBA = (C × H_per_cyl + H) × S_per_track + (S − 1)
        long lba = ((long)cylinder * geo.heads + head) * geo.spt + (sector - 1);
        long byteOffset = lba * geo.bps;

        _log.Debug("INT13", $"Read {sectors} sector(s): C={cylinder} H={head} S={sector} → LBA={lba} → offset=0x{byteOffset:X}");

        int bytesRead = 0;
        for (int i = 0; i < sectors; i++)
        {
            long off = byteOffset + (long)i * geo.bps;
            if (off < 0 || off + geo.bps > image.Length)
            {
                _cpu.Regs.AH = 0x04; // Sector not found
                _cpu.Regs.AL = (byte)i; // Sectors actually read
                _lastStatus = 0x04;
                _cpu.Regs.Flags |= CpuFlags.Carry;
                return;
            }

            for (int b = 0; b < geo.bps; b++)
            {
                _mem.WriteByte(bufSeg, (ushort)(bufOff + bytesRead), image[off + b]);
                bytesRead++;
            }
        }

        _cpu.Regs.AH = 0; // Success
        _cpu.Regs.AL = sectors; // Sectors actually read
        _lastStatus = 0;
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void WriteSectors(byte drive)
    {
        if (!_diskImages.TryGetValue(drive, out var image) || !_geometry.TryGetValue(drive, out var geo))
        {
            _cpu.Regs.AH = 0x80;
            _lastStatus = 0x80;
            _cpu.Regs.Flags |= CpuFlags.Carry;
            return;
        }

        byte sectors = _cpu.Regs.AL;
        ushort cx = _cpu.Regs.CX;
        int cylinder = ((cx >> 8) & 0xFF) | ((cx & 0xC0) << 2);
        int sector = cx & 0x3F;
        int head = _cpu.Regs.DH;
        ushort bufSeg = _cpu.Regs.ES;
        ushort bufOff = _cpu.Regs.BX;

        long lba = ((long)cylinder * geo.heads + head) * geo.spt + (sector - 1);
        long byteOffset = lba * geo.bps;

        _log.Debug("INT13", $"Write {sectors} sector(s): C={cylinder} H={head} S={sector} → LBA={lba}");

        int bytesWritten = 0;
        for (int i = 0; i < sectors; i++)
        {
            long off = byteOffset + (long)i * geo.bps;
            if (off < 0 || off + geo.bps > image.Length)
            {
                _cpu.Regs.AH = 0x04;
                _cpu.Regs.AL = (byte)i;
                _lastStatus = 0x04;
                _cpu.Regs.Flags |= CpuFlags.Carry;
                return;
            }

            for (int b = 0; b < geo.bps; b++)
            {
                image[off + b] = _mem.ReadByte(bufSeg, (ushort)(bufOff + bytesWritten));
                bytesWritten++;
            }
        }

        _cpu.Regs.AH = 0;
        _cpu.Regs.AL = sectors;
        _lastStatus = 0;
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void GetDriveParameters(byte drive)
    {
        if (!_geometry.TryGetValue(drive, out var geo))
        {
            _cpu.Regs.AH = 0x07; // Drive parameter activity failed
            _cpu.Regs.Flags |= CpuFlags.Carry;
            return;
        }

        _cpu.Regs.AH = 0;
        _cpu.Regs.BL = (byte)(drive < 0x80 ? 0x04 : 0x00); // Drive type (0x04 = 1.44M floppy)
        _cpu.Regs.CH = (byte)((geo.cylinders - 1) & 0xFF);
        _cpu.Regs.CL = (byte)(((( geo.cylinders - 1) >> 2) & 0xC0) | (geo.spt & 0x3F));
        _cpu.Regs.DH = (byte)(geo.heads - 1);
        _cpu.Regs.DL = (byte)(drive < 0x80 ? 1 : 1); // Number of drives
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }
}
