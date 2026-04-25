using MsDos.Core.Interrupts;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Hardware;

/// <summary>
/// NEC µPD765 / Intel i8272A Floppy Disk Controller emulation.
/// Implements the command protocol used by the IBM PC BIOS and DOS
/// for floppy disk I/O.
///
/// Port mapping:
///   0x3F2 : Digital Output Register (DOR)
///   0x3F4 : Main Status Register (MSR) — read only
///   0x3F5 : Data Register — read/write (command/result/data FIFO)
///   0x3F7 : Digital Input Register (DIR) — read / Configuration Control — write
///
/// Command flow:
///   1. Host writes command bytes to port 0x3F5 (command phase)
///   2. Controller executes the command (execution phase, using DMA for data)
///   3. Host reads result bytes from port 0x3F5 (result phase)
///   4. Controller raises IRQ 6 when command completes
/// </summary>
public sealed class Fdc765Controller
{
    private readonly EmulatorLog? _log;
    private readonly DmaController _dma;
    private readonly MemoryBus _memory;
    private readonly PicController _pic;

    // Disk images for drives 0-3
    private readonly byte[]?[] _diskImages = new byte[4][];
    private readonly DiskGeometry[] _geometry = new DiskGeometry[4];
    private readonly bool[] _diskChanged = new bool[4];

    // Controller state
    private FdcPhase _phase = FdcPhase.Command;
    private readonly byte[] _commandBuffer = new byte[16];
    private int _commandLength;
    private int _commandIndex;
    private readonly byte[] _resultBuffer = new byte[16];
    private int _resultLength;
    private int _resultIndex;

    // DOR (Digital Output Register)
    private byte _dor;
    private int _selectedDrive;
    private bool _motorOn;
    private bool _dmaEnabled = true;
    private bool _controllerEnabled = true;

    // Status registers
    private byte _st0, _st1, _st2;
    private byte _currentCylinder;
    private byte _currentHead;
    private byte _currentSector;

    // Interrupt pending
    private bool _interruptPending;
    private byte _interruptSt0;

    // Specify parameters
    private byte _stepRateTime = 0x0D;
    private byte _headUnloadTime = 0x0F;
    private byte _headLoadTime = 0x02;
    private bool _nonDma;

    // Per-drive cylinder positions
    private readonly byte[] _driveCylinder = new byte[4];

    /// <summary>Fired when drive activity changes (for LED tracking).</summary>
    public event Action<int, bool>? OnDriveActivity;

    public Fdc765Controller(DmaController dma, MemoryBus memory, PicController pic, EmulatorLog? log = null)
    {
        _dma = dma;
        _memory = memory;
        _pic = pic;
        _log = log;

        for (int i = 0; i < 4; i++)
            _geometry[i] = DiskGeometry.None;
    }

    /// <summary>Load a disk image into a drive (0-3).</summary>
    public void LoadDisk(int drive, byte[] imageData)
    {
        if (drive < 0 || drive > 3) return;
        _diskImages[drive] = imageData;
        _geometry[drive] = DiskGeometry.DetectFromSize(imageData.Length);
        _diskChanged[drive] = true;
        _log?.Info("FDC", $"Disk loaded in drive {drive}: {imageData.Length} bytes, {_geometry[drive]}");
    }

    /// <summary>Eject a disk from a drive.</summary>
    public byte[]? EjectDisk(int drive)
    {
        if (drive < 0 || drive > 3) return null;
        var data = _diskImages[drive];
        _diskImages[drive] = null;
        _geometry[drive] = DiskGeometry.None;
        _diskChanged[drive] = true;
        return data;
    }

    /// <summary>Check if a drive has a disk inserted.</summary>
    public bool HasDisk(int drive) =>
        drive >= 0 && drive < 4 && _diskImages[drive] != null;

    /// <summary>
    /// Register I/O port handlers with the port bus.
    /// </summary>
    public void RegisterPorts(IOPortBus ports)
    {
        // 0x3F2 — Digital Output Register (DOR)
        ports.Register(0x3F2,
            port => _dor,
            (port, val) => WriteDor(val));

        // 0x3F4 — Main Status Register (MSR) — read only
        ports.Register(0x3F4,
            port => ReadMsr(),
            (port, val) => { }); // Writes ignored

        // 0x3F5 — Data Register (command/result FIFO)
        ports.Register(0x3F5,
            port => ReadData(),
            (port, val) => WriteData(val));

        // 0x3F7 — Digital Input Register (read) / Configuration Control (write)
        ports.Register(0x3F7,
            port => ReadDir(),
            (port, val) => { }); // CCR write ignored for now
    }

