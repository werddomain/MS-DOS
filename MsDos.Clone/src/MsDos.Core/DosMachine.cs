using MsDos.Core.Bios;
using MsDos.Core.Cpu;
using MsDos.Core.Dos;
using MsDos.Core.Hardware;
using MsDos.Core.Interrupts;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core;

/// <summary>
/// The main DOS machine emulator. Orchestrates the CPU, memory, interrupt
/// services, and DOS kernel. Platform-agnostic - requires injected
/// implementations of IGraphicsRenderer, IEventRegistry, and IStreamProvider.
/// </summary>
public sealed class DosMachine
{
    public Cpu8086 Cpu { get; }
    public MemoryBus Memory { get; }
    public InterruptController Interrupts { get; }
    public BiosVideoService Video { get; }
    public BiosKeyboardService Keyboard { get; }
    public BiosMiscService BiosMisc { get; }
    public BiosDiskService Disk { get; }
    public BiosMouseService Mouse { get; }
    public DosKernel Dos { get; }
    public BinaryLoader Loader { get; }
    public CommandShell Shell { get; }
    public EmulatorLog Log { get; }

    /// <summary>INT 08h timer tick handler — increments BDA tick counter.</summary>
    public BiosTimerService Timer { get; }

    /// <summary>INT 09h keyboard IRQ handler — writes to BDA keyboard buffer.</summary>
    public BiosKeyboardIrqHandler KeyboardIrq { get; }

    /// <summary>CGA/VGA register state tracker for ports 0x3D4-0x3DA etc.</summary>
    public CgaRegisterState CgaRegisters { get; }

    public IGraphicsRenderer Renderer { get; }
    public IEventRegistry Events { get; }
    public IStreamProvider Streams { get; }

    /// <summary>Floppy drive controller with A: and B: slots.</summary>
    public FloppyDriveController Floppy { get; }

    /// <summary>I/O port address space for hardware emulation.</summary>
    public IOPortBus Ports { get; }

    /// <summary>Programmable Interrupt Controller (8259A).</summary>
    public PicController Pic { get; }

    /// <summary>Programmable Interval Timer (8253/8254).</summary>
    public PitTimer Pit { get; }

    /// <summary>DOS memory control block chain manager.</summary>
    public MemoryManager MemoryManager { get; }

    /// <summary>Intel 8237A DMA controller — 4 channels for floppy, memory refresh, etc.</summary>
    public DmaController Dma { get; }

    /// <summary>MC146818 CMOS/RTC — real-time clock and configuration storage.</summary>
    public CmosRtc Cmos { get; }

    /// <summary>NEC µPD765 floppy disk controller — sector-level I/O via DMA.</summary>
    public Fdc765Controller Fdc { get; }

    /// <summary>PC speaker — tone generation via PIT channel 2 + port 0x61.</summary>
    public PcSpeaker Speaker { get; }

    /// <summary>Video memory mapper — translates writes to B800/B000/A000 into screen updates.</summary>
    public VideoMemoryMapper VideoMapper { get; }

    /// <summary>IBM PC boot sequence — POST, IVT, BDA, boot sector loading.</summary>
    public BootSequence BootSeq { get; }

    /// <summary>BIOS Setup utility — text-based setup menu for CMOS configuration.</summary>
    public BiosSetup BiosSetupScreen { get; }

    /// <summary>Platform-agnostic virtual keyboard for on-screen key input.</summary>
    public VirtualKeyboard VirtualKbd { get; }

    /// <summary>Drive letter → stream provider mapping. Populated by mounting disk images or directories.</summary>
    private readonly Dictionary<char, IStreamProvider> _drives = new(CharComparer.OrdinalIgnoreCase);

    /// <summary>Drive letter → raw disk image bytes. Kept for re-registration with INT 13h after reset.</summary>
    private readonly Dictionary<char, byte[]> _rawDiskImages = new(CharComparer.OrdinalIgnoreCase);

    private bool _running;
    private bool _terminated;
    private bool _poweredOn;
    private byte _exitCode;
    private CancellationTokenSource? _cts;

    // Activity tracking
    private bool _cpuActive;
    private bool _diskActive;
    private DateTime _diskActivityUntilUtc;

    /// <summary>Fired when HDD/disk activity state changes (true = active, false = idle).</summary>
    public event Action<bool>? OnDiskActivity;

    /// <summary>Fired when CPU activity state changes (true = executing, false = idle/halted).</summary>
    public event Action<bool>? OnCpuActivity;

    /// <summary>Fired when power state changes (true = on, false = off).</summary>
    public event Action<bool>? OnPowerStateChanged;

    /// <summary>Whether the machine is currently powered on.</summary>
    public bool IsPoweredOn => _poweredOn;

    /// <summary>Whether the CPU is actively executing instructions.</summary>
    public bool IsCpuActive => _cpuActive;

    /// <summary>Whether a disk operation is in progress.</summary>
    public bool IsDiskActive => _diskActive;

    // INT 15h AH=86h wait state (non-blocking)
    private readonly object _int15WaitSync = new();
    private DateTime _int15WaitUntilUtc;
    private TaskCompletionSource<bool>? _int15WaitSignal;
    private bool _int15WaitPending;

    /// <summary>Fired when the emulated process terminates.</summary>
    public event Action<byte>? OnProcessExit;

    /// <summary>Fired on each instruction step (for debugging).</summary>
    public event Action? OnStep;

    /// <summary>Fired after a step in manual clock mode, providing the memory diff.</summary>
    public event Action<MemoryDiff>? OnManualStep;

    /// <summary>Maximum instructions per frame (throttle).</summary>
    public int InstructionsPerFrame { get; set; } = 50000;

    /// <summary>Target frame interval in milliseconds.</summary>
    public int FrameIntervalMs { get; set; } = 16; // ~60 FPS

    /// <summary>
    /// When true, a BIOS INT 15h AH=86h wait is currently pending and CPU execution is paused.
    /// </summary>
    public bool IsBiosWaitPending
    {
        get
        {
            lock (_int15WaitSync)
                return _int15WaitPending;
        }
    }

    /// <summary>When true, the automatic CPU clock is disabled; instructions only execute on StepInstruction().</summary>
    public bool ManualClockMode { get; set; }

    /// <summary>Physical start address for memory snapshots in manual mode.</summary>
    public uint SnapshotStartAddress { get; set; }

    /// <summary>Length in bytes for memory snapshots in manual mode.</summary>
    public int SnapshotLength { get; set; } = 256;

    /// <summary>The last memory snapshot captured (before last step).</summary>
    public MemorySnapshot? LastSnapshot { get; private set; }

