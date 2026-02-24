namespace MsDos.Core.Dos;

/// <summary>
/// Creates a FAT12 disk image from in-memory file data.
/// Used to export the C: drive contents as an IMG file.
/// Can also create blank formatted disk images.
/// </summary>
public sealed class DiskImageWriter
{
    /// <summary>
    /// Create a blank FAT12 formatted floppy disk image (1.44MB).
    /// </summary>
    public static byte[] CreateBlankFloppy144()
    {
        return CreateBlankImage(2880, 1, 2, 224, 9, 0xF0, 18, 2);
    }

    /// <summary>
    /// Create a blank FAT12 formatted floppy disk image (360K).
    /// </summary>
    public static byte[] CreateBlankFloppy360()
    {
        return CreateBlankImage(720, 2, 2, 112, 2, 0xFD, 9, 2);
    }

    /// <summary>
    /// Create a blank FAT12 formatted disk image of the specified size.
    /// Supports common floppy sizes: 360K, 720K, 1.2MB, 1.44MB.
    /// </summary>
    /// <param name="imageSize">Total image size in bytes.</param>
    /// <param name="volumeLabel">Volume label (max 11 chars).</param>
    public static byte[] CreateBlankFat12Image(int imageSize, string volumeLabel = "MSDOSCLONE")
    {
        return imageSize switch
        {
            368640 => CreateBlankImage(720, 2, 2, 112, 2, 0xFD, 9, 2, volumeLabel),   // 360K
            737280 => CreateBlankImage(1440, 2, 2, 112, 3, 0xF9, 9, 2, volumeLabel),  // 720K
            1228800 => CreateBlankImage(2400, 1, 2, 224, 7, 0xF9, 15, 2, volumeLabel), // 1.2MB
            _ => CreateBlankImage(2880, 1, 2, 224, 9, 0xF0, 18, 2, volumeLabel),       // 1.44MB
        };
    }

    /// <summary>
    /// Create a blank formatted disk image with the given BPB parameters.
    /// </summary>
    public static byte[] CreateBlankImage(
        int totalSectors, int sectorsPerCluster, int numberOfFats,
        int rootEntryCount, int sectorsPerFat, byte mediaDescriptor,
        int sectorsPerTrack, int numberOfHeads, string volumeLabel = "MSDOSCLONE")
    {
        const int bytesPerSector = 512;
        byte[] image = new byte[totalSectors * bytesPerSector];

        // --- Boot sector ---
        image[0] = 0xEB; image[1] = 0x3C; image[2] = 0x90; // JMP short + NOP
        // OEM name
        byte[] oem = System.Text.Encoding.ASCII.GetBytes("MSDOS4.0");
        Array.Copy(oem, 0, image, 3, Math.Min(oem.Length, 8));

        // BPB
        WriteUInt16(image, 0x0B, (ushort)bytesPerSector);
        image[0x0D] = (byte)sectorsPerCluster;
        WriteUInt16(image, 0x0E, 1); // Reserved sectors
        image[0x10] = (byte)numberOfFats;
        WriteUInt16(image, 0x11, (ushort)rootEntryCount);
        WriteUInt16(image, 0x13, (ushort)(totalSectors <= 0xFFFF ? totalSectors : 0));
        image[0x15] = mediaDescriptor;
        WriteUInt16(image, 0x16, (ushort)sectorsPerFat);
        WriteUInt16(image, 0x18, (ushort)sectorsPerTrack);
        WriteUInt16(image, 0x1A, (ushort)numberOfHeads);

        // Boot signature
        image[0x1FE] = 0x55;
        image[0x1FF] = 0xAA;

        // --- Initialize FAT(s) ---
        int fatOffset = 1 * bytesPerSector; // After reserved sector
        for (int f = 0; f < numberOfFats; f++)
        {
            int off = fatOffset + f * sectorsPerFat * bytesPerSector;
            image[off] = mediaDescriptor;
            image[off + 1] = 0xFF;
            image[off + 2] = 0xFF;
        }

        // --- Root directory - write volume label ---
        int rootDirOffset = (1 + numberOfFats * sectorsPerFat) * bytesPerSector;
        byte[] label = System.Text.Encoding.ASCII.GetBytes(volumeLabel.PadRight(11)[..11]);
        Array.Copy(label, 0, image, rootDirOffset, 11);
        image[rootDirOffset + 11] = 0x08; // Volume label attribute

        return image;
    }