    // ═══════════════════════════════════════════════════════════════
    //  PORT HANDLERS
    // ═══════════════════════════════════════════════════════════════

    private void WriteDor(byte value)
    {
        bool wasReset = (_dor & 0x04) == 0;
        _dor = value;
        _selectedDrive = value & 0x03;
        _controllerEnabled = (value & 0x04) != 0;
        _dmaEnabled = (value & 0x08) != 0;
        _motorOn = (value & (0x10 << _selectedDrive)) != 0;

        // Rising edge of bit 2 = exit reset
        if (wasReset && _controllerEnabled)
        {
            _phase = FdcPhase.Command;
            _commandIndex = 0;

            // After reset, raise interrupt with ST0 indicating drive ready
            for (int i = 0; i < 4; i++)
            {
                _interruptSt0 = (byte)(0xC0 | i); // Polling, ready changed
                _interruptPending = true;
            }
            RaiseInterrupt();
        }

        _log?.Debug("FDC", $"DOR write: drive={_selectedDrive} motor={_motorOn} enabled={_controllerEnabled} dma={_dmaEnabled}");
    }

    private byte ReadMsr()
    {
        byte msr = 0;

        // Bits 0-3: drive busy flags
        // Bit 4: FDC busy (command in progress)
        // Bit 5: Non-DMA mode
        // Bit 6: Direction (0=host→FDC, 1=FDC→host)
        // Bit 7: Request for Master (RQM) — FDC ready for data transfer

        if (_phase == FdcPhase.Result)
        {
            msr |= 0xC0; // RQM + DIO (FDC→host)
            msr |= 0x10; // Busy
        }
        else if (_phase == FdcPhase.Command)
        {
            msr |= 0x80; // RQM (ready for command)
        }
        else if (_phase == FdcPhase.Execution)
        {
            msr |= 0x10; // Busy
            if (_nonDma)
                msr |= 0xA0; // RQM + non-DMA execution
        }

        return msr;
    }

    private byte ReadData()
    {
        if (_phase != FdcPhase.Result)
            return 0xFF;

        if (_resultIndex < _resultLength)
        {
            byte val = _resultBuffer[_resultIndex++];
            if (_resultIndex >= _resultLength)
            {
                // All result bytes read — return to command phase
                _phase = FdcPhase.Command;
                _commandIndex = 0;
            }
            return val;
        }

        return 0xFF;
    }

    private void WriteData(byte value)
    {
        if (_phase != FdcPhase.Command)
            return;

        if (_commandIndex == 0)
        {
            // First byte determines command and expected length
            _commandBuffer[0] = value;
            _commandLength = GetCommandLength(value);
            _commandIndex = 1;
        }
        else
        {
            _commandBuffer[_commandIndex++] = value;
        }

        // Execute when all command bytes received
        if (_commandIndex >= _commandLength)
        {
            ExecuteCommand();
        }
    }

    private byte ReadDir()
    {
        // Bit 7: Disk change signal
        byte dir = 0;
        if (_diskChanged[_selectedDrive])
        {
            dir |= 0x80;
            _diskChanged[_selectedDrive] = false;
        }
        return dir;
    }

    // ═══════════════════════════════════════════════════════════════
    //  COMMAND EXECUTION
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteCommand()
    {
        byte cmd = (byte)(_commandBuffer[0] & 0x1F); // Mask MT/MF/SK bits

        switch (cmd)
        {
            case 0x02: // Read Track
                ExecuteReadData();
                break;
            case 0x03: // Specify
                ExecuteSpecify();
                break;
            case 0x04: // Sense Drive Status
                ExecuteSenseDriveStatus();
                break;
            case 0x05: // Write Data
                ExecuteWriteData();
                break;
            case 0x06: // Read Data
                ExecuteReadData();
                break;
            case 0x07: // Recalibrate
                ExecuteRecalibrate();
                break;
            case 0x08: // Sense Interrupt Status
                ExecuteSenseInterruptStatus();
                break;
            case 0x09: // Write Deleted Data
                ExecuteWriteData(); // Treat same as write
                break;
            case 0x0A: // Read ID
                ExecuteReadId();
                break;
            case 0x0C: // Read Deleted Data
                ExecuteReadData(); // Treat same as read
                break;
            case 0x0D: // Format Track
                ExecuteFormatTrack();
                break;
            case 0x0F: // Seek
                ExecuteSeek();
                break;
            case 0x11: // Scan Equal (perpendicular mode in later controllers)
            case 0x19: // Scan Low or Equal
            case 0x1D: // Scan High or Equal
                ExecuteScanStub();
                break;
            default:
                _log?.Warn("FDC", $"Unknown command: {cmd:X2}h");
                // Return to command phase with invalid command
                SetResultInvalidCommand();
                break;
        }
    }

