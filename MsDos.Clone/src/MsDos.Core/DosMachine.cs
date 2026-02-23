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
    public DosKernel Dos { get; }
    public BinaryLoader Loader { get; }
    public CommandShell Shell { get; }
    public EmulatorLog Log { get; }

    public IGraphicsRenderer Renderer { get; }
    public IEventRegistry Events { get; }
    public IStreamProvider Streams { get; }

    /// <summary>Drive letter → stream provider mapping. Populated by mounting disk images or directories.</summary>
    private readonly Dictionary<char, IStreamProvider> _drives = new(CharComparer.OrdinalIgnoreCase);

    private bool _running;
    private bool _terminated;
    private byte _exitCode;
    private CancellationTokenSource? _cts;

    /// <summary>Fired when the emulated process terminates.</summary>
    public event Action<byte>? OnProcessExit;

    /// <summary>Fired on each instruction step (for debugging).</summary>
    public event Action? OnStep;

    /// <summary>Maximum instructions per frame (throttle).</summary>
    public int InstructionsPerFrame { get; set; } = 50000;

    /// <summary>Target frame interval in milliseconds.</summary>
    public int FrameIntervalMs { get; set; } = 16; // ~60 FPS

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
        Interrupts = new InterruptController(Cpu, Memory);
        Video = new BiosVideoService(Cpu, Memory, renderer);
        Keyboard = new BiosKeyboardService(Cpu, events);
        BiosMisc = new BiosMiscService(Cpu);
        Dos = new DosKernel(Cpu, Memory, streams, events, renderer, Video, Log);
        Loader = new BinaryLoader(Memory, Cpu);
        Shell = new CommandShell(Dos, Memory, Cpu, streams, events, Video, renderer, Log, this);

        // Mount C: as the default stream provider
        _drives['C'] = streams;

        // Register interrupt handlers
        Interrupts.RegisterHandler(0x10, Video.Handle);
        Interrupts.RegisterHandler(0x16, Keyboard.Handle);
        Interrupts.RegisterHandler(0x11, BiosMisc.HandleInt11);
        Interrupts.RegisterHandler(0x12, BiosMisc.HandleInt12);
        Interrupts.RegisterHandler(0x1A, BiosMisc.HandleInt1A);
        Interrupts.RegisterHandler(0x20, () => Dos.Terminate(0)); // INT 20h = terminate
        Interrupts.RegisterHandler(0x21, Dos.Handle);

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
    /// </summary>
    /// <param name="driveLetter">Drive letter (A-Z).</param>
    /// <param name="imageData">Raw disk image bytes.</param>
    /// <returns>True if the image was loaded and mounted.</returns>
    public bool MountDiskImage(char driveLetter, byte[] imageData)
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

    /// <summary>Case-insensitive char comparer for drive letters.</summary>
    private sealed class CharComparer : IEqualityComparer<char>
    {
        public static readonly CharComparer OrdinalIgnoreCase = new();
        public bool Equals(char x, char y) => char.ToUpperInvariant(x) == char.ToUpperInvariant(y);
        public int GetHashCode(char obj) => char.ToUpperInvariant(obj).GetHashCode();
    }

    /// <summary>
    /// Reset the machine to initial state.
    /// </summary>
    public void Reset()
    {
        Memory.Clear();
        Cpu.Reset();
        _running = false;
        _terminated = false;
        _exitCode = 0;

        // Set up initial video mode
        Renderer.SetMode(VideoMode.Text80x25);
        Renderer.Clear(0);

        Log.Info("Machine", "Machine reset");
    }

    /// <summary>
    /// Load a binary file (COM or EXE) from byte data and prepare for execution.
    /// </summary>
    /// <param name="data">The binary file content.</param>
    /// <param name="commandLine">Optional command line arguments.</param>
    /// <returns>True if loaded successfully.</returns>
    public bool LoadBinary(byte[] data, string commandLine = "")
    {
        Reset();

        bool loaded = Loader.Load(data);
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
                // Execute a batch of instructions
                for (int i = 0; i < InstructionsPerFrame && _running && !_terminated; i++)
                {
                    if (Cpu.IsHalted) break;

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
    /// </summary>
    public int StepInstruction()
    {
        if (Cpu.IsHalted || _terminated) return 0;
        return Cpu.Step();
    }

    /// <summary>
    /// Stop the running emulation.
    /// </summary>
    public void Stop()
    {
        _running = false;
        Shell.Stop();
        _cts?.Cancel();
    }

    /// <summary>
    /// Start the built-in command shell (COMMAND.COM emulation).
    /// Provides a DOS prompt with DIR, TYPE, VER, CLS, and other commands.
    /// Processes CONFIG.SYS and AUTOEXEC.BAT if they exist.
    /// </summary>
    public async Task RunShellAsync(CancellationToken cancellationToken = default)
    {
        Reset();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;

        try
        {
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
}
