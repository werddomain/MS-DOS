using MsDos.Core.Platform;

namespace MsDos.Core.Dos;

/// <summary>
/// Supported floppy disk types for drive slots A: and B:.
/// </summary>
public enum FloppyDiskType
{
    /// <summary>No disk inserted.</summary>
    None,
    /// <summary>5.25" 360KB double-density.</summary>
    Floppy360K,
    /// <summary>5.25" 1.2MB high-density.</summary>
    Floppy1200K,
    /// <summary>3.5" 720KB double-density.</summary>
    Floppy720K,
    /// <summary>3.5" 1.44MB high-density (most common).</summary>
    Floppy1440K
}

/// <summary>
/// Represents a single floppy drive slot (A: or B:).
/// Tracks the inserted disk image, disk type, and read/write activity.
/// </summary>
public sealed class FloppyDriveSlot
{
    /// <summary>Drive letter for this slot ('A' or 'B').</summary>
    public char DriveLetter { get; }

    /// <summary>The current disk type setting for this drive.</summary>
    public FloppyDiskType DiskType { get; set; } = FloppyDiskType.Floppy1440K;

    /// <summary>Whether a disk image is currently inserted.</summary>
    public bool HasDisk => ImageData != null;

    /// <summary>The raw disk image data (null if no disk inserted).</summary>
    public byte[]? ImageData { get; private set; }

    /// <summary>The label/filename of the current disk image.</summary>
    public string? DiskLabel { get; private set; }

    /// <summary>Whether the disk has been modified since insertion.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Whether a read operation is currently in progress.</summary>
    public bool IsReading { get; private set; }

    /// <summary>Whether a write operation is currently in progress.</summary>
    public bool IsWriting { get; private set; }

    /// <summary>Fired when read/write activity changes (for LED indicators).</summary>
    public event Action<FloppyDriveSlot>? ActivityChanged;

    /// <summary>Fired when a disk is inserted or ejected.</summary>
    public event Action<FloppyDriveSlot>? DiskChanged;

    public FloppyDriveSlot(char driveLetter)
    {
        DriveLetter = char.ToUpperInvariant(driveLetter);
    }

    /// <summary>
    /// Insert a disk image into this drive slot.
    /// </summary>
    /// <param name="imageData">Raw disk image bytes.</param>
    /// <param name="label">Disk label or filename.</param>
    public void InsertDisk(byte[] imageData, string? label = null)
    {
        ImageData = imageData;
        DiskLabel = label ?? $"DISK_{DriveLetter}";
        IsDirty = false;
        DiskChanged?.Invoke(this);
    }

    /// <summary>
    /// Eject the current disk from the drive.
    /// </summary>
    /// <returns>The disk image data (for saving), or null if no disk.</returns>
    public byte[]? EjectDisk()
    {
        var data = ImageData;
        ImageData = null;
        DiskLabel = null;
        IsDirty = false;
        DiskChanged?.Invoke(this);
        return data;
    }

    /// <summary>
    /// Signal that a read operation is starting.
    /// </summary>
    public void BeginRead()
    {
        IsReading = true;
        ActivityChanged?.Invoke(this);
    }

    /// <summary>
    /// Signal that a read operation has completed.
    /// </summary>
    public void EndRead()
    {
        IsReading = false;
        ActivityChanged?.Invoke(this);
    }

    /// <summary>
    /// Signal that a write operation is starting.
    /// </summary>
    public void BeginWrite()
    {
        IsWriting = true;
        IsDirty = true;
        ActivityChanged?.Invoke(this);
    }

    /// <summary>
    /// Signal that a write operation has completed.
    /// </summary>
    public void EndWrite()
    {
        IsWriting = false;
        ActivityChanged?.Invoke(this);
    }

    /// <summary>
    /// Get the expected disk size in bytes for the current disk type.
    /// </summary>
    public int GetExpectedSize() => DiskType switch
    {
        FloppyDiskType.Floppy360K => 368640,
        FloppyDiskType.Floppy720K => 737280,
        FloppyDiskType.Floppy1200K => 1228800,
        FloppyDiskType.Floppy1440K => 1474560,
        _ => 0
    };

