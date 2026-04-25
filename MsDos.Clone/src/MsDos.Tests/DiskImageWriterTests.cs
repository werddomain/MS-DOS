using MsDos.Core.Dos;
using MsDos.Core.Platform;

namespace MsDos.Tests;

public class DiskImageWriterTests
{
    [Fact]
    public void CreateBlankFloppy144_ReturnsCorrectSize()
    {
        byte[] image = DiskImageWriter.CreateBlankFloppy144();
        Assert.Equal(1474560, image.Length); // 1.44MB
    }

    [Fact]
    public void CreateBlankFloppy144_HasValidBootSector()
    {
        byte[] image = DiskImageWriter.CreateBlankFloppy144();
        // Boot signature
        Assert.Equal(0x55, image[0x1FE]);
        Assert.Equal(0xAA, image[0x1FF]);
        // BPB: bytes per sector = 512
        Assert.Equal(512, image[0x0B] | (image[0x0C] << 8));
        // BPB: total sectors = 2880
        Assert.Equal(2880, image[0x13] | (image[0x14] << 8));
    }

    [Fact]
    public void CreateBlankFloppy360_ReturnsCorrectSize()
    {
        byte[] image = DiskImageWriter.CreateBlankFloppy360();
        Assert.Equal(368640, image.Length); // 360K
    }

    [Fact]
    public void WriteFiles_AddsFileToImage()
    {
        byte[] image = DiskImageWriter.CreateBlankFloppy144();
        byte[] fileData = System.Text.Encoding.ASCII.GetBytes("Hello World");
        var files = new List<(string Name, byte[] Data)>
        {
            ("TEST.TXT", fileData)
        };

        byte[] result = DiskImageWriter.WriteFiles(image, files);
        
        // Parse back using DiskImageLoader
        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);
        Assert.True(loader.Load(result));
        
        var entries = loader.ListRootDirectory();
        // Should have volume label + our file
        Assert.Contains(entries, e => e.Name == "TEST.TXT");
    }

    [Fact]
    public void WriteFiles_FileContentIsReadable()
    {
        byte[] image = DiskImageWriter.CreateBlankFloppy144();
        byte[] fileData = System.Text.Encoding.ASCII.GetBytes("Hello FAT12");
        var files = new List<(string Name, byte[] Data)>
        {
            ("HELLO.TXT", fileData)
        };

        byte[] result = DiskImageWriter.WriteFiles(image, files);

        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);
        Assert.True(loader.Load(result));

        var entry = loader.ListRootDirectory().First(e => e.Name == "HELLO.TXT");
        byte[]? readBack = loader.ReadFile(entry);
        Assert.NotNull(readBack);
        Assert.Equal("Hello FAT12", System.Text.Encoding.ASCII.GetString(readBack));
    }

    [Fact]
    public void WriteFiles_MultipleFiles()
    {
        byte[] image = DiskImageWriter.CreateBlankFloppy144();
        var files = new List<(string Name, byte[] Data)>
        {
            ("FILE1.TXT", System.Text.Encoding.ASCII.GetBytes("File 1")),
            ("FILE2.COM", new byte[] { 0xCD, 0x20 }), // INT 20h
            ("FILE3.DAT", new byte[1024]) // 1KB of zeros
        };

        byte[] result = DiskImageWriter.WriteFiles(image, files);

        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);
        Assert.True(loader.Load(result));

        var entries = loader.ListRootDirectory().Where(e => !e.IsVolumeLabel).ToList();
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.Name == "FILE1.TXT");
        Assert.Contains(entries, e => e.Name == "FILE2.COM");
        Assert.Contains(entries, e => e.Name == "FILE3.DAT");
    }

    [Fact]
    public void RoundTrip_LoadWriteLoad()
    {
        // Read disk1.img (MASM 5.1) - test that we can read it correctly
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "imgs", "MASM_5.1", "disk1.img");
        if (!File.Exists(path)) return; // Skip if test images not available

        byte[] diskData = File.ReadAllBytes(path);
        var log = new EmulatorLog();
        var loader = new DiskImageLoader(log);
        Assert.True(loader.Load(diskData));

        var entries = loader.ListRootDirectory();
        Assert.True(entries.Count > 0);
        
        // Verify SETUP.BAT exists and is readable
        var setupBat = entries.FirstOrDefault(e => e.Name == "SETUP.BAT");
        Assert.False(string.IsNullOrEmpty(setupBat.Name), "SETUP.BAT not found in root directory");
        byte[]? content = loader.ReadFile(setupBat);
        Assert.NotNull(content);
        string text = System.Text.Encoding.ASCII.GetString(content);
        Assert.Contains("runme", text.ToLowerInvariant());
    }
}