    /// <summary>
    /// Write files into a disk image. The image must already be formatted.
    /// Returns the modified image with files written to the root directory and data area.
    /// </summary>
    public static byte[] WriteFiles(byte[] image, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        byte[] result = new byte[image.Length];
        Array.Copy(image, result, image.Length);

        // Parse BPB
        int bytesPerSector = ReadUInt16(result, 0x0B);
        int sectorsPerCluster = result[0x0D];
        int reservedSectors = ReadUInt16(result, 0x0E);
        int numberOfFats = result[0x10];
        int rootEntryCount = ReadUInt16(result, 0x11);
        int sectorsPerFat = ReadUInt16(result, 0x16);

        int rootDirOffset = (reservedSectors + numberOfFats * sectorsPerFat) * bytesPerSector;
        int rootDirSectors = ((rootEntryCount * 32) + (bytesPerSector - 1)) / bytesPerSector;
        int firstDataSector = reservedSectors + (numberOfFats * sectorsPerFat) + rootDirSectors;
        int clusterSize = sectorsPerCluster * bytesPerSector;
        int fatOffset = reservedSectors * bytesPerSector;

        // Find first free directory entry (skip volume label)
        int dirEntryIndex = 0;
        for (int i = 0; i < rootEntryCount; i++)
        {
            int eo = rootDirOffset + i * 32;
            byte first = result[eo];
            if (first == 0x00 || first == 0xE5)
            {
                dirEntryIndex = i;
                break;
            }
            dirEntryIndex = i + 1;
        }

        // Find first free cluster
        int nextFreeCluster = 2;
        int totalDataSectors = ReadUInt16(result, 0x13) - firstDataSector;
        if (totalDataSectors <= 0) totalDataSectors = result.Length / bytesPerSector - firstDataSector;
        int totalClusters = totalDataSectors / sectorsPerCluster + 2;

        foreach (var (name, data) in files)
        {
            if (dirEntryIndex >= rootEntryCount) break;

            // Parse 8.3 name
            string baseName, ext;
            int dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                baseName = name[..dot].ToUpperInvariant();
                ext = name[(dot + 1)..].ToUpperInvariant();
            }
            else
            {
                baseName = name.ToUpperInvariant();
                ext = "";
            }
            if (baseName.Length > 8) baseName = baseName[..8];
            if (ext.Length > 3) ext = ext[..3];

            // Write directory entry
            int eo = rootDirOffset + dirEntryIndex * 32;
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(baseName.PadRight(8));
            byte[] extBytes = System.Text.Encoding.ASCII.GetBytes(ext.PadRight(3));
            Array.Copy(nameBytes, 0, result, eo, 8);
            Array.Copy(extBytes, 0, result, eo + 8, 3);
            result[eo + 11] = 0x20; // Archive attribute

            // File size
            WriteUInt32(result, eo + 28, (uint)data.Length);

            if (data.Length > 0)
            {
                // Start cluster
                ushort startCluster = (ushort)nextFreeCluster;
                WriteUInt16(result, eo + 26, startCluster);

                // Write data clusters and FAT chain
                int written = 0;
                int cluster = nextFreeCluster;
                while (written < data.Length && cluster < totalClusters)
                {
                    int dataOffset = (firstDataSector + (cluster - 2) * sectorsPerCluster) * bytesPerSector;
                    int toWrite = Math.Min(clusterSize, data.Length - written);
                    if (dataOffset + toWrite > result.Length) break;
                    Array.Copy(data, written, result, dataOffset, toWrite);
                    written += toWrite;

                    int prevCluster = cluster;
                    cluster++;
                    nextFreeCluster = cluster;

                    // Write FAT entry
                    if (written >= data.Length)
                    {
                        WriteFat12Entry(result, fatOffset, prevCluster, 0xFFF); // End of chain
                    }
                    else
                    {
                        WriteFat12Entry(result, fatOffset, prevCluster, cluster);
                    }
                }
            }

            dirEntryIndex++;
        }

        // Copy FAT1 to FAT2
        if (numberOfFats > 1)
        {
            int fatSize = sectorsPerFat * bytesPerSector;
            Array.Copy(result, fatOffset, result, fatOffset + fatSize, fatSize);
        }

        return result;
    }

    /// <summary>
    /// Export files from an IStreamProvider as a formatted FAT12 1.44MB disk image.
    /// </summary>
    public static async Task<byte[]> ExportDriveAsImageAsync(MsDos.Core.Platform.IStreamProvider streams, MsDos.Core.Platform.EmulatorLog log)
    {
        // List files
        var entries = await streams.ListEntriesAsync("\\");
        var files = new List<(string Name, byte[] Data)>();

        foreach (var entry in entries)
        {
            try
            {
                using var stream = await streams.OpenReadAsync(entry);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                string name = (Path.GetFileName(entry) ?? entry).ToUpperInvariant();
                files.Add((name, ms.ToArray()));
            }
            catch (Exception ex)
            {
                log.Warn("DiskWriter", $"Skipping file {entry}: {ex.Message}");
            }
        }

        byte[] blank = CreateBlankFloppy144();
        byte[] result = WriteFiles(blank, files);

        log.Info("DiskWriter", $"Exported {files.Count} files to 1.44MB disk image");
        return result;
    }

    private static void WriteFat12Entry(byte[] image, int fatOffset, int cluster, int value)
    {
        int offset = fatOffset + (cluster * 3 / 2);
        if (offset + 1 >= image.Length) return;

        if ((cluster & 1) == 0)
        {
            image[offset] = (byte)(value & 0xFF);
            image[offset + 1] = (byte)((image[offset + 1] & 0xF0) | ((value >> 8) & 0x0F));
        }
        else
        {
            image[offset] = (byte)((image[offset] & 0x0F) | ((value << 4) & 0xF0));
            image[offset + 1] = (byte)((value >> 4) & 0xFF);
        }
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));
}