    private void ExecuteSpecify()
    {
        _stepRateTime = (byte)((_commandBuffer[1] >> 4) & 0x0F);
        _headUnloadTime = (byte)(_commandBuffer[1] & 0x0F);
        _headLoadTime = (byte)((_commandBuffer[2] >> 1) & 0x7F);
        _nonDma = (_commandBuffer[2] & 0x01) != 0;

        // No result phase for Specify
        _phase = FdcPhase.Command;
        _commandIndex = 0;
        _log?.Debug("FDC", $"SPECIFY: SRT={_stepRateTime} HUT={_headUnloadTime} HLT={_headLoadTime} ND={_nonDma}");
    }

    private void ExecuteSenseDriveStatus()
    {
        int drive = _commandBuffer[1] & 0x03;
        int head = (_commandBuffer[1] >> 2) & 0x01;

        byte st3 = (byte)(drive & 0x03);
        if (head != 0) st3 |= 0x04; // Head address
        if (_diskImages[drive] != null) st3 |= 0x20; // Ready
        if (_driveCylinder[drive] == 0) st3 |= 0x10; // Track 0
        st3 |= 0x08; // Two-sided

        _resultBuffer[0] = st3;
        _resultLength = 1;
        _resultIndex = 0;
        _phase = FdcPhase.Result;
    }

    private void ExecuteRecalibrate()
    {
        int drive = _commandBuffer[1] & 0x03;
        _driveCylinder[drive] = 0;

        OnDriveActivity?.Invoke(drive, true);

        // Raise interrupt when seek complete
        _interruptSt0 = (byte)(0x20 | drive); // Seek end
        _interruptPending = true;
        RaiseInterrupt();

        _phase = FdcPhase.Command;
        _commandIndex = 0;

        OnDriveActivity?.Invoke(drive, false);
        _log?.Debug("FDC", $"RECALIBRATE drive {drive}");
    }

    private void ExecuteSeek()
    {
        int drive = _commandBuffer[1] & 0x03;
        byte cylinder = _commandBuffer[2];
        _driveCylinder[drive] = cylinder;

        OnDriveActivity?.Invoke(drive, true);

        // Raise interrupt when seek complete
        _interruptSt0 = (byte)(0x20 | drive); // Seek end
        _interruptPending = true;
        RaiseInterrupt();

        _phase = FdcPhase.Command;
        _commandIndex = 0;

        OnDriveActivity?.Invoke(drive, false);
        _log?.Debug("FDC", $"SEEK drive {drive} to cylinder {cylinder}");
    }

    private void ExecuteSenseInterruptStatus()
    {
        if (_interruptPending)
        {
            _resultBuffer[0] = _interruptSt0;
            _resultBuffer[1] = _driveCylinder[_interruptSt0 & 0x03];
            _interruptPending = false;
        }
        else
        {
            _resultBuffer[0] = 0x80; // Invalid command (no interrupt pending)
            _resultBuffer[1] = 0;
        }
        _resultLength = 2;
        _resultIndex = 0;
        _phase = FdcPhase.Result;
    }

