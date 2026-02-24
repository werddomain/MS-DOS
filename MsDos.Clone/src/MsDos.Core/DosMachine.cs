using MsDos.Core.Cpu;
using MsDos.Core.Dos;
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

    /// <summary>Drive letter → stream provider mapping. Populated by mounting disk images or directories.</summary>
    private readonly Dictionary<char, IStreamProvider> _drives = new(CharComparer.OrdinalIgnoreCase);

    /// <summary>Drive letter → raw disk image bytes. Kept for re-registration with INT 13h after reset.</summary>
    private readonly Dictionary<char, byte[]> _rawDiskImages = new(CharComparer.OrdinalIgnoreCase);

    private bool _running;
    private bool _terminated;
    private byte _exitCode;
    private CancellationTokenSource? _cts;

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

        // CGA/VGA register state tracker (must be created before RegisterHardwarePorts)
        CgaRegisters = new CgaRegisterState();

        // Register common PC hardware ports (including CGA register tracking)
        RegisterHardwarePorts();

        Interrupts = new InterruptController(Cpu, Memory);
        Video = new BiosVideoService(Cpu, Memory, renderer);
        Keyboard = new BiosKeyboardService(Cpu, events, Memory);
        BiosMisc = new BiosMiscService(Cpu, Memory);
        Disk = new BiosDiskService(Cpu, Memory, Log);
        Mouse = new BiosMouseService(Cpu, events);
        Timer = new BiosTimerService(Cpu, Memory, Pic);
        KeyboardIrq = new BiosKeyboardIrqHandler(Memory, Pic);
        Dos = new DosKernel(Cpu, Memory, streams, events, renderer, Video, Log);
        Dos.SetMemoryManager(MemoryManager);
        Loader = new BinaryLoader(Memory, Cpu);
        Shell = new CommandShell(Dos, Memory, Cpu, streams, events, Video, renderer, Log, this);
        Floppy = new FloppyDriveController(Log);

        // Mount C: as the default stream provider
        _drives['C'] = streams;
        Dos.SetDriveProvider('C', streams);

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
        Events.KeyDown += e => KeyboardIrq.OnKeyEvent(e);

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

        // Re-register floppy images with INT 13h
        if (Floppy.SlotA.HasDisk && Floppy.SlotA.ImageData != null)
        {
            Disk.RegisterDiskAuto(0x00, Floppy.SlotA.ImageData);
        }

        if (Floppy.SlotB.HasDisk && Floppy.SlotB.ImageData != null)
        {
            Disk.RegisterDiskAuto(0x01, Floppy.SlotB.ImageData);
        }

        // Re-register hard drive images with INT 13h (C: and beyond)
        foreach (var kv in _rawDiskImages)
        {
            char dl = char.ToUpperInvariant(kv.Key);
            if (dl >= 'C')
            {
                byte biosDrive = (byte)(0x80 + (dl - 'C'));
                Disk.RegisterDiskAuto(biosDrive, kv.Value);
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
        // DMA controller (8237A) — channels 0-3 (ports 0x00-0x0F, 0xC0-0xDF)
        // Many programs probe these; return 0 for reads, ignore writes
        Ports.Register(0x00, 0x0F, port => 0, (port, val) => { });
        Ports.Register(0x80, 0x8F, port => 0, (port, val) => { }); // DMA page registers
        Ports.Register(0xC0, 0xDF, port => 0, (port, val) => { }); // DMA 16-bit (AT)

        // CMOS/RTC (ports 0x70-0x71)
        byte _cmosIndex = 0;
        Ports.Register(0x70, null, (port, val) => { _cmosIndex = (byte)(val & 0x7F); });
        Ports.Register(0x71, port =>
        {
            var now = DateTime.Now;
            return _cmosIndex switch
            {
                0x00 => ToBcd(now.Second),
                0x02 => ToBcd(now.Minute),
                0x04 => ToBcd(now.Hour),
                0x06 => ToBcd((int)now.DayOfWeek + 1),
                0x07 => ToBcd(now.Day),
                0x08 => ToBcd(now.Month),
                0x09 => ToBcd(now.Year % 100),
                0x0A => 0x26, // Status A: update not in progress
                0x0B => 0x02, // Status B: 24hr, BCD
                0x0C => 0x00, // Status C: no pending interrupt
                0x0D => 0x80, // Status D: battery OK
                0x32 => ToBcd(now.Year / 100), // Century
                0x10 => 0x44, // Floppy drive types: both 1.44M
                0x14 => 0x0D, // Equipment byte
                0x15 => 0x80, // Base memory low (640K)
                0x16 => 0x02, // Base memory high
                _ => 0
            };
        }, (port, val) => { });

        // Keyboard controller (8042) — ports 0x60, 0x61, 0x64
        Ports.Register(0x60, port => KeyboardIrq.ReadPort60(), (port, val) => { }); // Keyboard data (scancode)

        // Port 0x61 — System Control Port B
        //   Bit 0: PIT Channel 2 gate enable
        //   Bit 1: Speaker data enable
        //   Bit 4: Toggles with each refresh cycle (read)
        //   Bit 5: Channel 2 output (from PIT)
        byte _port61 = 0;
        bool _refreshToggle = false;
        Ports.Register(0x61, port =>
        {
            _refreshToggle = !_refreshToggle;
            byte val = _port61;
            if (_refreshToggle) val |= 0x10; // Bit 4 — refresh toggle
            if (Pit.Channel2Output) val |= 0x20; // Bit 5 — PIT ch2 output
            return val;
        }, (port, val) => { _port61 = val; });

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
    }

    /// <summary>
    /// Populate the BIOS Data Area (0040:0000 - 0040:00FF) with initial values.
    /// Called during Reset().
    /// </summary>
    private void PopulateBiosDataArea()
    {
        // Equipment word at 0040:0010
        Memory.WriteWord(0x0040, 0x0010, 0x0021); // Color 80x25, 1 floppy

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
