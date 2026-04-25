using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Hardware;

/// <summary>
/// IBM PC boot sequence emulation.
/// Performs the equivalent of the BIOS POST and bootstrap process:
///   1. POST (basic hardware init)
///   2. Set up Interrupt Vector Table (IVT) at 0000:0000
///   3. Set up BIOS Data Area (BDA) at 0040:0000
///   4. Read boot sector from first bootable drive into 0000:7C00
///   5. Jump CPU to 0000:7C00 with DL = boot drive number
///
/// This enables booting real MS-DOS from a disk image without relying
/// on the managed CommandShell/DosKernel path.
/// </summary>
public sealed class BootSequence
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _memory;
    private readonly EmulatorLog? _log;
    private IOPortBus? _ports;

    // Disk images indexed by BIOS drive number
    private readonly Dictionary<byte, byte[]> _bootableDisks = new();

    /// <summary>Custom BIOS ROM image (loaded at F000:0000, 64 KB max).</summary>
    private byte[]? _biosRomImage;

    /// <summary>Boot sector load address (standard IBM PC).</summary>
    private const ushort BootSectorSegment = 0x0000;
    private const ushort BootSectorOffset = 0x7C00;
    private const int BootSectorSize = 512;

    /// <summary>BIOS ROM segment (where the BIOS code would live).</summary>
    private const ushort BiosSegment = 0xF000;

    /// <summary>
    /// IRET stub offset inside segment E000 (well below F000:0000).
    /// Using a separate segment avoids custom BIOS ROM overwriting the stubs.
    /// </summary>
    private const ushort IretSegment = 0xE000;
    private const ushort IretOffset = 0x0000;

    /// <summary>Whether a custom BIOS ROM is loaded.</summary>
    public bool HasCustomBiosRom => _biosRomImage != null;

    /// <summary>When true, DEL was pressed during POST and BIOS setup should be invoked.</summary>
    public bool SetupRequested { get; private set; }

    /// <summary>Fired during POST to display messages (e.g., "Press DEL for Setup").</summary>
    public event Action<string>? PostMessage;

    public BootSequence(Cpu8086 cpu, MemoryBus memory, EmulatorLog? log = null)
    {
        _cpu = cpu;
        _memory = memory;
        _log = log;
    }

    /// <summary>Set the I/O port bus so POST can program the PIC.</summary>
    public void SetPorts(IOPortBus ports) => _ports = ports;

    /// <summary>
    /// Load a custom BIOS ROM image. The ROM will be installed at F000:0000
    /// (physical address 0xF0000) instead of the emulated BIOS stubs.
    /// Typically a 64 KB ROM image. The CPU reset vector at FFFF:0000
    /// will jump into this ROM, so the ROM must contain all BIOS code.
    /// </summary>
    /// <param name="romData">Raw BIOS ROM bytes (typically 8 KB to 64 KB).</param>
    public void LoadBiosRom(byte[] romData)
    {
        _biosRomImage = romData;
        _log?.Info("BOOT", $"Custom BIOS ROM loaded ({romData.Length} bytes)");
    }

    /// <summary>Remove the custom BIOS ROM so the emulated BIOS stubs are used.</summary>
    public void UnloadBiosRom()
    {
        _biosRomImage = null;
        _log?.Info("BOOT", "Custom BIOS ROM unloaded — using emulated BIOS");
    }

    /// <summary>
    /// Register a bootable disk image with a BIOS drive number.
    /// Drive 0x00 = first floppy (A:), 0x01 = second floppy (B:),
    /// 0x80 = first hard disk (C:), etc.
    /// </summary>
    public void RegisterDisk(byte driveNumber, byte[] imageData)
    {
        _bootableDisks[driveNumber] = imageData;
    }

    /// <summary>Remove a disk from the boot sequence.</summary>
    public void UnregisterDisk(byte driveNumber)
    {
        _bootableDisks.Remove(driveNumber);
    }

    /// <summary>Check whether a disk is registered for a given drive number.</summary>
    public bool HasDisk(byte driveNumber) => _bootableDisks.ContainsKey(driveNumber);

    /// <summary>
    /// Execute the boot sequence. Tries drives in the specified order,
    /// defaulting to floppy 0x00, floppy 0x01, hard disk 0x80.
    /// </summary>
    /// <param name="bootOrder">Optional custom boot order (array of BIOS drive numbers).</param>
    /// <returns>True if a bootable disk was found and boot sector loaded.</returns>
    public bool Execute(byte[]? bootOrder = null)
    {
        _log?.Info("BOOT", "Starting POST and bootstrap sequence...");
        SetupRequested = false;

        // 1. POST — basic initialization
        PerformPost();

        // 2. Set up IVT with BIOS interrupt stubs
        SetupInterruptVectorTable();

        // 3. Set up BIOS Data Area
        SetupBiosDataArea();

        // 4. Install BIOS code — custom ROM or emulated stubs
        InstallBiosCode();

        // 5. POST message — Press DEL for BIOS Setup
        PostMessage?.Invoke("Press DEL to enter BIOS Setup...");
        _log?.Info("BOOT", "POST complete — Press DEL to enter BIOS Setup");

        // 6. Try to boot from available drives
        byte[] order = bootOrder ?? new byte[] { 0x00, 0x01, 0x80 };
        foreach (byte drive in order)
        {
            if (drive == 0xFF) continue; // Disabled slot
            if (TryBootFromDrive(drive))
            {
                _log?.Info("BOOT", $"Boot sector loaded from drive 0x{drive:X2} → jumping to 0000:7C00");
                return true;
            }
        }

        _log?.Error("BOOT", "No bootable disk found — halting");
        return false;
    }

    /// <summary>
    /// Boot from a specific drive (bypasses auto-detection).
    /// </summary>
    public bool BootFromDrive(byte driveNumber)
    {
        SetupRequested = false;
        PerformPost();
        SetupInterruptVectorTable();
        SetupBiosDataArea();
        InstallBiosCode();

        if (TryBootFromDrive(driveNumber))
        {
            _log?.Info("BOOT", $"Boot sector loaded from drive 0x{driveNumber:X2} → jumping to 0000:7C00");
            return true;
        }

        _log?.Error("BOOT", $"Drive 0x{driveNumber:X2} is not bootable");
        return false;
    }

    /// <summary>
    /// Run only the POST/IVT/BDA/ROM setup without loading a boot sector.
    /// Used when entering BIOS setup before booting.
    /// </summary>
    public void PrepareForSetup()
    {
        SetupRequested = true;
        PerformPost();
        SetupInterruptVectorTable();
        SetupBiosDataArea();
        InstallBiosCode();
        _log?.Info("BOOT", "POST complete — entering BIOS Setup");
    }

    /// <summary>
    /// Install BIOS code: either custom ROM image or emulated stubs.
    /// </summary>
    private void InstallBiosCode()
    {
        if (_biosRomImage != null)
        {
            InstallCustomBiosRom();
        }
        else
        {
            InstallBiosStubs();
        }
    }

    /// <summary>
    /// Install a custom BIOS ROM at F000:0000.
    /// For ROMs smaller than 64 KB, they are loaded at the top of the segment
    /// so that the reset vector at FFFF:0000 (physical 0xFFFF0) is correct.
    /// </summary>
    private void InstallCustomBiosRom()
    {
        if (_biosRomImage == null) return;

        int romSize = _biosRomImage.Length;
        uint baseAddr;

        if (romSize >= 65536)
        {
            // Full 64KB ROM — load at F000:0000
            baseAddr = BiosSegment * 16u;
            for (int i = 0; i < 65536 && i < romSize; i++)
                _memory.WriteByte(baseAddr + (uint)i, _biosRomImage[i]);
        }
        else
        {
            // Smaller ROM — align to end of segment so FFFF:0000 reset vector works
            baseAddr = 0x100000u - (uint)romSize;
            for (int i = 0; i < romSize; i++)
                _memory.WriteByte(baseAddr + (uint)i, _biosRomImage[i]);
        }

        _log?.Info("BOOT", $"Custom BIOS ROM installed at {baseAddr:X5}h ({romSize} bytes)");
    }

    private bool TryBootFromDrive(byte driveNumber)
    {
        if (!_bootableDisks.TryGetValue(driveNumber, out var disk))
            return false;

        if (disk.Length < BootSectorSize)
            return false;

        // Check boot signature (0x55AA at offset 510-511)
        ushort signature = (ushort)(disk[510] | (disk[511] << 8));
        if (signature != 0xAA55)
        {
            _log?.Warn("BOOT", $"Drive 0x{driveNumber:X2}: invalid boot signature 0x{signature:X4} (expected 0xAA55)");
            // Still try to boot — some early DOS disks don't have the signature
        }

        // Load boot sector into 0000:7C00
        uint loadAddress = (uint)(BootSectorSegment * 16 + BootSectorOffset);
        for (int i = 0; i < BootSectorSize; i++)
        {
            _memory.WriteByte(loadAddress + (uint)i, disk[i]);
        }

        // Set CPU state as BIOS would before jumping to boot sector
        _cpu.Regs.Reset();
        _cpu.Regs.CS = BootSectorSegment;
        _cpu.Regs.IP = BootSectorOffset;
        _cpu.Regs.DL = driveNumber;              // Boot drive
        _cpu.Regs.AX = 0;
        _cpu.Regs.BX = 0;
        _cpu.Regs.CX = 0;
        _cpu.Regs.DX = (ushort)(driveNumber);    // DH=0 (head), DL=drive
        _cpu.Regs.SS = 0x0000;
        _cpu.Regs.SP = 0x7C00;                   // Stack just below boot sector
        _cpu.Regs.DS = 0x0000;
        _cpu.Regs.ES = 0x0000;
        _cpu.Regs.Flags = CpuFlags.Interrupt;     // Interrupts enabled

        return true;
    }

    private void PerformPost()
    {
        _log?.Info("BOOT", "POST: Memory test... 640K OK");
        // Memory is already initialized by MemoryBus constructor

        // Program the 8259A PIC via I/O ports (real BIOS does this during POST).
        // ICW1 (0x11) → ICW2 (base 0x08) → ICW3 (slave on IRQ2) → ICW4 (8086 mode)
        // Then unmask IRQ 0 (timer), IRQ 1 (keyboard), IRQ 2 (cascade), IRQ 6 (floppy).
        if (_ports != null)
        {
            // Master PIC (ports 0x20/0x21)
            _ports.WriteByte(0x20, 0x11); // ICW1: edge-triggered, cascade, ICW4 needed
            _ports.WriteByte(0x21, 0x08); // ICW2: base vector 08h (IRQ0 → INT 08h)
            _ports.WriteByte(0x21, 0x04); // ICW3: slave on IRQ2
            _ports.WriteByte(0x21, 0x01); // ICW4: 8086 mode, normal EOI
            _ports.WriteByte(0x21, 0xB8); // OCW1: unmask IRQ 0,1,2,6 (mask = 10111000 = 0xB8 → unmask 0,1,2,6)
            // Actually 0xB8 masks bits 3,4,5,7. Unmask = 0,1,2,6.
            // Binary: bit0=IRQ0(timer)=0(unmask), bit1=IRQ1(kbd)=0, bit2=IRQ2(cascade)=0,
            //   bit3=IRQ3=1(mask), bit4=IRQ4=1, bit5=IRQ5=1, bit6=IRQ6(floppy)=0, bit7=IRQ7=1
            // So mask = 10111000 = 0xB8

            // Slave PIC (ports 0xA0/0xA1)
            _ports.WriteByte(0xA0, 0x11); // ICW1
            _ports.WriteByte(0xA1, 0x70); // ICW2: base vector 70h
            _ports.WriteByte(0xA1, 0x02); // ICW3: slave ID 2
            _ports.WriteByte(0xA1, 0x01); // ICW4: 8086 mode
            _ports.WriteByte(0xA1, 0xFF); // OCW1: mask all slave IRQs

            _log?.Debug("BOOT", "PIC initialized: IRQ 0,1,2,6 unmasked");
        }
    }

    /// <summary>
    /// Set up the Interrupt Vector Table at 0000:0000.
    /// Each entry is a 4-byte FAR pointer (offset:segment).
    /// We point them to IRET stubs in the BIOS ROM area.
    /// </summary>
    private void SetupInterruptVectorTable()
    {
        // Place IRET stub at E000:0000 (physical 0xE0000) — safely below the F000
        // segment so custom BIOS ROMs at F000:0000 cannot overwrite it.
        _memory.WriteByte((uint)(IretSegment * 16 + IretOffset), 0xCF); // IRET

        for (int i = 0; i < 256; i++)
        {
            uint ivtAddr = (uint)(i * 4);
            _memory.WriteWord(0x0000, (ushort)ivtAddr, IretOffset);    // Offset
            _memory.WriteWord(0x0000, (ushort)(ivtAddr + 2), IretSegment); // Segment
        }

        // Set up specific well-known vectors
        // INT 1Eh — Disk parameter table (needed by boot sector)
        SetupDiskParameterTable();

        // Restore font IVT vectors (PopulateBiosDataArea sets these, but we just
        // blanked all 256 entries above — re-point them to the font at E000:0100).
        // INT 0x1F = upper 128 chars (80h-FFh), offset into font by 128*8 = 0x400
        _memory.WriteWord(0x0000, 0x1F * 4, (ushort)(Interrupts.CgaFont8x8.RomOffset + 0x400));
        _memory.WriteWord(0x0000, 0x1F * 4 + 2, Interrupts.CgaFont8x8.RomSegment);
        // INT 0x43 = full 256-char font pointer (EGA/VGA current font)
        _memory.WriteWord(0x0000, 0x43 * 4, Interrupts.CgaFont8x8.RomOffset);
        _memory.WriteWord(0x0000, 0x43 * 4 + 2, Interrupts.CgaFont8x8.RomSegment);

        _log?.Debug("BOOT", "IVT initialized with 256 default IRET handlers at E000:0000");
    }

    /// <summary>
    /// Set up the disk parameter table pointed to by INT 1Eh.
    /// This table describes floppy disk timing and geometry parameters.
    /// </summary>
    private void SetupDiskParameterTable()
    {
        // Place the disk parameter table at F000:EFC7 (standard BIOS location)
        ushort dptOffset = 0xEFC7;
        uint dptAddr = (uint)(BiosSegment * 16 + dptOffset);

        // Determine SPT from the first registered floppy disk
        byte spt = 18; // default 1.44MB
        byte gapRw = 0x1B;
        byte gapFmt = 0x6C;
        if (_bootableDisks.TryGetValue(0x00, out var disk0))
        {
            spt = disk0.Length switch
            {
                163840 => 8,   // 160K
                184320 => 9,   // 180K
                327680 => 8,   // 320K
                368640 => 9,   // 360K
                737280 => 9,   // 720K
                1228800 => 15, // 1.2M
                2949120 => 36, // 2.88M
                _ => 18        // 1.44M
            };
            gapRw = spt <= 9 ? (byte)0x2A : (byte)0x1B;
            gapFmt = spt <= 9 ? (byte)0x50 : (byte)0x6C;
        }

        // 11 bytes: floppy parameters matching the boot disk
        byte[] dpt = {
            0xDF, // SRT=D, HUT=F (step rate time, head unload time)
            0x02, // HLT=01, ND=0 (head load time, DMA)
            0x25, // Motor wait time (ticks until motor off)
            0x02, // Bytes per sector (2 = 512)
            spt,  // Sectors per track
            gapRw, // Gap length for read/write
            0xFF, // Data length (ignored when sector size specified)
            gapFmt, // Gap length for format
            0xF6, // Fill byte for format
            0x0F, // Head settle time (ms)
            0x08  // Motor start time (1/8 seconds)
        };

        for (int i = 0; i < dpt.Length; i++)
            _memory.WriteByte(dptAddr + (uint)i, dpt[i]);

        // Point INT 1Eh vector to this table
        _memory.WriteWord(0x0000, 0x1E * 4, dptOffset);
        _memory.WriteWord(0x0000, 0x1E * 4 + 2, BiosSegment);
    }

    /// <summary>
    /// Set up the BIOS Data Area (BDA) at segment 0040h.
    /// Contains hardware configuration data that DOS and programs read.
    /// </summary>
    private void SetupBiosDataArea()
    {
        // COM port base addresses (0040:0000-0007)
        _memory.WriteWord(0x0040, 0x0000, 0x03F8); // COM1
        _memory.WriteWord(0x0040, 0x0002, 0x02F8); // COM2
        _memory.WriteWord(0x0040, 0x0004, 0x0000); // COM3
        _memory.WriteWord(0x0040, 0x0006, 0x0000); // COM4

        // LPT port base addresses (0040:0008-000D)
        _memory.WriteWord(0x0040, 0x0008, 0x0378); // LPT1
        _memory.WriteWord(0x0040, 0x000A, 0x0000); // LPT2
        _memory.WriteWord(0x0040, 0x000C, 0x0000); // LPT3

        // Equipment word (0040:0010)
        // Bit 0: floppy installed
        // Bits 1: math coprocessor
        // Bits 4-5: initial video mode (10 = 80x25 color)
        // Bits 6-7: number of floppies - 1
        // Always report 2 floppy drives (standard PC has A: and B: bays)
        // to prevent DOS phantom B: aliasing where B: mirrors A:
        ushort equipment = 0x0061; // 80x25 color, 2 floppies (bits 6-7 = 01)
        _memory.WriteWord(0x0040, 0x0010, equipment);

        // Base memory size in KB (0040:0013)
        _memory.WriteWord(0x0040, 0x0013, 640);

        // Keyboard shift flags (0040:0017-0018)
        _memory.WriteByte(0x0040, 0x0017, 0x00);
        _memory.WriteByte(0x0040, 0x0018, 0x00);

        // Keyboard buffer head/tail (0040:001A-001D)
        _memory.WriteWord(0x0040, 0x001A, 0x001E); // Head
        _memory.WriteWord(0x0040, 0x001C, 0x001E); // Tail (same = empty)

        // Keyboard buffer (circular, 0040:001E-003D)
        // Initial buffer is empty

        // Current video mode (0040:0049)
        _memory.WriteByte(0x0040, 0x0049, 0x03); // Mode 3: 80x25 text color

        // Columns per row (0040:004A)
        _memory.WriteWord(0x0040, 0x004A, 80);

        // Video page size (0040:004C)
        _memory.WriteWord(0x0040, 0x004C, 4000); // 80*25*2

        // Cursor positions for video pages (0040:0050-005F)
        for (int i = 0; i < 16; i++)
            _memory.WriteByte(0x0040, (ushort)(0x0050 + i), 0);

        // Active video page (0040:0062)
        _memory.WriteByte(0x0040, 0x0062, 0);

        // CRT controller base port (0040:0063)
        _memory.WriteWord(0x0040, 0x0063, 0x03D4); // Color

        // Timer tick count (0040:006C-006F)
        var midnight = DateTime.Today;
        uint ticks = (uint)((DateTime.Now - midnight).TotalSeconds * 18.2065);
        _memory.WriteWord(0x0040, 0x006C, (ushort)(ticks & 0xFFFF));
        _memory.WriteWord(0x0040, 0x006E, (ushort)(ticks >> 16));
        _memory.WriteByte(0x0040, 0x0070, 0); // Midnight flag

        // Number of hard drives (0040:0075)
        byte hddCount = 0;
        for (byte d = 0x80; d < 0x84; d++)
            if (_bootableDisks.ContainsKey(d)) hddCount++;
        _memory.WriteByte(0x0040, 0x0075, hddCount);

        // Keyboard buffer start/end pointers (0040:0080-0083)
        _memory.WriteWord(0x0040, 0x0080, 0x001E);
        _memory.WriteWord(0x0040, 0x0082, 0x003E);

        // Rows minus one (0040:0084)
        _memory.WriteByte(0x0040, 0x0084, 24);

        // Character height (0040:0085)
        _memory.WriteWord(0x0040, 0x0085, 16);

        _log?.Debug("BOOT", "BDA initialized at 0040:0000");
    }

    /// <summary>
    /// Install small BIOS code stubs in the ROM area (F000 segment).
    /// These allow the boot sector to use INT 10h, INT 13h, INT 16h, etc.
    /// through the managed interrupt handlers rather than native BIOS code.
    ///
    /// Each stub is just: INT xx / IRET — the managed InterruptController
    /// will intercept the INT and dispatch to the C# handler.
    /// </summary>
    private void InstallBiosStubs()
    {
        // CPU reset vector at FFFF:0000 (physical F000:FFF0)
        // JMP FAR F000:E05B (standard BIOS entry point)
        uint resetVector = 0xFFFF0;
        _memory.WriteByte(resetVector, 0xEA);       // JMP FAR
        _memory.WriteByte(resetVector + 1, 0x5B);    // offset low
        _memory.WriteByte(resetVector + 2, 0xE0);    // offset high
        _memory.WriteByte(resetVector + 3, 0x00);    // segment low
        _memory.WriteByte(resetVector + 4, 0xF0);    // segment high

        // BIOS entry point at F000:E05B — just HLT for now
        // (real boot goes through our managed path, not through reset vector)
        uint biosEntry = (uint)(BiosSegment * 16 + 0xE05B);
        _memory.WriteByte(biosEntry, 0xF4); // HLT

        // INT 19h bootstrap at F000:E6F2 — standard location
        // The managed handler will handle this, but install a stub just in case
        uint int19Stub = (uint)(BiosSegment * 16 + 0xE6F2);
        _memory.WriteByte(int19Stub, 0xCD);     // INT 19h
        _memory.WriteByte(int19Stub + 1, 0x19);
        _memory.WriteByte(int19Stub + 2, 0xCF); // IRET

        // Place the BIOS date string at F000:FFF5 (8 bytes, "01/01/86")
        byte[] biosDate = { 0x30, 0x31, 0x2F, 0x30, 0x31, 0x2F, 0x38, 0x36 }; // "01/01/86"
        uint dateAddr = 0xFFFF5;
        for (int i = 0; i < biosDate.Length; i++)
            _memory.WriteByte(dateAddr + (uint)i, biosDate[i]);

        // Machine ID at FFFFE (0xFF = original PC, 0xFE = XT, 0xFC = AT)
        _memory.WriteByte(0xFFFFE, 0xFF); // Original IBM PC

        _log?.Debug("BOOT", "BIOS stubs installed in ROM area");
    }
}