    private void ExecuteReadData()
    {
        bool mt = (_commandBuffer[0] & 0x80) != 0; // Multi-track
        bool mf = (_commandBuffer[0] & 0x40) != 0; // MFM mode
        bool sk = (_commandBuffer[0] & 0x20) != 0; // Skip deleted

        int drive = _commandBuffer[1] & 0x03;
        int head = (_commandBuffer[1] >> 2) & 0x01;
        byte cylinder = _commandBuffer[2];
        byte headInId = _commandBuffer[3];
        byte startSector = _commandBuffer[4];
        byte sectorSize = _commandBuffer[5]; // 2 = 512 bytes
        byte eot = _commandBuffer[6]; // End of track (last sector)
        byte gapLength = _commandBuffer[7];
        byte dataLength = _commandBuffer[8];

        int bytesPerSector = 128 << sectorSize;
        OnDriveActivity?.Invoke(drive, true);

        _log?.Debug("FDC", $"READ DATA: drive={drive} C={cylinder} H={head} S={startSector} EOT={eot} size={bytesPerSector}");

        _st0 = 0;
        _st1 = 0;
        _st2 = 0;

        if (_diskImages[drive] == null)
        {
            // No disk — set error
            _st0 = (byte)(0x40 | (head << 2) | drive); // Abnormal termination
            _st1 = 0x01; // Missing address mark
            SetReadWriteResult(drive, head, cylinder, startSector);
            OnDriveActivity?.Invoke(drive, false);
            return;
        }

        var geo = _geometry[drive];
        var disk = _diskImages[drive]!;

        // Read sectors via DMA
        var (dmaAddr, dmaCount) = _dma.GetTransferParameters(2);
        int sectorsToRead = (eot - startSector + 1);
        int totalBytes = sectorsToRead * bytesPerSector;

        int currentSector = startSector;
        int memOffset = dmaAddr;

        for (int s = 0; s < sectorsToRead; s++)
        {
            int lba = ChsToLba(cylinder, head, currentSector, geo);
            int diskOffset = lba * bytesPerSector;

            if (diskOffset + bytesPerSector > disk.Length)
            {
                _st0 = (byte)(0x40 | (head << 2) | drive);
                _st1 = 0x04; // No data
                break;
            }

            // Transfer sector data to memory via DMA address
            for (int b = 0; b < bytesPerSector; b++)
            {
                _memory.WriteByte((uint)(memOffset + b), disk[diskOffset + b]);
            }

            memOffset += bytesPerSector;
            currentSector++;
        }

        if (_st0 == 0)
        {
            _st0 = (byte)((head << 2) | drive); // Normal termination
        }

        _dma.UpdateAfterTransfer(2, totalBytes);
        _dma.SignalTransferComplete(2);

        SetReadWriteResult(drive, head, cylinder, (byte)(currentSector));
        RaiseInterrupt();
        OnDriveActivity?.Invoke(drive, false);
    }

    private void ExecuteWriteData()
    {
        bool mt = (_commandBuffer[0] & 0x80) != 0;
        bool mf = (_commandBuffer[0] & 0x40) != 0;

        int drive = _commandBuffer[1] & 0x03;
        int head = (_commandBuffer[1] >> 2) & 0x01;
        byte cylinder = _commandBuffer[2];
        byte headInId = _commandBuffer[3];
        byte startSector = _commandBuffer[4];
        byte sectorSize = _commandBuffer[5];
        byte eot = _commandBuffer[6];
        byte gapLength = _commandBuffer[7];
        byte dataLength = _commandBuffer[8];

        int bytesPerSector = 128 << sectorSize;
        OnDriveActivity?.Invoke(drive, true);

        _log?.Debug("FDC", $"WRITE DATA: drive={drive} C={cylinder} H={head} S={startSector} EOT={eot}");

        _st0 = 0;
        _st1 = 0;
        _st2 = 0;

        if (_diskImages[drive] == null)
        {
            _st0 = (byte)(0x40 | (head << 2) | drive);
            _st1 = 0x01;
            SetReadWriteResult(drive, head, cylinder, startSector);
            OnDriveActivity?.Invoke(drive, false);
            return;
        }

        var geo = _geometry[drive];
        var disk = _diskImages[drive]!;

        var (dmaAddr, dmaCount) = _dma.GetTransferParameters(2);
        int sectorsToWrite = (eot - startSector + 1);
        int totalBytes = sectorsToWrite * bytesPerSector;

        int currentSector = startSector;
        int memOffset = dmaAddr;

        for (int s = 0; s < sectorsToWrite; s++)
        {
            int lba = ChsToLba(cylinder, head, currentSector, geo);
            int diskOffset = lba * bytesPerSector;

            if (diskOffset + bytesPerSector > disk.Length)
            {
                _st0 = (byte)(0x40 | (head << 2) | drive);
                _st1 = 0x04;
                break;
            }

            // Transfer data from memory to disk
            for (int b = 0; b < bytesPerSector; b++)
            {
                disk[diskOffset + b] = _memory.ReadByte((uint)(memOffset + b));
            }

            memOffset += bytesPerSector;
            currentSector++;
        }

        if (_st0 == 0)
        {
            _st0 = (byte)((head << 2) | drive);
        }

        _dma.UpdateAfterTransfer(2, totalBytes);
        _dma.SignalTransferComplete(2);

        SetReadWriteResult(drive, head, cylinder, (byte)(currentSector));
        RaiseInterrupt();
        OnDriveActivity?.Invoke(drive, false);
    }

