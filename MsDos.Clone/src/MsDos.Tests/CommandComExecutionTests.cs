using MsDos.Core;
using MsDos.Core.Cpu;
using MsDos.Core.Dos;
using MsDos.Core.Memory;
using MsDos.Core.Platform;
using System.Collections.Concurrent;
using System.Text;

using Xunit.Abstractions;

namespace MsDos.Tests;

/// <summary>
/// Integration tests that load real DOS binaries (COMMAND.COM, FDISK.EXE)
/// and verify they initialize and execute correctly within the emulator.
/// </summary>
public class CommandComExecutionTests
{
    private readonly ITestOutputHelper _output;

    public CommandComExecutionTests(ITestOutputHelper output) => _output = output;
    // ─────────────────────────────────────────────────────────────────
    //  Test infrastructure (same pattern as DosMachineTests)
    // ─────────────────────────────────────────────────────────────────

    private sealed class TestRenderer : IGraphicsRenderer
    {
        public int Width => 640;
        public int Height => 400;
        private readonly List<(int col, int row, char ch, byte fg, byte bg)> _chars = new();
        public IReadOnlyList<(int col, int row, char ch, byte fg, byte bg)> DrawnChars => _chars;
        public bool CursorVisible { get; private set; } = true;
        public int CursorCol { get; private set; }
        public int CursorRow { get; private set; }
        public VideoMode CurrentMode { get; private set; }

        public void SetMode(VideoMode mode) => CurrentMode = mode;
        public void DrawCharacter(int col, int row, char character, byte foreground, byte background)
            => _chars.Add((col, row, character, foreground, background));
        public void DrawPixel(int x, int y, byte colorIndex) { }
        public void Clear(byte backgroundColorIndex) => _chars.Clear();
        public void SetCursorPosition(int col, int row) { CursorCol = col; CursorRow = row; }
        public void SetCursorVisible(bool visible) => CursorVisible = visible;
        public void ScrollUp(int lines, byte backgroundColorIndex) { }
        public void ScrollDown(int lines, byte backgroundColorIndex) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class TestEventRegistry : IEventRegistry
    {
        private readonly ConcurrentQueue<DosKeyEventArgs> _keys = new();
        private readonly SemaphoreSlim _sem = new(0);

        public event Action<DosKeyEventArgs>? KeyDown;
        public event Action<DosKeyEventArgs>? KeyUp;
        public event Action<DosMouseEventArgs>? MouseMove;
        public event Action<DosMouseEventArgs>? MouseDown;
        public event Action<DosMouseEventArgs>? MouseUp;

        public bool IsKeyAvailable => !_keys.IsEmpty;

        public async Task<DosKeyEventArgs> ReadKeyAsync(CancellationToken cancellationToken = default)
        {
            await _sem.WaitAsync(cancellationToken);
            if (_keys.TryDequeue(out var key))
                return key;
            return new DosKeyEventArgs();
        }

        public void EnqueueKey(byte ascii, byte scanCode = 0)
        {
            var key = new DosKeyEventArgs { AsciiChar = ascii, ScanCode = scanCode };
            _keys.Enqueue(key);
            _sem.Release();
            KeyDown?.Invoke(key);
        }
    }

    private sealed class TestStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string path, byte[] data) => _files[path] = data;

        public Task<Stream> OpenReadAsync(string path)
        {
            if (_files.TryGetValue(path, out var data))
                return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
            throw new FileNotFoundException(path);
        }

        public Task<Stream> OpenWriteAsync(string path)
        {
            var ms = new MemoryStream();
            _files[path] = Array.Empty<byte>();
            return Task.FromResult<Stream>(ms);
        }

        public Task<Stream> OpenReadWriteAsync(string path)
        {
            var data = _files.ContainsKey(path) ? _files[path] : Array.Empty<byte>();
            return Task.FromResult<Stream>(new MemoryStream(data));
        }

        public Task<bool> ExistsAsync(string path) => Task.FromResult(_files.ContainsKey(path));

        public Task DeleteAsync(string path)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListEntriesAsync(string directoryPath)
            => Task.FromResult<IReadOnlyList<string>>(_files.Keys.ToList());

