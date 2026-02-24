using MsDos.Core.Platform;

namespace MsDos.Core.Dos;

/// <summary>
/// Loads raw/IMG disk image files (floppy or hard disk) and provides
/// access to the FAT12/FAT16 file system contained within.
/// Supports 160K–1.44MB floppy images and small hard disk images.
/// </summary>
public sealed class DiskImageLoader
{
    private byte[]? _imageData;
    private readonly EmulatorLog _log;

    /// <summary>
    /// Byte offset within the image where the active partition starts.
    /// Zero for floppy / un-partitioned images.
    /// </summary>
    private int _partitionOffset;

    private const int Fat12EndOfChain = 0xFF8;
    private const int Fat12BadCluster = 0xFFF;
    private const int Fat16EndOfChain = 0xFFF8;
    private const int Fat16BadCluster = 0xFFFF;

    // BPB (BIOS Parameter Block) values parsed from the boot sector
    public int BytesPerSector { get; private set; }
    public int SectorsPerCluster { get; private set; }
    public int ReservedSectors { get; private set; }
    public int NumberOfFats { get; private set; }
    public int RootEntryCount { get; private set; }
    public int TotalSectors { get; private set; }
    public byte MediaDescriptor { get; private set; }
    public int SectorsPerFat { get; private set; }
    public int SectorsPerTrack { get; private set; }
    public int NumberOfHeads { get; private set; }
    public int HiddenSectors { get; private set; }

    /// <summary>FAT type detected from the image.</summary>
    public FatType DetectedFatType { get; private set; }

    /// <summary>Whether an image is currently loaded.</summary>
    public bool IsLoaded => _imageData != null;

    /// <summary>The raw image data.</summary>
    public ReadOnlySpan<byte> RawData => _imageData;

    /// <summary>Byte offset of the active partition (0 for un-partitioned images).</summary>
    public int PartitionOffset => _partitionOffset;

    public DiskImageLoader(EmulatorLog log)
    {
        _log = log;
    }

    /// <summary>
    /// Load a raw/IMG disk image from byte data.
    /// Parses the BPB from the boot sector.  If the image starts with an
    /// MBR partition table, the first active (or first valid) partition is
    /// located and the BPB is read from that partition's boot sector.
    /// </summary>
    /// <returns>True if the image was loaded and parsed successfully.</returns>
    public bool Load(byte[] data)
    {
        if (data == null || data.Length < 512)
        {
            _log.Error("DiskImage", "Image data is too small (minimum 512 bytes for boot sector)");
            return false;
        }

        _imageData = data;
        _partitionOffset = 0;

        // Detect MBR partition table: check for 0x55AA signature and
        // probe whether sector 0 looks like a raw BPB or an MBR.
        if (data.Length > 512 && data[510] == 0x55 && data[511] == 0xAA)
        {
            int bpbBps = ReadUInt16(data, 0x0B);
            int bpbSpc = data[0x0D];

            // If offset 0 does NOT look like a valid BPB, try MBR.
            if (bpbBps == 0 || bpbSpc == 0 || bpbBps > 4096)
            {
                int partOff = TryFindPartition(data);
                if (partOff > 0)
                {
                    _partitionOffset = partOff;
                    _log.Info("DiskImage", $"MBR detected — partition at byte offset {_partitionOffset}");
                }
            }
        }

        int bpbBase = _partitionOffset;

        // Parse BPB from the (partition) boot sector
        BytesPerSector = ReadUInt16(data, bpbBase + 0x0B);
        SectorsPerCluster = data[bpbBase + 0x0D];
        ReservedSectors = ReadUInt16(data, bpbBase + 0x0E);
        NumberOfFats = data[bpbBase + 0x10];
        RootEntryCount = ReadUInt16(data, bpbBase + 0x11);
        TotalSectors = ReadUInt16(data, bpbBase + 0x13);
        MediaDescriptor = data[bpbBase + 0x15];
        SectorsPerFat = ReadUInt16(data, bpbBase + 0x16);
        SectorsPerTrack = ReadUInt16(data, bpbBase + 0x18);
        NumberOfHeads = ReadUInt16(data, bpbBase + 0x1A);
        HiddenSectors = ReadUInt16(data, bpbBase + 0x1C);

        // If TotalSectors is 0, use the 32-bit value at offset 0x20
        if (TotalSectors == 0)
            TotalSectors = ReadInt32(data, bpbBase + 0x20);

        // Validate BPB
        if (BytesPerSector == 0 || BytesPerSector > 4096 ||
            SectorsPerCluster == 0 || NumberOfFats == 0)
        {
            // May be a non-BPB image — try to guess from size
            if (!TryGuessGeometry(data.Length))
            {
                _log.Error("DiskImage", $"Invalid BPB in boot sector. BytesPerSector={BytesPerSector}, SectorsPerCluster={SectorsPerCluster}");
                _imageData = null;
                return false;
            }
        }

        // Detect FAT type
        int rootDirSectors = ((RootEntryCount * 32) + (BytesPerSector - 1)) / BytesPerSector;
        int dataSectors = TotalSectors - (ReservedSectors + (NumberOfFats * SectorsPerFat) + rootDirSectors);
        int totalClusters = dataSectors / SectorsPerCluster;

        DetectedFatType = totalClusters < 4085 ? FatType.Fat12 :
                          totalClusters < 65525 ? FatType.Fat16 : FatType.Unknown;

        _log.Info("DiskImage", $"Loaded {data.Length} bytes: {DetectedFatType}, {BytesPerSector} bytes/sector, " +
                  $"{SectorsPerCluster} sectors/cluster, {TotalSectors} total sectors" +
                  (_partitionOffset > 0 ? $" (partition at offset {_partitionOffset})" : ""));
        return true;
    }

