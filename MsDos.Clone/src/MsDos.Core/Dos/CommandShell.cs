using MsDos.Core.Cpu;
using MsDos.Core.Interrupts;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Dos;

/// <summary>
/// Built-in command shell emulating COMMAND.COM functionality.
/// Provides a DOS prompt with basic internal commands (DIR, TYPE, VER, CLS, ECHO, etc.).
/// Instead of executing as 8086 machine code, this runs as a managed handler
/// that uses DOS services to interact with the emulated environment.
/// </summary>
public sealed class CommandShell
{
    private readonly DosKernel _dos;
    private readonly MemoryBus _mem;
    private readonly Cpu8086 _cpu;
    private readonly IStreamProvider _defaultStreams;
    private readonly IEventRegistry _events;
    private readonly BiosVideoService _video;
    private readonly IGraphicsRenderer _renderer;
    private readonly EmulatorLog _log;
    private readonly DosMachine _machine;

    private bool _running;
    private string _currentDir = "\\";
    private char _currentDrive = 'C';

    public CommandShell(
        DosKernel dos,
        MemoryBus mem,
        Cpu8086 cpu,
        IStreamProvider streams,
        IEventRegistry events,
        BiosVideoService video,
        IGraphicsRenderer renderer,
        EmulatorLog log,
        DosMachine machine)
    {
        _dos = dos;
        _mem = mem;
        _cpu = cpu;
        _defaultStreams = streams;
        _events = events;
        _video = video;
        _renderer = renderer;
        _log = log;
        _machine = machine;
    }

    /// <summary>Current stream provider (resolves to the active drive).</summary>
    private IStreamProvider CurrentStreams =>
        _machine.GetDriveProvider(_currentDrive) ?? _defaultStreams;

    /// <summary>
    /// Run the command shell loop. Displays prompt, reads commands, executes them.
    /// This runs asynchronously so the UI can remain responsive.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _running = true;

        // Display banner
        PrintLine("");
        PrintLine("MS-DOS .NET Clone [Version 4.0]");
        PrintLine("(C) MS-DOS .NET Emulator Project");
        PrintLine("");

        while (_running && !cancellationToken.IsCancellationRequested)
        {
            // Display prompt with current drive letter
            Print($"{_currentDrive}:{_currentDir}>");

            // Flush display
            await _renderer.FlushAsync();

            // Read command line
            string? line = await ReadLineAsync(cancellationToken);
            if (line == null) break;

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            try
            {
                await ExecuteCommand(line, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                PrintLine($"Error: {ex.Message}");
                _log.Error("Shell", ex.Message);
            }

            await _renderer.FlushAsync();
        }
    }

    public void Stop() => _running = false;

