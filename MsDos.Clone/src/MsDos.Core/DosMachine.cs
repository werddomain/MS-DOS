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

    public IGraphicsRenderer Renderer { get; }
    public IEventRegistry Events { get; }
    public IStreamProvider Streams { get; }

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

        Memory = new MemoryBus();
        Cpu = new Cpu8086(Memory);
        Interrupts = new InterruptController(Cpu, Memory);
        Video = new BiosVideoService(Cpu, Memory, renderer);
        Keyboard = new BiosKeyboardService(Cpu, events);
        BiosMisc = new BiosMiscService(Cpu);
        Dos = new DosKernel(Cpu, Memory, streams, events, renderer, Video);
        Loader = new BinaryLoader(Memory, Cpu);
        Shell = new CommandShell(Dos, Memory, Cpu, streams, events, Video, renderer);

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
        if (!loaded) return false;

        Dos.CurrentPSP = 0x1000; // Default load segment
        Dos.DtaSegment = 0x1000;
        Dos.DtaOffset = 0x0080;

        if (!string.IsNullOrEmpty(commandLine))
            Loader.SetCommandTail(0x1000, commandLine);

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

        try
        {
            while (_running && !_terminated && !_cts.Token.IsCancellationRequested)
            {
                // Execute a batch of instructions
                for (int i = 0; i < InstructionsPerFrame && _running && !_terminated; i++)
                {
                    if (Cpu.IsHalted) break;
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
    /// </summary>
    public async Task RunShellAsync(CancellationToken cancellationToken = default)
    {
        Reset();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;

        try
        {
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

    /// <summary>Whether the machine is currently running.</summary>
    public bool IsRunning => _running;

    /// <summary>Whether the emulated process has terminated.</summary>
    public bool IsTerminated => _terminated;

    /// <summary>The exit code from the last terminated process.</summary>
    public byte ExitCode => _exitCode;
}
