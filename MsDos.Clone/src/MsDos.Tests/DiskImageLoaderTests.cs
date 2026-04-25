using MsDos.Core.Dos;
using MsDos.Core.Platform;

namespace MsDos.Tests;

public class DiskImageLoaderTests
{
    /// <summary>Build a minimal FAT12 1.44MB floppy disk image with a boot sector and root directory.</summary>
    private static byte[] CreateMinimalFat12Image()
    {
        // 1.44MB floppy: 2880 sectors × 512 bytes = 1,474,560 bytes
        var image = new byte[1474560];

        // Jump instruction at offset 0
        image[0] = 0xEB; // JMP SHORT
        image[1] = 0x3C; // offset
        image[2] = 0x90; // NOP

        // OEM name (offset 3-10)
        var oem = System.Text.Encoding.ASCII.GetBytes("MSDOS5.0");
        Array.Copy(oem, 0, image, 3, 8);

        // BPB (BIOS Parameter Block)
        // Bytes per sector: 512
        image[0x0B] = 0x00; image[0x0C] = 0x02;
        // Sectors per cluster: 1
        image[0x0D] = 0x01;
        // Reserved sectors: 1
        image[0x0E] = 0x01; image[0x0F] = 0x00;
        // Number of FATs: 2
        image[0x10] = 0x02;
        // Root directory entries: 224
        image[0x11] = 0xE0; image[0x12] = 0x00;
        // Total sectors: 2880
        image[0x13] = 0x40; image[0x14] = 0x0B;
        // Media descriptor: 0xF0 (3.5" 1.44MB)
        image[0x15] = 0xF0;
        // Sectors per FAT: 9
        image[0x16] = 0x09; image[0x17] = 0x00;
        // Sectors per track: 18
        image[0x18] = 0x12; image[0x19] = 0x00;
        // Number of heads: 2
        image[0x1A] = 0x02; image[0x1B] = 0x00;
        // Hidden sectors: 0
        image[0x1C] = 0x00; image[0x1D] = 0x00;

        // Boot sector signature
        image[510] = 0x55;
        image[511] = 0xAA;

        // FAT1 starts at sector 1 (offset 512)
        int fatOffset = 512;
        image[fatOffset] = 0xF0; // Media byte
        image[fatOffset + 1] = 0xFF;
        image[fatOffset + 2] = 0xFF;
        // First file occupies cluster 2 (FAT entries: cluster 2 = end of chain)
        // FAT12: cluster 2 entry at byte offset 3
        image[fatOffset + 3] = 0xFF;
        image[fatOffset + 4] = 0x0F;

        // Root directory starts at sector 1 + 2*9 = 19 (offset 19 * 512 = 9728)
        int rootDirOffset = (1 + 2 * 9) * 512;

        // Add a volume label entry
        var volLabel = System.Text.Encoding.ASCII.GetBytes("TESTDISK   ");
        Array.Copy(volLabel, 0, image, rootDirOffset, 11);
        image[rootDirOffset + 11] = 0x08; // Volume label attribute

        // Add a file entry: HELLO.TXT
        int fileEntry = rootDirOffset + 32;
        var fileName = System.Text.Encoding.ASCII.GetBytes("HELLO   TXT");
        Array.Copy(fileName, 0, image, fileEntry, 11);
        image[fileEntry + 11] = 0x20; // Archive attribute
        // Start cluster: 2
        image[fileEntry + 26] = 0x02;
        image[fileEntry + 27] = 0x00;
        // File size: 13 bytes
        image[fileEntry + 28] = 0x0D;
        image[fileEntry + 29] = 0x00;
        image[fileEntry + 30] = 0x00;
        image[fileEntry + 31] = 0x00;

        // Add another file entry: README.COM
        int file2Entry = rootDirOffset + 64;
        var file2Name = System.Text.Encoding.ASCII.GetBytes("README  COM");
        Array.Copy(file2Name, 0, image, file2Entry, 11);
        image[file2Entry + 11] = 0x20; // Archive attribute
        image[file2Entry + 26] = 0x03; // Start cluster 3
        image[file2Entry + 27] = 0x00;
        image[file2Entry + 28] = 0x05; // 5 bytes
        image[file2Entry + 29] = 0x00;
        image[file2Entry + 30] = 0x00;
        image[file2Entry + 31] = 0x00;

        // Write file data for HELLO.TXT at cluster 2
        // Cluster 2 starts at first data sector
        int rootDirSectors = (224 * 32 + 511) / 512; // = 14
        int firstDataSector = 1 + (2 * 9) + rootDirSectors; // = 33
        int cluster2Offset = firstDataSector * 512;
        var helloData = System.Text.Encoding.ASCII.GetBytes("Hello, World!");
        Array.Copy(helloData, 0, image, cluster2Offset, helloData.Length);

        return image;
    }