    private void ExecuteReadId()
    {
        int drive = _commandBuffer[1] & 0x03;
        int head = (_commandBuffer[1] >> 2) & 0x01;

        if (_diskImages[drive] == null)
        {
            _st0 = (byte)(0x40 | (head << 2) | drive);
            _st1 = 0x01;
            _st2 = 0;
            _resultBuffer[0] = _st0;
            _resultBuffer[1] = _st1;
            _resultBuffer[2] = _st2;
            _resultBuffer[3] = _driveCylinder[drive];
            _resultBuffer[4] = (byte)head;
            _resultBuffer[5] = 1; // Sector
            _resultBuffer[6] = 2; // Sector size (512)
        }
        else
        {
            _st0 = (byte)((head << 2) | drive);
            _resultBuffer[0] = _st0;
            _resultBuffer[1] = 0;
            _resultBuffer[2] = 0;
            _resultBuffer[3] = _driveCylinder[drive];
            _resultBuffer[4] = (byte)head;
            _resultBuffer[5] = 1; // First sector
            _resultBuffer[6] = 2; // 512 bytes
        }

        _resultLength = 7;
        _resultIndex = 0;
        _phase = FdcPhase.Result;
        RaiseInterrupt();
    }

    private void ExecuteFormatTrack()
    {
        int drive = _commandBuffer[1] & 0x03;
        int head = (_commandBuffer[1] >> 2) & 0x01;
        byte sectorSize = _commandBuffer[2];
        byte sectorsPerTrack = _commandBuffer[3];
        byte gapLength = _commandBuffer[4];
        byte fillByte = _commandBuffer[5];

        int bytesPerSector = 128 << sectorSize;
        OnDriveActivity?.Invoke(drive, true);

        _log?.Debug("FDC", $"FORMAT TRACK: drive={drive} H={head} sectors={sectorsPerTrack} fill=0x{fillByte:X2}");

        _st0 = 0;
        _st1 = 0;
        _st2 = 0;

        if (_diskImages[drive] == null)
        {
            _st0 = (byte)(0x40 | (head << 2) | drive);
            _st1 = 0x01;
        }
        else
        {
            var geo = _geometry[drive];
            var disk = _diskImages[drive]!;

            // Read 4 bytes per sector from DMA (C, H, R, N for each sector)
            var (dmaAddr, _) = _dma.GetTransferParameters(2);

            for (int s = 0; s < sectorsPerTrack; s++)
            {
                byte c = _memory.ReadByte((uint)(dmaAddr + s * 4));
                byte h = _memory.ReadByte((uint)(dmaAddr + s * 4 + 1));
                byte r = _memory.ReadByte((uint)(dmaAddr + s * 4 + 2));
                byte n = _memory.ReadByte((uint)(dmaAddr + s * 4 + 3));

                int lba = ChsToLba(c, h, r, geo);
                int offset = lba * bytesPerSector;
                if (offset + bytesPerSector <= disk.Length)
                {
                    Array.Fill(disk, fillByte, offset, bytesPerSector);
                }
            }

            _st0 = (byte)((head << 2) | drive);
        }

        SetReadWriteResult(drive, head, _driveCylinder[drive], 1);
        _dma.SignalTransferComplete(2);
        RaiseInterrupt();
        OnDriveActivity?.Invoke(drive, false);
    }