    public DosMachine(
        IGraphicsRenderer renderer,
        IEventRegistry events,
        IStreamProvider streams)
    {
        Renderer = renderer;
        Events = events;
        Streams = streams;
        Log = new EmulatorLog();

        Memory = new MemoryBus();
        Cpu = new Cpu8086(Memory);
        Cpu.SetLog(Log);

        // x87 FPU coprocessor
        var fpu = new MsDos.Core.Cpu.Fpu8087(Cpu, Memory);
        Cpu.SetFpu(fpu);

        // I/O port subsystem and hardware controllers
        Ports = new IOPortBus();
        Cpu.SetIOPortBus(Ports);

        Pic = new PicController();
        Pic.RegisterPorts(Ports);
        Pic.InterruptRequest += vector =>
        {
            Cpu.Unhalt(); // IRQ wakes CPU from HLT
            if (Cpu.Regs.Flags.HasFlag(CpuFlags.Interrupt))
            {
                Cpu.Regs.Flags &= ~CpuFlags.Interrupt; // Disable IF while servicing
                // Dispatch through the interrupt controller so INT 08h, 09h, etc. actually run
                Cpu.RaiseHardwareInterrupt(vector);
            }
        };

        Pit = new PitTimer();
        Pit.RegisterPorts(Ports);
        Pit.TimerTick += () => Pic.RaiseIRQ(0); // Channel 0 → IRQ 0 → INT 08h

        // Memory manager (MCB chain)
        MemoryManager = new MemoryManager(Memory);

        // Hardware controllers
        Dma = new DmaController();
        Dma.RegisterPorts(Ports);

        Cmos = new CmosRtc();
        Cmos.RegisterPorts(Ports);

        Speaker = new PcSpeaker(Ports);
        Speaker.RegisterPorts();

        Fdc = new Fdc765Controller(Dma, Memory, Pic, Log);
        Fdc.RegisterPorts(Ports);

        VideoMapper = new VideoMemoryMapper(Memory, Renderer, Log);
        BootSeq = new BootSequence(Cpu, Memory, Log);
        BootSeq.SetPorts(Ports); // Allow POST to program the PIC
        BiosSetupScreen = new BiosSetup(Cmos, Renderer, Events, Log);
        VirtualKbd = new VirtualKeyboard(Events);

        // Wire PIT channel 2 output to speaker
        Pit.Channel2OutputChanged += reload => Speaker.UpdatePitChannel2(reload);

        // CGA/VGA register state tracker (must be created before RegisterHardwarePorts)
        CgaRegisters = new CgaRegisterState();

        // Register common PC hardware ports (including CGA register tracking)
        RegisterHardwarePorts();

        Interrupts = new InterruptController(Cpu, Memory);
        Video = new BiosVideoService(Cpu, Memory, renderer);
        Keyboard = new BiosKeyboardService(Cpu, events, Memory);
        BiosMisc = new BiosMiscService(Cpu, Memory);
        Disk = new BiosDiskService(Cpu, Memory, Log);
        Mouse = new BiosMouseService(Cpu, Memory, events);
        Timer = new BiosTimerService(Cpu, Memory, Pic);
        KeyboardIrq = new BiosKeyboardIrqHandler(Memory, Pic);
        Dos = new DosKernel(Cpu, Memory, streams, events, renderer, Video, Log);
        Dos.SetMemoryManager(MemoryManager);
        Dos.OnFileIo = () => NotifyDiskActivity();
        Loader = new BinaryLoader(Memory, Cpu);
        Shell = new CommandShell(Dos, Memory, Cpu, streams, events, Video, renderer, Log, this);
        Floppy = new FloppyDriveController(Log);

        // Mount C: as the default stream provider
        _drives['C'] = streams;
        Dos.SetDriveProvider('C', streams);

        // Create a blank hard disk image for C: so INT 13h / FDISK can detect drive 0x80
        byte[] hdImage = DiskImageWriter.CreateBlankHardDiskImage(32);
        _rawDiskImages['C'] = hdImage;
        Disk.RegisterDiskAuto(0x80, hdImage);
        BootSeq.RegisterDisk(0x80, hdImage);

        // Register interrupt handlers
        Interrupts.RegisterHandler(0x08, Timer.HandleInt08);   // System timer tick (IRQ 0)
        Interrupts.RegisterHandler(0x10, Video.Handle);
        Interrupts.RegisterHandler(0x13, Disk.Handle);
        Interrupts.RegisterHandler(0x16, Keyboard.Handle);
        Interrupts.RegisterHandler(0x11, BiosMisc.HandleInt11);
        Interrupts.RegisterHandler(0x12, BiosMisc.HandleInt12);
        Interrupts.RegisterHandler(0x1A, BiosMisc.HandleInt1A);
        Interrupts.RegisterHandler(0x15, HandleInt15);     // System services
        Interrupts.RegisterHandler(0x14, BiosMisc.HandleInt14); // Serial port
        Interrupts.RegisterHandler(0x17, BiosMisc.HandleInt17); // Printer
        Interrupts.RegisterHandler(0x19, () => { Reset(); Log.Info("INT19", "Bootstrap requested"); }); // Reboot
        Interrupts.RegisterHandler(0x20, () => Dos.Terminate(0)); // INT 20h = terminate
        Interrupts.RegisterHandler(0x21, Dos.Handle);
        Interrupts.RegisterHandler(0x23, HandleInt23);    // Ctrl-C handler
        Interrupts.RegisterHandler(0x24, HandleInt24);    // Critical error handler
        Interrupts.RegisterHandler(0x33, Mouse.Handle);

        // Wire keyboard events to the BDA keyboard buffer (INT 09h path)
        // BIOS Setup intercepts keys when active
        Events.KeyDown += e =>
        {
            if (BiosSetupScreen.IsActive)
            {
                BiosSetupScreen.HandleKey(e);
                return;
            }
            KeyboardIrq.OnKeyEvent(e);
        };

        // Wire Ctrl+Alt+Del to machine reset
        KeyboardIrq.OnRebootRequested += () => { Reset(); Log.Info("Machine", "Ctrl+Alt+Del — reboot"); };

        // Handle process termination
        Dos.ProcessTerminated += code =>
        {
            _exitCode = code;
            _terminated = true;
            _running = false;
            OnProcessExit?.Invoke(code);
        };

        Log.Info("Machine", "DOS machine initialized");
    }

