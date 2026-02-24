using MsDos.Core.Cpu;
using MsDos.Core.Memory;
using MsDos.Core.Platform;

namespace MsDos.Core.Dos;

/// <summary>
/// DOS INT 21h service implementation. Provides file I/O, console I/O,
/// process management, and memory allocation services.
/// </summary>
public sealed class DosKernel
{
    private readonly Cpu8086 _cpu;
    private readonly MemoryBus _mem;
    private readonly IStreamProvider _streams;
    private readonly IEventRegistry _events;
    private readonly IGraphicsRenderer _renderer;
    private readonly Interrupts.BiosVideoService _video;
    private readonly EmulatorLog _log;

    // DOS version to report
    private const byte MajorVersion = 4;
    private const byte MinorVersion = 0;

    // File handle table (5 standard + user files)
    private readonly Dictionary<ushort, DosFileHandle> _fileHandles = new();
    private ushort _nextHandle = 5; // 0-4 are reserved (stdin, stdout, stderr, stdaux, stdprn)

    // Return code from last terminated process
    private byte _lastReturnCode;

    // PSP segment of current process
    public ushort CurrentPSP { get; set; }

    // DTA (Disk Transfer Area) address
    public ushort DtaSegment { get; set; }
    public ushort DtaOffset { get; set; } = 0x0080;

    // Current default drive (0=A, 1=B, 2=C)
    private byte _currentDrive = 2; // C:

    // Drive provider map for resolving paths on different drives
    private readonly Dictionary<char, IStreamProvider> _driveProviders = new(new CharOrdinalIgnoreCaseComparer());

    /// <summary>
    /// Register a drive letter with its stream provider so the kernel can resolve file paths.
    /// </summary>
    public void SetDriveProvider(char driveLetter, IStreamProvider provider)
    {
        _driveProviders[char.ToUpperInvariant(driveLetter)] = provider;
    }

    /// <summary>
    /// Remove a drive letter mapping (e.g., when ejecting a floppy).
    /// </summary>
    public void RemoveDriveProvider(char driveLetter)
    {
        _driveProviders.Remove(char.ToUpperInvariant(driveLetter));
    }

    /// <summary>
    /// Set the current default drive number (0=A, 1=B, 2=C...).
    /// </summary>
    public void SetCurrentDrive(byte driveNumber)
    {
        _currentDrive = driveNumber;
    }

    /// <summary>
    /// Resolve a DOS file path to the correct stream provider.
    /// If the path has a drive letter prefix (e.g., "A:\FILE.EXE"), uses that drive's provider.
    /// Otherwise uses the current drive's provider, falling back to the default.
    /// Returns the provider and the path with the drive prefix stripped.
    /// </summary>
    private (IStreamProvider provider, string resolvedPath) ResolveFilePath(string path)
    {
        // Check for drive letter prefix (e.g., "A:" or "A:\")
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            char drive = char.ToUpperInvariant(path[0]);
            string subPath = path.Length > 2 ? path[2..].TrimStart('\\', '/') : "";
            if (_driveProviders.TryGetValue(drive, out var driveProvider))
                return (driveProvider, subPath);
        }

        // Use current drive's provider if available
        char currentDriveLetter = (char)('A' + _currentDrive);
        if (_driveProviders.TryGetValue(currentDriveLetter, out var currentProvider))
            return (currentProvider, path);