    /// <summary>
    /// Process AUTOEXEC.BAT if it exists. Runs each line as a command.
    /// </summary>
    public async Task ProcessAutoexecAsync(CancellationToken ct)
    {
        try
        {
            if (!await _defaultStreams.ExistsAsync("AUTOEXEC.BAT")) return;

            _log.Info("Shell", "Processing AUTOEXEC.BAT...");
            using var stream = await _defaultStreams.OpenReadAsync("AUTOEXEC.BAT");
            using var reader = new StreamReader(stream);
            string content = await reader.ReadToEndAsync();

            foreach (string rawLine in content.Split('\n', '\r'))
            {
                if (ct.IsCancellationRequested) break;
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("@")) line = line[1..]; // Strip @ prefix
                if (line.StartsWith("REM", StringComparison.OrdinalIgnoreCase)) continue;

                _log.Debug("AUTOEXEC", line);
                try
                {
                    await ExecuteCommand(line, ct);
                    await _renderer.FlushAsync();
                }
                catch (Exception ex)
                {
                    _log.Warn("AUTOEXEC", $"Error executing '{line}': {ex.Message}");
                }
            }
        }
        catch (FileNotFoundException)
        {
            // No AUTOEXEC.BAT - fine
        }
        catch (Exception ex)
        {
            _log.Warn("Shell", $"Error processing AUTOEXEC.BAT: {ex.Message}");
        }
    }

    private async Task ExecuteCommand(string commandLine, CancellationToken ct)
    {
        // Split command and arguments
        string command;
        string args;
        int spaceIdx = commandLine.IndexOf(' ');
        if (spaceIdx >= 0)
        {
            command = commandLine[..spaceIdx].ToUpperInvariant();
            args = commandLine[(spaceIdx + 1)..].Trim();
        }
        else
        {
            command = commandLine.ToUpperInvariant();
            args = "";
        }

        // Check for drive letter change (e.g., "A:", "C:")
        if (command.Length == 2 && command[1] == ':' && char.IsLetter(command[0]))
        {
            char drive = char.ToUpperInvariant(command[0]);
            var provider = _machine.GetDriveProvider(drive);
            if (provider != null)
            {
                _currentDrive = drive;
                _currentDir = "\\";
                _log.Info("Shell", $"Changed to drive {drive}:");
            }
            else
            {
                PrintLine($"Invalid drive specification");
                _log.Warn("Shell", $"Drive {drive}: not mounted");
            }
            return;
        }

        switch (command)
        {
            case "DIR":
                await CmdDir(args);
                break;
            case "CLS":
                CmdCls();
                break;
            case "VER":
                CmdVer();
                break;
            case "ECHO":
                CmdEcho(args);
                break;
            case "TYPE":
                await CmdType(args);
                break;
            case "DATE":
                CmdDate();
                break;
            case "TIME":
                CmdTime();
                break;
            case "MEM":
                CmdMem();
                break;
            case "HELP":
            case "?":
                CmdHelp();
                break;
            case "CD":
            case "CHDIR":
                CmdCd(args);
                break;
            case "SET":
                CmdSet(args);
                break;
            case "EXIT":
                _running = false;
                _dos.Terminate(0);
                break;
            case "PROMPT":
                // Ignore PROMPT command (just accept it)
                break;
            case "PATH":
                PrintLine("PATH=C:\\");
                break;
            default:
                // Try to load and execute as binary
                if (await TryLoadBinary(command, args))
                    return;
                PrintLine($"Bad command or file name - '{command}'");
                break;
        }
    }

    private async Task CmdDir(string args)
    {
        string pattern = string.IsNullOrEmpty(args) ? "*.*" : args;
        PrintLine($" Volume in drive {_currentDrive} has no label");
        PrintLine($" Directory of {_currentDrive}:{_currentDir}");
        PrintLine("");

        try
        {
            var entries = await CurrentStreams.ListEntriesAsync(_currentDir);
            int fileCount = 0;

            foreach (var entry in entries)
            {
                string name = Path.GetFileName(entry).ToUpperInvariant();

                // Filter by pattern
                if (pattern != "*.*" && pattern != "*")
                {
                    if (!DosKernel.MatchWildcard(pattern, name))
                        continue;
                }

                // Format like DOS DIR output
                string dosName = FormatDos83Name(name);
                var now = DateTime.Now;
                PrintLine($"{dosName}          0 {now:MM-dd-yy} {now:hh:mmp}");
                fileCount++;
            }

            PrintLine($"        {fileCount} file(s)");
            PrintLine($"    33,554,432 bytes free");
        }
        catch (Exception ex)
        {
            PrintLine("File not found");
            _log.Debug("Shell", $"DIR error: {ex.Message}");
        }
    }

    private void CmdCls()
    {
        _renderer.Clear(0);
        _renderer.SetCursorPosition(0, 0);
    }

    private void CmdVer()
    {
        PrintLine("");
        PrintLine("MS-DOS .NET Clone Version 4.0");
        PrintLine("8086 CPU Emulator with DOS/BIOS Services");
        PrintLine("");
    }

    private void CmdEcho(string args)
    {
        if (string.IsNullOrEmpty(args) ||
            args.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
            args.Equals("OFF", StringComparison.OrdinalIgnoreCase))
        {
            PrintLine("ECHO is on");
        }
        else
        {
            PrintLine(args);
        }
    }

    private async Task CmdType(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("Required parameter missing");
            return;
        }

        try
        {
            using var stream = await CurrentStreams.OpenReadAsync(args);
            using var reader = new StreamReader(stream);
            string content = await reader.ReadToEndAsync();
            Print(content);
            if (!content.EndsWith('\n'))
                PrintLine("");
        }
        catch (Exception ex)
        {
            PrintLine($"File not found - {args}");
            _log.Debug("Shell", $"TYPE error for '{args}': {ex.Message}");
        }
    }

    private void CmdDate()
    {
        var now = DateTime.Now;
        PrintLine($"Current date is {now.DayOfWeek} {now:MM-dd-yyyy}");
    }

    private void CmdTime()
    {
        var now = DateTime.Now;
        PrintLine($"Current time is {now:HH:mm:ss.ff}");
    }

    private void CmdMem()
    {
        PrintLine("");
        PrintLine("Memory Type        Total    =    Used    +    Free");
        PrintLine("----------------  --------    --------    --------");
        PrintLine("Conventional         640K         64K        576K");
        PrintLine("");
        PrintLine("Total memory         640K         64K        576K");
        PrintLine("");
    }

    private void CmdHelp()
    {
        PrintLine("");
        PrintLine("Available commands:");
        PrintLine("  DIR [pattern]   - List directory contents");
        PrintLine("  TYPE filename   - Display file contents");
        PrintLine("  CLS             - Clear screen");
        PrintLine("  VER             - Show version");
        PrintLine("  DATE            - Show current date");
        PrintLine("  TIME            - Show current time");
        PrintLine("  MEM             - Show memory information");
        PrintLine("  ECHO [text]     - Display text");
        PrintLine("  CD [path]       - Change directory");
        PrintLine("  SET [var=val]   - Show/set environment variables");
        PrintLine("  PATH            - Display search path");
        PrintLine("  EXIT            - Exit the shell");
        PrintLine("  HELP            - Show this help");
        PrintLine("");
        PrintLine("You can also load .COM and .EXE files by typing their name.");
        PrintLine("");
    }

    private void CmdCd(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine($"{_currentDrive}:{_currentDir}");
        }
        else if (args == "..")
        {
            if (_currentDir != "\\")
            {
                int lastSep = _currentDir.TrimEnd('\\').LastIndexOf('\\');
                _currentDir = lastSep <= 0 ? "\\" : _currentDir[..lastSep];
            }
        }
        else if (args == "\\")
        {
            _currentDir = "\\";
        }
        else
        {
            _currentDir = "\\" + args.TrimStart('\\').TrimEnd('\\');
        }
    }

    private void CmdSet(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("COMSPEC=C:\\COMMAND.COM");
            PrintLine("PATH=C:\\");
            PrintLine("PROMPT=$P$G");
        }
    }

    private async Task<bool> TryLoadBinary(string command, string args)
    {
        // Try with extensions
        string[] extensions = { "", ".COM", ".EXE" };
        foreach (var ext in extensions)
        {
            string path = command + ext;
            try
            {
                if (await CurrentStreams.ExistsAsync(path))
                {
                    // File exists - signal that we want to load it
                    PrintLine($"Loading {path}...");
                    _log.Info("Shell", $"Loading binary: {path}");
                    return true;
                }
            }
            catch
            {
                // Continue trying
            }
        }
        return false;
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var buffer = new List<char>();

        while (!ct.IsCancellationRequested)
        {
            DosKeyEventArgs key;
            try
            {
                key = await _events.ReadKeyAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (key.AsciiChar == 0x0D) // Enter
            {
                PrintLine("");
                return new string(buffer.ToArray());
            }
            else if (key.AsciiChar == 0x08) // Backspace
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                    _video.TtyOutput('\b');
                    _video.TtyOutput(' ');
                    _video.TtyOutput('\b');
                    await _renderer.FlushAsync();
                }
            }
            else if (key.AsciiChar >= 0x20 && key.AsciiChar < 0x7F)
            {
                buffer.Add((char)key.AsciiChar);
                _video.TtyOutput((char)key.AsciiChar);
                await _renderer.FlushAsync();
            }
        }

        return null;
    }

    private void Print(string text)
    {
        foreach (char ch in text)
            _video.TtyOutput(ch);
    }

    private void PrintLine(string text)
    {
        Print(text);
        _video.TtyOutput('\r');
        _video.TtyOutput('\n');
    }

    /// <summary>Format a filename in DOS 8.3 format for DIR listing.</summary>
    private static string FormatDos83Name(string name)
    {
        int dotIdx = name.LastIndexOf('.');
        string baseName;
        string ext;
        if (dotIdx >= 0)
        {
            baseName = name[..dotIdx];
            ext = name[(dotIdx + 1)..];
        }
        else
        {
            baseName = name;
            ext = "";
        }

        if (baseName.Length > 8) baseName = baseName[..8];
        if (ext.Length > 3) ext = ext[..3];

        return $"{baseName,-8} {ext,-3}";
    }
}