    private void ExecuteScanStub()
    {
        // Scan commands are rarely used — stub with normal termination
        int drive = _commandBuffer[1] & 0x03;
        int head = (_commandBuffer[1] >> 2) & 0x01;
        _st0 = (byte)((head << 2) | drive);
        _st1 = 0;
        _st2 = 0x08; // Scan equal hit
        SetReadWriteResult(drive, head, _commandBuffer[2], _commandBuffer[4]);
        RaiseInterrupt();
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════

    private void SetReadWriteResult(int drive, int head, byte cylinder, byte sector)
    {
        _resultBuffer[0] = _st0;
        _resultBuffer[1] = _st1;
        _resultBuffer[2] = _st2;
        _resultBuffer[3] = cylinder;
        _resultBuffer[4] = (byte)head;
        _resultBuffer[5] = sector;
        _resultBuffer[6] = 2; // 512 bytes/sector
        _resultLength = 7;
        _resultIndex = 0;
        _phase = FdcPhase.Result;
    }

    private void SetResultInvalidCommand()
    {
        _resultBuffer[0] = 0x80; // Invalid command
        _resultLength = 1;
        _resultIndex = 0;
        _phase = FdcPhase.Result;
    }

    private void RaiseInterrupt()
    {
        if (_dmaEnabled && _controllerEnabled)
            _pic.RaiseIRQ(6); // IRQ 6 = floppy
    }

    private static int ChsToLba(int cylinder, int head, int sector, DiskGeometry geo)
    {
        if (geo.SectorsPerTrack == 0) return 0;
        return (cylinder * geo.Heads + head) * geo.SectorsPerTrack + (sector - 1);
    }

    private static int GetCommandLength(byte firstByte)
    {
        byte cmd = (byte)(firstByte & 0x1F);
        return cmd switch
        {
            0x02 => 9,  // Read Track
            0x03 => 3,  // Specify
            0x04 => 2,  // Sense Drive Status
            0x05 => 9,  // Write Data
            0x06 => 9,  // Read Data
            0x07 => 2,  // Recalibrate
            0x08 => 1,  // Sense Interrupt Status
            0x09 => 9,  // Write Deleted Data
            0x0A => 2,  // Read ID
            0x0C => 9,  // Read Deleted Data
            0x0D => 6,  // Format Track
            0x0F => 3,  // Seek
            0x11 => 9,  // Scan Equal
            0x19 => 9,  // Scan Low or Equal
            0x1D => 9,  // Scan High or Equal
            _ => 1      // Unknown — single byte
        };
    }

    private enum FdcPhase
    {
        Command,
        Execution,
        Result
    }
}

/// <summary>
/// Floppy disk geometry (CHS parameters).
/// </summary>
public readonly struct DiskGeometry
{
    public int Cylinders { get; init; }
    public int Heads { get; init; }
    public int SectorsPerTrack { get; init; }
    public int BytesPerSector { get; init; }

    public static readonly DiskGeometry None = new();

    /// <summary>Total size in bytes.</summary>
    public int TotalBytes => Cylinders * Heads * SectorsPerTrack * BytesPerSector;

    /// <summary>Detect geometry from disk image size.</summary>
    public static DiskGeometry DetectFromSize(int imageSize)
    {
        return imageSize switch
        {
            163840 => new DiskGeometry { Cylinders = 40, Heads = 1, SectorsPerTrack = 8, BytesPerSector = 512 },   // 160K
            184320 => new DiskGeometry { Cylinders = 40, Heads = 1, SectorsPerTrack = 9, BytesPerSector = 512 },   // 180K
            327680 => new DiskGeometry { Cylinders = 40, Heads = 2, SectorsPerTrack = 8, BytesPerSector = 512 },   // 320K
            368640 => new DiskGeometry { Cylinders = 40, Heads = 2, SectorsPerTrack = 9, BytesPerSector = 512 },   // 360K
            737280 => new DiskGeometry { Cylinders = 80, Heads = 2, SectorsPerTrack = 9, BytesPerSector = 512 },   // 720K
            1228800 => new DiskGeometry { Cylinders = 80, Heads = 2, SectorsPerTrack = 15, BytesPerSector = 512 }, // 1.2M
            1474560 => new DiskGeometry { Cylinders = 80, Heads = 2, SectorsPerTrack = 18, BytesPerSector = 512 }, // 1.44M
            2949120 => new DiskGeometry { Cylinders = 80, Heads = 2, SectorsPerTrack = 36, BytesPerSector = 512 }, // 2.88M
            _ => GuessGeometry(imageSize)
        };
    }

    private static DiskGeometry GuessGeometry(int imageSize)
    {
        // Try common sector sizes
        if (imageSize % 512 != 0)
            return None;

        int totalSectors = imageSize / 512;

        // Try to find reasonable CHS values
        foreach (int spt in new[] { 18, 15, 9, 8 })
        {
            if (totalSectors % spt != 0) continue;
            int cylHeads = totalSectors / spt;
            foreach (int heads in new[] { 2, 1 })
            {
                if (cylHeads % heads != 0) continue;
                int cyls = cylHeads / heads;
                if (cyls <= 255)
                    return new DiskGeometry { Cylinders = cyls, Heads = heads, SectorsPerTrack = spt, BytesPerSector = 512 };
            }
        }

        return None;
    }

    public override string ToString() =>
        $"C={Cylinders} H={Heads} S={SectorsPerTrack} ({TotalBytes / 1024}K)";
}