    /// <summary>
    /// Scan MBR partition entries and return the byte offset of the first
    /// bootable (0x80) FAT12/FAT16 partition found, or the first FAT
    /// partition if none is bootable.  Returns -1 if nothing suitable.
    /// </summary>
    private static int TryFindPartition(byte[] data)
    {
        int fallback = -1;
        for (int i = 0; i < 4; i++)
        {
            int pe = 446 + i * 16;
            byte status = data[pe];
            byte type   = data[pe + 4];
            int  lba    = (int)ReadUInt32(data, pe + 8);
            int  count  = (int)ReadUInt32(data, pe + 12);

            // Recognised FAT types: 0x01 FAT12, 0x04/0x06/0x0E FAT16
            bool isFat = type == 0x01 || type == 0x04 || type == 0x06 || type == 0x0E;

            if (!isFat || lba == 0 || count == 0)
                continue;

            int byteOff = lba * 512;
            if (byteOff + 512 > data.Length)
                continue;

            if (status == 0x80)
                return byteOff;           // Active/bootable — use immediately

            if (fallback < 0)
                fallback = byteOff;       // Remember first suitable
        }

        return fallback;
    }

    /// <summary>
    /// List files in the root directory of the disk image.
    /// </summary>
    public IReadOnlyList<DiskFileEntry> ListRootDirectory()
    {
        if (_imageData == null)
            return Array.Empty<DiskFileEntry>();

        var entries = new List<DiskFileEntry>();
        int rootDirOffset = _partitionOffset + (ReservedSectors + (NumberOfFats * SectorsPerFat)) * BytesPerSector;

        for (int i = 0; i < RootEntryCount; i++)
        {
            int entryOffset = rootDirOffset + (i * 32);
            if (entryOffset + 32 > _imageData.Length) break;

            byte firstByte = _imageData[entryOffset];
            if (firstByte == 0x00) break;    // End of directory
            if (firstByte == 0xE5) continue; // Deleted entry

            byte attr = _imageData[entryOffset + 11];
            if (attr == 0x0F) continue; // Long filename entry (skip)

            // Read 8.3 filename
            string name = System.Text.Encoding.ASCII.GetString(_imageData, entryOffset, 8).TrimEnd();
            string ext = System.Text.Encoding.ASCII.GetString(_imageData, entryOffset + 8, 3).TrimEnd();

            string fullName = string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";

            uint fileSize = ReadUInt32(_imageData, entryOffset + 28);
            ushort startCluster = ReadUInt16(_imageData, entryOffset + 26);
            ushort time = ReadUInt16(_imageData, entryOffset + 22);
            ushort date = ReadUInt16(_imageData, entryOffset + 24);

            entries.Add(new DiskFileEntry
            {
                Name = fullName,
                Attributes = (FileAttributes)attr,
                Size = fileSize,
                StartCluster = startCluster,
                DosTime = time,
                DosDate = date
            });
        }

        return entries;
    }

    /// <summary>
    /// Read a file from the disk image by following the FAT chain.
    /// </summary>
    public byte[]? ReadFile(DiskFileEntry entry)
    {
        if (_imageData == null || entry.Size == 0)
            return entry.Size == 0 ? Array.Empty<byte>() : null;

        int rootDirSectors = ((RootEntryCount * 32) + (BytesPerSector - 1)) / BytesPerSector;
        int firstDataSector = ReservedSectors + (NumberOfFats * SectorsPerFat) + rootDirSectors;
        int clusterSize = SectorsPerCluster * BytesPerSector;
        int fatOffset = _partitionOffset + ReservedSectors * BytesPerSector;

        var data = new byte[entry.Size];
        int written = 0;
        int cluster = entry.StartCluster;

        while (cluster >= 2 && written < (int)entry.Size)
        {
            int sectorOffset = firstDataSector + ((cluster - 2) * SectorsPerCluster);
            int byteOffset = _partitionOffset + sectorOffset * BytesPerSector;

            int toRead = Math.Min(clusterSize, (int)entry.Size - written);
            if (byteOffset + toRead > _imageData.Length)
            {
                _log.Error("DiskImage", $"Read beyond image bounds at cluster {cluster}");
                break;
            }

            Array.Copy(_imageData, byteOffset, data, written, toRead);
            written += toRead;

            // Follow FAT chain
            cluster = GetNextCluster(cluster, fatOffset);
            if (IsEndOfChain(cluster)) break;
        }

        return data;
    }