    [Fact]
    public void Load_ValidFat12Image_ParsesBpb()
    {
        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);
        var image = CreateMinimalFat12Image();

        bool loaded = loader.Load(image);

        Assert.True(loaded);
        Assert.Equal(FatType.Fat12, loader.DetectedFatType);
        Assert.Equal(512, loader.BytesPerSector);
        Assert.Equal(1, loader.SectorsPerCluster);
        Assert.Equal(2, loader.NumberOfFats);
        Assert.Equal(224, loader.RootEntryCount);
        Assert.Equal(2880, loader.TotalSectors);
        Assert.Equal(0xF0, loader.MediaDescriptor);
        Assert.Equal(9, loader.SectorsPerFat);
    }

    [Fact]
    public void Load_TooSmallData_ReturnsFalse()
    {
        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);

        Assert.False(loader.Load(new byte[100]));
        Assert.False(loader.IsLoaded);
    }

    [Fact]
    public void ListRootDirectory_ReturnsFileEntries()
    {
        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);
        loader.Load(CreateMinimalFat12Image());

        var entries = loader.ListRootDirectory();

        // Should have volume label + 2 files
        Assert.Equal(3, entries.Count);

        var volLabel = entries.FirstOrDefault(e => e.IsVolumeLabel);
        Assert.NotNull(volLabel.Name);
        Assert.Equal("TESTDISK", volLabel.Name.TrimEnd());

        var hello = entries.FirstOrDefault(e => e.Name == "HELLO.TXT");
        Assert.Equal(13u, hello.Size);
        Assert.False(hello.IsDirectory);
        Assert.Equal(2, hello.StartCluster);

        var readme = entries.FirstOrDefault(e => e.Name == "README.COM");
        Assert.Equal(5u, readme.Size);
    }

    [Fact]
    public void ReadFile_ReturnsFileContent()
    {
        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);
        loader.Load(CreateMinimalFat12Image());

        var entries = loader.ListRootDirectory();
        var hello = entries.First(e => e.Name == "HELLO.TXT");

        byte[]? data = loader.ReadFile(hello);

        Assert.NotNull(data);
        Assert.Equal(13, data!.Length);
        Assert.Equal("Hello, World!", System.Text.Encoding.ASCII.GetString(data));
    }

    [Fact]
    public void GetBootSector_Returns512Bytes()
    {
        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);
        loader.Load(CreateMinimalFat12Image());

        var boot = loader.GetBootSector();

        Assert.NotNull(boot);
        Assert.Equal(512, boot!.Length);
        Assert.Equal(0xEB, boot[0]); // JMP SHORT
        Assert.Equal(0x55, boot[510]); // Boot signature
        Assert.Equal(0xAA, boot[511]);
    }

    [Fact]
    public void Load_KnownFloppySize_GuessesGeometry()
    {
        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);

        // Create a 360K image with zeroed BPB (no valid BPB fields)
        var image = new byte[368640]; // 360K

        bool loaded = loader.Load(image);

        Assert.True(loaded);
        Assert.Equal(512, loader.BytesPerSector);
        Assert.Equal(720, loader.TotalSectors);
    }
}