        // Fall back to default stream provider
        return (_streams, path);
    }

    private sealed class CharOrdinalIgnoreCaseComparer : IEqualityComparer<char>
    {
        public bool Equals(char x, char y) => char.ToUpperInvariant(x) == char.ToUpperInvariant(y);
        public int GetHashCode(char obj) => char.ToUpperInvariant(obj).GetHashCode();
    }

    // FindFirst/FindNext state
    private IReadOnlyList<string>? _findResults;
    private int _findIndex;
    private string _findPattern = "*.*";

    /// <summary>Trigger process termination from external callers (e.g., INT 20h).</summary>
    public void Terminate(byte code) => ProcessTerminated?.Invoke(code);

    /// <summary>Fired when a process terminates (INT 21h AH=4Ch).</summary>
    public event Action<byte>? ProcessTerminated;

    /// <summary>Fired when DOS needs to output a character to the console.</summary>
    public event Action<char>? ConsoleOutput;

    public DosKernel(
        Cpu8086 cpu,
        MemoryBus mem,
        IStreamProvider streams,
        IEventRegistry events,
        IGraphicsRenderer renderer,
        Interrupts.BiosVideoService video,
        EmulatorLog log)
    {
        _cpu = cpu;
        _mem = mem;
        _streams = streams;
        _events = events;
        _renderer = renderer;
        _video = video;
        _log = log;
    }

    public void Handle()
    {
        byte func = _cpu.Regs.AH;
        switch (func)
        {
            case 0x00: // Terminate program
                ProcessTerminated?.Invoke(0);
                break;

            case 0x01: // Read character with echo
                HandleCharInputWithEcho();
                break;

            case 0x02: // Display character
                OutputChar((char)_cpu.Regs.DL);
                break;

            case 0x06: // Direct console I/O
                HandleDirectConsoleIO();
                break;

            case 0x07: // Direct character input (no echo)
            case 0x08: // Character input (no echo)
                HandleCharInputNoEcho();
                break;

            case 0x09: // Display string (terminated by '$')
                HandleDisplayString();
                break;

            case 0x0A: // Buffered keyboard input
                HandleBufferedInput();
                break;

            case 0x0B: // Check standard input status
                _cpu.Regs.AL = (byte)(_events.IsKeyAvailable ? 0xFF : 0x00);
                break;

            case 0x0C: // Clear input buffer and invoke input
                // Clear buffer, then call function in AL
                _cpu.Regs.AH = _cpu.Regs.AL;
                Handle();
                break;

            case 0x0E: // Select disk
                _currentDrive = _cpu.Regs.DL;
                _cpu.Regs.AL = 26; // Number of logical drives
                break;

            case 0x19: // Get current default drive
                _cpu.Regs.AL = _currentDrive;
                break;

            case 0x1A: // Set DTA
                DtaSegment = _cpu.Regs.DS;
                DtaOffset = _cpu.Regs.DX;
                break;

            case 0x25: // Set interrupt vector
                SetInterruptVector();
                break;

            case 0x2A: // Get date
                HandleGetDate();
                break;

            case 0x2C: // Get time
                HandleGetTime();
                break;

            case 0x30: // Get DOS version
                _cpu.Regs.AL = MajorVersion;
                _cpu.Regs.AH = MinorVersion;
                _cpu.Regs.BX = 0;
                _cpu.Regs.CX = 0;
                break;

            case 0x33: // Get/set Ctrl-Break flag
                if (_cpu.Regs.AL == 0)
                    _cpu.Regs.DL = 0; // Ctrl-Break checking off
                break;

            case 0x35: // Get interrupt vector
                GetInterruptVector();
                break;

            case 0x3C: // Create file
                HandleCreateFile();
                break;

            case 0x3D: // Open file
                HandleOpenFile();
                break;

            case 0x3E: // Close file
                HandleCloseFile();
                break;

            case 0x3F: // Read file
                HandleReadFile();
                break;

            case 0x40: // Write file
                HandleWriteFile();
                break;

            case 0x41: // Delete file
                HandleDeleteFile();
                break;

            case 0x42: // Seek (LSEEK)
                HandleSeek();
                break;

            case 0x43: // Get/set file attributes
                _cpu.Regs.CX = 0x0020; // Archive bit set
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x44: // IOCTL
                HandleIoctl();
                break;

            case 0x47: // Get current directory
                HandleGetCurrentDir();
                break;

            case 0x48: // Allocate memory
                HandleAllocateMemory();
                break;

            case 0x49: // Free memory
                _cpu.Regs.Flags &= ~CpuFlags.Carry; // success
                break;

            case 0x4A: // Resize memory block
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x4B: // EXEC - load and execute program
                HandleExec();
                break;

            case 0x4C: // Terminate with return code
                _lastReturnCode = _cpu.Regs.AL;
                ProcessTerminated?.Invoke(_cpu.Regs.AL);
                break;

            case 0x4D: // Get return code
                _cpu.Regs.AX = _lastReturnCode;
                break;

            case 0x4E: // Find first matching file
                HandleFindFirst();
                break;

            case 0x4F: // Find next matching file
                HandleFindNext();
                break;

            case 0x50: // Set PSP segment
                CurrentPSP = _cpu.Regs.BX;
                break;

            case 0x51: // Get PSP segment
            case 0x62: // Get PSP address
                _cpu.Regs.BX = CurrentPSP;
                break;

            case 0x54: // Get verify flag
                _cpu.Regs.AL = 0; // Verify off
                break;

            case 0x56: // Rename file
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x57: // Get/set file date/time
                HandleFileDateTime();
                break;

            case 0x58: // Get/set memory allocation strategy
                if (_cpu.Regs.AL == 0)
                    _cpu.Regs.AX = 0; // First fit
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x59: // Get extended error information
                _cpu.Regs.AX = 0; // No error
                _cpu.Regs.BH = 0;
                _cpu.Regs.BL = 0;
                _cpu.Regs.CH = 0;
                break;

            case 0x36: // Get disk free space
                HandleGetDiskFreeSpace();
                break;

            case 0x2F: // Get DTA address
                _cpu.Regs.ES = DtaSegment;
                _cpu.Regs.BX = DtaOffset;
                break;

            case 0x2E: // Set verify flag
                // Just accept and ignore
                break;

            case 0x38: // Get/set country info
                if (_cpu.Regs.AL == 0) // Get
                {
                    // Write minimal country info to DS:DX
                    ushort seg = _cpu.Regs.DS;
                    ushort off = _cpu.Regs.DX;
                    _mem.WriteWord(seg, off, 0); // Date format (0=US)
                    _mem.WriteByte(seg, (ushort)(off + 2), (byte)'$'); // Currency symbol
                    _mem.WriteByte(seg, (ushort)(off + 3), 0);
                    _mem.WriteByte(seg, (ushort)(off + 7), (byte)','); // Thousands separator
                    _mem.WriteByte(seg, (ushort)(off + 8), 0);
                    _mem.WriteByte(seg, (ushort)(off + 9), (byte)'.'); // Decimal separator
                    _mem.WriteByte(seg, (ushort)(off + 10), 0);
                    _cpu.Regs.BX = 1; // Country code
                }
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x39: // Create directory (MKDIR)
                _log.Info("DOS", $"MKDIR: {ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX)}");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x3A: // Remove directory (RMDIR)
                _log.Info("DOS", $"RMDIR: {ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX)}");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x3B: // Change directory (CHDIR)
                _log.Info("DOS", $"CHDIR: {ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX)}");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x55: // Create child PSP
                _log.Debug("DOS", $"Create child PSP at {_cpu.Regs.DX:X4}");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x63: // Get lead byte table (DBCS)
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x65: // Get extended country info
                _cpu.Regs.AX = 1; // Function not supported
                _cpu.Regs.Flags |= CpuFlags.Carry;
                break;

            case 0x66: // Get/set global code page
                _cpu.Regs.BX = 437; // US code page
                _cpu.Regs.DX = 437;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x0D: // Disk reset (flush buffers)
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x26: // Create new PSP (copy)
            {
                ushort newSeg = _cpu.Regs.DX;
                // Copy first 256 bytes of current PSP to new segment
                for (int i = 0; i < 256; i++)
                    _mem.WriteByte(newSeg, (ushort)i, _mem.ReadByte(CurrentPSP, (ushort)i));
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            }

            case 0x29: // Parse filename into FCB
                HandleParseFilename();
                break;

            case 0x2B: // Set date
                // Accept but ignore
                _cpu.Regs.AL = 0; // success
                break;

            case 0x2D: // Set time
                // Accept but ignore
                _cpu.Regs.AL = 0; // success
                break;

            case 0x34: // Get InDOS flag address
                // Return a fixed address in low memory where InDOS flag resides
                _cpu.Regs.ES = 0x0070;
                _cpu.Regs.BX = 0x0000;
                _mem.WriteByte(0x0070, 0x0000, 0); // InDOS flag = 0 (not in DOS)
                break;

            case 0x37: // Get/set switch character
                if (_cpu.Regs.AL == 0) // Get
                {
                    _cpu.Regs.DL = (byte)'/'; // Standard DOS switch char
                    _cpu.Regs.AL = 0;
                }
                else if (_cpu.Regs.AL == 1) // Set
                    _cpu.Regs.AL = 0; // Accept but ignore
                break;

            case 0x45: // Duplicate file handle (DUP)
                HandleDupHandle();
                break;

            case 0x46: // Force duplicate file handle (DUP2)
                HandleForceDupHandle();
                break;

            case 0x5A: // Create temporary file
                HandleCreateTempFile();
                break;

            case 0x5B: // Create new file (fail if exists)
                HandleCreateNewFile();
                break;

            case 0x60: // Canonicalize filename
                HandleCanonicalizePath();
                break;

            case 0x67: // Set handle count
                // Accept - we don't have a real limit
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;

            case 0x68: // Commit file (flush)
            {
                ushort handle = _cpu.Regs.BX;
                if (_fileHandles.TryGetValue(handle, out var fh))
                {
                    fh.Stream.Flush();
                    _cpu.Regs.Flags &= ~CpuFlags.Carry;
                }
                else
                {
                    _cpu.Regs.AX = 0x06;
                    _cpu.Regs.Flags |= CpuFlags.Carry;
                }
                break;
            }

            case 0x1B: // Get allocation info (default drive)
            case 0x1C: // Get allocation info (specific drive)
                _cpu.Regs.AL = 64;  // sectors per cluster
                _cpu.Regs.CX = 512; // bytes per sector
                _cpu.Regs.DX = 2048; // total clusters
                // DS:BX → pointer to media descriptor byte
                break;

            case 0x6C: // Extended open/create (DOS 4.0+)
                HandleExtendedOpen();
                break;

            default:
                _log.Warn("DOS", $"Unhandled INT 21h AH={func:X2}h");
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
        }
    }

    private void OutputChar(char ch)
    {
        _video.TtyOutput(ch);
        ConsoleOutput?.Invoke(ch);
    }

    private void HandleCharInputWithEcho()
    {
        var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
        _cpu.Regs.AL = key.AsciiChar;
        OutputChar((char)key.AsciiChar);
    }

    private void HandleCharInputNoEcho()
    {
        var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
        _cpu.Regs.AL = key.AsciiChar;
    }

    private void HandleDirectConsoleIO()
    {
        if (_cpu.Regs.DL == 0xFF)
        {
            // Input
            if (_events.IsKeyAvailable)
            {
                var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
                _cpu.Regs.AL = key.AsciiChar;
                _cpu.Regs.Flags &= ~CpuFlags.Zero;
            }
            else
            {
                _cpu.Regs.AL = 0;
                _cpu.Regs.Flags |= CpuFlags.Zero;
            }
        }
        else
        {
            // Output
            OutputChar((char)_cpu.Regs.DL);
        }
    }

    private void HandleDisplayString()
    {
        ushort seg = _cpu.Regs.DS;
        ushort off = _cpu.Regs.DX;
        while (true)
        {
            byte ch = _mem.ReadByte(seg, off);
            if (ch == (byte)'$') break;
            OutputChar((char)ch);
            off++;
        }
    }

    private void HandleBufferedInput()
    {
        ushort seg = _cpu.Regs.DS;
        ushort off = _cpu.Regs.DX;
        byte maxLen = _mem.ReadByte(seg, off);
        byte count = 0;

        for (int i = 0; i < maxLen - 1; i++)
        {
            var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
            if (key.AsciiChar == 0x0D) // Enter
            {
                OutputChar('\r');
                OutputChar('\n');
                break;
            }
            if (key.AsciiChar == 0x08 && count > 0) // Backspace
            {
                count--;
                OutputChar('\b');
                OutputChar(' ');
                OutputChar('\b');
                continue;
            }
            if (key.AsciiChar >= 0x20)
            {
                _mem.WriteByte(seg, (ushort)(off + 2 + count), key.AsciiChar);
                count++;
                OutputChar((char)key.AsciiChar);
            }
        }
        _mem.WriteByte(seg, (ushort)(off + 1), count);
        _mem.WriteByte(seg, (ushort)(off + 2 + count), 0x0D);
    }

    private void SetInterruptVector()
    {
        byte vector = _cpu.Regs.AL;
        uint ivtAddr = (uint)(vector * 4);
        _mem.WriteWord(ivtAddr, _cpu.Regs.DX);
        _mem.WriteWord(ivtAddr + 2, _cpu.Regs.DS);
    }

    private void GetInterruptVector()
    {
        byte vector = _cpu.Regs.AL;
        uint ivtAddr = (uint)(vector * 4);
        _cpu.Regs.BX = _mem.ReadWord(ivtAddr);
        _cpu.Regs.ES = _mem.ReadWord(ivtAddr + 2);
    }

    private void HandleGetDate()
    {
        var now = DateTime.Now;
        _cpu.Regs.CX = (ushort)now.Year;
        _cpu.Regs.DH = (byte)now.Month;
        _cpu.Regs.DL = (byte)now.Day;
        _cpu.Regs.AL = (byte)now.DayOfWeek;
    }

    private void HandleGetTime()
    {
        var now = DateTime.Now;
        _cpu.Regs.CH = (byte)now.Hour;
        _cpu.Regs.CL = (byte)now.Minute;
        _cpu.Regs.DH = (byte)now.Second;
        _cpu.Regs.DL = (byte)(now.Millisecond / 10);
    }

    private string ReadDosString(ushort segment, ushort offset)
    {
        var chars = new List<char>();
        while (true)
        {
            byte b = _mem.ReadByte(segment, offset++);
            if (b == 0) break;
            chars.Add((char)b);
        }
        return new string(chars.ToArray());
    }

    private void HandleCreateFile()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05; // Access denied
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleOpenFile()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            Stream stream;
            byte mode = (byte)(_cpu.Regs.AL & 3);
            stream = mode switch
            {
                0 => provider.OpenReadAsync(resolvedPath).GetAwaiter().GetResult(),
                1 => provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult(),
                _ => provider.OpenReadWriteAsync(resolvedPath).GetAwaiter().GetResult()
            };

            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
            _log.Debug("DOS", $"Open file '{path}' → handle {handle}");
        }
        catch (FileNotFoundException ex)
        {
            _log.Debug("DOS", $"Open file '{path}' not found: {ex.Message}");
            _cpu.Regs.AX = 0x02; // File not found
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Debug("DOS", $"Open file '{path}' access denied: {ex.Message}");
            _cpu.Regs.AX = 0x05; // Access denied
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
        catch (DirectoryNotFoundException ex)
        {
            _log.Debug("DOS", $"Open file '{path}' path not found: {ex.Message}");
            _cpu.Regs.AX = 0x03; // Path not found
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
        catch (Exception ex)
        {
            _log.Debug("DOS", $"Open file '{path}' failed: {ex.Message}");
            _cpu.Regs.AX = 0x02; // File not found (default)
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleCloseFile()
    {
        ushort handle = _cpu.Regs.BX;
        if (_fileHandles.TryGetValue(handle, out var fh))
        {
            fh.Stream.Dispose();
            _fileHandles.Remove(handle);
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        else
        {
            _cpu.Regs.AX = 0x06; // Invalid handle
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleReadFile()
    {
        ushort handle = _cpu.Regs.BX;
        ushort count = _cpu.Regs.CX;
        ushort bufSeg = _cpu.Regs.DS;
        ushort bufOff = _cpu.Regs.DX;

        if (handle == 0) // stdin
        {
            var key = _events.ReadKeyAsync().GetAwaiter().GetResult();
            _mem.WriteByte(bufSeg, bufOff, key.AsciiChar);
            _cpu.Regs.AX = 1;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
            return;
        }

        if (_fileHandles.TryGetValue(handle, out var fh))
        {
            try
            {
                var buffer = new byte[count];
                int read = fh.Stream.Read(buffer, 0, count);
                for (int i = 0; i < read; i++)
                    _mem.WriteByte(bufSeg, (ushort)(bufOff + i), buffer[i]);
                _cpu.Regs.AX = (ushort)read;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            catch
            {
                _cpu.Regs.AX = 0x05;
                _cpu.Regs.Flags |= CpuFlags.Carry;
            }
        }
        else
        {
            _cpu.Regs.AX = 0x06;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleWriteFile()
    {
        ushort handle = _cpu.Regs.BX;
        ushort count = _cpu.Regs.CX;
        ushort bufSeg = _cpu.Regs.DS;
        ushort bufOff = _cpu.Regs.DX;

        // stdout (1) or stderr (2)
        if (handle is 1 or 2)
        {
            for (int i = 0; i < count; i++)
            {
                byte ch = _mem.ReadByte(bufSeg, (ushort)(bufOff + i));
                OutputChar((char)ch);
            }
            _cpu.Regs.AX = count;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
            return;
        }

        if (_fileHandles.TryGetValue(handle, out var fh))
        {
            try
            {
                var buffer = new byte[count];
                for (int i = 0; i < count; i++)
                    buffer[i] = _mem.ReadByte(bufSeg, (ushort)(bufOff + i));
                fh.Stream.Write(buffer, 0, count);
                _cpu.Regs.AX = count;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            catch
            {
                _cpu.Regs.AX = 0x05;
                _cpu.Regs.Flags |= CpuFlags.Carry;
            }
        }
        else
        {
            _cpu.Regs.AX = 0x06;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleDeleteFile()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            provider.DeleteAsync(resolvedPath).GetAwaiter().GetResult();
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x02;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleSeek()
    {
        ushort handle = _cpu.Regs.BX;
        if (!_fileHandles.TryGetValue(handle, out var fh))
        {
            _cpu.Regs.AX = 0x06;
            _cpu.Regs.Flags |= CpuFlags.Carry;
            return;
        }

        long offset = (int)((_cpu.Regs.CX << 16) | _cpu.Regs.DX);
        SeekOrigin origin = _cpu.Regs.AL switch
        {
            0 => SeekOrigin.Begin,
            1 => SeekOrigin.Current,
            2 => SeekOrigin.End,
            _ => SeekOrigin.Begin
        };

        long newPos = fh.Stream.Seek(offset, origin);
        _cpu.Regs.DX = (ushort)(newPos >> 16);
        _cpu.Regs.AX = (ushort)(newPos & 0xFFFF);
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleIoctl()
    {
        byte subfunc = _cpu.Regs.AL;
        ushort handle = _cpu.Regs.BX;

        switch (subfunc)
        {
            case 0x00: // Get device information
                if (handle <= 2)
                    _cpu.Regs.DX = 0x80D3; // Console device
                else
                    _cpu.Regs.DX = 0x0000; // File device
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
            default:
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
                break;
        }
    }

    private void HandleGetCurrentDir()
    {
        // Return root directory "\"
        ushort seg = _cpu.Regs.DS;
        ushort off = _cpu.Regs.SI;
        _mem.WriteByte(seg, off, 0); // Empty string = root
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleAllocateMemory()
    {
        // Simplified: always allocate from a high segment
        ushort paragraphs = _cpu.Regs.BX;
        // Return a segment above the current program
        _cpu.Regs.AX = (ushort)(CurrentPSP + 0x1000);
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleExec()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        _log.Info("DOS", $"EXEC: {path}");
        // TODO: Implement child process loading
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleGetDiskFreeSpace()
    {
        // Return simulated 32 MB free space
        // DL = drive number (0=default, 1=A, 2=B, 3=C, ...)
        _cpu.Regs.AX = 64;     // Sectors per cluster
        _cpu.Regs.BX = 1024;   // Number of available clusters
        _cpu.Regs.CX = 512;    // Bytes per sector
        _cpu.Regs.DX = 2048;   // Total clusters on drive
    }

    private void HandleFindFirst()
    {
        string pattern = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        _findPattern = pattern;
        _findIndex = 0;

        try
        {
            // Extract directory part and filename pattern
            string dir = "";
            string filePattern = pattern;
            int lastSep = pattern.LastIndexOfAny(new[] { '\\', '/' });
            if (lastSep >= 0)
            {
                dir = pattern[..lastSep];
                filePattern = pattern[(lastSep + 1)..];
            }

            var (provider, resolvedDir) = ResolveFilePath(dir.Length > 0 ? dir : ".");
            var entries = provider.ListEntriesAsync(resolvedDir).GetAwaiter().GetResult();

            // Filter by pattern (simple wildcard matching)
            _findResults = entries
                .Where(e => MatchWildcard(filePattern, Path.GetFileName(e)))
                .ToList();

            if (_findResults.Count > 0)
            {
                WriteFindResult(_findResults[0]);
                _findIndex = 1;
                _cpu.Regs.Flags &= ~CpuFlags.Carry;
            }
            else
            {
                _cpu.Regs.AX = 0x12; // No more files
                _cpu.Regs.Flags |= CpuFlags.Carry;
            }
        }
        catch
        {
            _cpu.Regs.AX = 0x12;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleFindNext()
    {
        if (_findResults != null && _findIndex < _findResults.Count)
        {
            WriteFindResult(_findResults[_findIndex]);
            _findIndex++;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        else
        {
            _cpu.Regs.AX = 0x12;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    /// <summary>Write a FindFirst/FindNext result to the DTA.</summary>
    private void WriteFindResult(string filename)
    {
        // DTA structure (43 bytes):
        // 00-14h: Reserved for FindNext
        // 15h: File attribute
        // 16h-17h: File time
        // 18h-19h: File date
        // 1Ah-1Dh: File size (DWORD)
        // 1Eh-2Ah: Filename (ASCIIZ, 13 bytes)

        ushort seg = DtaSegment;
        ushort off = DtaOffset;

        // Clear DTA
        for (int i = 0; i < 43; i++)
            _mem.WriteByte(seg, (ushort)(off + i), 0);

        // File attribute (0x20 = archive)
        _mem.WriteByte(seg, (ushort)(off + 0x15), 0x20);

        // File time/date (current time)
        var now = DateTime.Now;
        ushort time = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
        ushort date = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
        WriteDtaWord(seg, off, 0x16, time);
        WriteDtaWord(seg, off, 0x18, date);

        // File size (0 for simplicity)
        WriteDtaWord(seg, off, 0x1A, 0);
        WriteDtaWord(seg, off, 0x1C, 0);

        // Filename (up to 12 chars + null, DOS 8.3 format)
        string name = Path.GetFileName(filename).ToUpperInvariant();
        if (name.Length > 12) name = name[..12];
        for (int i = 0; i < name.Length; i++)
            _mem.WriteByte(seg, (ushort)(off + 0x1E + i), (byte)name[i]);
        _mem.WriteByte(seg, (ushort)(off + 0x1E + name.Length), 0);
    }

    private void WriteDtaWord(ushort seg, ushort dtaOff, int fieldOffset, ushort value)
    {
        uint addr = Cpu.Registers.PhysicalAddress(seg, (ushort)(dtaOff + fieldOffset));
        _mem.WriteWord(addr, value);
    }

    /// <summary>Simple wildcard pattern matching (supports * and ?).</summary>
    internal static bool MatchWildcard(string pattern, string text)
    {
        if (pattern == "*.*" || pattern == "*") return true;

        pattern = pattern.ToUpperInvariant();
        text = text.ToUpperInvariant();

        int pi = 0, ti = 0;
        int starPi = -1, starTi = -1;

        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == text[ti]))
            {
                pi++;
                ti++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                starPi = pi;
                starTi = ti;
                pi++;
            }
            else if (starPi >= 0)
            {
                pi = starPi + 1;
                starTi++;
                ti = starTi;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    private void HandleFileDateTime()
    {
        if (_cpu.Regs.AL == 0) // Get
        {
            // Return current time in DOS packed format
            var now = DateTime.Now;
            _cpu.Regs.CX = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
            _cpu.Regs.DX = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        else
        {
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
    }

    private sealed class DosFileHandle
    {
        public string Path { get; }
        public Stream Stream { get; }
        public DosFileHandle(string path, Stream stream) { Path = path; Stream = stream; }
    }

    private void HandleParseFilename()
    {
        ushort siSeg = _cpu.Regs.DS;
        ushort siOff = _cpu.Regs.SI;
        ushort diSeg = _cpu.Regs.ES;
        ushort diOff = _cpu.Regs.DI;

        // Read the input string
        string input = ReadDosString(siSeg, siOff);

        // Parse drive, filename, extension
        byte drive = 0;
        int pos = 0;

        // Skip leading separators based on AL flags
        while (pos < input.Length && (input[pos] == ' ' || input[pos] == '\t'))
            pos++;

        // Check for drive letter
        if (pos + 1 < input.Length && input[pos + 1] == ':')
        {
            drive = (byte)(char.ToUpperInvariant(input[pos]) - 'A' + 1);
            pos += 2;
        }

        // Write drive byte to FCB
        _mem.WriteByte(diSeg, diOff, drive);

        // Parse filename (8 chars, space-padded)
        for (int i = 0; i < 8; i++)
        {
            if (pos < input.Length && input[pos] != '.' && input[pos] != ' ' && input[pos] != 0)
            {
                _mem.WriteByte(diSeg, (ushort)(diOff + 1 + i), (byte)char.ToUpperInvariant(input[pos]));
                pos++;
            }
            else
                _mem.WriteByte(diSeg, (ushort)(diOff + 1 + i), (byte)' ');
        }

        // Skip dot
        if (pos < input.Length && input[pos] == '.') pos++;

        // Parse extension (3 chars, space-padded)
        for (int i = 0; i < 3; i++)
        {
            if (pos < input.Length && input[pos] != ' ' && input[pos] != 0)
            {
                _mem.WriteByte(diSeg, (ushort)(diOff + 9 + i), (byte)char.ToUpperInvariant(input[pos]));
                pos++;
            }
            else
                _mem.WriteByte(diSeg, (ushort)(diOff + 9 + i), (byte)' ');
        }

        // AL = 0 if no wildcards, 1 if wildcards present
        _cpu.Regs.AL = (byte)(input.Contains('*') || input.Contains('?') ? 1 : 0);
        // DS:SI points past the parsed name
        _cpu.Regs.SI = (ushort)(siOff + pos);
    }

    private void HandleDupHandle()
    {
        ushort handle = _cpu.Regs.BX;
        if (handle <= 4 || _fileHandles.ContainsKey(handle))
        {
            ushort newHandle = _nextHandle++;
            if (_fileHandles.TryGetValue(handle, out var fh))
                _fileHandles[newHandle] = new DosFileHandle(fh.Path, fh.Stream);
            _cpu.Regs.AX = newHandle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        else
        {
            _cpu.Regs.AX = 0x06; // Invalid handle
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleForceDupHandle()
    {
        ushort srcHandle = _cpu.Regs.BX;
        ushort dstHandle = _cpu.Regs.CX;

        // Close destination if it's an open file
        if (_fileHandles.TryGetValue(dstHandle, out var existing))
        {
            existing.Stream.Dispose();
            _fileHandles.Remove(dstHandle);
        }

        if (_fileHandles.TryGetValue(srcHandle, out var fh))
            _fileHandles[dstHandle] = new DosFileHandle(fh.Path, fh.Stream);

        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleCreateTempFile()
    {
        string dir = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        string tempName = $"~DOS{_nextHandle:X4}.TMP";
        string path = string.IsNullOrEmpty(dir) ? tempName : $"{dir}\\{tempName}";

        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;

            // Write the temp filename back to DS:DX
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(path);
            ushort off = _cpu.Regs.DX;
            for (int i = 0; i < nameBytes.Length; i++)
                _mem.WriteByte(_cpu.Regs.DS, (ushort)(off + i), nameBytes[i]);
            _mem.WriteByte(_cpu.Regs.DS, (ushort)(off + nameBytes.Length), 0);

            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleCreateNewFile()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.DX);
        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            bool exists = provider.ExistsAsync(resolvedPath).GetAwaiter().GetResult();
            if (exists)
            {
                _cpu.Regs.AX = 0x50; // File already exists
                _cpu.Regs.Flags |= CpuFlags.Carry;
                return;
            }
            var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
            ushort handle = _nextHandle++;
            _fileHandles[handle] = new DosFileHandle(path, stream);
            _cpu.Regs.AX = handle;
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }

    private void HandleCanonicalizePath()
    {
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.SI);
        // Convert to uppercase, add drive letter if missing
        string canonical = path.ToUpperInvariant().Replace('/', '\\');
        if (canonical.Length < 2 || canonical[1] != ':')
            canonical = $"{(char)('A' + _currentDrive)}:\\{canonical}";

        // Write result to ES:DI
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(canonical);
        for (int i = 0; i < bytes.Length; i++)
            _mem.WriteByte(_cpu.Regs.ES, (ushort)(_cpu.Regs.DI + i), bytes[i]);
        _mem.WriteByte(_cpu.Regs.ES, (ushort)(_cpu.Regs.DI + bytes.Length), 0);
        _cpu.Regs.Flags &= ~CpuFlags.Carry;
    }

    private void HandleExtendedOpen()
    {
        // Extended open/create (DOS 4.0+)
        // BX = open mode, CX = attributes, DX = action
        // DS:SI = filename
        string path = ReadDosString(_cpu.Regs.DS, _cpu.Regs.SI);
        ushort action = _cpu.Regs.DX;
        byte mode = (byte)(_cpu.Regs.BX & 3);

        try
        {
            var (provider, resolvedPath) = ResolveFilePath(path);
            bool exists = provider.ExistsAsync(resolvedPath).GetAwaiter().GetResult();

            if (exists)
            {
                if ((action & 0x01) != 0) // Open if exists
                {
                    Stream stream = mode switch
                    {
                        0 => provider.OpenReadAsync(resolvedPath).GetAwaiter().GetResult(),
                        1 => provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult(),
                        _ => provider.OpenReadWriteAsync(resolvedPath).GetAwaiter().GetResult()
                    };
                    ushort handle = _nextHandle++;
                    _fileHandles[handle] = new DosFileHandle(path, stream);
                    _cpu.Regs.AX = handle;
                    _cpu.Regs.CX = 1; // File existed and was opened
                }
                else if ((action & 0x02) != 0) // Replace if exists
                {
                    var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
                    ushort handle = _nextHandle++;
                    _fileHandles[handle] = new DosFileHandle(path, stream);
                    _cpu.Regs.AX = handle;
                    _cpu.Regs.CX = 3; // File existed and was replaced
                }
                else
                {
                    _cpu.Regs.AX = 0x50; // File exists
                    _cpu.Regs.Flags |= CpuFlags.Carry;
                    return;
                }
            }
            else
            {
                if ((action & 0x10) != 0) // Create if not exists
                {
                    var stream = provider.OpenWriteAsync(resolvedPath).GetAwaiter().GetResult();
                    ushort handle = _nextHandle++;
                    _fileHandles[handle] = new DosFileHandle(path, stream);
                    _cpu.Regs.AX = handle;
                    _cpu.Regs.CX = 2; // File created
                }
                else
                {
                    _cpu.Regs.AX = 0x02; // File not found
                    _cpu.Regs.Flags |= CpuFlags.Carry;
                    return;
                }
            }
            _cpu.Regs.Flags &= ~CpuFlags.Carry;
        }
        catch
        {
            _cpu.Regs.AX = 0x05;
            _cpu.Regs.Flags |= CpuFlags.Carry;
        }
    }
}