    /// <summary>
    /// Read the partition boot sector (512 bytes).
    /// For partitioned images this returns the partition's VBR, not the MBR.
    /// </summary>
    public byte[]? GetBootSector()
    {
        if (_imageData == null || _partitionOffset + 512 > _imageData.Length)
            return null;

        var boot = new byte[512];
        Array.Copy(_imageData, _partitionOffset, boot, 0, 512);
        return boot;
    }

    /// <summary>
    /// Read raw sectors from the disk image.
    /// </summary>
    public byte[]? ReadSectors(int startSector, int count)
    {
        if (_imageData == null) return null;

        int offset = _partitionOffset + startSector * BytesPerSector;
        int length = count * BytesPerSector;

        if (offset + length > _imageData.Length)
        {
            _log.Warn("DiskImage", $"ReadSectors beyond image: sector {startSector}, count {count}");
            length = Math.Min(length, _imageData.Length - offset);
            if (length <= 0) return null;
        }

        var data = new byte[length];
        Array.Copy(_imageData, offset, data, 0, length);
        return data;
    }

    private int GetNextCluster(int cluster, int fatOffset)
    {
        if (_imageData == null) return Fat12BadCluster;

        if (DetectedFatType == FatType.Fat12)
        {
            int offset = fatOffset + (cluster * 3 / 2);
            if (offset + 1 >= _imageData.Length) return Fat12BadCluster;

            ushort val = (ushort)(_imageData[offset] | (_imageData[offset + 1] << 8));
            return (cluster & 1) != 0 ? (val >> 4) : (val & 0xFFF);
        }
        else // FAT16
        {
            int offset = fatOffset + (cluster * 2);
            if (offset + 1 >= _imageData.Length) return Fat16BadCluster;

            return ReadUInt16(_imageData, offset);
        }
    }

    private bool IsEndOfChain(int cluster) =>
        DetectedFatType == FatType.Fat12 ? cluster >= Fat12EndOfChain : cluster >= Fat16EndOfChain;

    private bool TryGuessGeometry(int imageSize)
    {
        // Common floppy sizes
        switch (imageSize)
        {
            case 163840:  // 160K (5.25" SSDD)
                SetFloppyGeometry(512, 1, 1, 2, 64, 320, 0xFE, 1, 8, 1);
                return true;
            case 184320:  // 180K (5.25" SSDD)
                SetFloppyGeometry(512, 1, 1, 2, 64, 360, 0xFC, 2, 9, 1);
                return true;
            case 327680:  // 320K (5.25" DSDD)
                SetFloppyGeometry(512, 2, 1, 2, 112, 640, 0xFF, 1, 8, 2);
                return true;
            case 368640:  // 360K (5.25" DSDD)
                SetFloppyGeometry(512, 2, 1, 2, 112, 720, 0xFD, 2, 9, 2);
                return true;
            case 737280:  // 720K (3.5" DD)
                SetFloppyGeometry(512, 2, 1, 2, 112, 1440, 0xF9, 3, 9, 2);
                return true;
            case 1228800: // 1.2MB (5.25" HD)
                SetFloppyGeometry(512, 1, 1, 2, 224, 2400, 0xF9, 7, 15, 2);
                return true;
            case 1474560: // 1.44MB (3.5" HD)
                SetFloppyGeometry(512, 1, 1, 2, 224, 2880, 0xF0, 9, 18, 2);
                return true;
            default:
                _log.Warn("DiskImage", $"Unknown image size {imageSize} bytes, attempting BPB parse");
                return false;
        }
    }

    private void SetFloppyGeometry(int bps, int spc, int reserved, int fats, int rootEntries,
                                    int totalSectors, byte media, int spf, int spt, int heads)
    {
        BytesPerSector = bps;
        SectorsPerCluster = spc;
        ReservedSectors = reserved;
        NumberOfFats = fats;
        RootEntryCount = rootEntries;
        TotalSectors = totalSectors;
        MediaDescriptor = media;
        SectorsPerFat = spf;
        SectorsPerTrack = spt;
        NumberOfHeads = heads;
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) |
               (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static int ReadInt32(byte[] data, int offset) =>
        data[offset] | (data[offset + 1] << 8) |
        (data[offset + 2] << 16) | (data[offset + 3] << 24);
}

/// <summary>FAT file system type.</summary>
public enum FatType
{
    Unknown,
    Fat12,
    Fat16
}

/// <summary>A file entry read from a FAT directory.</summary>
public struct DiskFileEntry
{
    public string Name;
    public FileAttributes Attributes;
    public uint Size;
    public ushort StartCluster;
    public ushort DosTime;
    public ushort DosDate;

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsVolumeLabel => (Attributes & FileAttributes.VolumeLabel) != 0;
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;
}

/// <summary>DOS file attributes.</summary>
[Flags]
public enum FileAttributes : byte
{
    ReadOnly = 0x01,
    Hidden = 0x02,
    System = 0x04,
    VolumeLabel = 0x08,
    Directory = 0x10,
    Archive = 0x20
}