    /// <summary>
    /// Mount a disk image (IMG/RAW) as a drive letter.
    /// For A:/B: drives, also registers with the floppy drive controller.
    /// Also registers it with INT 13h disk services for sector-level access.
    /// </summary>
    /// <param name="driveLetter">Drive letter (A-Z).</param>
    /// <param name="imageData">Raw disk image bytes.</param>
    /// <param name="label">Optional label for the disk.</param>
    /// <returns>True if the image was loaded and mounted.</returns>
    public bool MountDiskImage(char driveLetter, byte[] imageData, string? label = null)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);
        var diskLoader = new DiskImageLoader(Log);
        if (!diskLoader.Load(imageData))
        {
            Log.Error("Machine", $"Failed to mount disk image on drive {driveLetter}");
            return false;
        }

        var provider = new DiskImageStreamProvider(diskLoader, Log);
        _drives[driveLetter] = provider;
        Dos.SetDriveProvider(driveLetter, provider);

        // Track in floppy controller for A:/B:
        var floppySlot = Floppy.GetSlot(driveLetter);
        if (floppySlot != null)
        {
            floppySlot.InsertDisk(imageData, label);
        }

        // Register with INT 13h for sector-level access
        byte biosDrive = driveLetter switch
        {
            'A' => 0x00,
            'B' => 0x01,
            _ => (byte)(0x80 + (driveLetter - 'C'))
        };
        Disk.RegisterDiskAuto(biosDrive, imageData);

        // Register with boot sequence for raw booting
        BootSeq.RegisterDisk(biosDrive, imageData);

        // Register with FDC for sector-level floppy I/O
        if (biosDrive <= 0x01)
            Fdc.LoadDisk(biosDrive, imageData);

        // Track raw image data for re-registration after reset
        _rawDiskImages[driveLetter] = imageData;

        Log.Info("Machine", $"Mounted disk image on {driveLetter}: ({imageData.Length} bytes, {diskLoader.DetectedFatType})");
        return true;
    }

    /// <summary>
    /// Get the stream provider for a given drive letter.
    /// </summary>
    public IStreamProvider? GetDriveProvider(char driveLetter)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);
        return _drives.TryGetValue(driveLetter, out var provider) ? provider : null;
    }

    /// <summary>Get all mounted drive letters.</summary>
    public IReadOnlyList<char> GetMountedDrives() => _drives.Keys.OrderBy(c => c).ToList();

    /// <summary>
    /// Eject a floppy disk from drive A: or B: while the system is running.
    /// Returns the current disk image data (for saving), or null if no disk.
    /// </summary>
    public byte[]? EjectFloppyDisk(char driveLetter)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);
        if (driveLetter != 'A' && driveLetter != 'B') return null;

        var data = Floppy.EjectDisk(driveLetter);
        _drives.Remove(driveLetter);
        _rawDiskImages.Remove(driveLetter);
        Dos.RemoveDriveProvider(driveLetter);

        // Unregister from INT 13h, boot sequence, and FDC so reads stop working
        byte biosDrive = driveLetter == 'A' ? (byte)0x00 : (byte)0x01;
        Disk.UnregisterDisk(biosDrive);
        BootSeq.UnregisterDisk(biosDrive);
        Fdc.EjectDisk(biosDrive);

        Log.Info("Machine", $"Floppy {driveLetter}: ejected");
        return data;
    }

    /// <summary>
    /// Insert a floppy disk into drive A: or B: while the system is running (hot-swap).
    /// </summary>
    public bool InsertFloppyDisk(char driveLetter, byte[] imageData, string? label = null)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);
        if (driveLetter != 'A' && driveLetter != 'B') return false;

        return MountDiskImage(driveLetter, imageData, label);
    }

    /// <summary>
    /// Insert a blank formatted floppy disk into drive A: or B:.
    /// Creates a blank FAT12 disk image of the specified type.
    /// </summary>
    /// <param name="driveLetter">Drive letter ('A' or 'B').</param>
    /// <param name="diskType">The floppy disk type (determines image size).</param>
    /// <param name="label">Optional volume label (max 11 chars).</param>
    /// <returns>True if the blank disk was created and inserted.</returns>
    public bool InsertBlankFloppyDisk(char driveLetter, FloppyDiskType diskType = FloppyDiskType.Floppy1440K, string? label = null)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);
        if (driveLetter != 'A' && driveLetter != 'B') return false;

        int imageSize = diskType switch
        {
            FloppyDiskType.Floppy360K => 368640,
            FloppyDiskType.Floppy720K => 737280,
            FloppyDiskType.Floppy1200K => 1228800,
            FloppyDiskType.Floppy1440K => 1474560,
            _ => 1474560
        };

        byte[] blankImage = DiskImageWriter.CreateBlankFat12Image(imageSize, label ?? "BLANK DISK");
        Log.Info("Machine", $"Created blank {diskType} floppy disk for {driveLetter}:");
        return MountDiskImage(driveLetter, blankImage, label ?? "Blank Floppy");
    }

    /// <summary>
    /// Save the current state of a floppy drive to an IMG byte array.
    /// </summary>
    public byte[]? SaveFloppyState(char driveLetter)
    {
        return Floppy.SaveDriveState(driveLetter);
    }

    /// <summary>
    /// Perform the boot sequence: check A: → B: → C: for bootable media.
    /// Returns the drive letter that was selected for boot, or null if no bootable drive found.
    /// </summary>
    public char? CheckBootSequence()
    {
        Log.Info("Boot", "Checking boot sequence: A: → B: → C:");

        // Check A:
        if (Floppy.SlotA.HasDisk)
        {
            Log.Info("Boot", "Boot disk found in A:");
            return 'A';
        }

        // Check B:
        if (Floppy.SlotB.HasDisk)
        {
            Log.Info("Boot", "Boot disk found in B:");
            return 'B';
        }

        // Check C:
        if (_drives.ContainsKey('C'))
        {
            Log.Info("Boot", "Booting from C: (hard drive)");
            return 'C';
        }

        Log.Warn("Boot", "No bootable media found");
        return null;
    }

    /// <summary>Case-insensitive char comparer for drive letters.</summary>
    private sealed class CharComparer : IEqualityComparer<char>
    {
        public static readonly CharComparer OrdinalIgnoreCase = new();
        public bool Equals(char x, char y) => char.ToUpperInvariant(x) == char.ToUpperInvariant(y);
        public int GetHashCode(char obj) => char.ToUpperInvariant(obj).GetHashCode();
    }

    /// <summary>
    /// Reset the machine to initial state.
    /// Mounted disk images are preserved and re-registered.
    /// </summary>
    public void Reset()
    {
        ClearInt15WaitState();
        Memory.Clear();
        Cpu.Reset();
        _running = false;
        _terminated = false;
        _exitCode = 0;

        // Re-register all mounted drives so disk state survives the reset
        RestoreDriveMounts();

        // Populate BIOS Data Area
        PopulateBiosDataArea();

        // Initialize memory manager MCB chain
        // Start after DOS kernel area (0x0600), end at 640K boundary (0x9FFF)
        MemoryManager.Initialize(0x0600, 0x9FFF);

        // Set up initial video mode
        Renderer.SetMode(VideoMode.Text80x25);
        Renderer.Clear(0);

        Log.Info("Machine", "Machine reset (drives preserved)");
    }

    /// <summary>
    /// Re-register all mounted drives with the DOS kernel and BIOS disk service.
    /// Called during Reset() to ensure disk state survives memory/CPU reset.
    /// </summary>
    private void RestoreDriveMounts()
    {
        // Re-register DOS kernel drive providers
        foreach (var kv in _drives)
        {
            Dos.SetDriveProvider(kv.Key, kv.Value);
        }

        // Re-register floppy images with INT 13h, BootSeq, and FDC
        if (Floppy.SlotA.HasDisk && Floppy.SlotA.ImageData != null)
        {
            Disk.RegisterDiskAuto(0x00, Floppy.SlotA.ImageData);
            BootSeq.RegisterDisk(0x00, Floppy.SlotA.ImageData);
            Fdc.LoadDisk(0x00, Floppy.SlotA.ImageData);
        }

        if (Floppy.SlotB.HasDisk && Floppy.SlotB.ImageData != null)
        {
            Disk.RegisterDiskAuto(0x01, Floppy.SlotB.ImageData);
            BootSeq.RegisterDisk(0x01, Floppy.SlotB.ImageData);
            Fdc.LoadDisk(0x01, Floppy.SlotB.ImageData);
        }

        // Re-register hard drive images with INT 13h and BootSeq (C: and beyond)
        foreach (var kv in _rawDiskImages)
        {
            char dl = char.ToUpperInvariant(kv.Key);
            if (dl >= 'C')
            {
                byte biosDrive = (byte)(0x80 + (dl - 'C'));
                Disk.RegisterDiskAuto(biosDrive, kv.Value);
                BootSeq.RegisterDisk(biosDrive, kv.Value);
            }
        }

        if (_drives.Count > 1) // More than just C:
        {
            var driveList = string.Join(", ", _drives.Keys.OrderBy(c => c).Select(c => $"{c}:"));
            Log.Info("Machine", $"Drives restored after reset: {driveList}");
        }
    }

    /// <summary>
    /// Load a binary file (COM or EXE) from byte data and prepare for execution.
    /// Preserves drive mounts across the reset.
    /// </summary>
    /// <param name="data">The binary file content.</param>
    /// <param name="commandLine">Optional command line arguments.</param>
    /// <param name="programPath">Full path of the program (e.g., "A:\RUNME.EXE") for the environment block.</param>
    /// <returns>True if loaded successfully.</returns>
    public bool LoadBinary(byte[] data, string commandLine = "", string? programPath = null)
    {
        // Reset() now automatically preserves and re-registers all drives
        Reset();

        // Set up proper MCB chain so the loaded program has a valid memory
        // block it can resize/free via INT 21h/4Ah-49h.
        string mcbName = "";
        if (!string.IsNullOrEmpty(programPath))
        {
            mcbName = Path.GetFileNameWithoutExtension(programPath).ToUpperInvariant();
            if (mcbName.Length > 8) mcbName = mcbName[..8];
        }
        MemoryManager.SetupForLoadedProgram(0x1000, mcbName);

        // Pass shell environment variables to the loader so the PSP environment
        // block contains the correct COMSPEC, PATH, etc.
        Loader.EnvironmentVariables = Shell?.GetEnvironmentVariables();

        bool loaded = Loader.Load(data, programPath: programPath);
        if (!loaded)
        {
            Log.Error("Machine", "Failed to load binary (unsupported format or too large)");
            return false;
        }

        Dos.CurrentPSP = 0x1000; // Default load segment
        Dos.DtaSegment = 0x1000;
        Dos.DtaOffset = 0x0080;

        if (!string.IsNullOrEmpty(commandLine))
            Loader.SetCommandTail(0x1000, commandLine);

        Log.Info("Machine", $"Binary loaded at 1000:0100 ({data.Length} bytes)");
        return true;
    }

    /// <summary>
    /// Load a disk image file (IMG/RAW) and mount it as drive A:.
    /// Also loads the boot sector into memory for potential execution.
    /// </summary>
    public bool LoadDiskImage(byte[] imageData, char driveLetter = 'A')
    {
        if (!MountDiskImage(driveLetter, imageData))
            return false;

        Log.Info("Machine", $"Disk image mounted as {char.ToUpperInvariant(driveLetter)}:");
        return true;
    }

    /// <summary>
    /// Load a binary file from the stream provider and prepare for execution.
    /// </summary>
    public async Task<bool> LoadBinaryFromPathAsync(string path, string commandLine = "")
    {
        using var stream = await Streams.OpenReadAsync(path);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return LoadBinary(ms.ToArray(), commandLine);
    }

    /// <summary>
    /// Run the emulation loop until the process terminates or is stopped.
    /// Uses cooperative async to allow UI updates between frames.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;
        _terminated = false;
        _poweredOn = true;

        SetCpuActive(true);

        Log.Info("Machine", $"Emulation started at {Cpu.Regs.CS:X4}:{Cpu.Regs.IP:X4}");

        try
        {
            while (_running && !_terminated && !_cts.Token.IsCancellationRequested)
            {
                // INT 15h AH=86h wait gate (non-blocking pause)
                if (IsBiosWaitPending)
                {
                    await Renderer.FlushAsync();
                    await AwaitInt15WaitAsync(_cts.Token).ConfigureAwait(false);
                    continue;
                }

                // Keyboard input wait gate: yield to allow UI to deliver key events
                if (Cpu.IsWaitingForInput)
                {
                    await Renderer.FlushAsync();
                    await AwaitKeypressAsync(_cts.Token).ConfigureAwait(false);
                    continue;
                }

                // In manual clock mode, just yield and wait for manual steps
                if (ManualClockMode)
                {
                    await Renderer.FlushAsync();
                    await Task.Delay(FrameIntervalMs, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                // Execute a batch of instructions
                for (int i = 0; i < InstructionsPerFrame && _running && !_terminated; i++)
                {
                    if (IsBiosWaitPending) break;
                    if (Cpu.IsHalted) break;
                    if (Cpu.IsWaitingForInput) break;

                    if (Log.TraceInstructions)
                    {
                        ushort cs = Cpu.Regs.CS, ip = Cpu.Regs.IP;
                        byte opcode = Memory.ReadByte(cs, ip);
                        Log.Trace("CPU", $"{cs:X4}:{ip:X4}  opcode={opcode:X2}  AX={Cpu.Regs.AX:X4} BX={Cpu.Regs.BX:X4} CX={Cpu.Regs.CX:X4} DX={Cpu.Regs.DX:X4}");
                    }

                    Cpu.Step();
                    OnStep?.Invoke();
                }

                // Flush rendering
                await Renderer.FlushAsync();

                // Yield to allow UI updates
                await Task.Delay(FrameIntervalMs, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        finally
        {
            _running = false;
            SetCpuActive(false);
            Log.Info("Machine", "Emulation stopped");
        }
    }

    /// <summary>
    /// Execute a single CPU instruction (for debugging/stepping).
    /// In manual clock mode, captures memory snapshots and fires OnManualStep with a diff.
    /// </summary>
    public int StepInstruction()
    {
        if (IsBiosWaitPending) return 0;
        if (Cpu.IsWaitingForInput) return 0;
        if (Cpu.IsHalted || _terminated) return 0;

        if (ManualClockMode)
        {
            // Capture snapshot before execution
            uint snapAddr = SnapshotStartAddress;
            if (snapAddr == 0)
                snapAddr = Cpu.Regs.CS * 16u + Cpu.Regs.IP;

            var before = Memory.CaptureSnapshot(snapAddr, SnapshotLength, Cpu.Regs);

            // Log the instruction about to execute
            ushort cs = Cpu.Regs.CS, ip = Cpu.Regs.IP;
            byte opcode = Memory.ReadByte(cs, ip);
            Log.Trace("CPU", $"STEP {cs:X4}:{ip:X4}  opcode={opcode:X2}  AX={Cpu.Regs.AX:X4} BX={Cpu.Regs.BX:X4} CX={Cpu.Regs.CX:X4} DX={Cpu.Regs.DX:X4}");

            int cycles = Cpu.Step();
            OnStep?.Invoke();

            // Capture snapshot after execution
            var after = Memory.CaptureSnapshot(snapAddr, SnapshotLength, Cpu.Regs);
            LastSnapshot = after;

            var diff = before.CompareTo(after);
            OnManualStep?.Invoke(diff);

            return cycles;
        }

        return Cpu.Step();
    }

    /// <summary>
    /// Stop the running emulation.
    /// </summary>
    public void Stop()
    {
        ClearInt15WaitState();
        _running = false;
        Shell.Stop();
        _cts?.Cancel();
    }

    /// <summary>
    /// Resume a pending BIOS INT 15h AH=86h wait early (UI hook).
    /// Useful when the host platform drives wait completion from an async UI timer.
    /// </summary>
    public void ResumeBiosWait()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (_int15WaitSync)
        {
            if (!_int15WaitPending) return;
            _int15WaitPending = false;
            signal = _int15WaitSignal;
            _int15WaitSignal = null;
        }

        signal?.TrySetResult(true);
    }

    /// <summary>
    /// Host/UI clock hook: resumes a pending BIOS INT 15h AH=86h wait only when
    /// the requested wait duration has elapsed on host time.
    /// Returns true when a pending wait was completed by this call.
    /// </summary>
    public bool PumpBiosWaitFromHostClock()
    {
        bool shouldResume;
        lock (_int15WaitSync)
        {
            shouldResume = _int15WaitPending && DateTime.UtcNow >= _int15WaitUntilUtc;
        }

        if (!shouldResume) return false;
        ResumeBiosWait();
        return true;
    }

    private void StartInt15Wait(uint microseconds)
    {
        lock (_int15WaitSync)
        {
            _int15WaitPending = true;
            _int15WaitUntilUtc = DateTime.UtcNow.AddTicks((long)microseconds * 10L); // 1us = 10 ticks
            _int15WaitSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ClearInt15WaitState()
    {
        TaskCompletionSource<bool>? signal = null;
        lock (_int15WaitSync)
        {
            _int15WaitPending = false;
            signal = _int15WaitSignal;
            _int15WaitSignal = null;
        }

        signal?.TrySetCanceled();
    }

    private async Task AwaitInt15WaitAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? signal;
        DateTime waitUntil;

        lock (_int15WaitSync)
        {
            if (!_int15WaitPending) return;
            signal = _int15WaitSignal;
            waitUntil = _int15WaitUntilUtc;
        }

        if (signal == null)
        {
            ResumeBiosWait();
            return;
        }

        var remaining = waitUntil - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ResumeBiosWait();
            return;
        }

        var delayTask = Task.Delay(remaining, cancellationToken);
        var completed = await Task.WhenAny(signal.Task, delayTask).ConfigureAwait(false);
        if (completed == delayTask)
            ResumeBiosWait();
    }

    /// <summary>
    /// Await a keypress when the CPU is spinning on an INT handler that needs input.
    /// Subscribes to KeyDown, then awaits a TCS that completes when a key arrives.
    /// This allows the Blazor UI thread to process JavaScript key events.
    /// </summary>
    private async Task AwaitKeypressAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnKeyDown(DosKeyEventArgs _) => tcs.TrySetResult();

        Events.KeyDown += OnKeyDown;
        try
        {
            // A key may have arrived between the batch-loop break and now
            if (Events.IsKeyAvailable)
            {
                Cpu.IsWaitingForInput = false;
                return;
            }

            using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
            await tcs.Task.ConfigureAwait(false);
            Cpu.IsWaitingForInput = false;
        }
        finally
        {
            Events.KeyDown -= OnKeyDown;
        }
    }

    /// <summary>
    /// Start the built-in command shell (COMMAND.COM emulation).
    /// Performs boot sequence check (A: → B: → C:), then provides a DOS prompt.
    /// Processes CONFIG.SYS and AUTOEXEC.BAT if they exist.
    /// </summary>
    public async Task RunShellAsync(CancellationToken cancellationToken = default)
    {
        Reset();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;

        try
        {
            // Check boot sequence: A: → B: → C:
            var bootDrive = CheckBootSequence();
            if (bootDrive.HasValue && bootDrive.Value != 'C')
            {
                // Set the shell's current drive to the boot floppy
                var provider = GetDriveProvider(bootDrive.Value);
                if (provider != null)
                {
                    Shell.SetCurrentDrive(bootDrive.Value, provider);
                    Log.Info("Boot", $"Shell starting on {bootDrive.Value}:");
                }
            }

            // Process CONFIG.SYS if present
            await ProcessConfigSysAsync();

            // Process AUTOEXEC.BAT if present
            await Shell.ProcessAutoexecAsync(_cts.Token);

            await Shell.RunAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// Parse and process CONFIG.SYS settings.
    /// Recognizes FILES=, BUFFERS=, LASTDRIVE=, DEVICE=, SHELL=, COUNTRY= directives.
    /// </summary>
    private async Task ProcessConfigSysAsync()
    {
        try
        {
            if (!await Streams.ExistsAsync("CONFIG.SYS")) return;

            Log.Info("Machine", "Processing CONFIG.SYS...");
            using var stream = await Streams.OpenReadAsync("CONFIG.SYS");
            using var reader = new StreamReader(stream);
            string content = await reader.ReadToEndAsync();

            foreach (string rawLine in content.Split('\n', '\r'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
                    continue;

                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string key = line[..eq].Trim().ToUpperInvariant();
                string value = line[(eq + 1)..].Trim();

                switch (key)
                {
                    case "FILES":
                        Log.Info("CONFIG.SYS", $"FILES={value}");
                        break;
                    case "BUFFERS":
                        Log.Info("CONFIG.SYS", $"BUFFERS={value}");
                        break;
                    case "LASTDRIVE":
                        Log.Info("CONFIG.SYS", $"LASTDRIVE={value}");
                        break;
                    case "DEVICE":
                    case "DEVICEHIGH":
                        Log.Info("CONFIG.SYS", $"DEVICE={value} (device drivers not yet supported)");
                        break;
                    case "SHELL":
                        Log.Info("CONFIG.SYS", $"SHELL={value}");
                        break;
                    case "COUNTRY":
                        Log.Info("CONFIG.SYS", $"COUNTRY={value}");
                        break;
                    default:
                        Log.Debug("CONFIG.SYS", $"Unrecognized: {line}");
                        break;
                }
            }
        }
        catch (FileNotFoundException)
        {
            // No CONFIG.SYS - that's fine
        }
        catch (Exception ex)
        {
            Log.Warn("Machine", $"Error processing CONFIG.SYS: {ex.Message}");
        }
    }

    /// <summary>Whether the machine is currently running.</summary>
    public bool IsRunning => _running;

    /// <summary>Whether the emulated process has terminated.</summary>
    public bool IsTerminated => _terminated;

    /// <summary>The exit code from the last terminated process.</summary>
    public byte ExitCode => _exitCode;

    // ═══════════════════════════════════════════════════════════════════
    //  POWER ON / OFF
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Power on the machine. Performs a real PC-style boot:
    ///   1. POST — initialise hardware, IVT, BDA
    ///   2. Optionally enter BIOS Setup if DEL was pressed
    ///   3. Try boot order from BIOS Setup (CMOS) or default A: → C: → B:
    ///   4. If a bootable disk is found, load boot sector → native CPU loop
    ///   5. If no bootable disk, fall back to the managed DOS shell
    /// Set <see cref="BootMode"/> before calling to control boot behaviour.
    /// </summary>
    public async Task PowerOnAsync(CancellationToken cancellationToken = default)
    {
        if (_poweredOn) return;
        _poweredOn = true;
        OnPowerStateChanged?.Invoke(true);
        Log.Info("Machine", "Power ON");

        Reset();
        SetCpuActive(true);

        // Determine boot order from BIOS Setup / CMOS
        byte[] bootOrder = BiosSetupScreen.GetBootOrder();

        // Attempt real boot — try each device in order
        bool booted = false;
        foreach (byte drive in bootOrder)
        {
            if (drive == 0xFF) continue; // Disabled

            if (BootSeq.HasDisk(drive) || BootSeq.HasCustomBiosRom)
            {
                Log.Info("Machine", $"Trying boot from drive 0x{drive:X2}...");

                if (BootSeq.BootFromDrive(drive))
                {
                    // CPU is set to 0000:7C00 — run native boot loop
                    Interrupts.NativeBootMode = true;
                    _running = true;
                    _terminated = false;
                    _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    try
                    {
                        await NativeBootLoopAsync(_cts.Token);
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        SetCpuActive(false);
                    }

                    booted = true;
                    break;
                }
            }
        }

        if (!booted)
        {
            // No bootable disk found — fall back to managed DOS shell
            Interrupts.NativeBootMode = false;
            Log.Info("Machine", "No bootable disk — starting managed DOS shell");
            await RunShellAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Load a custom BIOS ROM image. On next boot, this ROM replaces
    /// the emulated BIOS stubs. The ROM is loaded at F000:0000 (up to 64 KB).
    /// </summary>
    /// <param name="romData">Raw BIOS ROM bytes.</param>
    public void LoadBiosRom(byte[] romData)
    {
        BootSeq.LoadBiosRom(romData);
        Log.Info("Machine", $"Custom BIOS ROM loaded ({romData.Length} bytes) — will be active on next boot");
    }

    /// <summary>Remove the custom BIOS ROM so the emulated BIOS stubs are used.</summary>
    public void UnloadBiosRom()
    {
        BootSeq.UnloadBiosRom();
    }

    /// <summary>
    /// Enter BIOS Setup. Can be called during POST (before boot) or at any time
    /// the machine is powered on. Setup is modal — it takes over the screen and
    /// keyboard until the user exits.
    /// </summary>
    public void EnterBiosSetup()
    {
        BiosSetupScreen.Enter();
    }

    /// <summary>
    /// Boot from a raw disk image. Performs a real IBM PC boot sequence:
    /// POST → IVT setup → BDA → load boot sector → execute at 0000:7C00.
    /// The CPU runs native x86 code from that point (no managed DOS layer).
    /// Use this to boot actual MS-DOS from a bootable floppy or hard disk image.
    /// </summary>
    /// <param name="bootDrive">BIOS drive number (0x00=A:, 0x01=B:, 0x80=C:).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task BootFromDiskImageAsync(byte bootDrive = 0x00, CancellationToken cancellationToken = default)
    {
        if (_poweredOn) return;
        _poweredOn = true;
        OnPowerStateChanged?.Invoke(true);
        Log.Info("Machine", $"Raw boot from drive {bootDrive:X2}h");

        Reset();
        SetCpuActive(true);

        // Run IBM PC boot sequence: POST, IVT, BDA, boot sector
        if (!BootSeq.BootFromDrive(bootDrive))
        {
            Log.Error("Machine", "Boot failed — no bootable disk found");
            PowerOff();
            return;
        }

        // CPU is now set to execute at 0000:7C00 with DL = boot drive
        // Run CPU loop executing native instructions
        Interrupts.NativeBootMode = true;
        _running = true;
        _terminated = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await NativeBootLoopAsync(_cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            SetCpuActive(false);
        }
    }

    /// <summary>
    /// Main CPU execution loop for native (raw) boot.
    /// Executes instructions until HLT, triple fault, or cancellation.
    /// Yields periodically to allow UI rendering.
    /// </summary>
    private async Task NativeBootLoopAsync(CancellationToken ct)
    {
        int instructionCount = 0;
        Log.Info("CPU", $"Native boot: starting execution at {Cpu.Regs.CS:X4}:{Cpu.Regs.IP:X4}  DL={Cpu.Regs.DL:X2}h");

        // Flush an initial frame so the user sees POST/boot activity
        await Renderer.FlushAsync();

        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                // If BIOS setup is active, route keys through it instead of CPU
                if (BiosSetupScreen.IsActive)
                {
                    await Renderer.FlushAsync();
                    await Task.Delay(FrameIntervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                // Handle CPU halted — wait for interrupt
                if (Cpu.IsHalted)
                {
                    // Service pending interrupts which may unhalt
                    Pic.ServicePendingInterrupts();
                    if (Cpu.IsHalted)
                    {
                        await Renderer.FlushAsync();
                        await Task.Delay(1, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                Cpu.Step();
                instructionCount++;
                OnStep?.Invoke();

                // Fire pending hardware interrupts
                if (instructionCount % 100 == 0)
                {
                    // Advance PIT
                    int ticks = Pit.AdvanceTicks(100);
                    for (int t = 0; t < ticks; t++)
                        Pic.RaiseIRQ(0);

                    // Service any pending keyboard/timer IRQs
                    Pic.ServicePendingInterrupts();

                    // Check disk activity timeout
                    if (_diskActive && DateTime.UtcNow > _diskActivityUntilUtc)
                        SetDiskActive(false);
                }

                // Yield periodically for rendering
                if (instructionCount % InstructionsPerFrame == 0)
                {
                    await Renderer.FlushAsync();
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error("CPU", $"Exception at {Cpu.Regs.CS:X4}:{Cpu.Regs.IP:X4}: {ex.Message}");
                Log.Error("CPU", $"  AX={Cpu.Regs.AX:X4} BX={Cpu.Regs.BX:X4} CX={Cpu.Regs.CX:X4} DX={Cpu.Regs.DX:X4}");
                Log.Error("CPU", $"  SI={Cpu.Regs.SI:X4} DI={Cpu.Regs.DI:X4} SP={Cpu.Regs.SP:X4} BP={Cpu.Regs.BP:X4}");
                Log.Error("CPU", $"  DS={Cpu.Regs.DS:X4} ES={Cpu.Regs.ES:X4} SS={Cpu.Regs.SS:X4} Flags={Cpu.Regs.Flags}");

                // Write error to video memory so user sees it
                string errMsg = $"CPU FAULT @ {Cpu.Regs.CS:X4}:{Cpu.Regs.IP:X4}: {ex.Message}";
                for (int i = 0; i < errMsg.Length && i < 78; i++)
                {
                    Memory.WriteByte(0xB800, (ushort)(24 * 160 + i * 2), (byte)errMsg[i]);
                    Memory.WriteByte(0xB800, (ushort)(24 * 160 + i * 2 + 1), 0x4F); // White on Red
                }
                await Renderer.FlushAsync();

                _running = false;
            }
        }

        Log.Info("CPU", $"Native boot loop ended after {instructionCount} instructions");
    }

    /// <summary>
    /// Power off the machine. Stops emulation and clears CPU state.
    /// Drive mounts are preserved for next power-on.
    /// </summary>
    public void PowerOff()
    {
        if (!_poweredOn) return;

        Stop();
        _poweredOn = false;
        SetCpuActive(false);
        SetDiskActive(false);

        // Clear video
        Renderer.SetMode(VideoMode.Text80x25);
        Renderer.Clear(0);

        OnPowerStateChanged?.Invoke(false);
        Log.Info("Machine", "Power OFF");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FILE UPLOAD / DOWNLOAD HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Upload a file to a drive's file system, converting the filename to DOS 8.3 format.
    /// Returns the actual DOS filename used on the target drive.
    /// </summary>
    /// <param name="driveLetter">Target drive letter (A-Z).</param>
    /// <param name="filename">Original filename (will be converted to 8.3).</param>
    /// <param name="data">File content bytes.</param>
    /// <returns>The DOS 8.3 filename that was used, or null if upload failed.</returns>
    public async Task<string?> UploadFileToDriveAsync(char driveLetter, string filename, byte[] data)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);
        string dosName = DosFileName.ToShortName(filename);

        NotifyDiskActivity();
        Log.Info("FileIO", $"Uploading '{filename}' → '{dosName}' to {driveLetter}: ({data.Length} bytes)");

        try
        {
            var provider = GetDriveProvider(driveLetter);
            if (provider == null)
            {
                Log.Error("FileIO", $"Drive {driveLetter}: not mounted");
                return null;
            }

            // For floppy drives, we need to update the raw disk image
            var floppySlot = Floppy.GetSlot(driveLetter);
            if (floppySlot != null && floppySlot.HasDisk && floppySlot.ImageData != null)
            {
                // Write directly into the FAT image
                var files = new List<(string Name, byte[] Data)> { (dosName, data) };
                byte[] updatedImage = DiskImageWriter.WriteFilesToExistingImage(floppySlot.ImageData, files);
                if (updatedImage != null)
                {
                    // Re-mount with updated image
                    MountDiskImage(driveLetter, updatedImage, floppySlot.DiskLabel);
                    Log.Info("FileIO", $"File '{dosName}' written to {driveLetter}: floppy image");
                    return dosName;
                }
                else
                {
                    Log.Error("FileIO", $"Failed to write '{dosName}' to floppy image");
                    return null;
                }
            }

            // For non-floppy drives (C: etc.), use the stream provider
            await provider.ImportBinaryAsync(dosName, data);
            Log.Info("FileIO", $"File '{dosName}' uploaded to {driveLetter}:");
            return dosName;
        }
        catch (Exception ex)
        {
            Log.Error("FileIO", $"Upload failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download a file from a drive's file system.
    /// </summary>
    /// <param name="driveLetter">Source drive letter (A-Z).</param>
    /// <param name="filename">DOS filename to download.</param>
    /// <returns>The file content bytes, or null if not found.</returns>
    public async Task<byte[]?> DownloadFileFromDriveAsync(char driveLetter, string filename)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);
        string dosName = DosFileName.ToShortName(filename);

        NotifyDiskActivity();
        Log.Info("FileIO", $"Downloading '{dosName}' from {driveLetter}:");

        try
        {
            var provider = GetDriveProvider(driveLetter);
            if (provider == null)
            {
                Log.Error("FileIO", $"Drive {driveLetter}: not mounted");
                return null;
            }

            if (!await provider.ExistsAsync(dosName))
            {
                Log.Warn("FileIO", $"File '{dosName}' not found on {driveLetter}:");
                return null;
            }

            using var stream = await provider.OpenReadAsync(dosName);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            byte[] data = ms.ToArray();
            Log.Info("FileIO", $"Downloaded '{dosName}' from {driveLetter}: ({data.Length} bytes)");
            return data;
        }
        catch (Exception ex)
        {
            Log.Error("FileIO", $"Download failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// List files on a drive.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListDriveFilesAsync(char driveLetter)
    {
        driveLetter = char.ToUpperInvariant(driveLetter);
        var provider = GetDriveProvider(driveLetter);
        if (provider == null) return null;

        try
        {
            return await provider.ListEntriesAsync("");
        }
        catch (Exception ex)
        {
            Log.Error("FileIO", $"Failed to list {driveLetter}: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ACTIVITY TRACKING
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Signal that a disk operation occurred. Automatically turns off after a short delay.
    /// Called internally from DosKernel file operations and INT 13h.
    /// </summary>
    public void NotifyDiskActivity()
    {
        _diskActivityUntilUtc = DateTime.UtcNow.AddMilliseconds(150);
        SetDiskActive(true);
    }

    /// <summary>
    /// Pump activity LEDs — call from UI timer to auto-deactivate disk LED.
    /// </summary>
    public void PumpActivityLeds()
    {
        if (_diskActive && DateTime.UtcNow >= _diskActivityUntilUtc)
        {
            SetDiskActive(false);
        }
    }

    private void SetCpuActive(bool active)
    {
        if (_cpuActive != active)
        {
            _cpuActive = active;
            OnCpuActivity?.Invoke(active);
        }
    }

    private void SetDiskActive(bool active)
    {
        if (_diskActive != active)
        {
            _diskActive = active;
            OnDiskActivity?.Invoke(active);
        }
    }

    /// <summary>INT 15h — System services (simplified).</summary>
    private void HandleInt15()
    {
        switch (Cpu.Regs.AH)
        {
            case 0x86: // Wait (microseconds in CX:DX)
            {
                uint microseconds = (uint)(Cpu.Regs.CX << 16) | Cpu.Regs.DX;
                // Emulate BIOS wait without blocking host runtime threads.
                // Execution resumes when timeout elapses or when UI calls ResumeBiosWait().
                if (microseconds > 0)
                    StartInt15Wait(microseconds);
                Cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            }
            case 0x87: // Extended memory block move
                Cpu.Regs.AH = 0x86; // Not supported
                Cpu.Regs.Flags |= CpuFlags.Carry;
                break;
            case 0x88: // Get extended memory size
                Cpu.Regs.AX = 0; // No extended memory in 8086
                Cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            case 0xC0: // Get system configuration
                Cpu.Regs.AH = 0x86; // Not supported
                Cpu.Regs.Flags |= CpuFlags.Carry;
                break;
            case 0x41: // Wait on external event
                Cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            default:
                Log.Debug("INT15", $"Unhandled AH={Cpu.Regs.AH:X2}h");
                Cpu.Regs.Flags |= CpuFlags.Carry;
                break;
        }
    }

    /// <summary>
    /// Register stub I/O port handlers for common PC hardware that programs may probe.
    /// </summary>
    private void RegisterHardwarePorts()
    {
        // DMA controller (8237A) — already registered by Dma.RegisterPorts()
        // DMA 16-bit (AT) — secondary controller stubs
        Ports.Register(0xC0, 0xDF, port => 0, (port, val) => { });

        // CMOS/RTC — already registered by Cmos.RegisterPorts()

        // Keyboard controller (8042) — ports 0x60, 0x64
        Ports.Register(0x60, port => KeyboardIrq.ReadPort60(), (port, val) => { });
        // Port 0x61 — already registered by Speaker.RegisterPorts()
        Ports.Register(0x64, port => 0x14, (port, val) => { }); // Status: input buffer empty, system flag set

        // Serial ports COM1-COM4 (stubs)
        Ports.Register(0x3F8, 0x3FF, port => 0x60, (port, val) => { }); // COM1 — LSR bit 5+6 set (THRE+TEMT)
        Ports.Register(0x2F8, 0x2FF, port => 0x60, (port, val) => { }); // COM2

        // Parallel port LPT1 (stub)
        Ports.Register(0x378, 0x37F, port => 0xDF, (port, val) => { }); // Status: bit 7=not busy

        // CGA/EGA/VGA registers — real register state tracking
        CgaRegisters.RegisterPorts(Ports);

        // Game port (joystick) — port 0x201
        Ports.Register(0x201, port => 0xFF, (port, val) => { }); // All buttons released

        // FDC — already registered by Fdc.RegisterPorts()
    }

    /// <summary>
    /// Populate the BIOS Data Area (0040:0000 - 0040:00FF) with initial values.
    /// Called during Reset().
    /// </summary>
    private void PopulateBiosDataArea()
    {
        // Equipment word at 0040:0010
        // Bits 7-6: number of floppy drives minus 1 (00=1, 01=2)
        // Bits 5-4: initial video mode (10=80x25 color)
        // Bit 0: boot from floppy
        // Always report 2 floppy drives (standard PC has A: and B: bays)
        // to prevent DOS phantom B: aliasing where B: mirrors A:
        ushort equipmentWord = 0x0061; // 80x25 color, 2 floppies
        Memory.WriteWord(0x0040, 0x0010, equipmentWord);

        // Base memory size in KB at 0040:0013
        Memory.WriteWord(0x0040, 0x0013, 640);

        // Video mode at 0040:0049
        Memory.WriteByte(0x0040, 0x0049, 0x03); // Mode 3 (80x25 text)

        // Number of columns at 0040:004A
        Memory.WriteWord(0x0040, 0x004A, 80);

        // Video page size at 0040:004C
        Memory.WriteWord(0x0040, 0x004C, 4000); // 80*25*2

        // Active display page at 0040:0062
        Memory.WriteByte(0x0040, 0x0062, 0x00);

        // Cursor positions for pages 0-7 (0040:0050-005F)
        for (int i = 0; i < 16; i++)
            Memory.WriteByte(0x0040, (ushort)(0x0050 + i), 0);

        // Video port base at 0040:0063
        Memory.WriteWord(0x0040, 0x0063, 0x03D4); // Color CRT

        // Number of rows minus one at 0040:0084
        Memory.WriteByte(0x0040, 0x0084, 24);

        // Character height at 0040:0085
        Memory.WriteWord(0x0040, 0x0085, 16);

        // Keyboard buffer head/tail pointers at 0040:001A and 0040:001C
        Memory.WriteWord(0x0040, 0x001A, 0x001E); // Head
        Memory.WriteWord(0x0040, 0x001C, 0x001E); // Tail (same = empty)

        // Keyboard buffer start/end at 0040:0080/0082
        Memory.WriteWord(0x0040, 0x0080, 0x001E);
        Memory.WriteWord(0x0040, 0x0082, 0x003E);

        // Shift key flags at 0040:0017
        Memory.WriteByte(0x0040, 0x0017, 0x00);

        // Number of hard drives at 0040:0075
        Memory.WriteByte(0x0040, 0x0075, (_drives.ContainsKey('C') ? (byte)1 : (byte)0));

        // Timer tick count at 0040:006C (set to approximate current time)
        var midnight = DateTime.Today;
        uint ticks = (uint)((DateTime.Now - midnight).TotalSeconds * 18.2065);
        Memory.WriteWord(0x0040, 0x006C, (ushort)(ticks & 0xFFFF));
        Memory.WriteWord(0x0040, 0x006E, (ushort)((ticks >> 16) & 0xFFFF));

        // Number of floppy drives at 0040:0010 is in equipment word (already set)
        // COM port base addresses at 0040:0000
        Memory.WriteWord(0x0040, 0x0000, 0x03F8); // COM1
        Memory.WriteWord(0x0040, 0x0002, 0x02F8); // COM2
        Memory.WriteWord(0x0040, 0x0004, 0x0000); // COM3 (not present)
        Memory.WriteWord(0x0040, 0x0006, 0x0000); // COM4 (not present)

        // LPT port base addresses at 0040:0008
        Memory.WriteWord(0x0040, 0x0008, 0x0378); // LPT1
        Memory.WriteWord(0x0040, 0x000A, 0x0000); // LPT2
        Memory.WriteWord(0x0040, 0x000C, 0x0000); // LPT3

        // --- ROM Font & IVT vectors for character generator ---
        // Install 8×8 CP437 font into ROM area at F000:F000
        uint fontPhysical = ((uint)CgaFont8x8.RomSegment << 4) + CgaFont8x8.RomOffset;
        for (int i = 0; i < CgaFont8x8.FontData.Length; i++)
            Memory.WriteByte(fontPhysical + (uint)i, CgaFont8x8.FontData[i]);

        // IVT 0x1F — Graphics character bitmap table (upper 128 chars, 80h-FFh)
        // Points to offset 0x400 (128*8) into the font for chars 128-255
        Memory.WriteWord(0x0000, 0x1F * 4, (ushort)(CgaFont8x8.RomOffset + 0x400));
        Memory.WriteWord(0x0000, 0x1F * 4 + 2, CgaFont8x8.RomSegment);

        // IVT 0x43 — EGA/VGA current font pointer (full 256-char table)
        Memory.WriteWord(0x0000, 0x43 * 4, CgaFont8x8.RomOffset);
        Memory.WriteWord(0x0000, 0x43 * 4 + 2, CgaFont8x8.RomSegment);
    }

    /// <summary>INT 23h — Ctrl-C handler. Default: terminate process.</summary>
    private void HandleInt23()
    {
        Log.Info("INT23", "Ctrl-C received — terminating process");
        Dos.Terminate(0);
    }

    /// <summary>INT 24h — Critical error handler. Default: fail the DOS call.</summary>
    private void HandleInt24()
    {
        // AH = error type (0=read, 1=write), AL = drive number
        // Return AL = 3 (fail the system call) — safest default
        Cpu.Regs.AL = 0x03;
        Log.Warn("INT24", $"Critical error on drive {Cpu.Regs.AL}: error type {Cpu.Regs.AH:X2}h — failing call");
    }

    private static byte ToBcd(int value) =>
        (byte)(((value / 10) << 4) | (value % 10));
}