    /// <summary>
    /// Get the disk geometry (cylinders, heads, sectors per track) for the current disk type.
    /// </summary>
    public (int cylinders, int heads, int sectorsPerTrack) GetGeometry() => DiskType switch
    {
        FloppyDiskType.Floppy360K => (40, 2, 9),
        FloppyDiskType.Floppy720K => (80, 2, 9),
        FloppyDiskType.Floppy1200K => (80, 2, 15),
        FloppyDiskType.Floppy1440K => (80, 2, 18),
        _ => (0, 0, 0)
    };
}

/// <summary>
/// Controls the two floppy drive slots (A: and B:).
/// Provides disk insertion/ejection, activity tracking, and save-to-IMG functionality.
/// Fires events for UI indicators (LED read/write lights).
/// </summary>
public sealed class FloppyDriveController
{
    /// <summary>Drive slot A:.</summary>
    public FloppyDriveSlot SlotA { get; }

    /// <summary>Drive slot B:.</summary>
    public FloppyDriveSlot SlotB { get; }

    private readonly EmulatorLog _log;

    public FloppyDriveController(EmulatorLog log)
    {
        _log = log;
        SlotA = new FloppyDriveSlot('A');
        SlotB = new FloppyDriveSlot('B');
    }

    /// <summary>
    /// Get the drive slot for a given drive letter.
    /// </summary>
    public FloppyDriveSlot? GetSlot(char driveLetter)
    {
        return char.ToUpperInvariant(driveLetter) switch
        {
            'A' => SlotA,
            'B' => SlotB,
            _ => null
        };
    }

    /// <summary>
    /// Insert a disk image into the specified drive slot.
    /// </summary>
    public bool InsertDisk(char driveLetter, byte[] imageData, string? label = null)
    {
        var slot = GetSlot(driveLetter);
        if (slot == null) return false;

        slot.InsertDisk(imageData, label);
        _log.Info("Floppy", $"Disk inserted into {driveLetter}: ({imageData.Length} bytes, {label ?? "unlabeled"})");
        return true;
    }

    /// <summary>
    /// Eject the disk from the specified drive slot.
    /// Returns the disk image data for saving.
    /// </summary>
    public byte[]? EjectDisk(char driveLetter)
    {
        var slot = GetSlot(driveLetter);
        if (slot == null) return null;

        var data = slot.EjectDisk();
        _log.Info("Floppy", $"Disk ejected from {driveLetter}:");
        return data;
    }

    /// <summary>
    /// Save the current state of a drive to a byte array (IMG format).
    /// </summary>
    public byte[]? SaveDriveState(char driveLetter)
    {
        var slot = GetSlot(driveLetter);
        if (slot?.ImageData == null) return null;

        // Return a copy of the current image data
        var copy = new byte[slot.ImageData.Length];
        Buffer.BlockCopy(slot.ImageData, 0, copy, 0, slot.ImageData.Length);
        _log.Info("Floppy", $"Drive {driveLetter}: state saved ({copy.Length} bytes)");
        return copy;
    }

    /// <summary>
    /// Check if a floppy drive has a bootable disk (checks for boot signature 0x55AA).
    /// </summary>
    public bool IsBootable(char driveLetter)
    {
        var slot = GetSlot(driveLetter);
        if (slot?.ImageData == null || slot.ImageData.Length < 512) return false;

        // Check for boot signature at offset 510-511
        return slot.ImageData[510] == 0x55 && slot.ImageData[511] == 0xAA;
    }

    /// <summary>
    /// Change the disk type of a drive slot (before bootup).
    /// </summary>
    public void SetDiskType(char driveLetter, FloppyDiskType diskType)
    {
        var slot = GetSlot(driveLetter);
        if (slot == null) return;

        slot.DiskType = diskType;
        _log.Info("Floppy", $"Drive {driveLetter}: type set to {diskType}");
    }
}