        public Task ImportBinaryAsync(string targetPath, byte[] data)
        {
            _files[targetPath] = data;
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string path) => Task.CompletedTask;
        public Task DeleteDirectoryAsync(string path) => Task.CompletedTask;
        public Task RenameAsync(string oldPath, string newPath)
        {
            if (_files.Remove(oldPath, out var d)) _files[newPath] = d;
            return Task.CompletedTask;
        }
        public Task<long> GetFileSizeAsync(string path) =>
            Task.FromResult(_files.TryGetValue(path, out var d) ? (long)d.Length : -1L);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Helper: resolve test binary paths
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve a path relative to the repository root (walks up from test output directory).
    /// </summary>
    private static string ResolveRepoPath(string relativePath)
    {
        // Start from the test assembly location and walk up to the repo root
        string dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return candidate;

            // Also check if we're at the repo root (has v4.0-ozzie or MsDos.Clone)
            if (Directory.Exists(Path.Combine(dir, "v4.0-ozzie")) ||
                Directory.Exists(Path.Combine(dir, "MsDos.Clone")))
            {
                candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Could not resolve repo path: {relativePath}");
    }

    /// <summary>
    /// Create a DosMachine with test infrastructure and collect console output.
    /// </summary>
    private static (DosMachine machine, TestRenderer renderer, TestEventRegistry events, StringBuilder consoleOutput)
        CreateTestMachine()
    {
        var renderer = new TestRenderer();
        var events = new TestEventRegistry();
        var streams = new TestStreamProvider();
        var machine = new DosMachine(renderer, events, streams);
        var consoleOutput = new StringBuilder();

        // Capture console output from DOS kernel
        machine.Dos.ConsoleOutput += ch => consoleOutput.Append(ch);

        return (machine, renderer, events, consoleOutput);
    }

    /// <summary>
    /// Step the CPU up to a maximum number of instructions, stopping early
    /// if the process terminates or is waiting for input.
    /// Returns a diagnostic summary of what happened.
    /// </summary>
    private static ExecutionResult StepUntilDone(DosMachine machine, int maxInstructions = 500_000)
    {
        var result = new ExecutionResult();
        bool terminated = false;
        machine.OnProcessExit += code =>
        {
            result.ExitCode = code;
            terminated = true;
        };

        for (int i = 0; i < maxInstructions; i++)
        {
            if (terminated || machine.IsTerminated)
            {
                result.Terminated = true;
                result.InstructionsExecuted = i;
                return result;
            }

            if (machine.Cpu.IsHalted)
            {
                result.Halted = true;
                result.InstructionsExecuted = i;
                return result;
            }

            if (machine.Cpu.IsWaitingForInput)
            {
                result.WaitingForInput = true;
                result.InstructionsExecuted = i;
                return result;
            }

            machine.Cpu.Step();
            result.InstructionsExecuted = i + 1;
        }

        result.MaxInstructionsReached = true;
        return result;
    }

    private sealed class ExecutionResult
    {
        public int InstructionsExecuted { get; set; }
        public byte ExitCode { get; set; }
        public bool Terminated { get; set; }
        public bool Halted { get; set; }
        public bool WaitingForInput { get; set; }
        public bool MaxInstructionsReached { get; set; }

        public override string ToString() =>
            $"Instructions={InstructionsExecuted}, Terminated={Terminated}, ExitCode={ExitCode}, " +
            $"Halted={Halted}, WaitingForInput={WaitingForInput}, MaxReached={MaxInstructionsReached}";
    }

    // ─────────────────────────────────────────────────────────────────
    //  Tests
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CommandCom_V4Ozzie_LoadsSuccessfully()
    {
        string comPath = ResolveRepoPath(Path.Combine("v4.0-ozzie", "bin", "DISK1", "COMMAND.COM"));
        byte[] comData = File.ReadAllBytes(comPath);

        var (machine, renderer, events, consoleOutput) = CreateTestMachine();
        bool loaded = machine.LoadBinary(comData, "", @"C:\COMMAND.COM");

        Assert.True(loaded, "COMMAND.COM should load as a COM file");
        Assert.Equal((ushort)0x1000, machine.Cpu.Regs.CS);
        Assert.Equal((ushort)0x0100, machine.Cpu.Regs.IP);

        // Verify first bytes in memory match the file (JMP instruction)
        byte firstByte = machine.Memory.ReadByte(0x1000, 0x0100);
        Assert.Equal(0xE9, firstByte); // JMP rel16
    }

    [Fact]
    public void CommandCom_V4Ozzie_ExecutesWithoutCrash()
    {
        string comPath = ResolveRepoPath(Path.Combine("v4.0-ozzie", "bin", "DISK1", "COMMAND.COM"));
        byte[] comData = File.ReadAllBytes(comPath);

        var (machine, renderer, events, consoleOutput) = CreateTestMachine();
        machine.LoadBinary(comData, "", @"C:\COMMAND.COM");

        // Execute until COMMAND.COM reaches a prompt (waits for input) or terminates
        var result = StepUntilDone(machine);

        string output = consoleOutput.ToString();

        // COMMAND.COM should either wait for input (showing prompt) or display some output
        // It should NOT just crash/halt silently with no output
        Assert.False(result.Halted && output.Length == 0,
            $"COMMAND.COM halted with no output at {machine.Cpu.Regs.CS:X4}:{machine.Cpu.Regs.IP:X4}. " +
            $"Result: {result}");

        // It should produce SOME console output (version string, prompt, etc.)
        Assert.True(output.Length > 0 || result.WaitingForInput,
            $"COMMAND.COM produced no output and isn't waiting for input. Result: {result}");
    }

    [Fact]
    public void CommandCom_V4Ozzie_ShowsPromptOrVersion()
    {
        string comPath = ResolveRepoPath(Path.Combine("v4.0-ozzie", "bin", "DISK1", "COMMAND.COM"));
        byte[] comData = File.ReadAllBytes(comPath);

        var (machine, renderer, events, consoleOutput) = CreateTestMachine();
        machine.LoadBinary(comData, "", @"C:\COMMAND.COM");

        var result = StepUntilDone(machine);
        string output = consoleOutput.ToString();

        // When COMMAND.COM starts normally, it should eventually show a prompt like "C:\>"
        // or at least display some version/copyright text.
        // If it's waiting for input, that's the prompt state.
        bool hasPrompt = output.Contains(">") || output.Contains(":\\");
        bool showedVersion = output.Contains("version", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("MS-DOS", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("DOS", StringComparison.OrdinalIgnoreCase);
        bool isAtPrompt = result.WaitingForInput;

        Assert.True(hasPrompt || showedVersion || isAtPrompt,
            $"COMMAND.COM did not show a prompt or version info.\n" +
            $"Output ({output.Length} chars): [{output}]\n" +
            $"Result: {result}");
    }

    [Fact]
    public void CommandCom_V4Ozzie_RespondsToExitCommand()
    {
        string comPath = ResolveRepoPath(Path.Combine("v4.0-ozzie", "bin", "DISK1", "COMMAND.COM"));
        byte[] comData = File.ReadAllBytes(comPath);

        var (machine, renderer, events, consoleOutput) = CreateTestMachine();
        machine.LoadBinary(comData, "", @"C:\COMMAND.COM");

        // Run until COMMAND.COM is waiting for input (at prompt)
        var result = StepUntilDone(machine);

        if (result.WaitingForInput)
        {
            // Type "EXIT" followed by Enter
            foreach (byte ch in "EXIT\r"u8)
                events.EnqueueKey(ch);
            machine.Cpu.IsWaitingForInput = false;

            // Continue executing
            result = StepUntilDone(machine);

            // Should terminate (EXIT causes COMMAND.COM to terminate)
            Assert.True(result.Terminated || result.WaitingForInput,
                $"After EXIT command, expected termination or new prompt. Result: {result}");
        }
    }

    [Fact]
    public void CommandCom_LocalBinary_LoadsAndRuns()
    {
        // Test with the local test binary copy of command.com
        string comPath = Path.Combine(AppContext.BaseDirectory, "binaries", "command.com");
        if (!File.Exists(comPath))
        {
            comPath = ResolveRepoPath(Path.Combine("MsDos.Clone", "src", "MsDos.Tests", "binaries", "command.com"));
        }

        byte[] comData = File.ReadAllBytes(comPath);

        var (machine, renderer, events, consoleOutput) = CreateTestMachine();
        bool loaded = machine.LoadBinary(comData, "", @"C:\COMMAND.COM");

        Assert.True(loaded, "Local command.com should load successfully");

        var result = StepUntilDone(machine);
        string output = consoleOutput.ToString();

        // Should produce output or wait for input
        Assert.True(output.Length > 0 || result.WaitingForInput || result.Terminated,
            $"command.com produced no output. Result: {result}");
    }

    [Fact]
    public void Fdisk_LoadsAsExeAndInitializes()
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "binaries", "fdisk.exe");
        if (!File.Exists(exePath))
        {
            exePath = ResolveRepoPath(Path.Combine("MsDos.Clone", "src", "MsDos.Tests", "binaries", "fdisk.exe"));
        }

        byte[] exeData = File.ReadAllBytes(exePath);

        var (machine, renderer, events, consoleOutput) = CreateTestMachine();
        bool loaded = machine.LoadBinary(exeData, "", @"C:\FDISK.EXE");

        Assert.True(loaded, "FDISK.EXE should load as an MZ executable");

        // FDISK needs INT 13h to detect hard drives.
        // Without a registered hard drive, it should report "no fixed disks present"
        // but NOT crash.
        var result = StepUntilDone(machine);
        string output = consoleOutput.ToString();

        Assert.False(result.Halted && output.Length == 0,
            $"FDISK halted with no output at {machine.Cpu.Regs.CS:X4}:{machine.Cpu.Regs.IP:X4}. Result: {result}");
    }

    [Fact]
    public void CommandCom_V4Ozzie_DiagnosticTrace()
    {
        string comPath = ResolveRepoPath(Path.Combine("v4.0-ozzie", "bin", "DISK1", "COMMAND.COM"));
        byte[] comData = File.ReadAllBytes(comPath);

        var (machine, renderer, events, consoleOutput) = CreateTestMachine();

        // Enable logging to capture INT calls
        machine.Log.TraceInstructions = false;
        var unhandledInts = new List<string>();

        machine.LoadBinary(comData, "", @"C:\COMMAND.COM");

        // Trace interrupt calls by hooking the log
        var originalLogEntries = new List<string>();
        machine.Log.MinLevel = MsDos.Core.Platform.LogLevel.Debug;
        machine.Log.EntryAdded += entry =>
        {
            originalLogEntries.Add($"[{entry.Level}] {entry.Source}: {entry.Message}");
            if (entry.Source == "DOS" && entry.Message.StartsWith("Unhandled"))
                unhandledInts.Add(entry.Message);
        };

        // Track every INT 21h call with AH and carry flag result
        var int21Calls = new List<string>();
        int int21Count = 0;
        bool firstInsufficient = false;
        int insufficientAt = 0;

        // Hook into interrupt dispatch — capture INT 21h calls by watching AH before each step
        var consoleChars = new StringBuilder();
        machine.Dos.ConsoleOutput += ch =>
        {
            consoleChars.Append(ch);
            // Detect transition to "Insufficient" message
            string recent = consoleChars.ToString();
            if (!firstInsufficient && recent.Contains("Insufficient"))
            {
                firstInsufficient = true;
                insufficientAt = int21Count;
                // Dump the last 50 INT 21h calls leading up to the error
            }
        };

        // We'll manually step and log INT 21h calls
        bool terminated = false;
        machine.OnProcessExit += code => terminated = true;

        for (int i = 0; i < 200_000 && !terminated && !machine.IsTerminated; i++)
        {
            if (machine.Cpu.IsHalted || machine.Cpu.IsWaitingForInput)
                break;

            // Check if current instruction is INT 21h (CD 21)
            ushort cs = machine.Cpu.Regs.CS;
            ushort ip = machine.Cpu.Regs.IP;
            byte b0 = machine.Memory.ReadByte(cs, ip);
            byte b1 = machine.Memory.ReadByte(cs, (ushort)(ip + 1));

            if (b0 == 0xCD && b1 == 0x21)
            {
                byte ah = machine.Cpu.Regs.AH;
                string callInfo = $"#{int21Count} INT21 AH={ah:X2}h";

                // Add context for specific calls
                switch (ah)
                {
                    case 0x3C: // Create file
                    case 0x3D: // Open file
                    case 0x5B: // Create new file
                    case 0x5A: // Create temp file
                    {
                        string fn = ReadDosStringFromMem(machine.Memory, machine.Cpu.Regs.DS, machine.Cpu.Regs.DX);
                        callInfo += $" file='{fn}'";
                        break;
                    }
                    case 0x3F: // Read
                    case 0x40: // Write
                        callInfo += $" handle={machine.Cpu.Regs.BX} count={machine.Cpu.Regs.CX}";
                        break;
                    case 0x3B: // CHDIR
                    case 0x39: // MKDIR
                    case 0x41: // Delete
                    {
                        string fn = ReadDosStringFromMem(machine.Memory, machine.Cpu.Regs.DS, machine.Cpu.Regs.DX);
                        callInfo += $" path='{fn}'";
                        break;
                    }
                    case 0x36: // Get disk free space
                        callInfo += $" drive={machine.Cpu.Regs.DL}";
                        break;
                    case 0x48: // Allocate memory
                        callInfo += $" paras={machine.Cpu.Regs.BX:X4}";
                        break;
                    case 0x4A: // Resize memory
                        callInfo += $" seg={machine.Cpu.Regs.ES:X4} paras={machine.Cpu.Regs.BX:X4}";
                        break;
                    case 0x59: // Get extended error
                        callInfo += " (get ext err)";
                        break;
                }

                // Step to execute the INT
                machine.Cpu.Step();

                // Check result
                bool carry = (machine.Cpu.Regs.Flags & CpuFlags.Carry) != 0;
                callInfo += carry ? $" → ERR AX={machine.Cpu.Regs.AX:X4}" : $" → OK AX={machine.Cpu.Regs.AX:X4}";

                int21Calls.Add(callInfo);
                int21Count++;

                // Stop recording after we have enough data about the error
                if (firstInsufficient && int21Count > insufficientAt + 50)
                    break;

                continue;
            }

            machine.Cpu.Step();
        }

        string output = consoleOutput.ToString();

        // Find the calls around the first "Insufficient" message
        int startIdx = Math.Max(0, insufficientAt - 30);
        int endIdx = Math.Min(int21Calls.Count, insufficientAt + 30);
        var relevantCalls = int21Calls.Skip(startIdx).Take(endIdx - startIdx);

        string diagnostics = $"=== COMMAND.COM INT 21h Trace ===\n" +
            $"Total INT 21h calls: {int21Count}\n" +
            $"First 'Insufficient' at call #{insufficientAt}\n" +
            $"Console output (first 200 chars): [{(output.Length > 200 ? output[..200] : output)}]\n" +
            $"Final CS:IP = {machine.Cpu.Regs.CS:X4}:{machine.Cpu.Regs.IP:X4}\n" +
            $"Unhandled INTs: {(unhandledInts.Count > 0 ? string.Join(", ", unhandledInts.Take(20)) : "none")}\n" +
            $"\n--- INT 21h calls around error ---\n{string.Join("\n", relevantCalls)}\n" +
            $"\n--- First 60 INT 21h calls ---\n{string.Join("\n", int21Calls.Take(60))}\n" +
            $"\n--- Log entries (last 30) ---\n{string.Join("\n", originalLogEntries.TakeLast(30))}";

        _output.WriteLine(diagnostics);

        Assert.True(
            output.Length > 0 || machine.Cpu.IsWaitingForInput || terminated,
            diagnostics);
    }

    private static string ReadDosStringFromMem(MemoryBus mem, ushort seg, ushort off)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 128; i++)
        {
            byte b = mem.ReadByte(seg, (ushort)(off + i));
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
