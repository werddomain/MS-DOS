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

    /// <summary>The current drive letter.</summary>
    public char CurrentDrive => _currentDrive;

    /// <summary>
    /// Set the current drive and stream provider (used by boot sequence).
    /// </summary>
    public void SetCurrentDrive(char driveLetter, IStreamProvider provider)
    {
        _currentDrive = char.ToUpperInvariant(driveLetter);
        _currentDir = "\\";
        // Update COMSPEC to point to the boot drive where COMMAND.COM resides
        _envVars["COMSPEC"] = $"{_currentDrive}:\\COMMAND.COM";
        _envVars["PATH"] = $"{_currentDrive}:\\";
    }

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

    // Environment variables
    private readonly Dictionary<string, string> _envVars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["COMSPEC"] = "C:\\COMMAND.COM",
        ["PATH"] = "C:\\",
        ["PROMPT"] = "$P$G",
    };

    /// <summary>
    /// Get a copy of the current environment variables for use in PSP environment blocks.
    /// </summary>
    public Dictionary<string, string> GetEnvironmentVariables()
        => new(_envVars, StringComparer.OrdinalIgnoreCase);

    private async Task ExecuteCommand(string commandLine, CancellationToken ct)
    {
        // Expand environment variables (%VAR%)
        commandLine = ExpandEnvironmentVars(commandLine);

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
        if (IsDriveLetterCommand(command))
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
            case "COPY":
                await CmdCopy(args);
                break;
            case "REN":
            case "RENAME":
                await CmdRen(args);
                break;
            case "DEL":
            case "ERASE":
                await CmdDel(args);
                break;
            case "MKDIR":
            case "MD":
                await CmdMkdir(args);
                break;
            case "RMDIR":
            case "RD":
                await CmdRmdir(args);
                break;
            case "IF":
                await CmdIf(args, ct);
                break;
            case "FOR":
                await CmdFor(args, ct);
                break;
            case "CALL":
                await CmdCall(args, ct);
                break;
            case "GOTO":
                // GOTO only meaningful in batch files — ignore at prompt
                break;
            case "PAUSE":
                PrintLine("Press any key to continue . . .");
                await _events.ReadKeyAsync(ct);
                break;
            case "VOL":
                PrintLine($" Volume in drive {_currentDrive} has no label");
                PrintLine($" Volume Serial Number is 1234-5678");
                break;
            case "VERIFY":
                PrintLine("VERIFY is off");
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
            foreach (var kv in _envVars.OrderBy(k => k.Key))
                PrintLine($"{kv.Key}={kv.Value}");
            return;
        }

        int eq = args.IndexOf('=');
        if (eq < 0)
        {
            // Display single variable
            string varName = args.Trim().ToUpperInvariant();
            if (_envVars.TryGetValue(varName, out var val))
                PrintLine($"{varName}={val}");
            else
                PrintLine($"Environment variable {varName} not defined");
            return;
        }

        string name = args[..eq].Trim();
        string value = args[(eq + 1)..];
        if (string.IsNullOrEmpty(value))
            _envVars.Remove(name);
        else
            _envVars[name] = value;
    }

    private string ExpandEnvironmentVars(string input)
    {
        int start = 0;
        while (true)
        {
            int pct1 = input.IndexOf('%', start);
            if (pct1 < 0) break;
            int pct2 = input.IndexOf('%', pct1 + 1);
            if (pct2 < 0) break;
            string varName = input[(pct1 + 1)..pct2];
            if (_envVars.TryGetValue(varName, out var val))
            {
                input = string.Concat(input.AsSpan(0, pct1), val, input.AsSpan(pct2 + 1));
                start = pct1 + val.Length;
            }
            else
            {
                start = pct2 + 1;
            }
        }
        return input;
    }

    private async Task<bool> TryLoadBinary(string command, string args)
    {
        // Try BAT files first
        string[] batExtensions = { ".BAT" };
        foreach (var ext in batExtensions)
        {
            string batPath = command.Contains('.') ? command : command + ext;
            try
            {
                if (await CurrentStreams.ExistsAsync(batPath))
                {
                    await ExecuteBatchFile(batPath);
                    return true;
                }
            }
            catch { }
        }

        // Try COM/EXE binary files
        string[] exeExtensions = { "", ".COM", ".EXE" };
        foreach (var ext in exeExtensions)
        {
            string path = command + ext;
            try
            {
                if (await CurrentStreams.ExistsAsync(path))
                {
                    _log.Info("Shell", $"Loading binary: {path}");
                    using var stream = await CurrentStreams.OpenReadAsync(path);
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    byte[] data = ms.ToArray();

                    if (data.Length == 0)
                    {
                        PrintLine($"Error: {path} is empty");
                        return true;
                    }

                    // Save shell state
                    var savedDrive = _currentDrive;
                    var savedDir = _currentDir;

                    // Build full program path for the environment block
                    string programPath = $"{_currentDrive}:\\{path}";

                    PrintLine($"Loading {path}...");
                    bool loaded = _machine.LoadBinary(data, args, programPath);

                    // Set the DosKernel's current drive to match the shell's
                    _machine.Dos.SetCurrentDrive((byte)(_currentDrive - 'A'));
                    if (!loaded)
                    {
                        PrintLine($"Error loading {path} - unsupported format");
                        return true;
                    }

                    // Run the binary
                    try
                    {
                        await _machine.RunAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("Shell", $"Binary execution error: {ex}");
                    }

                    // Restore shell state after binary exits
                    _currentDrive = savedDrive;
                    _currentDir = savedDir;

                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Debug("Shell", $"Error trying to load {path}: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// Execute a BAT batch file by reading it line by line and executing each command.
    /// Supports REM comments, ECHO, IF ERRORLEVEL, GOTO, labels, and @ prefix.
    /// </summary>
    private async Task ExecuteBatchFile(string path)
    {
        _log.Info("Shell", $"Executing batch file: {path}");
        try
        {
            using var stream = await CurrentStreams.OpenReadAsync(path);
            using var reader = new StreamReader(stream);
            string content = await reader.ReadToEndAsync();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            bool echoOn = true;
            int i = 0;

            while (i < lines.Length)
            {
                string line = lines[i].Trim();
                i++;

                if (string.IsNullOrEmpty(line)) continue;

                // Handle @ prefix (suppress echo for this line)
                bool suppressEcho = false;
                if (line.StartsWith("@"))
                {
                    suppressEcho = true;
                    line = line[1..].TrimStart();
                }

                // Handle ECHO OFF/ON
                if (line.StartsWith("ECHO", StringComparison.OrdinalIgnoreCase))
                {
                    string echoArg = line.Length > 4 ? line[4..].TrimStart() : "";
                    if (echoArg.Equals("OFF", StringComparison.OrdinalIgnoreCase))
                    {
                        echoOn = false;
                        continue;
                    }
                    if (echoArg.Equals("ON", StringComparison.OrdinalIgnoreCase))
                    {
                        echoOn = true;
                        continue;
                    }
                }

                // Echo the line if echo is on
                if (echoOn && !suppressEcho)
                {
                    PrintLine($"{_currentDrive}:{_currentDir}>{line}");
                }

                // Skip REM comments
                if (line.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Handle labels (lines starting with :)
                if (line.StartsWith(":"))
                    continue;

                // Handle GOTO
                if (line.StartsWith("GOTO", StringComparison.OrdinalIgnoreCase))
                {
                    string label = line.Length > 4 ? line[4..].TrimStart().ToUpperInvariant() : "";
                    // Find the label in the lines
                    bool found = false;
                    for (int j = 0; j < lines.Length; j++)
                    {
                        string l = lines[j].Trim();
                        if (l.StartsWith(":") && l.Length > 1 && l[1..].Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
                        {
                            i = j + 1;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        PrintLine($"Label not found - {label}");
                    continue;
                }

                // Handle PAUSE
                if (line.Equals("PAUSE", StringComparison.OrdinalIgnoreCase))
                {
                    PrintLine("Press any key to continue . . .");
                    await _renderer.FlushAsync();
                    try { await _events.ReadKeyAsync(CancellationToken.None); } catch { }
                    continue;
                }

                // Execute as normal command
                try
                {
                    await ExecuteCommand(line, CancellationToken.None);
                    await _renderer.FlushAsync();
                }
                catch (Exception ex)
                {
                    _log.Warn("BAT", $"Error executing '{line}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            PrintLine($"Error reading batch file: {ex.Message}");
            _log.Error("Shell", $"Batch file error: {ex.Message}");
        }
    }

    private async Task CmdCopy(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("Required parameter missing");
            return;
        }

        // Split source and destination
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            PrintLine("Required parameter missing");
            return;
        }

        string src = parts[0];
        string dst = parts[1];

        try
        {
            // Determine source stream provider (check for drive letter)
            IStreamProvider srcProvider = CurrentStreams;
            if (src.Length >= 2 && src[1] == ':')
            {
                var p = _machine.GetDriveProvider(src[0]);
                if (p != null) srcProvider = p;
                src = src[2..].TrimStart('\\');
            }

            IStreamProvider dstProvider = CurrentStreams;
            if (dst.Length >= 2 && dst[1] == ':')
            {
                var p = _machine.GetDriveProvider(dst[0]);
                if (p != null) dstProvider = p;
                dst = dst[2..].TrimStart('\\');
            }

            using var stream = await srcProvider.OpenReadAsync(src);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            byte[] data = ms.ToArray();

            // If destination is a directory or doesn't have a filename, use source name
            if (string.IsNullOrEmpty(Path.GetFileName(dst)))
                dst = Path.Combine(dst, Path.GetFileName(src) ?? src);

            await dstProvider.ImportBinaryAsync(dst, data);
            PrintLine($"        1 file(s) copied");
        }
        catch (Exception ex)
        {
            PrintLine($"File not found - {src}");
            _log.Debug("Shell", $"COPY error: {ex.Message}");
        }
    }

    private async Task CmdDel(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("Required parameter missing");
            return;
        }
        try
        {
            await CurrentStreams.DeleteAsync(args);
            PrintLine($"File deleted: {args}");
        }
        catch (Exception ex)
        {
            PrintLine($"File not found - {args}");
            _log.Debug("Shell", $"DEL error: {ex.Message}");
        }
    }

    private async Task CmdRen(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("Syntax: REN <oldname> <newname>");
            return;
        }
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            PrintLine("Required parameter missing");
            return;
        }
        try
        {
            await CurrentStreams.RenameAsync(parts[0], parts[1]);
            PrintLine($"        1 file(s) renamed");
        }
        catch (Exception ex)
        {
            PrintLine($"File not found - {parts[0]}");
            _log.Debug("Shell", $"REN error: {ex.Message}");
        }
    }

    private async Task CmdMkdir(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("Required parameter missing");
            return;
        }
        try
        {
            await CurrentStreams.CreateDirectoryAsync(args);
            _log.Info("Shell", $"Created directory: {args}");
        }
        catch (Exception ex)
        {
            PrintLine($"Unable to create directory - {args}");
            _log.Debug("Shell", $"MKDIR error: {ex.Message}");
        }
    }

    private async Task CmdRmdir(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("Required parameter missing");
            return;
        }
        try
        {
            await CurrentStreams.DeleteDirectoryAsync(args);
            _log.Info("Shell", $"Removed directory: {args}");
        }
        catch (Exception ex)
        {
            PrintLine($"Invalid path, not directory, or directory not empty");
            _log.Debug("Shell", $"RMDIR error: {ex.Message}");
        }
    }

    private async Task CmdIf(string args, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("Syntax Error");
            return;
        }

        bool negate = false;
        string remaining = args;

        if (remaining.StartsWith("NOT ", StringComparison.OrdinalIgnoreCase))
        {
            negate = true;
            remaining = remaining[4..].TrimStart();
        }

        bool condition = false;

        if (remaining.StartsWith("EXIST ", StringComparison.OrdinalIgnoreCase))
        {
            // IF EXIST filename command
            remaining = remaining[6..].TrimStart();
            int spaceIdx = remaining.IndexOf(' ');
            if (spaceIdx < 0) return;
            string filename = remaining[..spaceIdx];
            remaining = remaining[(spaceIdx + 1)..].TrimStart();
            try { condition = await CurrentStreams.ExistsAsync(filename); } catch { }
        }
        else if (remaining.StartsWith("ERRORLEVEL ", StringComparison.OrdinalIgnoreCase))
        {
            // IF ERRORLEVEL n command
            remaining = remaining[11..].TrimStart();
            int spaceIdx = remaining.IndexOf(' ');
            if (spaceIdx < 0) return;
            if (int.TryParse(remaining[..spaceIdx], out int level))
            {
                condition = _machine.ExitCode >= level;
            }
            remaining = remaining[(spaceIdx + 1)..].TrimStart();
        }
        else
        {
            // IF string1==string2 command
            int eqIdx = remaining.IndexOf("==", StringComparison.Ordinal);
            if (eqIdx > 0)
            {
                string left = remaining[..eqIdx].Trim().Trim('"');
                remaining = remaining[(eqIdx + 2)..];
                int spaceIdx = remaining.IndexOf(' ');
                string right = (spaceIdx >= 0 ? remaining[..spaceIdx] : remaining).Trim().Trim('"');
                remaining = spaceIdx >= 0 ? remaining[(spaceIdx + 1)..].TrimStart() : "";
                condition = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (negate) condition = !condition;

        if (condition && !string.IsNullOrEmpty(remaining))
        {
            await ExecuteCommand(remaining, ct);
        }
    }

    private async Task CmdFor(string args, CancellationToken ct)
    {
        // FOR %v IN (set) DO command
        // Simplified: FOR %x IN (*.COM *.EXE) DO ECHO %x
        if (string.IsNullOrEmpty(args)) { PrintLine("Syntax Error"); return; }

        // Parse: %variable IN (set) DO command
        string remaining = args;
        if (!remaining.StartsWith("%", StringComparison.Ordinal) &&
            !remaining.StartsWith("%%", StringComparison.Ordinal))
        { PrintLine("Syntax Error"); return; }

        int inIdx = remaining.IndexOf(" IN ", StringComparison.OrdinalIgnoreCase);
        if (inIdx < 0) { PrintLine("Syntax Error"); return; }
        string varName = remaining[..inIdx].Trim();
        remaining = remaining[(inIdx + 4)..].TrimStart();

        int openParen = remaining.IndexOf('(');
        int closeParen = remaining.IndexOf(')');
        if (openParen < 0 || closeParen < 0 || closeParen <= openParen)
        { PrintLine("Syntax Error"); return; }

        string setStr = remaining[(openParen + 1)..closeParen].Trim();
        remaining = remaining[(closeParen + 1)..].TrimStart();

        if (!remaining.StartsWith("DO ", StringComparison.OrdinalIgnoreCase))
        { PrintLine("Syntax Error"); return; }
        string cmdTemplate = remaining[3..].TrimStart();

        var items = setStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string item in items)
        {
            // If item contains wildcard, expand it
            if (item.Contains('*') || item.Contains('?'))
            {
                try
                {
                    var entries = await CurrentStreams.ListEntriesAsync(_currentDir);
                    foreach (var entry in entries)
                    {
                        string name = Path.GetFileName(entry);
                        if (DosKernel.MatchWildcard(item, name))
                        {
                            string cmd = cmdTemplate.Replace(varName, name, StringComparison.OrdinalIgnoreCase);
                            await ExecuteCommand(cmd, ct);
                        }
                    }
                }
                catch { }
            }
            else
            {
                string cmd = cmdTemplate.Replace(varName, item, StringComparison.OrdinalIgnoreCase);
                await ExecuteCommand(cmd, ct);
            }
        }
    }

    private async Task CmdCall(string args, CancellationToken ct)
    {
        // CALL executes another batch file inline
        if (string.IsNullOrEmpty(args))
        {
            PrintLine("Required parameter missing");
            return;
        }

        string batchFile = args.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!batchFile.EndsWith(".BAT", StringComparison.OrdinalIgnoreCase))
            batchFile += ".BAT";

        try
        {
            if (await CurrentStreams.ExistsAsync(batchFile))
            {
                using var stream = await CurrentStreams.OpenReadAsync(batchFile);
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync();
                // Execute batch file — reuse existing batch execution logic
                foreach (string rawLine in content.Split('\n', '\r'))
                {
                    if (ct.IsCancellationRequested) break;
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("REM", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("@")) line = line[1..];
                    if (line.StartsWith(":")) continue;
                    await ExecuteCommand(line, ct);
                }
            }
            else
            {
                PrintLine($"Batch file not found - {batchFile}");
            }
        }
        catch (Exception ex)
        {
            PrintLine($"Error: {ex.Message}");
        }
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

    /// <summary>Check if the command is a drive letter change (e.g. "A:", "C:").</summary>
    private static bool IsDriveLetterCommand(string command) =>
        command.Length == 2 && command[1] == ':' && char.IsLetter(command[0]);

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
